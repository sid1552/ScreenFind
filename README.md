# ScreenFind — Ctrl+F for Your Entire Screen

A lightweight Windows tool that lets you search for any text visible on your screen using OCR.  
Press **Ctrl+Shift+F** anywhere → type your search → see matches highlighted live on screen.

Uses the **built-in Windows 10/11 OCR engine** — no Tesseract, no cloud APIs, no dependencies.

---

## How It Works

1. Press **Ctrl + Shift + F** from anywhere
2. Your screen freezes (screenshot taken) and dims
3. A Spotlight-style search bar appears
4. Start typing — exact matches highlighted in **yellow**, fuzzy matches in **blue**
5. Press **Enter** to jump to the next match, **Shift+Enter** for previous
6. Press **Ctrl+C** to copy the current match text
7. Press **Escape** to dismiss

---

## Setup (Step by Step)

### 1. Install .NET 8 SDK

Download from: **https://dotnet.microsoft.com/download/dotnet/8.0**

- Click the **SDK** download for Windows x64 (the larger download, not "Runtime")
- Run the installer
- To verify, open **Command Prompt** or **PowerShell** and type:

```
dotnet --version
```

You should see something like `8.0.xxx`.

### 2. Download & Extract ScreenFind

Put the `ScreenFind` folder anywhere you like, for example:

```
C:\Users\YourName\ScreenFind\
```

The folder should contain these files:

```
ScreenFind/
├── ScreenFind.csproj
├── app.manifest
├── App.xaml
├── App.xaml.cs
├── MainWindow.xaml
├── MainWindow.xaml.cs
├── OverlayWindow.xaml
├── OverlayWindow.xaml.cs
├── Models.cs
├── ImagePreprocessor.cs
└── Settings.cs
```

### 3. Run It

Open **Command Prompt** or **PowerShell**, navigate to the folder, and run:

```
cd C:\Users\YourName\ScreenFind
dotnet run
```

The first run takes ~30 seconds (compiling). After that it starts instantly.

A small dark window will appear showing the hotkey. You can **minimize to tray** — it keeps running in the background.

### 4. (Optional) Build a Standalone .exe

If you want a double-clickable exe:

```
dotnet publish -c Release -r win-x64 --self-contained false
```

The exe will be in `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\ScreenFind.exe`.

For a fully self-contained exe (no .NET install needed on target machine):

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

---

## Keyboard Shortcuts

| Key              | Action                    |
|------------------|---------------------------|
| Ctrl + Shift + F | Open screen search        |
| (type)           | Search in real time       |
| Enter            | Jump to next match        |
| Shift + Enter    | Jump to previous match    |
| F3 / Shift+F3    | Next / previous match     |
| Ctrl + C         | Copy current match text   |
| Escape           | Close the overlay         |

---

## Changing the Hotkey

If **Ctrl+Shift+F** conflicts with another app, edit `MainWindow.xaml.cs`:

Find this section near the top:

```csharp
private const uint MOD_CTRL  = 0x0002;
private const uint MOD_SHIFT = 0x0004;
private const uint VK_F = 0x46;
```

Common alternatives:

| Hotkey           | Change to                                        |
|------------------|--------------------------------------------------|
| Ctrl + Alt + F   | `MOD_CTRL = 0x0002; MOD_SHIFT → MOD_ALT = 0x0001` |
| Ctrl + Shift + S | `VK_F → VK_S = 0x53`                              |
| Ctrl + Space     | Remove MOD_SHIFT, `VK_F → VK_SPACE = 0x20`        |
| Win + Shift + F  | Add `MOD_WIN = 0x0008`                             |

Full list of virtual key codes: https://learn.microsoft.com/en-us/windows/win32/inputdev/virtual-key-codes

---

## Requirements

- **Windows 10** (build 19041 / version 2004) or later, or **Windows 11**
- **.NET 8 SDK** (for building) or **.NET 8 Runtime** (for running published exe)
- An OCR language pack installed (English is included by default on Windows 10/11)

---

## Troubleshooting

**"Could not register hotkey"**  
→ Another app is already using Ctrl+Shift+F. Close that app or change the hotkey (see above).

**OCR returns no results / "No OCR language pack"**  
→ Go to **Windows Settings → Time & Language → Language** → Make sure you have a language installed with the **Basic typing** option. English should work by default.

**Highlights are offset / wrong position**  
→ This can happen with unusual DPI scaling setups or multi-monitor configurations. The app currently supports the primary monitor at any DPI scale.

**Build error about target framework**  
→ Make sure you installed the **.NET 8 SDK** (not just the runtime). Run `dotnet --list-sdks` to verify.

---

## Architecture

```
Hotkey pressed
    │
    ├── Screen captured (GDI+ BitBlt)
    │
    ├── Overlay window shown instantly
    │       ├── Frozen screenshot (dimmed)
    │       └── Search bar (ready for input)
    │
    └── OCR runs async (~200-500ms)
            │
            └── Results ready → search + highlight as you type
```

- **Screen capture**: GDI+ `CopyFromScreen` — fast and reliable
- **OCR**: `Windows.Media.Ocr.OcrEngine` — built into Windows, returns word-level bounding boxes
- **Image preprocessing**: Optional grayscale + contrast boost for difficult text (via `ImagePreprocessor`)
- **Overlay**: WPF borderless topmost window with canvas-drawn highlights
- **DPI**: Fully handled — works at 100%, 125%, 150%, 200% scaling
- **Settings**: JSON config in `%AppData%\ScreenFind\settings.json`

---

## Features

- **Real-time search** — matches highlighted as you type
- **Fuzzy matching** — catches OCR misreads (e.g. "rn" → "m"), shown in blue
- **System tray** — minimize to tray, runs silently in background
- **Enhanced OCR mode** — optional image preprocessing for better detection of desktop icons and low-contrast text
- **Copy match text** — Ctrl+C copies the currently selected match
- **Settings persistence** — preferences saved to AppData

## Future Ideas

- [ ] Multi-monitor support (capture all screens)
- [ ] Click on a match to jump to / interact with that location
- [ ] Configurable hotkey via settings file
- [ ] Auto-start with Windows

---

## License

Free to use, modify, and share. No attribution required.
