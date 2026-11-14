using System.Windows;
using System.Windows.Controls;
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

        RunSystemChecks();
    }

    private void RunSystemChecks()
    {
        var ocrStatus = WindowsOcrService.CheckEnglishOcrAvailability(out _);
        if (ocrStatus.IsAvailable)
        {
            OcrStatusText.Text = $"✓ Windows OCR (İngilizce: {ocrStatus.LanguageTag}) yüklü ve hazır.";
        }
        else
        {
            OcrStatusText.Text = $"⚠ {ocrStatus.ErrorMessage} {ocrStatus.FixInstructions}";
            OcrStatusText.Foreground = System.Windows.Media.Brushes.Orange;
        }

        var gpu = NvidiaDriverChecker.Check();
        if (gpu.WarningMessage is null)
        {
            GpuStatusText.Text = $"✓ NVIDIA CUDA Sürücüsü hazır (CUDA {gpu.CudaVersionDisplay}).";
        }
        else
        {
            GpuStatusText.Text = $"⚠ {gpu.WarningMessage} {gpu.UpdateInstructions}";
            GpuStatusText.Foreground = System.Windows.Media.Brushes.Gold;
        }
    }

    private void EngineChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        bool isLocal = LocalRadio.IsChecked == true;
        QuantPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        StartBtn.Content = isLocal
            ? "Yerel Modeli İndir ve Başla"
            : "İndirmeden Devam Et (Google Translate)";
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
