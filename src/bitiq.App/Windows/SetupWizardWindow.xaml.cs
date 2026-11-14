using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bitiq.Core.Models;
using Bitiq.Core.Ocr;
using Bitiq.Core.Translation;

namespace Bitiq.App.Windows;

public partial class SetupWizardWindow : Window
{
    private readonly ModelDownloadService _downloadService;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _downloadCts;
    private bool _isDownloading;

    private bool IsEnglishUi => string.Equals(_settings.UiLanguage, "en", StringComparison.OrdinalIgnoreCase);

    public SetupWizardWindow(AppSettings settings, ModelDownloadService downloadService)
    {
        InitializeComponent();
        _settings = settings;
        _downloadService = downloadService;

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
        QuantLabelText.Text = en ? "Local Model Quality:" : "Yerel Model Kalitesi:";
        QuantQ8Item.Content = en
            ? "Q8_0 (~3.85 GB — Highest Quality, Default)"
            : "Q8_0 (~3.85 GB — En Yüksek Kalite, Varsayılan)";
        QuantQ4Item.Content = en
            ? "Q4_K_M (~2.32 GB — Faster & Lighter)"
            : "Q4_K_M (~2.32 GB — Daha Hızlı ve Hafif)";
        CancelBtn.Content = en ? "Cancel" : "İptal";

        // ControlTemplate içindeki RadioButton çocuklarına ApplyTemplate sonrası eriş
        LocalRadio.ApplyTemplate();
        GoogleRadio.ApplyTemplate();

        if (LocalRadio.Template.FindName("LocalCardTitleText", LocalRadio) is TextBlock lTitle)
            lTitle.Text = en ? "Local (TranslateGemma)" : "Yerel (TranslateGemma)";
        if (LocalRadio.Template.FindName("LocalCardSubtitleText", LocalRadio) is TextBlock lSub)
            lSub.Text = en ? "Offline, more natural" : "İnternetsiz, daha doğal";
        if (LocalRadio.Template.FindName("LocalCardDescText", LocalRadio) is TextBlock lDesc)
            lDesc.Text = en
                ? "• Ultra-low latency on RTX GPU\n• No internet required after setup\n• Downloads ~2.9–4.7 GB once (resumable)"
                : "• RTX GPU ile ultra düşük gecikme\n• İnternet bağlantısı gerekmez\n• İlk seferde ~2.9–4.7 GB indirilir (kesilirse devam eder)";

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
        if (gpu.WarningMessage is null)
        {
            GpuStatusText.Text = en
                ? $"✓ NVIDIA CUDA Driver ready (CUDA {gpu.CudaVersionDisplay})."
                : $"✓ NVIDIA CUDA Sürücüsü hazır (CUDA {gpu.CudaVersionDisplay}).";
            GpuStatusText.Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240));
        }
        else
        {
            GpuStatusText.Text = $"⚠ {gpu.WarningMessage} {gpu.UpdateInstructions}";
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

        var selectedTag = (QuantCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var quant = selectedTag == "Q8_0" ? ModelQuantization.Q8_0 : ModelQuantization.Q4_K_M;

        _settings.TranslationEngine = TranslationEngineKind.Local;
        _settings.LocalModelQuantization = quant;

        _isDownloading = true;
        StartBtn.IsEnabled = false;
        LocalRadio.IsEnabled = false;
        GoogleRadio.IsEnabled = false;
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
            await _downloadService.EnsureLocalAssetsAsync(quant, backendKey, progress, _downloadCts.Token);
            _settings.FirstRunCompleted = true;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText.Text = "İndirme duraklatıldı (.part dosyası korundu, devam edebilirsiniz).";
        }
        catch (Exception ex)
        {
            DownloadStatusText.Text = $"İndirme hatası: {ex.Message}";
            MessageBox.Show(
                $"Yerel model dosyaları indirilirken hata oluştu:\n{ex.Message}\n\nTekrar deneyebilir veya Google Translate seçeneğiyle devam edebilirsiniz.",
                "İndirme Hatası",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _isDownloading = false;
            StartBtn.IsEnabled = true;
            LocalRadio.IsEnabled = true;
            GoogleRadio.IsEnabled = true;
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
