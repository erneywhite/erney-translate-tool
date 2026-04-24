# Erney's Translate Tool

**Перевод текста с экрана в реальном времени** — для игр, визуальных новелл и любого софта на иностранном языке. Программа находит текст в выбранном окне через OCR, переводит и показывает русский перевод полупрозрачной «табличкой» поверх оригинала. Кликам не мешает.

![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)
![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%2B-blue)
![.NET 8](https://img.shields.io/badge/.NET-8.0-purple)

---

## Возможности

- **3 OCR-движка на выбор** — PaddleOCR (нейросеть, лучшее качество), Tesseract (быстрее), Windows OCR (системный)
- **12 языков для распознавания** — английский, японский, китайский (упр./трад.), корейский, латиница (DE/FR/ES/IT/PT/PL/NL...), кириллица, арабский, хинди, телугу, тамильский, каннада + **режим «Авто»** который запускает несколько моделей параллельно
- **4 переводчика** — MyMemory (бесплатно, без регистрации), Google Translate, DeepL, LibreTranslate
- **Click-through оверлей** — переводы поверх игры не мешают кликать
- **8 готовых тем оверлея** + кастомные цвета/прозрачность/скругления
- **3 темы программы** — Dark / Light / Nord, переключаются на лету
- **Сворачивание в трей** — программа живёт фоном, окно не нужно держать открытым
- **Глобальные горячие клавиши** — `Ctrl+Shift+T` старт/стоп, `Ctrl+Shift+H` скрыть оверлей
- **Кэш переводов** + **история сессий** в SQLite
- **Авто-проверка обновлений** через GitHub Releases

---

## Скриншоты

> *(добавлю после первого релиза)*

---

## Системные требования

- Windows 10 (1809+) или Windows 11, x64
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) (или Windows Desktop Runtime)
- [Visual C++ Redistributable 2015–2022](https://aka.ms/vs/17/release/vc_redist.x64.exe) — нужен для PaddleOCR
- ~2 ГБ свободной RAM (для PaddleOCR с моделями)

---

## Быстрый старт

### Скачать готовую сборку

1. Идите в [Releases](https://github.com/erneywhite/erney-translate-tool/releases) и скачайте последнюю версию
2. Распакуйте/установите
3. Запустите `ErneyTranslateTool.exe`

### Использовать

1. Запустите игру в **оконном** или **borderless**-режиме (полный экран не подходит)
2. На вкладке **«Главная»**: «Обновить список» → выберите окно игры → «Запустить» (или горячая клавиша `Ctrl+Shift+T`)
3. Поверх английского/японского/любого иностранного текста появятся русские переводы
4. Игру можно тыкать как обычно — оверлей пропускает клики

### Горячие клавиши

| Действие | Комбинация |
|---|---|
| Включить/выключить перевод | `Ctrl+Shift+T` |
| Показать/скрыть оверлей | `Ctrl+Shift+H` |

---

## Сборка из исходников

```bash
git clone https://github.com/erneywhite/erney-translate-tool.git
cd erney-translate-tool/ErneyTranslateTool
dotnet build
dotnet run
```

Требуется .NET 8 SDK ([download](https://dotnet.microsoft.com/download/dotnet/8.0)).

### Структура проекта

```
ErneyTranslateTool/
├── App.xaml(.cs)              — точка входа, DI-контейнер
├── MainWindow.xaml(.cs)       — главное окно, хоткеи, трей
├── Core/
│   ├── CaptureService         — захват окна через PrintWindow(RENDERFULLCONTENT)
│   ├── HotkeyService          — глобальные хоткеи Win32 RegisterHotKey
│   ├── OverlayManager         — управление click-through окном-оверлеем
│   ├── RegionGrouper          — склейка соседних строк OCR в абзацы
│   ├── ThemeManager           — переключение тем приложения
│   ├── TranslationEngine      — пайплайн capture → OCR → translate → overlay
│   ├── Ocr/
│   │   ├── IOcrBackend         — абстракция OCR-движка
│   │   ├── PaddleOcrBackend    — нейросеть PaddleOCR (через Sdcb.PaddleOCR)
│   │   ├── TesseractOcrBackend — Tesseract 5 (через charlesw/tesseract)
│   │   ├── WindowsOcrBackend   — встроенный Windows.Media.OCR
│   │   └── TessdataManager     — скачивание/управление tessdata-файлами
│   ├── Translators/
│   │   ├── ITranslator           — абстракция переводчика
│   │   ├── DeepLTranslator       — DeepL API (нужен ключ)
│   │   ├── GoogleFreeTranslator  — публичный gtx endpoint Google
│   │   ├── MyMemoryTranslator    — MyMemory.translated.net API
│   │   └── LibreTranslator       — LibreTranslate (любой инстанс)
│   ├── Tray/                  — иконка в трее
│   └── Updates/               — проверка обновлений на GitHub
├── Data/
│   ├── AppSettings            — настройки в settings.json + DPAPI шифрование ключей
│   ├── CacheRepository        — SQLite-кэш переводов
│   └── HistoryRepository      — SQLite-история сессий
├── Models/                    — POCO модели
├── ViewModels/                — MVVM ViewModels
├── Views/
│   ├── OverlayWindow          — само click-through окно-оверлей
│   └── Tabs/                  — 5 вкладок UI
├── Resources/
│   ├── Themes/                — Dark, Light, Nord
│   ├── Styles.xaml            — общие стили WPF
│   └── Strings.ru.xaml        — все строки UI на русском
├── Installer/setup.iss        — Inno Setup скрипт
└── tessdata/                  — bundled английский/русский/японский для Tesseract
```

---

## Где хранятся данные

Программа портативная: всё рядом с `ErneyTranslateTool.exe` если папка доступна на запись, иначе в `%AppData%\ErneyTranslateTool\`.

```
settings.json    — настройки
cache.db         — кэш переводов
history.db       — история сессий
logs/            — журналы работы
tessdata/        — модели Tesseract
```

PaddleOCR кэширует свои модели в `%LocalAppData%\Sdcb.Paddle.OnlineModels\`.

---

## Совместимость с играми

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
