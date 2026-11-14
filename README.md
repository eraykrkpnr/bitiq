# bitiq

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-0284C7?style=flat-square)](#sistem-gereksinimleri)
[![Runtime](https://img.shields.io/badge/.NET-8.0%20Self--Contained-512BD4?style=flat-square)](#kaynak-koddan-derleme)
[![Translation](https://img.shields.io/badge/%C3%87eviri-TranslateGemma%204B%20%7C%20Google-10B981?style=flat-square)](#%C3%B6zellikler)
[![Release](https://img.shields.io/github/v/release/eraykrkpnr/bitiq?style=flat-square&label=Release)](https://github.com/eraykrkpnr/bitiq/releases)

**bitiq**, oyun içi sahneler, capture card önizleme pencereleri (PS5 / konsol vb.) veya videolar için geliştirilmiş, kurulum gerektirmeyen gerçek zamanlı **İngilizce → Türkçe** ekran altyazısı yakalama, OCR ve çeviri uygulamasıdır. Ekranın seçtiğiniz bölgesini veya kilitlediğiniz bir pencereyi izler, Türkçe çeviriyi düşük gecikmeyle hem ekranınızda hem de **OBS Studio** üzerinde canlı olarak gösterir.

---

## Özellikler

- **Pencereye Kilitli Yakalama & Ekran Bölgesi Seçimi**: Hedef pencereyi (oyun, tarayıcı, medya oynatıcı veya capture card önizleme penceresi) seçip içinden altyazı alanını işaretleyebilirsiniz. Yakalanan pencerenin üzerine başka bir pencere veya altyazı katmanı gelse bile bitiq yalnızca kilitli pencereyi yakalamaya devam eder.
- **Çift Çeviri Motoru**:
  - **Yerel Yapay Zeka (TranslateGemma 4B)**: İnternet gerektirmeden, doğrudan ekran kartınızda (`CUDA 13.5` / `CUDA 12.4`, yedek olarak `Vulkan` veya `CPU`) doğal ve bağlamsal çeviri yapar.
  - **Google Translate**: Sıfır VRAM/RAM tüketimi ve sıfır disk alanı ile anında hafif çeviri sunar. Google moduna geçildiğinde `llama-server` bellekten (RAM + GPU VRAM) tamamen boşaltılır.
- **Akıllı OCR & Altyazı Kararlılığı**: `Windows.Media.Ocr` motorunu akıllı arka plan temizleme, metin kararlılık filtresi, bitmemiş cümle birleştirme ve isteğe bağlı daktilo (typewriter) efekti ile birleştirir.
- **Esnek Ekran Katmanı & OBS Desteği**:
  - Tıklamaları arkadaki oyuna geçiren şeffaf ekran altyazı katmanı tek tıkla açılıp kapatılabilir (dilerseniz yalnızca OBS çıktısıyla kullanabilirsiniz).
  - Yerleşik yerel `127.0.0.1` HTTP + SSE sunucusu ile yönetici izni gerektirmeden **OBS Tarayıcı Kaynağı (Browser Source)** ve yedek **Chroma Key** penceresi sunar.
  - Yazı tipi, boyut, kenarlık, gölge, konum önayarları, taşma davranışı ve JSON stil profilleri desteklenir.
- **Sıfır Yük Performans Monitörü**: **Performans** sekmesi üzerinden `bitiq` ve `llama-server` süreçlerinin CPU, RAM ile (`nvml.dll` üzerinden ek alt süreç başlatmadan) NVIDIA GPU ve VRAM kullanımlarını anlık izleyebilirsiniz.

---

## İndirme ve Kurulum

Uygulamayı çalıştırmak için .NET veya Python kurmanız gerekmez. Hazır tek dosya sürümünü **Releases** sekmesinden indirebilirsiniz:

1. Sağdaki veya yukarıdaki **[Releases](https://github.com/eraykrkpnr/bitiq/releases)** sekmesine gidin ve en güncel sürümden **`bitiq-win-x64.zip`** dosyasını indirin.
2. İndirdiğiniz ZIP arşivini bilgisayarınızda kalıcı bir klasöre çıkarın (ör. `C:\Program Files\bitiq` veya `D:\Apps\bitiq`).
3. **`bitiq.exe`** dosyasına çift tıklayarak uygulamayı çalıştırın.
   - **Windows SmartScreen Uyarısı**: Uygulama bireysel geliştirici imzası taşımadığı için Windows *"Windows bilgisayarınızı korudu"* uyarısı gösterebilir. **"Ek bilgi"** *(More info)* → **"Yine de çalıştır"** *(Run anyway)* adımlarını izleyerek başlatabilirsiniz.
4. **İlk Kurulum Sihirbazı**:
   - **Google Translate** seçerseniz ek dosya indirmeden hemen kullanıma başlarsınız.
   - **Yerel (TranslateGemma 4B)** seçerseniz uygun `llama.cpp` (`b12378`) paketi ve seçtiğiniz GGUF modeli (`Q8_0` ~3.85 GB veya `Q4_K_M` ~2.32 GB), `%LOCALAPPDATA%\bitiq` klasörüne kaldığı yerden devam etme ve SHA-256 doğrulaması ile otomatik indirilir.
5. **Kullanıma Başlayın**:
   - **Genel** sekmesinde isterseniz **"Yakalama Kaynağı"** listesinden hedef pencereyi seçin (veya doğrudan ekran seçimi için tüm ekran modunda bırakın).
   - **"🎯 Çevrilecek Alanı Seç"** düğmesiyle altyazıların çıktığı bölgeyi sürükleyip seçin.
   - **"▶ Başlat"** düğmesine basın.

---

## Sistem Gereksinimleri

| Bileşen | Minimum (Google Translate / CPU) | Önerilen (Yerel GPU — TranslateGemma 4B) |
|---|---|---|
| **İşletim Sistemi** | Windows 10 (19041+) veya Windows 11 **(64-bit / x64)** | Windows 11 **(64-bit / x64)** |
| **İngilizce OCR Paketi** | Windows `en-US` OCR bileşeni *(Zorunlu)* | Windows `en-US` OCR bileşeni *(Zorunlu)* |
| **İşlemci (CPU)** | 4 çekirdekli modern x64 işlemci | 6+ çekirdekli modern x64 işlemci |
| **Bellek (RAM)** | 4 GB RAM *(Google modu için < 200 MB kullanım)* | 16 GB RAM |
| **Ekran Kartı (GPU)** | Gerekmez *(Google modu)* / Vulkan destekli GPU | **NVIDIA RTX GPU** (Q4_K_M için **4+ GB VRAM**, Q8_0 için **6+ GB VRAM**) |
| **NVIDIA Sürücüsü** | — | CUDA 12.4 için **551.61+**, CUDA 13.5 için güncel **CUDA 13.5+** sürücüsü |
| **Depolama** | ~200 MB boş alan | **5 – 7 GB** boş SSD alanı *(model ve llama.cpp dosyaları için)* |
| **İnternet Bağlantısı** | Google Translate modu için gerekli | Yalnızca ilk model indirmede gerekli (sonrasında tamamen çevrimdışı) |

### Windows İngilizce (`en-US`) OCR Bileşeni Kurulumu

`bitiq`, yerleşik `Windows.Media.Ocr` API'sini kullanır. Sisteminizde İngilizce OCR paketi yüklü değilse uygulama açılışta uyarı gösterir. Yüklemek için:

- **Ayarlar Yolu**: *Ayarlar → Zaman ve Dil → Dil ve Bölge → Dil ekle → English (United States)* → **"Temel yazma"** ve **"Optik karakter tanıma (OCR)"** bileşenlerinin yüklü olduğundan emin olun.
- **PowerShell Yolu (Yönetici olarak)**:
  ```powershell
  Add-WindowsCapability -Online -Name Language.OCR~~~en-US~0.0.1.0
  ```

---

## OBS Studio Entegrasyonu

### Yöntem 1: OBS Tarayıcı Kaynağı (Önerilen — Şeffaf Arka Plan)

`bitiq`, yalnızca yerel makinede (`http://127.0.0.1:19555/`, port doluysa otomatik boş port) yönetici izni gerektirmeyen hafif bir HTTP + SSE sunucusu çalıştırır.

1. OBS Studio'da **Kaynaklar (Sources)** altından **`+`** → **Tarayıcı (Browser)** ekleyin.
2. `bitiq` içindeki **OBS** sekmesindeki URL adresini yapıştırın.
3. **Genişlik** ve **Yükseklik** değerlerini OBS tuval çözünürlüğünüzle eşitleyin (ör. `1920 × 1080`).
4. Ekranda çift altyazı görmek istemiyorsanız ana ekrandaki **"👁 Ekran Altyazısı: Açık"** düğmesine tıklayarak yalnızca OBS çıktısını aktif bırakabilirsiniz.

### Yöntem 2: Chroma Key Penceresi (Yedek)

1. **OBS** sekmesinden **"Düz renkli OBS Chroma Key penceresini aç"** seçeneğini işaretleyin (varsayılan arka plan `#00FF00`).
2. OBS'te **Pencere Yakalama (Window Capture)** ekleyin ve **Filtreler → Renk Anahtarı (Chroma Key)** uygulayın.

> **Capture Card Gecikmesi İpucu:** Capture card önizleme pencerenizdeki donanım gecikmesiyle altyazı senkronu farklılaşırsa OBS'te video kaynağınıza sağ tıklayıp **Filtreler → Render Delay** ile milisaniye bazında eşitleme yapabilirsiniz.

---

## Kaynak Koddan Derleme

Geliştirme veya kendi makinenizde derleme için **.NET 8 SDK** gereklidir:

```powershell
git clone https://github.com/eraykrkpnr/bitiq.git
cd bitiq

# Release derleme
dotnet build bitiq.slnx -c Release

# Birim testleri
dotnet test bitiq.slnx -c Release

# Tek dosya (self-contained) win-x64 çıktı alma
dotnet publish src/bitiq.App/bitiq.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/bitiq
```

---

## Kullanılan Açık Modeller ve Araçlar

- **Çıkarım Motoru**: [`ggml-org/llama.cpp`](https://github.com/ggml-org/llama.cpp) (`b12378` — CUDA 13.5, CUDA 12.4, Vulkan, CPU)
- **Yerel Çeviri Modeli**: [`mradermacher/translategemma-4b-it-GGUF`](https://huggingface.co/mradermacher/translategemma-4b-it-GGUF) (`Q8_0` / `Q4_K_M`, Google Gemma Kullanım Şartları kapsamındadır; yalnızca Yerel mod seçildiğinde indirilir)
