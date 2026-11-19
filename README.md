# bitiq

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-0284C7?style=flat-square)](#system-requirements)
[![Runtime](https://img.shields.io/badge/.NET-8.0%20Self--Contained-512BD4?style=flat-square)](#building-from-source)
[![Translation](https://img.shields.io/badge/Translation-Hy--MT2%207B%20%7C%20TranslateGemma%20%7C%20Google-10B981?style=flat-square)](#features)
[![Release](https://img.shields.io/github/v/release/eraykrkpnr/bitiq?style=flat-square&label=Release)](https://github.com/eraykrkpnr/bitiq/releases)

**bitiq** is a portable, zero-install, real-time screen and window subtitle capture, OCR, and translation desktop app for Windows 10/11 (x64). Built for PC games, console capture card preview windows (PS5, etc.), and videos, it tracks your selected region or locked window and renders live, context-aware translations with ultra-low latency both as a click-through desktop overlay and inside **OBS Studio**.

---

## Features

- **Window-Locked Capture & Region Selection**: Pick any target window (game, browser, media player, or capture card preview window) and mark the subtitle area inside it. Even when other windows or the translated overlay appear on top, bitiq continues capturing only the locked target window.
- **Context-Aware Local AI + Fast Modes**:
  - **Local AI — Hy-MT2 7B (`Q6_K`, Recommended)**: Context-aware offline translation (`tencent/Hy-MT2-7B-GGUF`) running on your GPU (`CUDA 13.5` / `CUDA 12.4`, with `Vulkan` and `CPU` fallbacks). Uses your active **Game Profile** and **sliding dialogue memory** (4–12 prior lines with hysteresis trimming and automatic scene-idle reset) ordered for static-prefix KV caching, plus automatic hallucination/copy fallback protection.
  - **Local AI — TranslateGemma 4B (`Q8_0` / `Q4_K_M`)**: Lightweight, context-free mode for fast sentence-by-sentence translation on 4–6 GB VRAM GPUs.
  - **Google Translate**: Lightweight, instant translation with zero GPU/VRAM usage and zero extra disk space. Switching to Google mode completely offloads `llama-server` from both RAM and GPU VRAM.
- **Game Profiles & Terminology Glossary**: Create, edit, export, and import per-game JSON profiles (`profiles/*.json`) containing game synopsis, tone/register instructions, character names/roles, and `Source → Target` glossary rules—with a live `[Background Information]` prompt preview.
- **Stream-Safe Dynamic VRAM Budgeting**: Checks live GPU headroom via NVML before launch, reserves 400 MB for OBS/DWM/NVENC, and dynamically fits transformer layers on GPU or cleanly splits overflow layers to system RAM—preventing WDDM shared-memory spillover and OBS rendering lag. Includes `q8_0` KV-cache quantization, `BelowNormal` server CPU priority, a **Simulate VRAM Limit** (`6–16 GB`) test slider, and an in-app OBS streaming checklist.
- **Dual OCR Engine (`OneOCR` Bundled + `Windows.Media.Ocr`)**:
  - Bundled **OneOCR** ONNX runtime (`oneocr\`) for high-accuracy multi-language recognition alongside built-in `Windows.Media.Ocr`.
  - Smart spatial block grouping, typewriter text growth detection, 2x Catmull-Rom bicubic upscaling with 7x7 sliding-median background suppression for borderless white subtitles, background uppercase noise filtering (e.g. stray UI/signboard artifacts), incomplete-sentence merging, and optional typewriter streaming.
- **Multi-Language OCR & Translation (12 Languages) + Bilingual UI (`TR` / `EN`)**: Translate across `English, Turkish, German, French, Spanish, Italian, Portuguese, Russian, Japanese, Korean, Chinese, Arabic`, install missing Windows OCR packs in-app with one click, and switch the UI between Turkish (`TR`) and English (`EN`).
- **Flexible Desktop Overlay & OBS Integration**:
  - Click-through transparent desktop subtitle overlay that can be toggled on/off with one click (for OBS-only streaming setups).
  - Built-in local `127.0.0.1` HTTP + SSE server requiring no administrator privileges for **OBS Browser Source**, plus a fallback **Chroma Key** window.
  - Full styling controls (font family, size, outline, shadow, position presets, overflow modes, and JSON style profiles).
- **Portable App-Directory Storage & Telemetry**: Stores `settings.json`, `profiles/`, `models/`, `llama/`, and `oneocr/` directly inside the application folder. Inspect live CPU, RAM, NVIDIA GPU/VRAM headroom, per-stage latencies, KV cache hit ratio (`KV Hit`), and generation speed (`tok/s`) without spawning external helper processes.

---

## Download & Installation

No .NET SDK or Python installation is required to run **bitiq**. Download the portable build from the **Releases** tab:

1. Open the **[Releases](https://github.com/eraykrkpnr/bitiq/releases)** page and download **`bitiq-win-x64.zip`** from the latest release.
2. Extract the entire archive—keeping `bitiq.exe` and the `oneocr\` folder together—into a permanent, writable folder (for example, `C:\Apps\bitiq` or `D:\Apps\bitiq`).
3. Double-click **`bitiq.exe`** to launch the application.
   - **Windows SmartScreen Notice**: Because the binary is not code-signed with a commercial certificate, Windows may display *"Windows protected your PC"*. Click **"More info"** → **"Run anyway"** to start the app.
4. **First-Run Setup Wizard**:
   - Choose **Google Translate** to start immediately without downloading extra files.
   - Choose **Local AI** (`Hy-MT2 7B Q6_K` ~5.74 GiB / 6.16 GB, or `TranslateGemma 4B` `Q8_0` ~3.85 GiB / `Q4_K_M` ~2.32 GiB) to automatically download the matching `llama.cpp` (`b12378`) runtime and GGUF model into `llama\` and `models\` inside your bitiq folder with HTTP resume support and SHA-256 verification.
5. **Start Translating**:
   - Optionally open the **Game Profile** tab to set up game title, character roles, tone notes, and glossary terms.
   - In the main screen, optionally choose a target window from the **Capture Source** selector (or leave full-screen mode selected).
   - Click **"Çevrilecek Alanı Seç"** (*Select Capture Region*) and drag over the subtitle area.
   - Click **"Başlat"** (*Start*).

---

## System Requirements

| Component | Minimum (Google Translate / CPU) | Recommended (Local GPU — Hy-MT2 7B / TranslateGemma 4B) |
|---|---|---|
| **Operating System** | Windows 10 (build 19041+) or Windows 11 **(64-bit / x64)** | Windows 11 **(64-bit / x64)** |
| **OCR Engine** | Bundled `OneOCR` ONNX runtime + `Windows.Media.Ocr` *(12 languages)* | Bundled `OneOCR` + `Windows.Media.Ocr` *(in-app 1-click install)* |
| **Processor (CPU)** | 4-core modern x64 CPU | 6+ core modern x64 CPU |
| **Memory (RAM)** | 4 GB RAM *(Google mode uses < 200 MB)* | 16–32 GB RAM *(32 GB ideal when streaming with OBS)* |
| **Graphics (GPU)** | None required *(Google mode)* / Vulkan-capable GPU | **NVIDIA RTX GPU** (**8–10+ GB VRAM** for full-GPU `Hy-MT2 7B Q6_K`; **4–6 GB VRAM** for `TranslateGemma 4B` or auto-offload) |
| **NVIDIA Driver** | — | **551.61+** for CUDA 12.4, or **CUDA 13.5+** compatible driver |
| **Storage** | ~200 MB free space | **5 – 8 GB** free SSD space *(for GGUF model & `llama.cpp` binaries)* |
| **Internet Access** | Required for Google Translate mode | Required only for the initial model download (offline afterward) |

### Installing Windows OCR Language Packs

`bitiq` bundles **OneOCR** (inside `oneocr\`) and also supports built-in `Windows.Media.Ocr` across 12 source languages (`en`, `tr`, `de`, `fr`, `es`, `it`, `pt`, `ru`, `ja`, `ko`, `zh`, `ar`). Any missing Windows OCR pack can be downloaded and enabled with one click directly from the source-language dropdown inside `bitiq` (or installed manually):

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

> **Drop-Free Streaming Tip:** When running `Hy-MT2 7B` alongside 1080p60 OBS streaming on a 10 GB GPU, run OBS as Administrator, keep NVENC *Look-ahead* and *Psycho Visual Tuning* disabled, set `CUDA - Sysmem Fallback Policy` to *Prefer No Sysmem Fallback* for `llama-server.exe`, and click **Start** in bitiq before going live.
>
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

# Publish a self-contained single-file win-x64 executable (includes oneocr\ runtime)
dotnet publish src/bitiq.App/bitiq.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/bitiq
```

---

## Open Models & Third-Party Tools

- **Inference Engine**: [`ggml-org/llama.cpp`](https://github.com/ggml-org/llama.cpp) (`b12378` — CUDA 13.5, CUDA 12.4, Vulkan, CPU)
- **Context-Aware Local Model**: [`tencent/Hy-MT2-7B-GGUF`](https://huggingface.co/tencent/Hy-MT2-7B-GGUF) (`HY-MT2-7B-Q6_K.gguf`, Apache-2.0)
- **Fast Local Model**: [`mradermacher/translategemma-4b-it-GGUF`](https://huggingface.co/mradermacher/translategemma-4b-it-GGUF) (`Q8_0` / `Q4_K_M`, subject to Google Gemma Terms of Use)
- **Bundled OCR Runtime**: Windows 11 `OneOCR` ONNX runtime (`oneocr.dll`, `oneocr.onemodel`, `onnxruntime.dll`)
