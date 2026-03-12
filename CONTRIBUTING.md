# Contributing to ScreenFind

## Building from Source

### Prerequisites

- **.NET 8 SDK** — [download here](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Windows 10** (build 19041+) or **Windows 11**

Verify your setup:
```
dotnet --version
```

### Clone & Run

```
git clone https://github.com/sid1552/ScreenFind.git
cd ScreenFind
dotnet run
```

### Build Standalone Exe

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\ScreenFind.exe`

> **Note:** The `-p:IncludeNativeLibrariesForSelfExtract=true` flag is required — WPF native DLLs can't be bundled in a single file without it.

---

## Architecture

```
App startup
    └── Pre-warm overlay windows (HWND + layout cached, invisible)

Hotkey pressed
    │
    ├── Each monitor captured (GDI+ BitBlt, background thread)
    │
    ├── Pre-warmed overlays activated instantly (~100-200ms total)
    │       ├── Frozen screenshot (dimmed)
    │       ├── Search bar (primary monitor only)
    │       ├── Selection canvas (drag-to-select)
    │       └── Search synced across all overlays
    │
    └── OCR runs async per monitor (~200-500ms)
            │
            └── Results ready → search + highlight as you type
```

### Key Components

- **Screen capture**: GDI+ `CopyFromScreen` — one capture per monitor, runs on a background thread
- **Pre-warmed overlays**: WPF windows are created at startup so the hotkey response is near-instant (~100-200ms vs ~1s without pre-warming)
- **Multi-monitor**: Each screen gets its own overlay; the primary monitor hosts the search bar, and search results sync across all overlays
- **OCR engine**: `Windows.Media.Ocr.OcrEngine` — built into Windows, returns word-level bounding boxes
- **Overlay**: WPF borderless topmost window with canvas-drawn highlights
- **Settings**: JSON config in `%AppData%\ScreenFind\settings.json`

---

## File Structure

```
ScreenFind/
├── ScreenFind.csproj         — Project config (WPF + WinForms + WinRT target)
├── app.manifest              — DPI awareness (PerMonitorV2)
├── App.xaml / App.xaml.cs     — WPF Application entry point
├── MainWindow.xaml/.cs        — Settings window + global hotkey registration
├── OverlayWindow.xaml/.cs     — Fullscreen search overlay (main logic)
├── Models.cs                  — OcrWordInfo, OcrLineInfo, MatchResult data classes
├── Settings.cs                — Settings model + JSON persistence
├── FuzzyMatcher.cs            — Levenshtein-based fuzzy matching for OCR misreads
├── ImagePreprocessor.cs       — Grayscale + contrast boost for enhanced OCR mode
└── Themes/
    ├── DarkTheme.xaml             — Catppuccin Mocha
    ├── LightTheme.xaml            — Catppuccin Latte
    ├── DarkHighContrastTheme.xaml  — High contrast dark
    └── LightHighContrastTheme.xaml — High contrast light
```

---

## How It Works Under the Hood

### OCR Pipeline

1. Screenshot saved to temp BMP file
2. Loaded via WinRT `StorageFile` → `BitmapDecoder` → `SoftwareBitmap`
3. Passed to `OcrEngine.RecognizeAsync`
4. Results converted to `List<OcrLineInfo>`, each containing `List<OcrWordInfo>` with bounding boxes
5. OCR runs async so the overlay appears instantly while processing continues (~200-500ms)

### Search Algorithm

- For each OCR line, a character-offset-to-word-index map is built
- The full line text is searched with `String.IndexOf` (case-insensitive)
- Character ranges are mapped back to word bounding boxes
- Adjacent matched word boxes are combined for multi-word queries
- Fuzzy matching uses Levenshtein distance to catch OCR misreads

### DPI Handling

- App is DPI-aware via manifest (`PerMonitorV2`)
- Screen capture is in physical pixels; WPF renders in DIPs (device-independent pixels)
- All bounding box coordinates are divided by `_scaleX` / `_scaleY` when drawing on the canvas
- Each monitor's DPI is handled independently

### Theming

- Four themes defined as XAML resource dictionaries in `Themes/`
- Switched at runtime by swapping the merged resource dictionary
- All UI elements use `DynamicResource` so they update live on theme change
- Based on the [Catppuccin](https://catppuccin.com/) color palette
