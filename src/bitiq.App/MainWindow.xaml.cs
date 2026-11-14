using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Bitiq.App.Controls;
using Bitiq.App.Windows;
using Bitiq.Core.Capture;
using Bitiq.Core.Models;
using Bitiq.Core.Obs;
using Bitiq.Core.Ocr;
using Bitiq.Core.Pipeline;
using Bitiq.Core.Services;
using Bitiq.Core.Translation;
using Microsoft.Win32;

namespace Bitiq.App;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly DiagnosticsTracker _diagnostics = new(50);
    private readonly TranslationLruCache _translationCache = new(500);

    private readonly WgcRegionCaptureService _captureService = new();
    private readonly WindowsOcrService _ocrService;
    private readonly ModelDownloadService _downloadService;
    private readonly LlamaServerManager _llamaManager;
    private readonly GoogleGtxTranslator _googleTranslator;
    private readonly LlamaCppTranslator _llamaTranslator;
    private readonly ObsHttpServer _obsServer;
    private readonly SubtitlePipeline _pipeline;

    private readonly SubtitleOverlayWindow _overlayWindow = new();
    private readonly PerformanceMonitorService _perfMonitor = new();
    private ObsChromaWindow? _chromaWindow;
    private TestSubtitleWindow? _testWindow;

    private AppSettings _settings;
    private List<string> _allFontNames = [];
    private int _uiSuppressCount = 1;
    private bool _isStartingOrSwitching;
    private DispatcherTimer? _overlapCheckTimer;
    private DispatcherTimer? _perfTimer;
    private SubtitleUpdateEvent? _lastSubtitleEvent;

    private bool IsUiSuppressed => _uiSuppressCount > 0 || !IsLoaded || _settings is null || _obsServer is null;

    public MainWindow()
    {
        _settings = _settingsService.Load();
        InitializeComponent();

        _ocrService = new WindowsOcrService(_settings.OcrPreprocessEnabled);
        _downloadService = new ModelDownloadService(_settingsService);
        _llamaManager = new LlamaServerManager(_downloadService);
        _googleTranslator = new GoogleGtxTranslator(_translationCache);
        _llamaTranslator = new LlamaCppTranslator(
            _llamaManager,
            _translationCache,
            () => _settings.LocalModelQuantization,
            () => _settings.LocalBackend,
            () => _settings.LlamaPort,
            () => _settings.LlamaThreads);

        _obsServer = new ObsHttpServer(_settings.ActiveStyle);
        _pipeline = new SubtitlePipeline(
            _captureService,
            _ocrService,
            _diagnostics,
            GetActiveTranslator,
            () => _settings);

        WireEvents();
        Loaded += OnMainLoaded;
        Closing += OnMainClosing;
    }

    private ITranslator GetActiveTranslator() =>
        _settings.TranslationEngine == TranslationEngineKind.Local
            ? _llamaTranslator
            : _googleTranslator;

    private void WireEvents()
    {
        _pipeline.SubtitleUpdated += evt => Dispatcher.BeginInvoke(() => OnSubtitleUpdated(evt));
        _pipeline.StateChanged += (state, msg) => Dispatcher.BeginInvoke(() => UpdateStatusDisplay(state, msg));
        _diagnostics.RecordAdded += _ => Dispatcher.BeginInvoke(RefreshDiagnosticsUi);

        _llamaManager.DownloadProgress += info => Dispatcher.BeginInvoke(() =>
        {
            InlineDownloadPanel.Visibility = Visibility.Visible;
            InlineDownloadText.Text = info.StatusText;
            InlineDownloadBar.Value = info.Percent;
            if (info.Percent >= 99.9 && info.StatusText.Contains("Hazır", StringComparison.OrdinalIgnoreCase))
                InlineDownloadPanel.Visibility = Visibility.Collapsed;
        });

        _llamaManager.StatusChanged += msg => Dispatcher.BeginInvoke(() =>
        {
            StatusDetailText.Text = msg;
            if (!msg.Contains("indiriliyor", StringComparison.OrdinalIgnoreCase) &&
                !msg.Contains("doğrulanıyor", StringComparison.OrdinalIgnoreCase))
            {
                InlineDownloadPanel.Visibility = Visibility.Collapsed;
            }
        });

        _overlayWindow.BoundsCommitted += bounds =>
        {
            _settings.OverlayBounds = bounds;
            _settingsService.Save(_settings);
            CheckWindowOverlap();
        };
        _overlayWindow.PositionEditExited += () =>
        {
            PositionOverlayBtn.IsChecked = false;
            CheckWindowOverlap();
        };
    }

    private void OnMainLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // İlk çalıştırma sihirbazı (yalnızca Yerel seçilirse indirir)
            if (!_settings.FirstRunCompleted)
            {
                var wizard = new SetupWizardWindow(_settings, _downloadService) { Owner = this };
                wizard.ShowDialog();
                _settingsService.Save(_settings);
            }

            // Yerel HTTP OBS sunucusunu başlat
            try
            {
                _obsServer.Start(_settings.ObsPort);
                ObsUrlBox.Text = _obsServer.Url;
            }
            catch (Exception ex)
            {
                ObsUrlBox.Text = $"OBS sunucusu başlatılamadı: {ex.Message}";
            }

            PopulateFontList();
            LoadSettingsIntoUi();
            CheckEnvironmentWarnings();

            // Ekran overlay'ini hazırla
            _overlayWindow.ApplySavedBounds(_settings.OverlayBounds);
            _overlayWindow.UpdateStyle(_settings.ActiveStyle);
            if (_settings.ShowScreenOverlay)
                _overlayWindow.Show();

            if (_settings.ShowObsChromaWindow)
                EnsureChromaWindowVisible();

            // Örnek önizleme metni
            StylePreviewPresenter.SetStyle(_settings.ActiveStyle);
            StylePreviewPresenter.SetSubtitle(new SubtitleUpdateEvent
            {
                SequenceId = 1,
                EnglishText = "Wait! Don't open that door, it's a trap!",
                TurkishText = "Bekle! O kapıyı açma, bu bir tuzak!",
                IsCleared = false
            });

            _overlapCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _overlapCheckTimer.Tick += (_, _) => CheckWindowOverlap();
            _overlapCheckTimer.Start();

            // Google modundaysa başlangıçta sessizce ısınma isteği gönder
            if (_settings.TranslationEngine == TranslationEngineKind.Google)
            {
                _ = _googleTranslator.InitializeAsync();
            }
        }
        finally
        {
            _uiSuppressCount = 0;
        }

        BroadcastStyleLive();
    }

    private void CheckEnvironmentWarnings()
    {
        if (!_ocrService.IsAvailable)
        {
            OcrWarningBanner.Visibility = Visibility.Visible;
            OcrWarningText.Text = $"⚠ {_ocrService.Status.ErrorMessage} {_ocrService.Status.FixInstructions}";
        }
        else
        {
            OcrWarningBanner.Visibility = Visibility.Collapsed;
        }

        var driver = NvidiaDriverChecker.Check();
        if (!string.IsNullOrEmpty(driver.WarningMessage))
        {
            DriverWarningBanner.Visibility = Visibility.Visible;
            DriverWarningText.Text = $"⚠ {driver.WarningMessage} {driver.UpdateInstructions}";
        }
        else
        {
            DriverWarningBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void PopulateFontList(string filter = "")
    {
        if (_allFontNames.Count == 0)
        {
            _allFontNames = Fonts.SystemFontFamilies
                .Select(f => f.Source)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var filtered = string.IsNullOrWhiteSpace(filter)
            ? _allFontNames
            : _allFontNames.Where(n => n.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        string current = _settings.ActiveStyle.FontFamily;
        _uiSuppressCount++;
        try
        {
            FontFamilyCombo.ItemsSource = filtered;
            if (filtered.Contains(current, StringComparer.OrdinalIgnoreCase))
                FontFamilyCombo.SelectedItem = filtered.First(n => n.Equals(current, StringComparison.OrdinalIgnoreCase));
            else if (filtered.Count > 0)
                FontFamilyCombo.SelectedIndex = 0;
        }
        finally
        {
            _uiSuppressCount--;
        }
    }

    private sealed class CaptureTargetOption
    {
        public string Label { get; init; } = string.Empty;
        public CaptureWindowInfo? Window { get; init; }
        public override string ToString() => Label;
    }

    private void LoadSettingsIntoUi()
    {
        _uiSuppressCount++;
        try
        {
            EngineLocalRadio.IsChecked = _settings.TranslationEngine == TranslationEngineKind.Local;
            EngineGoogleRadio.IsChecked = _settings.TranslationEngine == TranslationEngineKind.Google;

            RefreshCaptureTargetList();
            UpdateRegionSummary();
            RefreshProfileCombo();
            LoadStyleIntoUi(_settings.ActiveStyle);

            ShowChromaWindowCheck.IsChecked = _settings.ShowObsChromaWindow;
            ChromaColorPicker.ColorHex = _settings.ObsChromaColorHex;
            SyncScreenOverlayControls(_settings.ShowScreenOverlay);

            SelectComboTag(QuantSelectionCombo, _settings.LocalModelQuantization.ToString());
            SelectComboTag(BackendCombo, _settings.LocalBackend.ToString());
            SelectComboTag(StabilityFramesCombo, _settings.StabilityFrames.ToString());
            SelectComboTag(ThreadsCombo, _settings.LlamaThreads.ToString());
            DebounceSlider.Value = _settings.TypewriterDebounceMs;
            DebounceLabel.Text = $"Daktilo Beklemesi (N ms değişmeyince çevir): {_settings.TypewriterDebounceMs} ms";
            StreamingCheck.IsChecked = _settings.StreamingEnabled;
            StreamingMainCheck.IsChecked = _settings.StreamingEnabled;
            PreprocessCheck.IsChecked = _settings.OcrPreprocessEnabled;
            MergeIncompleteCheck.IsChecked = _settings.MergeIncompleteSentencesEnabled;
            MergeIncompleteAdvCheck.IsChecked = _settings.MergeIncompleteSentencesEnabled;
        }
        finally
        {
            _uiSuppressCount--;
        }
    }

    private void LoadStyleIntoUi(StyleProfile s)
    {
        _uiSuppressCount++;
        try
        {
            if (!string.IsNullOrWhiteSpace(s.FontFamily) && _allFontNames.Contains(s.FontFamily, StringComparer.OrdinalIgnoreCase))
                FontFamilyCombo.SelectedItem = _allFontNames.First(n => n.Equals(s.FontFamily, StringComparison.OrdinalIgnoreCase));

        FontSizeSlider.Value = s.FontSize;
        FontSizeLabel.Text = $"2. Yazı Boyutu: {s.FontSize:0} px";
        TextColorPicker.ColorHex = s.TextColorHex;
        BgColorPicker.ColorHex = s.BackgroundColorHex;
        BgOpacitySlider.Value = s.BackgroundOpacity;
        BgOpacityLabel.Text = $"5. Arka Plan Opaklığı: %{s.BackgroundOpacity:0}";
        SelectComboTag(PositionPresetCombo, s.PositionPreset.ToString());

        SelectComboTag(FontWeightCombo, s.FontWeight.ToString());
        SelectComboTag(TextAlignCombo, s.TextAlignment.ToString());
        LetterSpacingSlider.Value = s.LetterSpacing;
        LetterSpacingLabel.Text = $"Harf Aralığı: {s.LetterSpacing:0.0} px";
        LineHeightSlider.Value = s.LineHeight;
        LineHeightLabel.Text = $"Satır Aralığı: {s.LineHeight:0.00}";
        ItalicCheck.IsChecked = s.IsItalic;
        UppercaseCheck.IsChecked = s.Uppercase;

        TextOpacitySlider.Value = s.TextOpacity;
        TextOpacityLabel.Text = $"Yazı Opaklığı: %{s.TextOpacity:0}";
        OutlineColorPicker.ColorHex = s.OutlineColorHex;
        OutlineThickSlider.Value = s.OutlineThickness;
        OutlineThickLabel.Text = $"Kontur Kalınlığı: {s.OutlineThickness:0.0} px";
        ShadowColorPicker.ColorHex = s.ShadowColorHex;
        ShadowOpacitySlider.Value = s.ShadowOpacity;
        ShadowOpacityLabel.Text = $"Gölge Opaklığı: %{s.ShadowOpacity:0}";
        ShadowBlurSlider.Value = s.ShadowBlur;
        ShadowBlurLabel.Text = $"Gölge Bulanıklığı: {s.ShadowBlur:0}";
        ShadowXSlider.Value = s.ShadowOffsetX;
        ShadowXLabel.Text = $"Gölge X: {s.ShadowOffsetX:0}";
        ShadowYSlider.Value = s.ShadowOffsetY;
        ShadowYLabel.Text = $"Gölge Y: {s.ShadowOffsetY:0}";

        SelectComboTag(BgModeCombo, s.BackgroundMode.ToString());
        RadiusSlider.Value = s.BorderRadius;
        RadiusLabel.Text = $"Köşe Yuvarlaklığı: {s.BorderRadius:0}";
        PadHSlider.Value = s.PaddingHorizontal;
        PadHLabel.Text = $"Yatay İç Boşluk: {s.PaddingHorizontal:0}";
        PadVSlider.Value = s.PaddingVertical;
        PadVLabel.Text = $"Dikey İç Boşluk: {s.PaddingVertical:0}";
        BorderColorPicker.ColorHex = s.BorderColorHex;
        BorderThickSlider.Value = s.BorderThickness;
        BorderThickLabel.Text = $"Kenarlık Kalınlığı: {s.BorderThickness:0.0}";

        OffsetXSlider.Value = s.OffsetXPercent;
        OffsetXLabel.Text = $"İnce Ayar X: %{s.OffsetXPercent:0}";
        OffsetYSlider.Value = s.OffsetYPercent;
        OffsetYLabel.Text = $"İnce Ayar Y: %{s.OffsetYPercent:0}";
        MaxWidthSlider.Value = s.MaxWidthPercent;
        MaxWidthLabel.Text = $"Azami Genişlik: %{s.MaxWidthPercent:0}";
        MaxLinesSlider.Value = s.MaxLines;
        MaxLinesLabel.Text = $"Azami Satır: {s.MaxLines}";
        SelectComboTag(OverflowCombo, s.OverflowMode.ToString());

        SelectComboTag(AnimTypeCombo, s.AnimationType.ToString());
        AnimDurSlider.Value = s.AnimationDurationMs;
        AnimDurLabel.Text = $"Animasyon Süresi: {s.AnimationDurationMs} ms";
        LingerSlider.Value = s.LingerAfterDisappearMs;
        LingerLabel.Text = $"Kaybolma Gecikmesi: {s.LingerAfterDisappearMs} ms";
        MinDisplaySlider.Value = s.MinDisplayDurationMs;
        MinDisplayLabel.Text = $"En Az Gösterim: {s.MinDisplayDurationMs} ms";

        ShowOriginalCheck.IsChecked = s.ShowOriginalText;
        OrigSizeSlider.Value = s.OriginalFontSize;
        OrigSizeLabel.Text = $"Orijinal Boyut: {s.OriginalFontSize:0} px";
            OrigColorPicker.ColorHex = s.OriginalTextColorHex;
            OrigOpacitySlider.Value = s.OriginalTextOpacity;
            OrigOpacityLabel.Text = $"Orijinal Opaklık: %{s.OriginalTextOpacity:0}";
            OrigItalicCheck.IsChecked = s.OriginalIsItalic;
        }
        finally
        {
            _uiSuppressCount--;
        }

        BroadcastStyleLive();
    }

    private void ReadUiIntoStyle()
    {
        if (IsUiSuppressed)
            return;

        var s = _settings.ActiveStyle;
        if (FontFamilyCombo.SelectedItem is string fontName && !string.IsNullOrWhiteSpace(fontName))
            s.FontFamily = fontName;

        s.FontSize = FontSizeSlider.Value;
        FontSizeLabel.Text = $"2. Yazı Boyutu: {s.FontSize:0} px";
        s.TextColorHex = TextColorPicker.ColorHex;
        s.BackgroundColorHex = BgColorPicker.ColorHex;
        s.BackgroundOpacity = BgOpacitySlider.Value;
        BgOpacityLabel.Text = $"5. Arka Plan Opaklığı: %{s.BackgroundOpacity:0}";
        if (Enum.TryParse<SubtitlePositionPreset>(GetComboTag(PositionPresetCombo), out var preset))
            s.PositionPreset = preset;

        if (int.TryParse(GetComboTag(FontWeightCombo), out int weight))
            s.FontWeight = weight;
        if (Enum.TryParse<SubtitleTextAlign>(GetComboTag(TextAlignCombo), out var align))
            s.TextAlignment = align;
        s.LetterSpacing = LetterSpacingSlider.Value;
        LetterSpacingLabel.Text = $"Harf Aralığı: {s.LetterSpacing:0.0} px";
        s.LineHeight = LineHeightSlider.Value;
        LineHeightLabel.Text = $"Satır Aralığı: {s.LineHeight:0.00}";
        s.IsItalic = ItalicCheck.IsChecked == true;
        s.Uppercase = UppercaseCheck.IsChecked == true;

        s.TextOpacity = TextOpacitySlider.Value;
        TextOpacityLabel.Text = $"Yazı Opaklığı: %{s.TextOpacity:0}";
        s.OutlineColorHex = OutlineColorPicker.ColorHex;
        s.OutlineThickness = OutlineThickSlider.Value;
        OutlineThickLabel.Text = $"Kontur Kalınlığı: {s.OutlineThickness:0.0} px";
        s.ShadowColorHex = ShadowColorPicker.ColorHex;
        s.ShadowOpacity = ShadowOpacitySlider.Value;
        ShadowOpacityLabel.Text = $"Gölge Opaklığı: %{s.ShadowOpacity:0}";
        s.ShadowBlur = ShadowBlurSlider.Value;
        ShadowBlurLabel.Text = $"Gölge Bulanıklığı: {s.ShadowBlur:0}";
        s.ShadowOffsetX = ShadowXSlider.Value;
        ShadowXLabel.Text = $"Gölge X: {s.ShadowOffsetX:0}";
        s.ShadowOffsetY = ShadowYSlider.Value;
        ShadowYLabel.Text = $"Gölge Y: {s.ShadowOffsetY:0}";

        if (Enum.TryParse<SubtitleBackgroundMode>(GetComboTag(BgModeCombo), out var bgMode))
            s.BackgroundMode = bgMode;
        s.BorderRadius = RadiusSlider.Value;
        RadiusLabel.Text = $"Köşe Yuvarlaklığı: {s.BorderRadius:0}";
        s.PaddingHorizontal = PadHSlider.Value;
        PadHLabel.Text = $"Yatay İç Boşluk: {s.PaddingHorizontal:0}";
        s.PaddingVertical = PadVSlider.Value;
        PadVLabel.Text = $"Dikey İç Boşluk: {s.PaddingVertical:0}";
        s.BorderColorHex = BorderColorPicker.ColorHex;
        s.BorderThickness = BorderThickSlider.Value;
        BorderThickLabel.Text = $"Kenarlık Kalınlığı: {s.BorderThickness:0.0}";

        s.OffsetXPercent = OffsetXSlider.Value;
        OffsetXLabel.Text = $"İnce Ayar X: %{s.OffsetXPercent:0}";
        s.OffsetYPercent = OffsetYSlider.Value;
        OffsetYLabel.Text = $"İnce Ayar Y: %{s.OffsetYPercent:0}";
        s.MaxWidthPercent = MaxWidthSlider.Value;
        MaxWidthLabel.Text = $"Azami Genişlik: %{s.MaxWidthPercent:0}";
        s.MaxLines = (int)MaxLinesSlider.Value;
        MaxLinesLabel.Text = $"Azami Satır: {s.MaxLines}";
        if (Enum.TryParse<SubtitleOverflowMode>(GetComboTag(OverflowCombo), out var overflow))
            s.OverflowMode = overflow;

        if (Enum.TryParse<SubtitleAnimationType>(GetComboTag(AnimTypeCombo), out var anim))
            s.AnimationType = anim;
        s.AnimationDurationMs = (int)AnimDurSlider.Value;
        AnimDurLabel.Text = $"Animasyon Süresi: {s.AnimationDurationMs} ms";
        s.LingerAfterDisappearMs = (int)LingerSlider.Value;
        LingerLabel.Text = $"Kaybolma Gecikmesi: {s.LingerAfterDisappearMs} ms";
        s.MinDisplayDurationMs = (int)MinDisplaySlider.Value;
        MinDisplayLabel.Text = $"En Az Gösterim: {s.MinDisplayDurationMs} ms";

        s.ShowOriginalText = ShowOriginalCheck.IsChecked == true;
        s.OriginalFontSize = OrigSizeSlider.Value;
        OrigSizeLabel.Text = $"Orijinal Boyut: {s.OriginalFontSize:0} px";
        s.OriginalTextColorHex = OrigColorPicker.ColorHex;
        s.OriginalTextOpacity = OrigOpacitySlider.Value;
        OrigOpacityLabel.Text = $"Orijinal Opaklık: %{s.OriginalTextOpacity:0}";
        s.OriginalIsItalic = OrigItalicCheck.IsChecked == true;

        BroadcastStyleLive();
        _settingsService.Save(_settings);
    }

    private void BroadcastStyleLive()
    {
        StylePreviewPresenter.SetStyle(_settings.ActiveStyle);
        _overlayWindow.UpdateStyle(_settings.ActiveStyle);
        _chromaWindow?.UpdateStyle(_settings.ActiveStyle);
        _obsServer.PublishStyle(_settings.ActiveStyle);
    }

    private void OnSubtitleUpdated(SubtitleUpdateEvent evt)
    {
        _lastSubtitleEvent = evt;

        if (evt.IsCleared)
        {
            LiveEnglishText.Text = "—";
            LiveTurkishText.Text = "—";
        }
        else
        {
            LiveEnglishText.Text = evt.EnglishText;
            LiveTurkishText.Text = evt.TurkishText;
        }

        if (_settings.ShowScreenOverlay)
            _overlayWindow.UpdateSubtitle(evt);
        _chromaWindow?.UpdateSubtitle(evt);
        _obsServer.PublishSubtitle(evt);
    }

    private void UpdateStatusDisplay(PipelineState state, string message)
    {
        StatusDetailText.Text = message;
        switch (state)
        {
            case PipelineState.Running:
                StatusTitleText.Text = "Çalışıyor";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                StartStopBtn.Content = "■  Durdur";
                StartStopBtn.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                break;
            case PipelineState.Initializing:
                StatusTitleText.Text = "Model yükleniyor…";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                StartStopBtn.Content = "Bekleyin…";
                break;
            case PipelineState.Error:
                StatusTitleText.Text = "Hata";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                StartStopBtn.Content = "▶  Başlat";
                StartStopBtn.Background = new SolidColorBrush(Color.FromRgb(2, 132, 199));
                break;
            default:
                StatusTitleText.Text = "Hazır";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                StartStopBtn.Content = "▶  Başlat";
                StartStopBtn.Background = new SolidColorBrush(Color.FromRgb(2, 132, 199));
                break;
        }
    }

    private async void StartStopBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isStartingOrSwitching)
            return;

        if (_pipeline.State == PipelineState.Running)
        {
            _pipeline.Stop();
            return;
        }

        if (_settings.CaptureRegion is not { IsValid: true })
        {
            MessageBox.Show(
                "Henüz ekrandan altyazı bölgesi seçmediniz.\nÖnce 'Çevrilecek Alanı Seç' düğmesine basarak İngilizce altyazıların çıktığı dikdörtgeni işaretleyin.",
                "Alan Seçilmedi",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _isStartingOrSwitching = true;
        StartStopBtn.IsEnabled = false;
        try
        {
            await _pipeline.StartAsync();
        }
        catch (Exception ex)
        {
            UpdateStatusDisplay(PipelineState.Error, ex.Message);
            MessageBox.Show(ex.Message, "Başlatılamadı", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isStartingOrSwitching = false;
            StartStopBtn.IsEnabled = true;
        }
    }

    private async void EngineCard_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiSuppressed || _isStartingOrSwitching)
            return;

        var newEngine = EngineLocalRadio.IsChecked == true
            ? TranslationEngineKind.Local
            : TranslationEngineKind.Google;

        if (newEngine == _settings.TranslationEngine)
            return;

        bool wasRunning = _pipeline.State == PipelineState.Running;
        if (wasRunning)
            _pipeline.Stop();

        _settings.TranslationEngine = newEngine;
        _settingsService.Save(_settings);

        if (newEngine == TranslationEngineKind.Google)
        {
            // OFFLOAD: Google seçilince llama-server'ı sonlandır ve VRAM/RAM boşalmasını doğrula
            _isStartingOrSwitching = true;
            try
            {
                var offload = await _llamaManager.StopAndOffloadAsync();
                OffloadStatusText.Text = offload.Summary;
                StatusDetailText.Text = offload.Summary;
                await _googleTranslator.InitializeAsync();
            }
            finally
            {
                _isStartingOrSwitching = false;
            }
        }
        else
        {
            OffloadStatusText.Text = string.Empty;
        }

        if (wasRunning && _settings.CaptureRegion is { IsValid: true })
        {
            StartStopBtn_Click(this, new RoutedEventArgs());
        }
    }

    private void RefreshCaptureTargetList(CaptureWindowInfo? preferredWindow = null)
    {
        var prevSelected = preferredWindow ?? (CaptureTargetCombo.SelectedItem as CaptureTargetOption)?.Window;
        var windows = WindowEnumerationService.GetCapturableWindows();

        var items = new List<CaptureTargetOption>(windows.Count + 1)
        {
            new CaptureTargetOption
            {
                Label = "🖥 Tüm Ekran (Masaüstü / Monitör Yakalama)",
                Window = null
            }
        };

        foreach (var win in windows)
        {
            items.Add(new CaptureTargetOption
            {
                Label = win.IsMinimized ? $"🎯 {win.DisplayName} [Simge Durumunda]" : $"🎯 {win.DisplayName}",
                Window = win
            });
        }

        _uiSuppressCount++;
        try
        {
            CaptureTargetCombo.ItemsSource = items;

            CaptureTargetOption? match = null;
            if (prevSelected is not null)
            {
                match = items.FirstOrDefault(i => i.Window is not null && i.Window.Hwnd == prevSelected.Hwnd)
                    ?? items.FirstOrDefault(i => i.Window is not null
                        && string.Equals(i.Window.ProcessName, prevSelected.ProcessName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(i.Window.Title, prevSelected.Title, StringComparison.OrdinalIgnoreCase));
            }
            else if (_settings.CaptureRegion is { IsWindowLocked: true } lockedRegion)
            {
                match = items.FirstOrDefault(i => i.Window is not null && i.Window.Hwnd.ToInt64() == lockedRegion.TargetWindowHandle)
                    ?? items.FirstOrDefault(i => i.Window is not null
                        && string.Equals(i.Window.ProcessName, lockedRegion.TargetWindowProcessName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(i.Window.Title, lockedRegion.TargetWindowTitle, StringComparison.OrdinalIgnoreCase))
                    ?? items.FirstOrDefault(i => i.Window is not null
                        && !string.IsNullOrWhiteSpace(lockedRegion.TargetWindowProcessName)
                        && string.Equals(i.Window.ProcessName, lockedRegion.TargetWindowProcessName, StringComparison.OrdinalIgnoreCase));
            }

            CaptureTargetCombo.SelectedItem = match ?? items[0];
        }
        finally
        {
            _uiSuppressCount--;
        }
    }

    private void CaptureTargetCombo_DropDownOpened(object sender, EventArgs e)
    {
        if (IsUiSuppressed)
            return;

        RefreshCaptureTargetList();
    }

    private void RefreshWindowsBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshCaptureTargetList();
    }

    private void CaptureTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsUiSuppressed)
            return;

        if (CaptureTargetCombo.SelectedItem is CaptureTargetOption { Window: { } win })
        {
            StatusDetailText.Text = $"Hedef pencere seçildi: [{win.ProcessName}] {win.Title}. Şimdi 'Çevrilecek Alanı Seç' ile pencere içindeki altyazı alanını işaretleyin.";
        }
        else
        {
            StatusDetailText.Text = "Tüm Ekran (Monitör) modu seçildi. Yeni alan seçmek için 'Çevrilecek Alanı Seç' düğmesini kullanın.";
        }
    }

    private async void SelectRegionBtn_Click(object sender, RoutedEventArgs e)
    {
        var selectedOption = CaptureTargetCombo.SelectedItem as CaptureTargetOption;
        CaptureWindowInfo? lockedWindow = selectedOption?.Window;

        RegionSelectorWindow selector;
        if (lockedWindow is not null)
        {
            if (!WindowEnumerationService.IsWindowValid(lockedWindow.Hwnd))
            {
                RefreshCaptureTargetList();
                MessageBox.Show(
                    "Seçilen hedef pencere artık açık değil. Lütfen 'Yenile' ile güncel pencere listesini kontrol edin.",
                    "Pencere Bulunamadı",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (WindowEnumerationService.TryGetWindowBounds(lockedWindow.Hwnd, out int l, out int t, out int w, out int h))
            {
                lockedWindow = lockedWindow with
                {
                    Left = l,
                    Top = t,
                    Width = w,
                    Height = h,
                    IsMinimized = WindowEnumerationService.IsWindowMinimized(lockedWindow.Hwnd)
                };
            }

            SelectRegionBtn.IsEnabled = false;
            StatusDetailText.Text = $"Pencere önizlemesi yakalanıyor: [{lockedWindow.ProcessName}] {lockedWindow.Title}…";
            CapturedFrameData? snapshot;
            try
            {
                var hwnd = lockedWindow.Hwnd;
                snapshot = await Task.Run(() => WgcRegionCaptureService.TryCaptureWindowSnapshot(hwnd, 900));
            }
            finally
            {
                SelectRegionBtn.IsEnabled = true;
            }

            selector = new RegionSelectorWindow(lockedWindow, snapshot) { Owner = this };
        }
        else
        {
            selector = new RegionSelectorWindow() { Owner = this };
        }

        if (selector.ShowDialog() == true && selector.SelectedRegion is { IsValid: true } region)
        {
            _settings.CaptureRegion = region;
            _settingsService.Save(_settings);
            UpdateRegionSummary();
            CheckWindowOverlap();

            if (_pipeline.State == PipelineState.Running)
            {
                _pipeline.Stop();
                try
                {
                    await _pipeline.StartAsync();
                }
                catch (Exception ex)
                {
                    UpdateStatusDisplay(PipelineState.Error, ex.Message);
                    MessageBox.Show(ex.Message, "Yeniden Başlatılamadı", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                _pipeline.ApplyLiveSettings(_settings);
            }
        }
        else
        {
            UpdateRegionSummary();
        }
    }

    private void UpdateRegionSummary()
    {
        if (_settings.CaptureRegion is { IsValid: true } r)
        {
            if (r.IsWindowLocked)
            {
                RegionSummaryText.Text = $"🎯 Pencereye Kilitli: [{r.TargetWindowProcessName}] {r.TargetWindowTitle}  •  İç Konum: ({r.WindowRelativeX}, {r.WindowRelativeY})  •  Boyut: {r.Width} × {r.Height} px";
            }
            else
            {
                RegionSummaryText.Text = $"🖥 Ekran Bölgesi: ({r.X}, {r.Y})  •  Boyut: {r.Width} × {r.Height} px  ({r.MonitorDeviceName})";
            }
        }
        else
        {
            RegionSummaryText.Text = "Henüz alan seçilmedi — yukarıdaki 'Çevrilecek Alanı Seç' düğmesine tıklayın.";
        }
    }

    private void PositionOverlayBtn_Click(object sender, RoutedEventArgs e)
    {
        bool edit = PositionOverlayBtn.IsChecked == true;
        if (edit && !_settings.ShowScreenOverlay)
        {
            ApplyScreenOverlayVisibility(true);
        }
        _overlayWindow.SetPositionEditMode(edit);
        CheckWindowOverlap();
    }

    private void OpenTestWindowBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_testWindow is null || !_testWindow.IsLoaded)
        {
            _testWindow = new TestSubtitleWindow();
            _testWindow.Closed += (_, _) => _testWindow = null;
        }
        _testWindow.Show();
        _testWindow.Activate();
    }

    private void CheckWindowOverlap()
    {
        if (_settings.CaptureRegion is not { IsValid: true } r || r.IsWindowLocked)
        {
            OverlapWarningBanner.Visibility = Visibility.Collapsed;
            return;
        }

        bool overlaps = false;
        if (_overlayWindow.IsVisible && _overlayWindow.GetPhysicalBounds(out int ox, out int oy, out int ow, out int oh))
        {
            if (r.IntersectsWith(ox, oy, ow, oh))
                overlaps = true;
        }

        if (_chromaWindow is { IsVisible: true } && _chromaWindow.GetPhysicalBounds(out int cx, out int cy, out int cw, out int ch))
        {
            if (r.IntersectsWith(cx, cy, cw, ch))
                overlaps = true;
        }

        OverlapWarningBanner.Visibility = overlaps ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FontSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsUiSuppressed)
            return;

        PopulateFontList(FontSearchBox.Text);
    }

    private void StyleInput_Changed(object sender, RoutedEventArgs e) => ReadUiIntoStyle();
    private void StyleCheck_Changed(object sender, RoutedEventArgs e) => ReadUiIntoStyle();
    private void StyleColor_Changed(string _) => ReadUiIntoStyle();

    private void RefreshProfileCombo()
    {
        _uiSuppressCount++;
        try
        {
            ProfileCombo.ItemsSource = null;
            ProfileCombo.ItemsSource = _settings.SavedProfiles;
            ProfileCombo.DisplayMemberPath = nameof(StyleProfile.Name);

            var match = _settings.SavedProfiles.FirstOrDefault(p =>
                p.Name.Equals(_settings.ActiveStyle.Name, StringComparison.OrdinalIgnoreCase));
            ProfileCombo.SelectedItem = match ?? _settings.SavedProfiles.FirstOrDefault();
        }
        finally
        {
            _uiSuppressCount--;
        }
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsUiSuppressed || ProfileCombo.SelectedItem is not StyleProfile selected)
            return;

        _settings.ActiveStyle = selected.Clone();
        _settingsService.Save(_settings);
        LoadStyleIntoUi(_settings.ActiveStyle);
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var existing = _settings.SavedProfiles.FirstOrDefault(p =>
            p.Name.Equals(_settings.ActiveStyle.Name, StringComparison.OrdinalIgnoreCase));

        if (existing is { IsBuiltIn: true })
        {
            SaveAsProfile_Click(sender, e);
            return;
        }

        if (existing is not null)
        {
            int idx = _settings.SavedProfiles.IndexOf(existing);
            _settings.SavedProfiles[idx] = _settings.ActiveStyle.Clone(isBuiltIn: false);
        }
        else
        {
            _settings.SavedProfiles.Add(_settings.ActiveStyle.Clone(isBuiltIn: false));
        }

        _settingsService.Save(_settings);
        RefreshProfileCombo();
    }

    private void SaveAsProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputPromptWindow("Yeni Stil Profili", "Profil adını yazın:", _settings.ActiveStyle.Name + " (Özel)") { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        string name = dlg.ResponseText;
        var clone = _settings.ActiveStyle.Clone(name, isBuiltIn: false);
        var existing = _settings.SavedProfiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            _settings.SavedProfiles.Remove(existing);

        _settings.SavedProfiles.Add(clone);
        _settings.ActiveStyle = clone.Clone();
        _settingsService.Save(_settings);
        RefreshProfileCombo();
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not StyleProfile selected || selected.IsBuiltIn)
        {
            MessageBox.Show("Yerleşik hazır stiller yeniden adlandırılamaz; 'Farklı Kaydet' kullanabilirsiniz.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new InputPromptWindow("Profili Yeniden Adlandır", "Yeni ad:", selected.Name) { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        selected.Name = dlg.ResponseText;
        _settings.ActiveStyle.Name = dlg.ResponseText;
        _settingsService.Save(_settings);
        RefreshProfileCombo();
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not StyleProfile selected || selected.IsBuiltIn)
        {
            MessageBox.Show("Yerleşik hazır stiller silinemez.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _settings.SavedProfiles.Remove(selected);
        _settings.ActiveStyle = _settings.SavedProfiles[0].Clone();
        _settingsService.Save(_settings);
        RefreshProfileCombo();
        LoadStyleIntoUi(_settings.ActiveStyle);
    }

    private void ExportStyleJson_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Filter = "JSON Stil Dosyası (*.json)|*.json",
            FileName = $"{_settings.ActiveStyle.Name.Replace(' ', '_')}.json"
        };
        if (sfd.ShowDialog() == true)
        {
            File.WriteAllText(sfd.FileName, _settings.ActiveStyle.ToJson());
        }
    }

    private void ImportStyleJson_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Filter = "JSON Stil Dosyası (*.json)|*.json"
        };
        if (ofd.ShowDialog() == true)
        {
            try
            {
                string json = File.ReadAllText(ofd.FileName);
                var loaded = StyleProfile.FromJson(json);
                if (loaded is null)
                    throw new InvalidDataException("Geçersiz stil JSON dosyası.");

                _settings.ActiveStyle = loaded;
                if (!_settings.SavedProfiles.Any(p => p.Name.Equals(loaded.Name, StringComparison.OrdinalIgnoreCase)))
                    _settings.SavedProfiles.Add(loaded.Clone(isBuiltIn: false));

                _settingsService.Save(_settings);
                RefreshProfileCombo();
                LoadStyleIntoUi(_settings.ActiveStyle);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Stil dosyası içe aktarılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void CopyObsUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_obsServer.Url))
            Clipboard.SetText(_obsServer.Url);
    }

    private void OpenObsInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_obsServer.Url))
            return;
        Process.Start(new ProcessStartInfo(_obsServer.Url) { UseShellExecute = true });
    }

    private void SyncScreenOverlayControls(bool show)
    {
        _uiSuppressCount++;
        try
        {
            ToggleScreenOverlayBtn.IsChecked = show;
            ToggleScreenOverlayBtn.Content = show
                ? "👁  Ekran Altyazısı: Açık"
                : "🚫  Ekran Altyazısı: Kapalı (OBS)";
            ShowScreenOverlayMainCheck.IsChecked = show;
            ShowScreenOverlayCheck.IsChecked = show;
        }
        finally
        {
            _uiSuppressCount--;
        }
    }

    private void ApplyScreenOverlayVisibility(bool show, bool save = true)
    {
        _settings.ShowScreenOverlay = show;
        SyncScreenOverlayControls(show);

        if (save)
            _settingsService.Save(_settings);

        if (show)
        {
            if (!_overlayWindow.IsVisible)
                _overlayWindow.Show();
            if (_lastSubtitleEvent is not null)
                _overlayWindow.UpdateSubtitle(_lastSubtitleEvent);
        }
        else
        {
            if (PositionOverlayBtn.IsChecked == true)
            {
                PositionOverlayBtn.IsChecked = false;
                _overlayWindow.SetPositionEditMode(false);
            }
            if (_overlayWindow.IsVisible)
                _overlayWindow.Hide();
        }

        CheckWindowOverlap();
    }

    private void ToggleScreenOverlayBtn_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiSuppressed)
            return;

        ApplyScreenOverlayVisibility(ToggleScreenOverlayBtn.IsChecked == true);
    }

    private void ScreenOverlayToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (IsUiSuppressed)
            return;

        ApplyScreenOverlayVisibility(ShowScreenOverlayMainCheck.IsChecked == true);
    }

    private void ObsOption_Changed(object sender, RoutedEventArgs e)
    {
        if (IsUiSuppressed)
            return;

        ApplyScreenOverlayVisibility(ShowScreenOverlayCheck.IsChecked == true, save: false);
        _settings.ShowObsChromaWindow = ShowChromaWindowCheck.IsChecked == true;
        _settingsService.Save(_settings);

        if (_settings.ShowObsChromaWindow)
        {
            EnsureChromaWindowVisible();
        }
        else
        {
            _chromaWindow?.Close();
            _chromaWindow = null;
        }

        CheckWindowOverlap();
    }

    private void EnsureChromaWindowVisible()
    {
        if (_chromaWindow is null || !_chromaWindow.IsLoaded)
        {
            _chromaWindow = new ObsChromaWindow();
            _chromaWindow.Closed += (_, _) =>
            {
                _chromaWindow = null;
                if (!IsUiSuppressed)
                {
                    _uiSuppressCount++;
                    try
                    {
                        ShowChromaWindowCheck.IsChecked = false;
                    }
                    finally
                    {
                        _uiSuppressCount--;
                    }
                    _settings.ShowObsChromaWindow = false;
                    _settingsService.Save(_settings);
                }
                CheckWindowOverlap();
            };
        }
        _chromaWindow.SetChromaColor(_settings.ObsChromaColorHex);
        _chromaWindow.UpdateStyle(_settings.ActiveStyle);
        _chromaWindow.Show();
    }

    private void ChromaColor_Changed(string hex)
    {
        if (IsUiSuppressed)
            return;

        _settings.ObsChromaColorHex = hex;
        _settingsService.Save(_settings);
        _chromaWindow?.SetChromaColor(hex);
    }

    private void AdvancedSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsUiSuppressed)
            return;

        if (Enum.TryParse<ModelQuantization>(GetComboTag(QuantSelectionCombo), out var q))
            _settings.LocalModelQuantization = q;
        if (Enum.TryParse<LocalBackendKind>(GetComboTag(BackendCombo), out var b))
            _settings.LocalBackend = b;
        if (int.TryParse(GetComboTag(StabilityFramesCombo), out int sf))
            _settings.StabilityFrames = sf;
        if (int.TryParse(GetComboTag(ThreadsCombo), out int th))
            _settings.LlamaThreads = th;

        _settingsService.Save(_settings);
        _pipeline.ApplyLiveSettings(_settings);
    }

    private void AdvancedSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsUiSuppressed)
            return;

        _settings.TypewriterDebounceMs = (int)DebounceSlider.Value;
        DebounceLabel.Text = $"Daktilo Beklemesi (N ms değişmeyince çevir): {_settings.TypewriterDebounceMs} ms";
        _settingsService.Save(_settings);
        _pipeline.ApplyLiveSettings(_settings);
    }

    private void AdvancedCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (IsUiSuppressed)
            return;

        _settings.OcrPreprocessEnabled = PreprocessCheck.IsChecked == true;
        _settingsService.Save(_settings);
        _pipeline.ApplyLiveSettings(_settings);
    }

    private void StreamingCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (IsUiSuppressed)
            return;

        bool enabled = ReferenceEquals(sender, StreamingMainCheck)
            ? StreamingMainCheck.IsChecked == true
            : StreamingCheck.IsChecked == true;

        _settings.StreamingEnabled = enabled;

        _uiSuppressCount++;
        try
        {
            StreamingCheck.IsChecked = enabled;
            StreamingMainCheck.IsChecked = enabled;
        }
        finally
        {
            _uiSuppressCount--;
        }

        _settingsService.Save(_settings);
        _pipeline.ApplyLiveSettings(_settings);
    }

    private void MergeIncompleteCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (IsUiSuppressed)
            return;

        bool enabled = ReferenceEquals(sender, MergeIncompleteAdvCheck)
            ? MergeIncompleteAdvCheck.IsChecked == true
            : MergeIncompleteCheck.IsChecked == true;

        _settings.MergeIncompleteSentencesEnabled = enabled;

        _uiSuppressCount++;
        try
        {
            MergeIncompleteCheck.IsChecked = enabled;
            MergeIncompleteAdvCheck.IsChecked = enabled;
        }
        finally
        {
            _uiSuppressCount--;
        }

        _settingsService.Save(_settings);
        _pipeline.ApplyLiveSettings(_settings);
    }

    private void OpenWizard_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new SetupWizardWindow(_settings, _downloadService) { Owner = this };
        if (wizard.ShowDialog() == true)
        {
            _settingsService.Save(_settings);
            LoadSettingsIntoUi();
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settingsService.SettingsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _settingsService.SettingsDirectory) { UseShellExecute = true });
    }

    private void RefreshDiagnosticsUi()
    {
        var s = _diagnostics.GetSummary();
        DiagnosticsSummaryText.Text = s.SampleCount == 0
            ? "Henüz ölçüm yok."
            : $"Örnek: {s.SampleCount}/50 (Önbellek: {s.CacheHits}) | " +
              $"Yakalama Ort/P95: {s.Capture.AverageMs}/{s.Capture.P95Ms} ms | " +
              $"OCR: {s.Ocr.AverageMs}/{s.Ocr.P95Ms} ms | " +
              $"1.Token: {s.FirstToken.AverageMs}/{s.FirstToken.P95Ms} ms | " +
              $"Çeviri: {s.TotalTranslate.AverageMs}/{s.TotalTranslate.P95Ms} ms | " +
              $"Gösterim: {s.Render.AverageMs}/{s.Render.P95Ms} ms | " +
              $"Uçtan Uca: {s.EndToEnd.AverageMs}/{s.EndToEnd.P95Ms} ms";

        DiagnosticsGrid.ItemsSource = _diagnostics.GetSnapshot().Reverse().ToList();
    }

    private void ClearDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        _diagnostics.Clear();
        RefreshDiagnosticsUi();
    }

    private static void SelectComboTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private static string GetComboTag(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.Source, MainTabs))
            return;

        if (Equals(MainTabs.SelectedItem, PerformanceTab))
            StartPerformanceMonitoring();
        else
            StopPerformanceMonitoring();
    }

    private void StartPerformanceMonitoring()
    {
        RefreshPerformanceUi();

        if (_perfTimer is null)
        {
            _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _perfTimer.Tick += (_, _) => RefreshPerformanceUi();
        }

        if (!_perfTimer.IsEnabled)
            _perfTimer.Start();
    }

    private void StopPerformanceMonitoring()
    {
        _perfTimer?.Stop();
    }

    private void RefreshPerformanceUi()
    {
        var snap = _perfMonitor.Sample(_llamaManager.ActiveProcessId, _llamaManager.ActiveBackendKey);

        PerfTotalCpuText.Text = $"%{snap.TotalCpuPercent:0.0}";
        PerfCpuBar.Value = Math.Clamp(snap.TotalCpuPercent, 0, 100);
        PerfAppCpuText.Text = $"bitiq (Arayüz + OCR): %{snap.AppCpuPercent:0.0}";
        PerfLlamaCpuText.Text = snap.IsLlamaRunning
            ? $"llama-server (PID {snap.LlamaPid}): %{snap.LlamaCpuPercent:0.0}"
            : "llama-server (Model): Kapalı";

        PerfTotalRamText.Text = $"{snap.TotalRamMb:0} MB";
        PerfRamBar.Value = Math.Clamp(snap.TotalRamMb, 0, PerfRamBar.Maximum);
        PerfAppRamText.Text = $"bitiq (Arayüz + OCR): {snap.AppRamMb:0} MB";
        PerfLlamaRamText.Text = snap.IsLlamaRunning
            ? $"llama-server (Model): {snap.LlamaRamMb:0} MB"
            : "llama-server (Model): Kapalı";

        if (snap.HasGpuMetrics)
        {
            PerfGpuLoadText.Text = $"%{snap.GpuUtilizationPercent} GPU";
            PerfGpuBar.Value = Math.Clamp(snap.GpuUtilizationPercent, 0, 100);
            PerfGpuNameText.Text = snap.GpuName;

            if (snap.HasPerProcessVram)
            {
                PerfVramDetailText.Text = $"Uygulama + Model VRAM: {snap.AppAndLlamaVramMb:0} MB\nToplam Kart VRAM: {snap.TotalVramUsedMb:0} / {snap.TotalVramCapacityMb:0} MB";
            }
            else
            {
                string gpuTag = snap.IsLlamaOnGpu ? "Model GPU'da aktif • " : string.Empty;
                PerfVramDetailText.Text = $"{gpuTag}Toplam Kart VRAM: {snap.TotalVramUsedMb:0} / {snap.TotalVramCapacityMb:0} MB";
            }
        }
        else
        {
            PerfGpuLoadText.Text = "Kullanılamıyor";
            PerfGpuBar.Value = 0;
            PerfGpuNameText.Text = "NVIDIA NVML bulunamadı";
            PerfVramDetailText.Text = "AMD/Intel GPU veya NVML sürücüsü yok; yalnızca CPU/RAM okunuyor.";
        }

        if (snap.IsLlamaRunning)
        {
            string backendLabel = string.IsNullOrWhiteSpace(snap.ActiveBackend) ? "bilinmiyor" : snap.ActiveBackend;
            PerfEngineStatusText.Text = $"Durum: Aktif (PID {snap.LlamaPid} • Arka uç: {backendLabel}) — {_llamaManager.LastStatusMessage}";
            UnloadLlamaBtn.IsEnabled = !_isStartingOrSwitching;
        }
        else
        {
            PerfEngineStatusText.Text = "Durum: Kapalı (RAM ve VRAM tüketmiyor)";
            UnloadLlamaBtn.IsEnabled = false;
        }
    }

    private async void UnloadLlamaBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isStartingOrSwitching)
            return;

        _isStartingOrSwitching = true;
        UnloadLlamaBtn.IsEnabled = false;
        try
        {
            if (_pipeline.State == PipelineState.Running && _settings.TranslationEngine == TranslationEngineKind.Local)
                _pipeline.Stop();

            var offload = await _llamaManager.StopAndOffloadAsync();
            OffloadStatusText.Text = offload.Summary;
            StatusDetailText.Text = offload.Summary;
            RefreshPerformanceUi();
        }
        finally
        {
            _isStartingOrSwitching = false;
            RefreshPerformanceUi();
        }
    }

    private void OnMainClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _overlapCheckTimer?.Stop();
        _perfTimer?.Stop();
        _perfMonitor.Dispose();
        _pipeline.Dispose();
        _ocrService.Dispose();
        _captureService.Dispose();
        _obsServer.Dispose();
        _llamaManager.Dispose();
        _overlayWindow.Close();
        _chromaWindow?.Close();
        _testWindow?.Close();
    }
}
