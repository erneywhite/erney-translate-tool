# Erney's Translate Tool — Project Prompt

This file contains the full specification prompt used to generate the base of the project via AI (arena.ai).

---

You are an expert C#/.NET developer. Create a complete, production-ready Windows desktop application called "Erney's Translate Tool" (namespace: ErneyTranslateTool, short alias: ETT) — a real-time screen OCR translator designed primarily for gaming.

---

## TECH STACK

- Language: C# (.NET 8, WPF)
- OCR Engine: Windows.Media.OCR (WinRT, built-in Windows 10/11)
- Translation API: DeepL API (official DeepL .NET SDK: DeepLcom/deepl-dotnet)
- Screen Capture: Windows Graphics Capture API (WGC) via Windows.Graphics.Capture
- Installer: Inno Setup script (.iss) bundled in project
- Logging: Serilog (Info / Warn / Error levels, rolling file log)
- Tray: Hardcodet.NotifyIcon.Wpf

---

## CORE FUNCTIONALITY

### Screen Capture
- Use Windows Graphics Capture API to capture a specific application window selected by the user (by window title / process picker)
- Capture must be bound to the selected window handle — when the game is minimized or out of focus, do NOT capture other windows
- Support for windowed and borderless fullscreen modes primarily; add best-effort support for exclusive fullscreen via WGC (Windows Fullscreen Optimizations on Win10/11 make this work in most modern games)
- Capture the full window content continuously

### OCR & Text Detection
- Use Windows.Media.OCR (WinRT OcrEngine) for text recognition
- Auto-detect source language (support: English, Japanese, Chinese Simplified, Chinese Traditional, and all other languages supported by WinRT OCR)
- For Japanese and Chinese: detect if Windows language packs are installed; if not — show an in-app warning with instructions on how to install them via Windows Settings
- Run OCR on the full captured window frame
- Detect all text bounding boxes (word/line level) returned by OcrEngine
- Compare the hash (SHA256 or xxHash) of each detected text region image with the previous frame's hash — only send to translation API if the content has changed (change detection, not timer-based)
- Skip any text regions that are entirely in Cyrillic/Russian — do not translate them
- For mixed regions (e.g., "Hello, my name is Ваня") — translate the whole string; preserve Cyrillic parts as-is in the translation output

### Translation
- Use DeepL API with automatic source language detection (do not pass source_lang parameter)
- Target language: Russian (RU)
- Implement a translation cache: store (sourceText → translatedText) pairs in a local SQLite database (Microsoft.Data.Sqlite); do not re-call API if the exact text was already translated in this session or previous sessions
- Cache is persistent across sessions (stored in %AppData%\ErneyTranslateTool\cache.db)
- Cache has no size limit but provide a "Clear Cache" button in Settings

### Overlay Window
- Create a transparent, click-through, always-on-top WPF overlay window (AllowsTransparency=True, Topmost=True, WindowStyle=None)
- Click-through via WinAPI: SetWindowLong with WS_EX_TRANSPARENT | WS_EX_LAYERED
- For each detected and translated text region:
  - Draw a dark semi-transparent rectangle (background color: #1A1A1A at ~85% opacity) over the exact bounding box of the original text
  - Render the translated Russian text inside that rectangle
  - Font: user-selectable from installed system fonts (default: Segoe UI), configurable in Settings
  - Font size: auto-scale to fit the bounding box height of the original text region (match source text size approximately)
  - Text color: white (#FFFFFF)
  - Add 4px padding inside the rectangle
- Overlay must correctly handle DPI scaling (set dpiAware=true in app manifest; use DPI-aware coordinate mapping between WGC capture and screen coordinates)
- When the game window moves or resizes, the overlay must reposition itself accordingly (track window position via WinAPI GetWindowRect in a background loop)
- Multi-monitor support: the overlay follows the game window to whichever monitor it is on

### Hotkeys (global, system-wide)
- Ctrl+Shift+T — Toggle translation on/off
- Ctrl+Shift+H — Hide/show overlay (without stopping translation engine)
- Register via RegisterHotKey WinAPI

---

## MAIN APPLICATION WINDOW (Settings & Control Panel)

Build a clean, modern WPF UI using standard WPF controls (no third-party UI libraries required). The UI should look polished and professional — dark theme preferred.

### Tab 1: Main / Control
- Window picker: dropdown or "Pick Window" button that opens a list of running windows/processes with their icons; user selects the target game window
- Status indicator: shows current state (Idle / Capturing / Translating / Paused / Error)
- Toggle button: Start / Stop translation (same as Ctrl+Shift+T)
- Quick stats: characters translated today, API calls saved by cache (hit rate %)

### Tab 2: Translation Settings
- DeepL API Key input (password field, masked by default, show/hide toggle)
- Instructions section: step-by-step guide on how to get a DeepL API Free key (with clickable link to https://www.deepl.com/pro-api), explaining it's free with 500K chars/month, no credit card needed
- Target language selector (default: Russian; allow changing to other DeepL-supported languages)
- "Test API Key" button — sends a test request and shows success/failure
- Language packs notice: detect installed WinRT OCR languages and show which are available; show install instructions for Japanese/Chinese if missing

### Tab 3: Overlay Settings
- Font family selector (ComboBox populated with installed system fonts)
- Font size mode: Auto (match source) or Manual (slider 8–32pt)
- Overlay background opacity slider (60–100%)
- Background color picker (default: #1A1A1A)
- Text color picker (default: #FFFFFF)
- Preview panel showing how the overlay will look with current settings

### Tab 4: History
- Session-based translation history stored in SQLite (%AppData%\ErneyTranslateTool\history.db)
- Each session is identified by: session start time + target window title (game name)
- Show sessions as collapsible groups, sorted newest-first
- Each entry shows: timestamp, original text, translated text
- Search/filter bar
- Export session to .txt or .csv
- "Clear History" button with confirmation dialog

### Tab 5: About / Info
- App name, version, author
- Link to DeepL API docs
- "Check for language packs" button
- "Open log file" button
- "Clear translation cache" button with cache size shown

---

## LOGGING (Serilog)

- Log file location: %AppData%\ErneyTranslateTool\logs\ett-.log (rolling daily)
- Log levels:
  - INFO: app start/stop, session start, translation stats, cache hits
  - WARN: OCR language pack missing, API rate limit approaching
  - ERROR: API call failed, capture failed, hotkey registration failed
- ERROR-level events: also show a Windows tray balloon notification with a brief user-friendly message
- INFO/WARN: log to file only, no UI interruption

---

## ANTI-CHEAT COMPATIBILITY

- Do NOT use any kernel-mode drivers or hooks
- Use only user-mode Windows APIs (WGC, WinRT OCR, WinAPI)
- Do NOT inject into game process memory
- Do NOT hook DirectX/OpenGL calls
- Add a disclaimer in the About tab: "This tool uses standard Windows screen capture APIs (Windows Graphics Capture). It does not modify game memory or inject code. However, use in online multiplayer games may still violate game ToS. Use at your own risk."

---

## PROJECT STRUCTURE

```
ErneyTranslateTool/
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / .cs          ← Main settings window
├── OverlayWindow.xaml / .cs       ← Transparent click-through overlay
├── Core/
│   ├── CaptureService.cs          ← WGC screen capture
│   ├── OcrService.cs              ← WinRT OCR, language detection, region hashing
│   ├── TranslationService.cs      ← DeepL API calls, cache logic
│   ├── OverlayManager.cs          ← Overlay positioning, DPI mapping, region rendering
│   ├── HotkeyService.cs           ← Global hotkey registration
│   └── WindowPickerService.cs     ← Enumerate windows, get handles
├── Data/
│   ├── CacheRepository.cs         ← SQLite cache CRUD
│   ├── HistoryRepository.cs       ← SQLite history CRUD
│   └── AppSettings.cs             ← JSON settings (System.Text.Json)
├── Models/
│   ├── TranslationRegion.cs       ← Bounding box + source text + translated text
│   ├── SessionHistory.cs
│   └── AppConfig.cs
├── ViewModels/                    ← MVVM (INotifyPropertyChanged, RelayCommand)
│   ├── MainViewModel.cs
│   ├── SettingsViewModel.cs
│   └── HistoryViewModel.cs
├── Views/
│   └── Tabs/
│       ├── MainTab.xaml
│       ├── TranslationSettingsTab.xaml
│       ├── OverlaySettingsTab.xaml
│       ├── HistoryTab.xaml
│       └── AboutTab.xaml
├── Resources/
│   ├── Styles.xaml                ← Dark theme, consistent WPF styles
│   └── Icons/                    ← App icon (.ico), tray icon
├── Installer/
│   └── setup.iss                  ← Inno Setup script
├── ErneyTranslateTool.csproj
└── README.md
```

---

## SETTINGS PERSISTENCE

- Store all user settings in `%AppData%\ErneyTranslateTool\settings.json`
- Encrypted storage for API key: use Windows DPAPI (ProtectedData.Protect) to encrypt the DeepL API key before saving
- Settings auto-save on change

---

## ADDITIONAL REQUIREMENTS

- MVVM architecture throughout (ViewModels, RelayCommand, INotifyPropertyChanged)
- Async/await everywhere for non-blocking UI (capture loop, OCR, API calls)
- App manifest: set dpiAwareness to PerMonitorV2
- Tray icon: app minimizes to tray (not taskbar) when main window is closed; double-click tray icon to restore
- App icon: design a simple, recognizable SVG icon for ETT — translate/language theme, export as .ico (256x256, 64x64, 32x32, 16x16 sizes)
- On first launch: show onboarding wizard (API key setup + language pack check)
- All strings that appear in UI should be in a ResourceDictionary (for future localization)
- NuGet packages to use:
  - DeepL.net (official DeepL SDK)
  - Microsoft.Data.Sqlite
  - Serilog + Serilog.Sinks.File + Serilog.Sinks.Debug
  - Hardcodet.NotifyIcon.Wpf
  - (NO other third-party UI frameworks)

Generate the complete, compilable solution with all files. Prioritize correctness and completeness. Add XML doc comments to all public methods. Use C# 12 features where appropriate (primary constructors, collection expressions).
