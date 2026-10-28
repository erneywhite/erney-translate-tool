# CLAUDE.md — рабочий гайд для Claude Code

> Этот файл Claude Code читает автоматически при старте сессии в этой папке.
> Подробная история и нерешённые задачи — в `docs/SESSION-HANDOFF.md`.

## Что это за проект

**Erney's Translate Tool** — WPF (.NET 10) десктоп-приложение под Windows.
Перевод текста с экрана в реальном времени: OCR находит текст в выбранном
окне игры/программы, переводит, рисует перевод полупрозрачной click-through
«табличкой» поверх оригинала. Основная аудитория — игры и визуальные новеллы.

- **Репозиторий:** github.com/erneywhite/erney-translate-tool (ветка `main`)
- **Текущая версия:** 1.0.30 (см. `<Version>` в csproj — это источник правды)
- **Язык общения с пользователем:** русский
- **Автор:** Erney White

## Технологии

- WPF, .NET 10 (`net10.0-windows10.0.19041.0`), x64, MVVM. Переезд с .NET 8 в v1.0.30
  (поддержка .NET 8 кончается 10.11.2026). На машине Erney стоит только .NET 10.
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
| `Core/Translators/ModelCatalog.cs` | Модели LLM: дефолты, пресеты, отключённые провайдером id + миграция `settings.json` (v1.0.30). Модели отключают часто — сверять раз в пару месяцев |
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
2. **Сборка-проверка и тесты:**
   `dotnet build ErneyTranslateTool/ErneyTranslateTool.csproj -c Release`
   `dotnet test ErneyTranslateTool.Tests -c Release`
3. **Publish (self-contained):**
   `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true`
4. **Инсталлятор (Inno Setup):**
   `"C:\Users\erney\AppData\Local\Programs\Inno Setup 6\ISCC.exe" Installer/setup.iss`
   (запускать из `ErneyTranslateTool/`)
5. **Результат:** `ErneyTranslateTool/Installer/Output/ErneyTranslateTool-Setup-X.Y.Z.exe` (~161 МБ)
6. **Commit + push** в `main`.
7. **Отдать пользователю текст для GitHub Release** (tag / title / body) — он
   сам публикует релиз и прикрепляет инсталлятор вручную.

## Конвенции

- **Commit-сообщения:** подробные, многострочные, с объяснением *почему* (не только что).
  Без трейлера `Co-Authored-By` (глобальное правило Erney).
- **Release notes («Что нового»):** пишутся на русском, **для конечного
  пользователя**, без фраз вроде «как ты просил» / «по твоей просьбе». Это
  текст для юзеров, а не для разработчика.
- **Версионирование:** patch-релизы 1.0.X на каждую фичу/фикс.
- **Билд-warnings:** DeepLTranslator CS8602, WindowsOcrBackend CS0414 — старые, игнорировать.
  NU1902 про SharpCompress 0.40.0 — известно: тянет `Sdcb.PaddleOCR.Models.Online` 3.0.1,
  а 0.48+ ломает скачивание моделей (`MissingMethodException`, проверено). Уйдёт вместе
  с обновлением PaddleOCR до 3.3.1 (нужна перепроверка склейки на играх).
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
- **PaddleOCR кэширует модели** в `%APPDATA%\paddleocr-models`.
- **LLM-модели нового поколения думают по умолчанию** (Claude 4.7+/5.x, GPT-5/6, Gemini 3,
  GPT-OSS на Groq): не принимают `temperature`/`max_tokens` и т. п. Формат запроса
  выбирается по id модели в `NewRequest` каждого переводчика.
- **Оверлей: промежуточный рендер (`isPartial`)** не удаляет таблички, которых ещё нет
  в списке. Режим входит в отпечаток `SetRegions`, иначе финальный рендер пропускается
  и перевод исчезнувшего текста зависает (пойман вживую при разработке v1.0.30).

## Главная нерешённая задача

**Мерцание оверлея при склейке строк** — на одной и той же сцене перевод иногда
прыгает между «склеено в один блок» и «разорвано на два». Перепробовано много
(v1.0.17–v1.0.28), частично помогло, но не вылечено. Подробности и план
следующей попытки — в `docs/SESSION-HANDOFF.md`.
