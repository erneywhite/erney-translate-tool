# CLAUDE.md — рабочий гайд для Claude Code

> Этот файл Claude Code читает автоматически при старте сессии в этой папке.
> Подробная история и нерешённые задачи — в `docs/SESSION-HANDOFF.md`.

## Что это за проект

**Erney's Translate Tool** — WPF (.NET 8) десктоп-приложение под Windows.
Перевод текста с экрана в реальном времени: OCR находит текст в выбранном
окне игры/программы, переводит, рисует перевод полупрозрачной click-through
«табличкой» поверх оригинала. Основная аудитория — игры и визуальные новеллы.

- **Репозиторий:** github.com/erneywhite/erney-translate-tool (ветка `main`)
- **Текущая версия:** 1.0.29 (см. `<Version>` в csproj — это источник правды)
- **Язык общения с пользователем:** русский
- **Автор:** Erney White

## Технологии

- WPF, .NET 8 (`net8.0-windows10.0.19041.0`), x64, MVVM
- SQLite (`Microsoft.Data.Sqlite`) — кэш, история, глоссарий, профили
- DPAPI — шифрование API-ключей
- OCR: PaddleOCR (`Sdcb.PaddleOCR`), Tesseract, Windows.Media.Ocr
- Перевод: DeepL.net + собственный HTTP для MyMemory / Google / LibreTranslate /
  OpenAI / Anthropic / Gemini / Groq
- Serilog (логи), Hardcodet.NotifyIcon.Wpf (трей), Inno Setup (инсталлятор)

## Архитектура — ключевые файлы

| Файл | Назначение |
|---|---|
| `Core/TranslationEngine.cs` | Пайплайн: capture → OCR → группировка → перевод → оверлей. Здесь frame-reuse (v1.0.25), grouping hysteresis (v1.0.23/24), OCR-jitter стабилизация (v1.0.19) |
| `Core/RegionGrouper.cs` | Склейка строк OCR в абзацы (text-aware пороги, LooksLikeLabel для меню-кнопок) |
| `Core/TranslationService.cs` | Оркестратор: кэш + глоссарий + переводчик, fallback-механизм, streaming-диспетч |
| `Core/Translators/` | `ITranslator` + `IStreamingTranslator` + 8 реализаций |
| `Core/WindowPickerService.cs` | Перечисление окон + фильтр системных/фоновых (v1.0.28) |
| `Core/OverlayManager.cs`, `Views/OverlayWindow.xaml.cs` | Click-through оверлей |
| `Views/Tabs/` | 8 вкладок UI |
| `Resources/Themes/` | 6 файлов тем (7 тем с учётом Auto) |
| `Resources/Strings.{ru,en}.xaml` | Локализация — ВСЕ user-facing строки сюда, парами RU/EN |

## Workflow релиза (СТРОГО по шагам)

1. **Поднять версию в 4 местах:**
   - `ErneyTranslateTool/ErneyTranslateTool.csproj` → `<Version>X.Y.Z</Version>`
   - `ErneyTranslateTool/Installer/setup.iss` → `#define MyAppVersion "X.Y.Z"`
   - `ErneyTranslateTool/Resources/Strings.ru.xaml` → `Strings.About.Subtitle` (`Версия X.Y.Z · MIT License`)
   - `ErneyTranslateTool/Resources/Strings.en.xaml` → `Strings.About.Subtitle` (`Version X.Y.Z · MIT License`)
2. **Сборка-проверка:**
   `dotnet build ErneyTranslateTool/ErneyTranslateTool.csproj -c Release`
3. **Publish (self-contained):**
   `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true`
4. **Инсталлятор (Inno Setup):**
   `"C:\Users\erney\AppData\Local\Programs\Inno Setup 6\ISCC.exe" Installer/setup.iss`
   (запускать из `ErneyTranslateTool/`)
5. **Результат:** `ErneyTranslateTool/Installer/Output/ErneyTranslateTool-Setup-X.Y.Z.exe` (~158 МБ)
6. **Commit + push** в `main`.
7. **Отдать пользователю текст для GitHub Release** (tag / title / body) — он
   сам публикует релиз и прикрепляет инсталлятор вручную.

## Конвенции

- **Commit-сообщения:** подробные, многострочные, с объяснением *почему* (не только что).
  В конце — trailer `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
  (модель из текущего окружения).
- **Release notes («Что нового»):** пишутся на русском, **для конечного
  пользователя**, без фраз вроде «как ты просил» / «по твоей просьбе». Это
  текст для юзеров, а не для разработчика.
- **Версионирование:** patch-релизы 1.0.X на каждую фичу/фикс.
- **Билд-warnings:** 4 штуки (DeepLTranslator CS8602, WindowsOcrBackend CS0414) —
  pre-existing, НЕ от нас, игнорировать.
- **git LF→CRLF warnings** — нормально, не обращать внимания.

## Важные технические гочи (грабли)

- **Каждое WPF `Window` — свой визуальный корень**, не наследует `TextOptions`
  от родителя. На каждом Window для чёткого текста нужно повторять
  `TextOptions.TextFormattingMode="Display"`, `TextRenderingMode="ClearType"`,
  `UseLayoutRounding`, `SnapsToDevicePixels`. (Баг размытого текста v1.0.15.)
- **`PasswordBox` не наследует** имплицитный стиль `TextBox` — ему нужен свой
  стиль (`DarkPasswordBoxStyle` в Styles.xaml, v1.0.19).
- **Нельзя биндить `DynamicResource` на `Color`** внутри `Binding.Source` —
  поэтому per-theme `PrimaryGradientBrush` определён в каждом файле темы отдельно.
- **Установленное приложение** живёт в `%LocalAppData%\Programs\ErneyTranslateTool`
  — отдельно от исходников. Автозапуск — через реестр HKCU\...\Run.
- **PaddleOCR кэширует модели** в `%LocalAppData%\Sdcb`.

## Главная нерешённая задача

**Мерцание оверлея при склейке строк** — на одной и той же сцене перевод иногда
прыгает между «склеено в один блок» и «разорвано на два». Перепробовано много
(v1.0.17–v1.0.28), частично помогло, но не вылечено. Подробности и план
следующей попытки — в `docs/SESSION-HANDOFF.md`.
