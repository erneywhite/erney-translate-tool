You are an expert C#/.NET developer. Create a complete, production-ready Windows desktop application called "Erney's Translate Tool" (namespace: ErneyTranslateTool, short alias: ETT) — a real-time screen OCR translator designed primarily for gaming.

---

## IMPORTANT OUTPUT INSTRUCTIONS

This is a large project. You MUST output ALL files completely — do not skip, abbreviate, or summarize any file with comments like "// rest of code here".

Output each file in a separate fenced code block, preceded by a comment line with the file path, like this:

// FILE: ErneyTranslateTool/App.xaml.cs
```csharp
... full file content ...
```

If you are approaching your output token limit and cannot finish all files in one response, at the end of your response write exactly:
**CONTINUE_FROM: [last file you completed]**

When the user sends "continue", resume from the next file in the project structure and keep going until all files are output. Do not repeat files already completed.

---

## LANGUAGE REQUIREMENTS

- The entire application UI must be in Russian language (all labels, buttons, tabs, tooltips, status messages, error messages, dialogs, onboarding wizard text)
- README.md must be written in Russian
- Code comments and XML doc comments must be in English (standard for code)
- Log file messages must be in English
- ResourceDictionary strings (for UI) must be in Russian

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
- For Japanese and Chinese: detect if Windows language packs are installed; if not — show an in-app warning (in Russian) with instructions on how to install them via Windows Settings
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
- Cache has no size limit but provide a "Очистить кэш" button in Settings

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

Build a clean, modern WPF UI using standard WPF controls (no third-party UI libraries required). The UI must be fully in Russian. Dark theme preferred.

### Вкладка 1: Главная
- Window picker: кнопка "Выбрать окно" — opens a list of running windows/processes with their icons; user selects the target game window
- Status indicator: shows current state in Russian (Ожидание / Захват / Перевод / Пауза / Ошибка)
- Toggle button: "Запустить" / "Остановить" перевод (same as Ctrl+Shift+T)
- Quick stats: переведено символов сегодня, запросов сохранено кэшем (% попаданий)

### Вкладка 2: Настройки перевода
- DeepL API Key input (поле с паролем, скрыто по умолчанию, кнопка показать/скрыть)
- Instructions section in Russian: пошаговая инструкция как получить бесплатный ключ DeepL API (с кликабельной ссылкой на https://www.deepl.com/pro-api), объясняя что это бесплатно — 500К символов/месяц, карта не нужна
- Target language selector (default: Russian; allow changing to other DeepL-supported languages) — label in Russian
- Кнопка "Проверить ключ" — sends a test request and shows success/failure in Russian
- Language packs notice in Russian: detect installed WinRT OCR languages and show which are available; show install instructions for Japanese/Chinese if missing

### Вкладка 3: Настройки оверлея
- Font family selector (ComboBox populated with installed system fonts) — label in Russian
- Font size mode: Авто (match source) or Вручную (slider 8–32pt)
- Overlay background opacity slider (60–100%) — label in Russian
- Background color picker (default: #1A1A1A)
- Text color picker (default: #FFFFFF)
- Preview panel showing how the overlay will look with current settings — labeled in Russian

### Вкладка 4: История
- Session-based translation history stored in SQLite (%AppData%\ErneyTranslateTool\history.db)
- Each session is identified by: session start time + target window title (game name taken from window title)
- Show sessions as collapsible groups, sorted newest-first
- Each entry shows: время, оригинальный текст, перевод
- Search/filter bar — labeled in Russian
- Export session to .txt or .csv — button labeled in Russian
- Кнопка "Очистить историю" with Russian confirmation dialog

### Вкладка 5: О программе
- App name, version, author
- Link to DeepL API docs
- Кнопка "Проверить языковые пакеты"
- Кнопка "Открыть файл лога"
- Кнопка "Очистить кэш переводов" with cache size shown
- Anti-cheat disclaimer in Russian: "Эта программа использует стандартный Windows API захвата экрана (Windows Graphics Capture). Она не изменяет память игры и не внедряет код. Тем не менее, использование в онлайн-играх с мультиплеером может нарушать условия использования игры. Используйте на свой страх и риск."

---

## LOGGING (Serilog)

- Log file location: %AppData%\ErneyTranslateTool\logs\ett-.log (rolling daily)
- Log levels:
  - INFO: app start/stop, session start, translation stats, cache hits
  - WARN: OCR language pack missing, API rate limit approaching
  - ERROR: API call failed, capture failed, hotkey registration failed
- ERROR-level events: also show a Windows tray balloon notification with a brief user-friendly message IN RUSSIAN
- INFO/WARN: log to file only, no UI interruption

---

## ANTI-CHEAT COMPATIBILITY

- Do NOT use any kernel-mode drivers or hooks
- Use only user-mode Windows APIs (WGC, WinRT OCR, WinAPI)
- Do NOT inject into game process memory
- Do NOT hook DirectX/OpenGL calls

---

## PROJECT STRUCTURE

```
ErneyTranslateTool/
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / .cs
├── OverlayWindow.xaml / .cs
├── Core/
│ ├── CaptureService.cs
│ ├── OcrService.cs
│ ├── TranslationService.cs
│ ├── OverlayManager.cs
│ ├── HotkeyService.cs
│ └── WindowPickerService.cs
├── Data/
│ ├── CacheRepository.cs
│ ├── HistoryRepository.cs
│ └── AppSettings.cs
├── Models/
│ ├── TranslationRegion.cs
│ ├── SessionHistory.cs
│ └── AppConfig.cs
├── ViewModels/
│ ├── MainViewModel.cs
│ ├── SettingsViewModel.cs
│ └── HistoryViewModel.cs
├── Views/
│ └── Tabs/
│ ├── MainTab.xaml
│ ├── TranslationSettingsTab.xaml
│ ├── OverlaySettingsTab.xaml
│ ├── HistoryTab.xaml
│ └── AboutTab.xaml
├── Resources/
│ ├── Styles.xaml
│ ├── Strings.ru.xaml ← Russian UI strings ResourceDictionary
│ └── Icons/
├── Installer/
│ └── setup.iss
├── ErneyTranslateTool.csproj
└── README.md
```


---

## README.md REQUIREMENTS

README.md must be written entirely in Russian and include:
- Название и описание программы
- Скриншот или ASCII-схема интерфейса
- Системные требования (Windows 10/11, .NET 8)
- Инструкция по установке
- Как получить и настроить DeepL API ключ
- Горячие клавиши
- Описание всех вкладок
- Инструкция по установке языковых пакетов Windows для японского/китайского
- Раздел "Совместимость с античитом"
- Лицензия (MIT)

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
- Tray icon: app minimizes to tray (not taskbar) when main window is closed; double-click tray icon to restore; tray context menu in Russian (Открыть, Запустить/Остановить, Выход)
- On first launch: show onboarding wizard in Russian (API key setup + language pack check)
- All UI strings must be defined in Resources/Strings.ru.xaml ResourceDictionary
- NuGet packages to use:
  - DeepL.net (official DeepL SDK)
  - Microsoft.Data.Sqlite
  - Serilog + Serilog.Sinks.File + Serilog.Sinks.Debug
  - Hardcodet.NotifyIcon.Wpf
  - (NO other third-party UI frameworks)

Generate the complete, compilable solution with all files. Prioritize correctness and completeness. Add XML doc comments (in English) to all public methods. Use C# 12 features where appropriate (primary constructors, collection expressions).
