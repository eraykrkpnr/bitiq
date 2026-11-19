using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bitiq.Core.Models;
using Bitiq.Core.Ocr;
using Bitiq.Core.Services;
using Bitiq.Core.Translation;

namespace Bitiq.App.Windows;

public partial class SetupWizardWindow : Window
{
    private readonly ModelDownloadService _downloadService;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _downloadCts;
    private bool _isDownloading;
    private bool _suppressSelection;

    private bool IsEnglishUi => string.Equals(_settings.UiLanguage, "en", StringComparison.OrdinalIgnoreCase);

    public SetupWizardWindow(AppSettings settings, ModelDownloadService downloadService)
    {
        InitializeComponent();
        _settings = settings;
        _downloadService = downloadService;

        _suppressSelection = true;
        try
        {
            string targetModelTag = _settings.LocalModel.ToString();
            foreach (var item in WizardModelCombo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), targetModelTag, StringComparison.OrdinalIgnoreCase))
                {
                    WizardModelCombo.SelectedItem = item;
                    break;
                }
            }
        }
        finally
        {
            _suppressSelection = false;
        }

        if (_settings.TranslationEngine == TranslationEngineKind.Local)
        {
            LocalRadio.IsChecked = true;
            QuantPanel.Visibility = Visibility.Visible;
        }
        else
        {
            GoogleRadio.IsChecked = true;
            QuantPanel.Visibility = Visibility.Collapsed;
        }

        ApplyWizardLanguage();
    }

    private LocalModelKind GetSelectedModel()
    {
        string? tag = (WizardModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return string.Equals(tag, nameof(LocalModelKind.TranslateGemma4B), StringComparison.OrdinalIgnoreCase)
            ? LocalModelKind.TranslateGemma4B
            : LocalModelKind.HyMt2_7B;
    }

    private void PopulateQuantComboForSelectedModel(LocalModelKind model)
    {
        bool en = IsEnglishUi;
        string currentTag = (QuantCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? _settings.LocalModelQuantization.ToString();

        _suppressSelection = true;
        try
        {
            QuantCombo.Items.Clear();
            if (model == LocalModelKind.HyMt2_7B)
            {
                QuantCombo.Items.Add(new ComboBoxItem
                {
                    Tag = "Q6_K",
                    Content = en
                        ? "Q6_K (~5.74 GB — Optimized for 10 GB RTX 3080)"
                        : "Q6_K (~5.74 GB — 10 GB RTX 3080 için Optimize)",
                    IsSelected = true
                });
                QuantCombo.SelectedIndex = 0;
                QuantCombo.IsEnabled = false;
            }
            else
            {
                var q8 = new ComboBoxItem
                {
                    Tag = "Q8_0",
                    Content = en
                        ? "Q8_0 (~3.85 GB — Highest Quality)"
                        : "Q8_0 (~3.85 GB — En Yüksek Kalite)"
                };
                var q4 = new ComboBoxItem
                {
                    Tag = "Q4_K_M",
                    Content = en
                        ? "Q4_K_M (~2.32 GB — Faster & Lighter)"
                        : "Q4_K_M (~2.32 GB — Daha Hızlı ve Hafif)"
                };
                QuantCombo.Items.Add(q8);
                QuantCombo.Items.Add(q4);
                QuantCombo.SelectedItem = string.Equals(currentTag, "Q4_K_M", StringComparison.OrdinalIgnoreCase) ? q4 : q8;
                QuantCombo.IsEnabled = true;
            }
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private void WizardModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressSelection)
            return;

        PopulateQuantComboForSelectedModel(GetSelectedModel());
    }

    private void WizardLangTrBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.UiLanguage = "tr";
        ApplyWizardLanguage();
    }

    private void WizardLangEnBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.UiLanguage = "en";
        ApplyWizardLanguage();
    }

    private void ApplyWizardLanguage()
    {
        bool en = IsEnglishUi;
        var activeBg = new SolidColorBrush(Color.FromRgb(2, 132, 199));
        var activeFg = Brushes.White;
        var idleBg = Brushes.Transparent;
        var idleFg = new SolidColorBrush(Color.FromRgb(148, 163, 184));

        WizardLangTrBtn.Background = en ? idleBg : activeBg;
        WizardLangTrBtn.Foreground = en ? idleFg : activeFg;
        WizardLangEnBtn.Background = en ? activeBg : idleBg;
        WizardLangEnBtn.Foreground = en ? activeFg : idleFg;

        WelcomeHeaderText.Text = en ? "Welcome to bitiq" : "bitiq'e Hoş Geldiniz";
        WelcomeDescText.Text = en
            ? "Choose which translation engine to start with. You can switch anytime from the main window."
            : "Başlamak için hangi çeviri motorunu kullanmak istediğinizi seçin. İstediğiniz zaman ana ekrandan değiştirebilirsiniz.";
        SysCheckHeaderText.Text = en ? "System Readiness Check" : "Sistem Hazırlık Kontrolü";
        WizardModelLabelText.Text = en ? "Local Translation Model:" : "Yerel Çeviri Modeli:";
        QuantLabelText.Text = en ? "Quantization:" : "Niceleme (Quant):";

        WizardModelHyMt2Item.Content = en
            ? "Hy-MT2 7B (Q6_K — Context-Aware, Recommended)"
            : "Hy-MT2 7B (Q6_K — Bağlamlı, Önerilen)";
        WizardModelTgItem.Content = en
            ? "TranslateGemma 4B (Fast / Context-Free)"
            : "TranslateGemma 4B (Hızlı / Bağlamsız)";
        CancelBtn.Content = en ? "Cancel" : "İptal";

        PopulateQuantComboForSelectedModel(GetSelectedModel());

        // ControlTemplate içindeki RadioButton çocuklarına ApplyTemplate sonrası eriş
        LocalRadio.ApplyTemplate();
        GoogleRadio.ApplyTemplate();

        if (LocalRadio.Template.FindName("LocalCardTitleText", LocalRadio) is TextBlock lTitle)
            lTitle.Text = en ? "Local AI (Hy-MT2 7B / TranslateGemma)" : "Yerel Yapay Zeka (Hy-MT2 7B / TranslateGemma)";
        if (LocalRadio.Template.FindName("LocalCardSubtitleText", LocalRadio) is TextBlock lSub)
            lSub.Text = en ? "Offline, context-aware & natural" : "İnternetsiz, bağlam duyarlı ve doğal";
        if (LocalRadio.Template.FindName("LocalCardDescText", LocalRadio) is TextBlock lDesc)
            lDesc.Text = en
                ? "• Ultra-low latency on RTX GPU\n• Hy-MT2 7B Q6_K: uses prior dialogue & glossary\n• One-time resumable download (~2.9–6.4 GB)"
                : "• RTX GPU ile ultra düşük gecikme\n• Hy-MT2 7B Q6_K: önceki replikler ve sözlükle doğal çeviri\n• İlk seferde indirilir (kesilirse kaldığı yerden devam eder)";

        if (GoogleRadio.Template.FindName("GoogleCardTitleText", GoogleRadio) is TextBlock gTitle)
            gTitle.Text = "Google Translate";
        if (GoogleRadio.Template.FindName("GoogleCardSubtitleText", GoogleRadio) is TextBlock gSub)
            gSub.Text = en ? "Lightweight & fast" : "Hafif ve hızlı";
        if (GoogleRadio.Template.FindName("GoogleCardDescText", GoogleRadio) is TextBlock gDesc)
            gDesc.Text = en
                ? "• Zero models or extra files downloaded\n• Zero GPU/VRAM usage\n• Ready immediately (requires internet)"
                : "• Hiçbir model veya ek dosya indirilmez\n• GPU/VRAM tüketimi sıfırdır\n• Hemen kullanıma hazır (internet gerektirir)";

        bool isLocal = LocalRadio.IsChecked == true;
        StartBtn.Content = en
            ? (isLocal ? "Download Local Model & Start" : "Continue Without Download (Google Translate)")
            : (isLocal ? "Yerel Modeli İndir ve Başla" : "İndirmeden Devam Et (Google Translate)");

        RunSystemChecks();
    }

    private void RunSystemChecks()
    {
        bool en = IsEnglishUi;
        var ocrStatus = WindowsOcrService.CheckEnglishOcrAvailability(out _);
        if (ocrStatus.IsAvailable)
        {
            OcrStatusText.Text = en
                ? $"✓ Windows OCR (English: {ocrStatus.LanguageTag}) is installed and ready."
                : $"✓ Windows OCR (İngilizce: {ocrStatus.LanguageTag}) yüklü ve hazır.";
            OcrStatusText.Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240));
        }
        else
        {
            OcrStatusText.Text = en
                ? "⚠ Windows English (en-US) OCR pack is missing. Install via Settings or PowerShell: Add-WindowsCapability -Online -Name Language.OCR~~~en-US~0.0.1.0"
                : $"⚠ {ocrStatus.ErrorMessage} {ocrStatus.FixInstructions}";
            OcrStatusText.Foreground = Brushes.Orange;
        }

        var gpu = NvidiaDriverChecker.Check();
        var vram = PerformanceMonitorService.QueryVramBudget();
        string vramNote = vram.Available
            ? (en
                ? $" • {vram.GpuName} ({vram.TotalMb / 1024.0:F1} GB VRAM, {vram.FreeMb / 1024.0:F1} GB free)"
                : $" • {vram.GpuName} ({vram.TotalMb / 1024.0:F1} GB VRAM, {vram.FreeMb / 1024.0:F1} GB boş)")
            : string.Empty;

        if (gpu.WarningMessage is null)
        {
            if (vram.Available && vram.TotalMb < 7500)
            {
                GpuStatusText.Text = en
                    ? $"✓ NVIDIA CUDA ready (CUDA {gpu.CudaVersionDisplay}){vramNote}. Note: Hy-MT2 7B Q6_K needs 8+ GB VRAM; TranslateGemma 4B is safer here."
                    : $"✓ NVIDIA CUDA hazır (CUDA {gpu.CudaVersionDisplay}){vramNote}. Not: Hy-MT2 7B Q6_K 8+ GB VRAM ister; bu GPU için TranslateGemma 4B daha güvenlidir.";
                GpuStatusText.Foreground = Brushes.Gold;
            }
            else
            {
                GpuStatusText.Text = en
                    ? $"✓ NVIDIA CUDA Driver ready (CUDA {gpu.CudaVersionDisplay}){vramNote}."
                    : $"✓ NVIDIA CUDA Sürücüsü hazır (CUDA {gpu.CudaVersionDisplay}){vramNote}.";
                GpuStatusText.Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240));
            }
        }
        else
        {
            GpuStatusText.Text = $"⚠ {gpu.WarningMessage} {gpu.UpdateInstructions}{vramNote}";
            GpuStatusText.Foreground = Brushes.Gold;
        }
    }

    private void EngineChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        bool en = IsEnglishUi;
        bool isLocal = LocalRadio.IsChecked == true;
        QuantPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        StartBtn.Content = en
            ? (isLocal ? "Download Local Model & Start" : "Continue Without Download (Google Translate)")
            : (isLocal ? "Yerel Modeli İndir ve Başla" : "İndirmeden Devam Et (Google Translate)");
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading)
            return;

        if (GoogleRadio.IsChecked == true)
        {
            // Google modunda hiçbir llama veya model dosyası indirilmez!
            _settings.TranslationEngine = TranslationEngineKind.Google;
            _settings.FirstRunCompleted = true;
            DialogResult = true;
            Close();
            return;
        }

        LocalModelKind model = GetSelectedModel();
        var selectedTag = (QuantCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        ModelQuantization requestedQuant = selectedTag switch
        {
            "Q6_K" => ModelQuantization.Q6_K,
            "Q8_0" => ModelQuantization.Q8_0,
            _ => ModelQuantization.Q4_K_M
        };
        ModelQuantization quant = ModelCatalog.ResolveQuantization(model, requestedQuant);

        _settings.TranslationEngine = TranslationEngineKind.Local;
        _settings.LocalModel = model;
        _settings.LocalModelQuantization = quant;

        _isDownloading = true;
        StartBtn.IsEnabled = false;
        LocalRadio.IsEnabled = false;
        GoogleRadio.IsEnabled = false;
        WizardModelCombo.IsEnabled = false;
        QuantCombo.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        CancelBtn.Visibility = Visibility.Visible;

        _downloadCts = new CancellationTokenSource();
        var driver = NvidiaDriverChecker.Check();
        string backendKey = ModelCatalog.ResolvePreferredBackendKey(_settings.LocalBackend, driver);

        var progress = new Progress<DownloadProgressInfo>(info =>
        {
            DownloadStatusText.Text = info.StatusText;
            DownloadBar.Value = info.Percent;
            double mbRecv = info.BytesReceived / (1024.0 * 1024.0);
            double mbTotal = info.TotalBytes / (1024.0 * 1024.0);
            double speedMb = info.SpeedBytesPerSec / (1024.0 * 1024.0);
            DownloadDetailText.Text = info.TotalBytes > 1
                ? $"{mbRecv:F1} / {mbTotal:F1} MB  •  {speedMb:F1} MB/sn"
                : string.Empty;
        });

        try
        {
            await _downloadService.EnsureLocalAssetsAsync(model, quant, backendKey, progress, _downloadCts.Token);
            _settings.FirstRunCompleted = true;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText.Text = IsEnglishUi
                ? "Download paused (.part file kept; you can resume anytime)."
                : "İndirme duraklatıldı (.part dosyası korundu, devam edebilirsiniz).";
        }
        catch (Exception ex)
        {
            DownloadStatusText.Text = IsEnglishUi ? $"Download error: {ex.Message}" : $"İndirme hatası: {ex.Message}";
            MessageBox.Show(
                IsEnglishUi
                    ? $"Failed to download local model files:\n{ex.Message}\n\nYou can retry or continue with Google Translate."
                    : $"Yerel model dosyaları indirilirken hata oluştu:\n{ex.Message}\n\nTekrar deneyebilir veya Google Translate seçeneğiyle devam edebilirsiniz.",
                IsEnglishUi ? "Download Error" : "İndirme Hatası",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _isDownloading = false;
            StartBtn.IsEnabled = true;
            LocalRadio.IsEnabled = true;
            GoogleRadio.IsEnabled = true;
            WizardModelCombo.IsEnabled = true;
            QuantCombo.IsEnabled = GetSelectedModel() == LocalModelKind.TranslateGemma4B;
            CancelBtn.Visibility = Visibility.Collapsed;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
    }
}
