# bitiq

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-0284C7?style=flat-square)](#system-requirements)
[![Runtime](https://img.shields.io/badge/.NET-8.0%20Self--Contained-512BD4?style=flat-square)](#building-from-source)
[![Translation](https://img.shields.io/badge/Translation-TranslateGemma%204B%20%7C%20Google-10B981?style=flat-square)](#features)
[![Release](https://img.shields.io/github/v/release/eraykrkpnr/bitiq?style=flat-square&label=Release)](https://github.com/eraykrkpnr/bitiq/releases)

**bitiq** is a zero-install, real-time screen and window subtitle capture, OCR, and translation desktop app for Windows 10/11 (x64). Built for PC games, console capture card preview windows (PS5, etc.), and videos, it tracks your selected region or locked window and renders live translations with ultra-low latency both as a click-through desktop overlay and inside **OBS Studio**.

---

## Features

- **Window-Locked Capture & Region Selection**: Pick any target window (game, browser, media player, or capture card preview window) and mark the subtitle area inside it. Even when other windows or the translated overlay appear on top, bitiq continues capturing only the locked target window.
- **Dual Translation Engine**:
  - **Local AI (TranslateGemma 4B)**: Runs offline directly on your GPU (`CUDA 13.5` / `CUDA 12.4`, with `Vulkan` and `CPU` fallbacks) for natural, context-aware translations.
  - **Google Translate**: Lightweight, instant translation with zero GPU/VRAM usage and zero extra disk space. Switching to Google mode completely offloads `llama-server` from both RAM and GPU VRAM.
- **Multi-Language OCR & Translation (12 Languages) + Bilingual UI (`TR` / `EN`)**: Translate from any supported on-screen language (`English, Turkish, German, French, Spanish, Italian, Portuguese, Russian, Japanese, Korean, Chinese, Arabic`) into your chosen target language, install missing Windows OCR packs directly in-app with one click, and switch the interface between Turkish (`TR`) and English (`EN`).
- **Smart OCR & Subtitle Stabilization**: Combines in-process `Windows.Media.Ocr` with 2x Catmull-Rom bicubic upscaling and adaptive 7x7 sliding-median background suppression for borderless or outlined white subtitles, frame-change gating, word-subset stability filtering, incomplete-sentence merging, and optional typewriter streaming.
- **Flexible Desktop Overlay & OBS Integration**:
  - Click-through transparent desktop subtitle overlay that can be toggled on/off with one click (for OBS-only streaming setups).
  - Built-in local `127.0.0.1` HTTP + SSE server requiring no administrator privileges for **OBS Browser Source**, plus a fallback **Chroma Key** window.
  - Full styling controls (font family, size, outline, shadow, position presets, overflow modes, and JSON style profiles).
- **Zero-Overhead Performance Monitor**: Inspect live CPU and RAM usage for both `bitiq` and `llama-server`, alongside in-process NVIDIA GPU and VRAM telemetry via `nvml.dll` without spawning external helper processes.

---

## Download & Installation

No .NET SDK or Python installation is required to run **bitiq**. Download the portable single-file build from the **Releases** tab:

1. Open the **[Releases](https://github.com/eraykrkpnr/bitiq/releases)** page and download **`bitiq-win-x64.zip`** from the latest release.
2. Extract the ZIP archive into a permanent folder (for example, `C:\Apps\bitiq` or `D:\Apps\bitiq`).
3. Double-click **`bitiq.exe`** to launch the application.
   - **Windows SmartScreen Notice**: Because the binary is not code-signed with a commercial certificate, Windows may display *"Windows protected your PC"*. Click **"More info"** → **"Run anyway"** to start the app.
4. **First-Run Setup Wizard**:
   - Choose **Google Translate** to start immediately without downloading extra files.
   - Choose **Local (TranslateGemma 4B)** to automatically download the matching `llama.cpp` (`b12378`) runtime and your chosen GGUF quantization (`Q8_0` ~3.85 GB or `Q4_K_M` ~2.32 GB) into `%LOCALAPPDATA%\bitiq` with HTTP resume support and SHA-256 verification.
5. **Start Translating**:
   - In the main screen, optionally choose a target window from the **Capture Source** selector (or leave full-screen mode selected).
   - Click **"Çevrilecek Alanı Seç"** (*Select Capture Region*) and drag over the subtitle area.
   - Click **"Başlat"** (*Start*).

---

## System Requirements

| Component | Minimum (Google Translate / CPU) | Recommended (Local GPU — TranslateGemma 4B) |
|---|---|---|
| **Operating System** | Windows 10 (build 19041+) or Windows 11 **(64-bit / x64)** | Windows 11 **(64-bit / x64)** |
| **Windows OCR Pack** | Built-in Windows OCR capability *(12 languages supported)* | Built-in Windows OCR capability *(in-app 1-click install)* |
| **Processor (CPU)** | 4-core modern x64 CPU | 6+ core modern x64 CPU |
| **Memory (RAM)** | 4 GB RAM *(Google mode uses < 200 MB)* | 16 GB RAM |
| **Graphics (GPU)** | None required *(Google mode)* / Vulkan-capable GPU | **NVIDIA RTX GPU** (**4+ GB VRAM** for `Q4_K_M`, **6+ GB VRAM** for `Q8_0`) |
| **NVIDIA Driver** | — | **551.61+** for CUDA 12.4, or **CUDA 13.5+** compatible driver |
| **Storage** | ~200 MB free space | **5 – 7 GB** free SSD space *(for GGUF model & `llama.cpp` binaries)* |
| **Internet Access** | Required for Google Translate mode | Required only for the initial model download (offline afterward) |

### Installing Windows OCR Language Packs

`bitiq` uses the built-in `Windows.Media.Ocr` API and supports 12 source languages (`en`, `tr`, `de`, `fr`, `es`, `it`, `pt`, `ru`, `ja`, `ko`, `zh`, `ar`). Any missing OCR pack can be downloaded and enabled with one click directly from the source-language dropdown inside `bitiq` (or installed manually):

- **Windows Settings**: *Settings → Time & language → Language & region → Add a language* and verify **Optical character recognition (OCR)** is installed.
- **PowerShell (Run as Administrator)** — e.g. for `en-US`:
  ```powershell
  Add-WindowsCapability -Online -Name Language.OCR~~~en-US~0.0.1.0
  ```

---

## OBS Studio Integration

### Method 1: OBS Browser Source (Recommended — Transparent Background)

`bitiq` hosts a lightweight local loopback HTTP + SSE server (`http://127.0.0.1:19555/`, automatically falling back to an available port if needed) without requiring admin privileges.

1. In OBS Studio, go to **Sources** → **`+`** → **Browser**.
2. Paste the URL displayed in the **OBS** tab of `bitiq`.
3. Set **Width** and **Height** to match your OBS canvas resolution (e.g. `1920 × 1080`). Layout and font sizes scale responsively (`vh`/`vw`) and reconnect automatically if interrupted.
4. If you only want subtitles visible on your stream without appearing on your gaming monitor, toggle off the desktop overlay button in the main window.

### Method 2: Chroma Key Window (Fallback)

1. In the **OBS** tab, enable the solid-color **Chroma Key** window (default `#00FF00`).
2. Add a **Window Capture** source in OBS and apply **Filters → Chroma Key**.

> **Capture Card Latency Tip:** If your capture card preview window has hardware delay compared to live OCR subtitles, right-click your video source in OBS → **Filters → Render Delay** to align them in milliseconds.

---

## Building from Source

Requires the **.NET 8 SDK** on Windows (x64):

```powershell
git clone https://github.com/eraykrkpnr/bitiq.git
cd bitiq

# Build in Release configuration
dotnet build bitiq.slnx -c Release

# Run unit tests
dotnet test bitiq.slnx -c Release

# Publish a self-contained single-file win-x64 executable
dotnet publish src/bitiq.App/bitiq.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/bitiq
```

---

## Open Models & Third-Party Tools

- **Inference Engine**: [`ggml-org/llama.cpp`](https://github.com/ggml-org/llama.cpp) (`b12378` — CUDA 13.5, CUDA 12.4, Vulkan, CPU)
- **Local Translation Model**: [`mradermacher/translategemma-4b-it-GGUF`](https://huggingface.co/mradermacher/translategemma-4b-it-GGUF) (`Q8_0` / `Q4_K_M`, subject to Google Gemma Terms of Use; downloaded at runtime only when Local mode is selected)
