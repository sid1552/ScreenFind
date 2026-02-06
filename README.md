# ScreenFind — Ctrl+F for Your Entire Screen

A lightweight Windows tool that lets you search for any text visible on your screen using OCR.
Press a hotkey anywhere → type your search → see matches highlighted live on screen.

Uses the **built-in Windows 10/11 OCR engine** — no Tesseract, no cloud APIs, no dependencies.

<a href="https://buymeacoffee.com/siddharthsqn"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" height="50"></a>
<a href="https://github.com/sponsors/sid1552"><img src="https://img.shields.io/badge/Sponsor_on_GitHub-ea4aaa?logo=githubsponsors&logoColor=white&style=for-the-badge" alt="GitHub Sponsors" height="50"></a>

![ScreenFind Demo](demo.gif)

---

## Download

**[Download ScreenFind.exe (v1.1.0)](https://github.com/sid1552/ScreenFind/releases/latest)**

Standalone exe — just download and double-click. No installation or .NET runtime needed.

---

## How It Works

1. Press your hotkey (default **Ctrl + Shift + F**) from anywhere
2. Your screen freezes (screenshot taken) and dims
3. A Spotlight-style search bar appears
4. Start typing — exact matches highlighted in **yellow**, fuzzy matches in **blue**
5. Press **Enter** to jump to the next match, **Shift+Enter** for previous
6. Press **Ctrl+C** to copy the current match text
7. **Click** any match highlight to copy its text
8. **Drag** anywhere to select and copy text (like selecting in a PDF)
9. Press **Escape** to dismiss

---

## Features

- **Real-time search** — matches highlighted as you type
- **Fuzzy matching** — catches OCR misreads (e.g. "rn" → "m"), shown in blue
- **Click-to-copy** — click any match highlight to copy its text
- **Drag-to-select** — lasso any text on screen, auto-copied to clipboard (toggleable)
- **Customizable hotkey** — click the hotkey display in the main window to record a new one
- **Enhanced OCR mode** — optional image preprocessing for low-contrast text and desktop icons
- **System tray** — minimize to tray, runs silently in background
- **Multi-monitor support** — captures all monitors simultaneously, each gets its own overlay. Choose which monitors to include in Settings.
- **Settings & About tabs** — reorganized main window with a clean tabbed UI for all preferences
- **Settings persistence** — preferences saved to `%AppData%\ScreenFind\settings.json`

---

## Keyboard Shortcuts

| Key              | Action                    |
|------------------|---------------------------|
| Ctrl + Shift + F | Open screen search (default, customizable) |
| (type)           | Search in real time       |
| Enter            | Jump to next match        |
| Shift + Enter    | Jump to previous match    |
| F3 / Shift+F3    | Next / previous match     |
| Ctrl + C         | Copy current match text   |
| Click match      | Copy that match's text    |
| Drag             | Select and copy text      |
| Escape           | Close the overlay         |

---

## Changing the Hotkey

Click the hotkey display in the main window → press your desired key combo → done. The new hotkey is saved automatically.

Requires at least one modifier (Ctrl, Alt, Shift, or Win) plus a key.

---

## Building from Source

### 1. Install .NET 8 SDK

Download from: **https://dotnet.microsoft.com/download/dotnet/8.0**

Verify with:
```
dotnet --version
```

### 2. Clone & Run

```
git clone https://github.com/sid1552/ScreenFind.git
cd ScreenFind
dotnet run
```

### 3. Build Standalone Exe

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\ScreenFind.exe`

---

## Requirements

- **Windows 10** (build 19041 / version 2004) or later, or **Windows 11**
- An OCR language pack installed (English is included by default)

For building from source: **.NET 8 SDK**

---

## Troubleshooting

**"Could not register hotkey"**
→ Another app is using that shortcut. Click the hotkey display to choose a different one.

**OCR returns no results / "No OCR language pack"**
→ Go to **Windows Settings → Time & Language → Language** → Make sure you have a language installed with the **Basic typing** option.

**Highlights are offset / wrong position**
→ This can happen with unusual DPI setups. Multi-monitor is fully supported — each monitor gets its own overlay with independent DPI scaling. If a specific monitor causes issues, you can exclude it in Settings.

---

## Architecture

```
Hotkey pressed
    │
    ├── Each monitor captured (GDI+ BitBlt)
    │
    ├── One overlay window per monitor shown instantly
    │       ├── Frozen screenshot (dimmed)
    │       ├── Search bar (primary monitor only)
    │       ├── Selection canvas (drag-to-select)
    │       └── Search synced across all overlays
    │
    └── OCR runs async per monitor (~200-500ms)
            │
            └── Results ready → search + highlight as you type
```

- **Screen capture**: GDI+ `CopyFromScreen` — one capture per monitor
- **Multi-monitor**: Each screen gets its own overlay; the primary monitor hosts the search bar, and search results sync across all overlays
- **OCR**: `Windows.Media.Ocr.OcrEngine` — built into Windows, returns word-level bounding boxes
- **Image preprocessing**: Optional grayscale + contrast boost for difficult text
- **Overlay**: WPF borderless topmost window with canvas-drawn highlights
- **DPI**: Fully handled per monitor — works at 100%, 125%, 150%, 200% scaling
- **Settings**: JSON config in `%AppData%\ScreenFind\settings.json`, includes monitor exclusion

---

## License

[MIT License](LICENSE) — free to use, modify, and share.
