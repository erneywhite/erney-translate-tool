# Erney's Translate Tool

**Перевод текста с экрана в реальном времени** — для игр, визуальных новелл и любого софта на иностранном языке. Программа находит текст в выбранном окне через OCR, переводит и показывает русский перевод полупрозрачной «табличкой» поверх оригинала. Кликам не мешает.

![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)
![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%2B-blue)
![.NET 8](https://img.shields.io/badge/.NET-8.0-purple)

---

## Возможности

### Распознавание и перевод
- **3 OCR-движка на выбор** — PaddleOCR (нейросеть, лучшее качество), Tesseract (быстрее), Windows OCR (системный, нужны языковые пакеты)
- **30+ языков для Tesseract** — Европа целиком (от английского и немецкого до прибалтийских и скандинавских), кириллица, восточноазиатские (jpn/chi/kor + вертикальные), арабский. Скачиваются одной кнопкой из встроенного каталога
- **12 языков для PaddleOCR** — латиница, кириллица, японский, китайский (упр./трад.), корейский, арабский, хинди + индийские, плюс **режим «Авто»** — несколько моделей параллельно
- **6 переводчиков** — MyMemory (бесплатно, без регистрации), Google Translate (бесплатно, без ключа), DeepL (нужен ключ), LibreTranslate (любой инстанс, можно свой), **OpenAI** (LLM, нужен платный ключ — лучшее качество для художественных текстов и игровых диалогов), **Anthropic Claude** (LLM, нужен платный ключ)
- **Резервный провайдер** — если основной несколько раз подряд возвращает ошибку, программа автоматически переключается на запасной и каждую минуту проверяет восстановился ли основной
- **Контекст последних реплик для LLM-провайдеров** — последние N (настраивается, по умолчанию 3) пар «оригинал — перевод» передаются как conversation history. Помогает корректно обрабатывать местоимения и продолжающиеся диалоги
- **Кэш переводов** в SQLite + **настраиваемый лимит размера** (50 МБ / 200 МБ / 500 МБ / 1 ГБ / без лимита) с LRU-вытеснением самых давно неиспользуемых записей
- **Маскирование API-ключей** в UI с кнопкой 👁 «показать», ключи шифруются через Windows DPAPI

### Глоссарий имён собственных ⭐
Заведи правило «Оригинал → Перевод» — программа подменит каждое вхождение, **минуя кэш и переводчика**. Спасает от того что автоперевод путает имена и названия (Geralt → Геральт каждый раз по-новому). Поддерживает регистр и word-boundary. Импорт/экспорт JSON для шеринга наборов под конкретные игры.

### Профили под игру 🎮
- При первом запуске перевода на новой игре программа **автоматически создаёт профиль** с именем процесса (`Witcher3.exe` → профиль «Witcher3»)
- При следующих запусках профиль подхватывается сам по `MatchByProcessName`
- Изменения настроек оверлея/перевода/OCR при активном профиле **сразу пишутся в этот профиль**
- В каждом профиле — свой OCR-движок, языки, провайдер, шрифт, цвета, прозрачность, флаг глоссария
- Импорт/экспорт JSON

### Оверлей
- **Click-through** — переводы поверх игры не мешают кликать по элементам
- **8 готовых пресетов** — Классика, Тёмная мягкая, Светлая, Sepia, Cyber neon, Discord, Hi-contrast, Glass
- **Кастомные цвета**, прозрачность, скругление углов, шрифт
- **Группировка соседних строк** — многострочный диалог переводится как одно предложение

### Системная интеграция
- **Сворачивание в трей** — программа живёт фоном
- **Цветная индикация в трее** — зелёная точка (перевод активен), серая (idle), серая моргающая (пауза при свёрнутой игре), жёлтая (есть уведомление, например доступно обновление), красная (ошибка)
- **Запуск с Windows (свёрнутым в трей)** — для тех кто играет регулярно
- **4 темы программы** — Авто (по системе Windows, переключается на лету), Dark, Light, Nord
- **Двуязычный интерфейс** — Русский / English, переключается на лету (без перезапуска)
- **Глобальные горячие клавиши** — `Ctrl+Shift+T` старт/стоп, `Ctrl+Shift+H` скрыть оверлей
- **Авто-пауза** при свёрнутом окне игры (статус-сообщение в окне + моргающая точка в трее)
- **Live-статистика** — символы за день, hit rate кэша, среднее время на фрейм (OCR + перевод + отрисовка) обновляются раз в секунду пока движок работает

### Авто-обновление в один клик
- Программа сама проверяет GitHub Releases на старте
- При наличии обновления показывает диалог с release notes и кнопкой **«Обновить сейчас»**
- Скачивает установщик в `%TEMP%` с прогресс-баром, запускает silent-установку, завершает текущий процесс, новая версия запускается автоматически
- При первом запуске после обновления показывает «Что нового» с описанием изменений

### История переводов
- Все сессии сохраняются в SQLite (`history.db`)
- Поиск по тексту, экспорт сессии в файл

---

## Скриншоты

> *(будут добавлены)*

---

## Системные требования

- Windows 10 (1809+) или Windows 11, x64
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) (или Windows Desktop Runtime) — устанавливается вместе с программой если её нет
- [Visual C++ Redistributable 2015–2022](https://aka.ms/vs/17/release/vc_redist.x64.exe) — нужен для PaddleOCR
- ~2 ГБ свободной RAM (для PaddleOCR с моделями)

---

## Быстрый старт

### Установка

1. Идите в [Releases](https://github.com/erneywhite/erney-translate-tool/releases) и скачайте последний `ErneyTranslateTool-Setup-X.Y.Z.exe`
2. Запустите установщик — ставится в `%LocalAppData%\Programs\ErneyTranslateTool` без UAC, без админских прав
3. Готово, дальше программа сама будет проверять и предлагать обновления

### Использование

1. Запустите игру в **оконном** или **borderless**-режиме (полный экран не подходит — Windows API не сможет захватить пиксели)
2. На вкладке **«Главная»**: «Обновить список» → выберите окно игры → «Запустить» (или горячая клавиша `Ctrl+Shift+T`)
3. Поверх английского/японского/любого иностранного текста появятся русские переводы
4. Игру можно тыкать как обычно — оверлей пропускает клики
5. **При первом запуске на этой игре** программа автоматически создаст для неё профиль — все будущие настройки будут привязаны к нему

### Горячие клавиши

| Действие | Комбинация |
|---|---|
| Включить/выключить перевод | `Ctrl+Shift+T` |
| Показать/скрыть оверлей | `Ctrl+Shift+H` |

Меняются в **Настройки перевода → Горячие клавиши**.

---

## Профили под игру — подробнее

После v1.0.6 настройки автоматически разделяются по играм. Workflow выглядит так:

1. Открываешь Witcher 3 → выбираешь окно → «Запустить». Программа видит что подходящего профиля нет, создаёт «Witcher3» (по имени процесса) и применяет к нему текущие настройки
2. Меняешь шрифт оверлея, выбираешь DeepL вместо MyMemory, включаешь подходящую модель PaddleOCR — все эти изменения по нажатию «Сохранить» автоматически уходят в профиль «Witcher3»
3. Открываешь Persona 5 → выбираешь окно → «Запустить». Программа создаёт «Persona5» с дефолтными настройками — Witcher 3 не затрагивается
4. Возвращаешься в Witcher 3 — профиль «Witcher3» подхватывается автоматически по имени exe, все твои настройки на месте

Управление — на вкладке **«Профили»**. Можно править имена, шаблоны совпадений (по window title или по process name), добавлять руками, импортировать готовые наборы JSON.

---

## Глоссарий — подробнее

Бывает что переводчик путает имена. «Geralt» сегодня — «Геральт», завтра — «Геральд», а в третий раз транскрибирует как «Жеральт». Глоссарий это лечит:

1. **Настройки → Глоссарий → Добавить**
2. Заполняешь «Оригинал» = `Geralt`, «Перевод» = `Геральт`
3. (Опционально) галка «Регистр», галка «Слово» (по умолчанию включена — `Mer` не подменит внутри `Merlin`)

При следующем переводе:
- Если OCR увидит ровно `Geralt` — программа возьмёт `Геральт` напрямую из правила, **не дёргая ни кэш, ни переводчик**. В Истории такая запись помечается языком `glossary`
- Если OCR увидит `I am Geralt` — переводчик переведёт как обычно (`Я Геральт`), затем глоссарий подменит каждое вхождение `Geralt` на `Геральт` (если переводчик использовал что-то другое — поправит)

Импорт/экспорт JSON — можно поделиться готовыми наборами для конкретных игр.

Мастер-тогл «Использовать глоссарий» отключает все правила одним кликом, не удаляя их.

---

## Где хранятся данные

Программа портативная: всё рядом с `ErneyTranslateTool.exe` если папка доступна на запись, иначе в `%AppData%\ErneyTranslateTool\`.

```
settings.json    — настройки приложения (тема, hotkeys, флаги)
cache.db         — кэш переводов (LRU, лимит настраивается)
history.db       — история сессий с поиском
glossary.db      — правила глоссария
profiles.db      — профили под игру
logs/            — журналы работы (хранится 30 дней)
tessdata/        — модели Tesseract (английский/русский/японский встроены, остальные качаются по кнопке)
```

PaddleOCR кэширует свои модели в `%LocalAppData%\Sdcb.Paddle.OnlineModels\`.

Установщик безопасный: при удалении сохраняет `settings.json`, `cache.db`, `history.db`, `glossary.db`, `profiles.db` (чистит только логи и tessdata) — переустановка не теряет твою конфигурацию.

---

## Сборка из исходников

```bash
git clone https://github.com/erneywhite/erney-translate-tool.git
cd erney-translate-tool/ErneyTranslateTool
dotnet build
dotnet run
```

Требуется .NET 8 SDK ([download](https://dotnet.microsoft.com/download/dotnet/8.0)).

### Сборка установщика

```bash
# 1. Self-contained publish
dotnet publish -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false

# 2. Inno Setup compiler (https://jrsoftware.org/isdl.php)
iscc Installer/setup.iss
```

### Структура проекта

```
ErneyTranslateTool/
├── App.xaml(.cs)              — точка входа, DI-контейнер
├── MainWindow.xaml(.cs)       — главное окно, хоткеи, трей
├── Core/
│   ├── CaptureService          — захват окна через PrintWindow(RENDERFULLCONTENT)
│   ├── HotkeyService           — глобальные хоткеи Win32 RegisterHotKey
│   ├── OverlayManager          — управление click-through окном-оверлеем
│   ├── RegionGrouper           — склейка соседних строк OCR в абзацы
│   ├── ThemeManager            — переключение тем (Auto/Dark/Light/Nord), слежение за системной темой
│   ├── LanguageManager         — переключение языка UI (RU/EN) через ResourceDictionary swap
│   ├── TranslationEngine       — пайплайн capture → OCR → translate → overlay
│   ├── TranslationService      — оркестратор кэш + глоссарий + переводчик
│   ├── Glossary/
│   │   └── GlossaryApplier     — exact-match (top priority) + word-boundary post-process
│   ├── Profiles/
│   │   └── ProfileManager      — миграция Default, поиск по window/process, авто-создание, авто-сохранение
│   ├── Ocr/
│   │   ├── IOcrBackend          — абстракция OCR-движка
│   │   ├── PaddleOcrBackend     — нейросеть PaddleOCR
│   │   ├── TesseractOcrBackend  — Tesseract 5
│   │   ├── WindowsOcrBackend    — встроенный Windows.Media.OCR
│   │   └── TessdataManager      — скачивание/управление tessdata-файлами
│   ├── Translators/
│   │   ├── ITranslator            — абстракция переводчика
│   │   ├── DeepLTranslator        — DeepL API (нужен ключ)
│   │   ├── GoogleFreeTranslator   — публичный gtx endpoint
│   │   ├── MyMemoryTranslator     — MyMemory.translated.net API
│   │   ├── LibreTranslator        — LibreTranslate (любой инстанс)
│   │   ├── OpenAITranslator       — OpenAI Chat Completions + sliding context
│   │   ├── AnthropicTranslator    — Anthropic Messages API + sliding context
│   │   └── LlmLanguageNames       — DeepL-codes -> English names for LLM prompts
│   ├── Startup/
│   │   └── AutoStartManager     — HKCU\...\Run для автозапуска с Windows
│   ├── Tray/
│   │   ├── TrayIconManager      — иконка + контекстное меню + sticky-состояния
│   │   └── TrayIconRenderer     — рендер иконки с цветными точками-индикаторами
│   └── Updates/
│       ├── UpdateChecker        — пинг GitHub Releases API
│       └── UpdateDownloader     — стриминг установщика в %TEMP% + silent install
├── Data/
│   ├── AppSettings              — settings.json + DPAPI шифрование ключей
│   ├── CacheRepository          — SQLite-кэш с LRU-вытеснением
│   ├── HistoryRepository        — SQLite-история сессий
│   ├── GlossaryRepository       — SQLite-правила глоссария
│   └── GameProfileRepository    — SQLite-профили
├── Models/                      — POCO модели (с INPC где нужно для DataGrid)
├── ViewModels/                  — MVVM ViewModels (Main, Settings, History, Glossary, Profiles)
├── Views/
│   ├── OverlayWindow            — само click-through окно-оверлей
│   ├── Controls/                — MaskedSecretBox (поле API-ключа с маскированием)
│   ├── Dialogs/                 — UpdateAvailableDialog, WhatsNewDialog
│   └── Tabs/                    — 7 вкладок UI
├── Resources/
│   ├── Themes/                  — Dark, Light, Nord
│   ├── Styles.xaml              — общие стили WPF
│   ├── Strings.ru.xaml          — все строки UI на русском
│   └── Strings.en.xaml          — все строки UI на английском
├── Installer/setup.iss          — Inno Setup скрипт (per-user, no UAC)
└── tessdata/                    — bundled английский/русский/японский для Tesseract
```

---

## Совместимость

| Тип | Работает |
|---|---|
| Одиночные игры (оконный/borderless) | ✅ Полностью |
| Визуальные новеллы (Ren'Py, Tyrano, Kirikiri и т.д.) | ✅ Идеально |
| Chromium-браузеры (Chrome, Brave, Edge) | ✅ |
| Firefox/Waterfox | ✅ |
| UWP-приложения | ✅ |
| Полноэкранные игры | ⚠️ Нет (только windowed/borderless) |
| Онлайн-игры с античитом (Vanguard, EAC, BattlEye, Ricochet) | ⚠️ **Не рекомендуется** — может нарушать условия игры |

Программа использует только стандартный Windows API захвата окна (`PrintWindow` с флагом `RENDERFULLCONTENT`), не лезет в память игр и не внедряет код. Тем не менее в онлайн-играх любые внешние программы — на свой страх и риск.

---

## Как поддержать

Программа полностью бесплатная и open-source под MIT. Если она оказалась полезной и хочется отблагодарить автора кофе — [**☕ donate page**](https://dalink.to/toristarm).

---

## Лицензия

[MIT](LICENSE) © 2026 Erney White

---

## Используемые библиотеки

- [Sdcb.PaddleOCR](https://github.com/sdcb/PaddleSharp) — .NET-обёртка над PaddleOCR (Apache 2.0)
- [Tesseract](https://github.com/charlesw/tesseract) — .NET wrapper для Tesseract (Apache 2.0)
- [DeepL.NET](https://github.com/DeepLcom/deepl-dotnet) — DeepL SDK (MIT)
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) — иконка в трее (CPOL)
- [Serilog](https://serilog.net/) — логирование (Apache 2.0)
- [OpenCvSharp](https://github.com/shimat/opencvsharp) — обёртка OpenCV (Apache 2.0)
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) — SQLite (MIT)
