using System.Collections.ObjectModel;
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
    private readonly GameProfileService _gameProfileService;
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
    private readonly ObservableCollection<CharacterEntry> _editingCharacters = [];
    private readonly ObservableCollection<GlossaryEntry> _editingGlossary = [];
    private ObsChromaWindow? _chromaWindow;
    private TestSubtitleWindow? _testWindow;

    private AppSettings _settings;
    private List<GameProfile> _gameProfiles = [];
    private GameProfile? _editingProfile;
    private volatile GameProfile? _cachedActiveProfile;
    private List<string> _allFontNames = [];
    private int _uiSuppressCount = 1;
    private bool _isStartingOrSwitching;
    private bool _isInstallingOcr;
    private PipelineState _currentPipelineState = PipelineState.Stopped;
    private DispatcherTimer? _overlapCheckTimer;
    private DispatcherTimer? _perfTimer;
    private SubtitleUpdateEvent? _lastSubtitleEvent;

    private bool IsUiSuppressed => _uiSuppressCount > 0 || !IsLoaded || _settings is null || _obsServer is null;
    private bool IsEnglishUi => string.Equals(_settings.UiLanguage, "en", StringComparison.OrdinalIgnoreCase);

    private static readonly SolidColorBrush BrushInstalledName = CreateFrozenBrush(15, 23, 42);
    private static readonly SolidColorBrush BrushMissingName = CreateFrozenBrush(100, 116, 139);
    private static readonly SolidColorBrush BrushInstalledStatus = CreateFrozenBrush(21, 128, 61);
    private static readonly SolidColorBrush BrushMissingStatus = CreateFrozenBrush(180, 83, 9);
    private static readonly SolidColorBrush BrushInfoMuted = CreateFrozenBrush(148, 163, 184);
    private static readonly SolidColorBrush BrushInfoAccent = CreateFrozenBrush(56, 189, 248);
    private static readonly SolidColorBrush BrushInfoOk = CreateFrozenBrush(74, 222, 128);
    private static readonly SolidColorBrush BrushInfoWarn = CreateFrozenBrush(253, 230, 138);
    private static readonly SolidColorBrush BrushInfoErr = CreateFrozenBrush(252, 165, 165);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public MainWindow()
    {
        _settings = _settingsService.Load();
        _gameProfileService = new GameProfileService(_settingsService);
        InitializeComponent();

        CharactersGrid.ItemsSource = _editingCharacters;
        GlossaryGrid.ItemsSource = _editingGlossary;

        _ocrService = new WindowsOcrService(
            _settings.OcrPreprocessEnabled,
            _settings.SourceLanguage,
            _settings.OcrEngine,
            _settings.OcrMinConfidence);
        _downloadService = new ModelDownloadService(_settingsService);
        _llamaManager = new LlamaServerManager(_downloadService);
        _googleTranslator = new GoogleGtxTranslator(_translationCache);
        _llamaTranslator = new LlamaCppTranslator(
            _llamaManager,
            _translationCache,
            () => _settings.LocalModel,
            () => _settings.LocalModelQuantization,
            () => _settings.LocalBackend,
            () => _settings.LlamaPort,
            () => _settings.LlamaThreads,
            GetActiveGameProfile,
            () => _settings.SimulateVramLimitEnabled,
            () => _settings.SimulatedVramLimitGb);

        _obsServer = new ObsHttpServer(_settings.ActiveStyle);
        _pipeline = new SubtitlePipeline(
            _captureService,
            _ocrService,
            _diagnostics,
            GetActiveTranslator,
            () => _settings,
            GetActiveGameProfile);

        WireEvents();
        Loaded += OnMainLoaded;
        Closing += OnMainClosing;
    }

    private GameProfile? GetActiveGameProfile() => _cachedActiveProfile;

    private ITranslator GetActiveTranslator() =>
        _settings.TranslationEngine == TranslationEngineKind.Local ? _llamaTranslator : _googleTranslator;

    private void WireEvents()
    {
        _pipeline.SubtitleUpdated += evt => Dispatcher.BeginInvoke(() => OnSubtitleUpdated(evt));
        _pipeline.StateChanged += (state, msg) => Dispatcher.BeginInvoke(() => UpdateStatusDisplay(state, msg));
        _pipeline.ContextLineCountChanged += count => Dispatcher.BeginInvoke(() => UpdateContextMemoryBadge(count));
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
            ReloadGameProfiles();
            LoadSettingsIntoUi();
            CheckEnvironmentWarnings();
            ApplyUiLanguage();

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
            DriverWarningText.Text = IsEnglishUi
                ? $"⚠ {driver.WarningMessage} Open GeForce Experience or nvidia.com to update your GPU driver."
                : $"⚠ {driver.WarningMessage} {driver.UpdateInstructions}";
        }
        else
        {
            DriverWarningBanner.Visibility = Visibility.Collapsed;
        }

        UpdateVramWarningBanner();
    }

    private void UpdateVramWarningBanner()
    {
        if (!string.IsNullOrWhiteSpace(_llamaManager.LastVramWarning))
        {
            VramWarningBanner.Visibility = Visibility.Visible;
            VramWarningText.Text = $"⚠ {_llamaManager.LastVramWarning}";
            return;
        }

        if (_settings.TranslationEngine == TranslationEngineKind.Local
            && _settings.LocalModel == LocalModelKind.HyMt2_7B
            && _settings.LocalBackend != LocalBackendKind.Cpu)
        {
            var vram = PerformanceMonitorService.QueryEffectiveVramBudget(
                _settings.SimulateVramLimitEnabled,
                _settings.SimulatedVramLimitGb);
            if (vram.Available)
            {
                if (vram.TotalMb < 7500)
                {
                    VramWarningBanner.Visibility = Visibility.Visible;
                    VramWarningText.Text = IsEnglishUi
                        ? $"⚠ Your GPU has {vram.TotalMb / 1024.0:F1} GB VRAM. Hy-MT2 7B (Q6_K) needs ~6.6 GB free VRAM; TranslateGemma 4B is recommended to avoid OBS drops."
                        : $"⚠ Ekran kartınız {vram.TotalMb / 1024.0:F1} GB VRAM'e sahip. Hy-MT2 7B (Q6_K) ~6.6 GB boş VRAM gerektirir; OBS drop olmaması için TranslateGemma 4B önerilir.";
                    return;
                }

                if (!_llamaManager.IsRunning && vram.FreeMb < 2800)
                {
                    var previewPlan = LlamaServerManager.ComputeGpuOffloadPlan(
                        _settings.LocalModel,
                        _settings.LocalModelQuantization,
                        vram);
                    VramWarningBanner.Visibility = Visibility.Visible;
                    VramWarningText.Text = IsEnglishUi
                        ? $"⚠ Very low free VRAM ({vram.FreeMb:0} / {vram.TotalMb:0} MB). Startup will shift layers to RAM ({previewPlan.Summary}); close heavy GPU apps for faster translation."
                        : $"⚠ Boş VRAM çok düşük ({vram.FreeMb:0} / {vram.TotalMb:0} MB). Başlatıldığında katmanların çoğu RAM'e kaydırılacak ({previewPlan.Summary}); daha hızlı çeviri için gereksiz GPU uygulamalarını kapatın.";
                    return;
                }
            }
        }

        VramWarningBanner.Visibility = Visibility.Collapsed;
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

    private sealed class SourceLanguageItem
    {
        public string Code { get; init; } = "en";
        public string DisplayName { get; init; } = string.Empty;
        public bool IsInstalled { get; init; }
        public string StatusText { get; init; } = string.Empty;
        public Brush NameBrush { get; init; } = Brushes.Black;
        public FontWeight NameWeight { get; init; } = FontWeights.Normal;
        public Brush StatusBrush { get; init; } = Brushes.DarkGreen;
        public string DownloadButtonText { get; init; } = string.Empty;
        public string DownloadTooltip { get; init; } = string.Empty;
        public Visibility DownloadButtonVisibility { get; init; } = Visibility.Collapsed;
        public override string ToString() => DisplayName;
    }

    private sealed class TargetLanguageItem
    {
        public string Code { get; init; } = "tr";
        public string DisplayName { get; init; } = string.Empty;
        public override string ToString() => DisplayName;
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

            SelectComboTag(LocalModelCombo, _settings.LocalModel.ToString());
            PopulateQuantComboForCurrentModel();
            SelectComboTag(BackendCombo, _settings.LocalBackend.ToString());
            SelectComboTag(StabilityFramesCombo, _settings.StabilityFrames.ToString());
            SelectComboTag(OcrEngineCombo, _settings.OcrEngine.ToString());
            SelectComboTag(ThreadsCombo, _settings.LlamaThreads.ToString());
            SelectComboTag(ContextMaxLinesCombo, _settings.ContextMaxLines.ToString());
            SelectComboTag(ContextIdleResetCombo, _settings.ContextIdleResetSeconds.ToString());
            ContextEnabledCheck.IsChecked = _settings.ContextEnabled;
            UpdateContextMemoryBadge(_pipeline.ContextLineCount);
            UpdateContextControlsState();
            DebounceSlider.Value = _settings.TypewriterDebounceMs;
            OcrConfidenceSlider.Value = Math.Clamp(Math.Round(_settings.OcrMinConfidence * 100.0), 0, 90);
            UpdateDebounceLabel();
            UpdateOcrConfidenceUi();
            StreamingCheck.IsChecked = _settings.StreamingEnabled;
            StreamingMainCheck.IsChecked = _settings.StreamingEnabled;
            PreprocessCheck.IsChecked = _settings.OcrPreprocessEnabled;
            MergeIncompleteCheck.IsChecked = _settings.MergeIncompleteSentencesEnabled;
            MergeIncompleteAdvCheck.IsChecked = _settings.MergeIncompleteSentencesEnabled;
            SimulateVramCheck.IsChecked = _settings.SimulateVramLimitEnabled;
            SimulateVramSlider.Value = Math.Clamp(_settings.SimulatedVramLimitGb, 6.0, 16.0);
            UpdateSimulateVramUi();
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
            TextColorPicker.ColorHex = s.TextColorHex;
            BgColorPicker.ColorHex = s.BackgroundColorHex;
            BgOpacitySlider.Value = s.BackgroundOpacity;
            SelectComboTag(PositionPresetCombo, s.PositionPreset.ToString());

            SelectComboTag(FontWeightCombo, s.FontWeight.ToString());
            SelectComboTag(TextAlignCombo, s.TextAlignment.ToString());
            LetterSpacingSlider.Value = s.LetterSpacing;
            LineHeightSlider.Value = s.LineHeight;
            ItalicCheck.IsChecked = s.IsItalic;
            UppercaseCheck.IsChecked = s.Uppercase;

            TextOpacitySlider.Value = s.TextOpacity;
            OutlineColorPicker.ColorHex = s.OutlineColorHex;
            OutlineThickSlider.Value = s.OutlineThickness;
            ShadowColorPicker.ColorHex = s.ShadowColorHex;
            ShadowOpacitySlider.Value = s.ShadowOpacity;
            ShadowBlurSlider.Value = s.ShadowBlur;
            ShadowXSlider.Value = s.ShadowOffsetX;
            ShadowYSlider.Value = s.ShadowOffsetY;

            SelectComboTag(BgModeCombo, s.BackgroundMode.ToString());
            RadiusSlider.Value = s.BorderRadius;
            PadHSlider.Value = s.PaddingHorizontal;
            PadVSlider.Value = s.PaddingVertical;
            BorderColorPicker.ColorHex = s.BorderColorHex;
            BorderThickSlider.Value = s.BorderThickness;

            OffsetXSlider.Value = s.OffsetXPercent;
            OffsetYSlider.Value = s.OffsetYPercent;
            MaxWidthSlider.Value = s.MaxWidthPercent;
            MaxLinesSlider.Value = s.MaxLines;
            SelectComboTag(OverflowCombo, s.OverflowMode.ToString());

            SelectComboTag(AnimTypeCombo, s.AnimationType.ToString());
            AnimDurSlider.Value = s.AnimationDurationMs;
            LingerSlider.Value = s.LingerAfterDisappearMs;
            MinDisplaySlider.Value = s.MinDisplayDurationMs;

            ShowOriginalCheck.IsChecked = s.ShowOriginalText;
            OrigSizeSlider.Value = s.OriginalFontSize;
            OrigColorPicker.ColorHex = s.OriginalTextColorHex;
            OrigOpacitySlider.Value = s.OriginalTextOpacity;
            OrigItalicCheck.IsChecked = s.OriginalIsItalic;

            UpdateStyleSliderLabels(s);
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
        s.TextColorHex = TextColorPicker.ColorHex;
        s.BackgroundColorHex = BgColorPicker.ColorHex;
        s.BackgroundOpacity = BgOpacitySlider.Value;
        if (Enum.TryParse<SubtitlePositionPreset>(GetComboTag(PositionPresetCombo), out var preset))
            s.PositionPreset = preset;

        if (int.TryParse(GetComboTag(FontWeightCombo), out int weight))
            s.FontWeight = weight;
        if (Enum.TryParse<SubtitleTextAlign>(GetComboTag(TextAlignCombo), out var align))
            s.TextAlignment = align;
        s.LetterSpacing = LetterSpacingSlider.Value;
        s.LineHeight = LineHeightSlider.Value;
        s.IsItalic = ItalicCheck.IsChecked == true;
        s.Uppercase = UppercaseCheck.IsChecked == true;

        s.TextOpacity = TextOpacitySlider.Value;
        s.OutlineColorHex = OutlineColorPicker.ColorHex;
        s.OutlineThickness = OutlineThickSlider.Value;
        s.ShadowColorHex = ShadowColorPicker.ColorHex;
        s.ShadowOpacity = ShadowOpacitySlider.Value;
        s.ShadowBlur = ShadowBlurSlider.Value;
        s.ShadowOffsetX = ShadowXSlider.Value;
        s.ShadowOffsetY = ShadowYSlider.Value;

        if (Enum.TryParse<SubtitleBackgroundMode>(GetComboTag(BgModeCombo), out var bgMode))
            s.BackgroundMode = bgMode;
        s.BorderRadius = RadiusSlider.Value;
        s.PaddingHorizontal = PadHSlider.Value;
        s.PaddingVertical = PadVSlider.Value;
        s.BorderColorHex = BorderColorPicker.ColorHex;
        s.BorderThickness = BorderThickSlider.Value;

        s.OffsetXPercent = OffsetXSlider.Value;
        s.OffsetYPercent = OffsetYSlider.Value;
        s.MaxWidthPercent = MaxWidthSlider.Value;
        s.MaxLines = (int)MaxLinesSlider.Value;
        if (Enum.TryParse<SubtitleOverflowMode>(GetComboTag(OverflowCombo), out var overflow))
            s.OverflowMode = overflow;

        if (Enum.TryParse<SubtitleAnimationType>(GetComboTag(AnimTypeCombo), out var anim))
            s.AnimationType = anim;
        s.AnimationDurationMs = (int)AnimDurSlider.Value;
        s.LingerAfterDisappearMs = (int)LingerSlider.Value;
        s.MinDisplayDurationMs = (int)MinDisplaySlider.Value;

        s.ShowOriginalText = ShowOriginalCheck.IsChecked == true;
        s.OriginalFontSize = OrigSizeSlider.Value;
        s.OriginalTextColorHex = OrigColorPicker.ColorHex;
        s.OriginalTextOpacity = OrigOpacitySlider.Value;
        s.OriginalIsItalic = OrigItalicCheck.IsChecked == true;

        UpdateStyleSliderLabels(s);
        BroadcastStyleLive();
        _settingsService.Save(_settings);
    }

    private void UpdateStyleSliderLabels(StyleProfile s)
    {
        if (IsEnglishUi)
        {
            FontSizeLabel.Text = $"2. Font Size: {s.FontSize:0} px";
            BgOpacityLabel.Text = $"5. Background Opacity: {s.BackgroundOpacity:0}%";
            LetterSpacingLabel.Text = $"Letter Spacing: {s.LetterSpacing:0.0} px";
            LineHeightLabel.Text = $"Line Height: {s.LineHeight:0.00}";
            TextOpacityLabel.Text = $"Text Opacity: {s.TextOpacity:0}%";
            OutlineThickLabel.Text = $"Outline Thickness: {s.OutlineThickness:0.0} px";
            ShadowOpacityLabel.Text = $"Shadow Opacity: {s.ShadowOpacity:0}%";
            ShadowBlurLabel.Text = $"Shadow Blur: {s.ShadowBlur:0}";
            ShadowXLabel.Text = $"Shadow X: {s.ShadowOffsetX:0}";
            ShadowYLabel.Text = $"Shadow Y: {s.ShadowOffsetY:0}";
            RadiusLabel.Text = $"Corner Radius: {s.BorderRadius:0}";
            PadHLabel.Text = $"Padding X: {s.PaddingHorizontal:0}";
            PadVLabel.Text = $"Padding Y: {s.PaddingVertical:0}";
            BorderThickLabel.Text = $"Border Thickness: {s.BorderThickness:0.0}";
            OffsetXLabel.Text = $"Fine Offset X: {s.OffsetXPercent:0}%";
            OffsetYLabel.Text = $"Fine Offset Y: {s.OffsetYPercent:0}%";
            MaxWidthLabel.Text = $"Max Width: {s.MaxWidthPercent:0}%";
            MaxLinesLabel.Text = $"Max Lines: {s.MaxLines}";
            AnimDurLabel.Text = $"Animation Duration: {s.AnimationDurationMs} ms";
            LingerLabel.Text = $"Linger Delay: {s.LingerAfterDisappearMs} ms";
            MinDisplayLabel.Text = $"Min Display: {s.MinDisplayDurationMs} ms";
            OrigSizeLabel.Text = $"Original Size: {s.OriginalFontSize:0} px";
            OrigOpacityLabel.Text = $"Original Opacity: {s.OriginalTextOpacity:0}%";
        }
        else
        {
            FontSizeLabel.Text = $"2. Yazı Boyutu: {s.FontSize:0} px";
            BgOpacityLabel.Text = $"5. Arka Plan Opaklığı: %{s.BackgroundOpacity:0}";
            LetterSpacingLabel.Text = $"Harf Aralığı: {s.LetterSpacing:0.0} px";
            LineHeightLabel.Text = $"Satır Aralığı: {s.LineHeight:0.00}";
            TextOpacityLabel.Text = $"Yazı Opaklığı: %{s.TextOpacity:0}";
            OutlineThickLabel.Text = $"Kontur Kalınlığı: {s.OutlineThickness:0.0} px";
            ShadowOpacityLabel.Text = $"Gölge Opaklığı: %{s.ShadowOpacity:0}";
            ShadowBlurLabel.Text = $"Gölge Bulanıklığı: {s.ShadowBlur:0}";
            ShadowXLabel.Text = $"Gölge X: {s.ShadowOffsetX:0}";
            ShadowYLabel.Text = $"Gölge Y: {s.ShadowOffsetY:0}";
            RadiusLabel.Text = $"Köşe Yuvarlaklığı: {s.BorderRadius:0}";
            PadHLabel.Text = $"Yatay İç Boşluk: {s.PaddingHorizontal:0}";
            PadVLabel.Text = $"Dikey İç Boşluk: {s.PaddingVertical:0}";
            BorderThickLabel.Text = $"Kenarlık Kalınlığı: {s.BorderThickness:0.0}";
            OffsetXLabel.Text = $"İnce Ayar X: %{s.OffsetXPercent:0}";
            OffsetYLabel.Text = $"İnce Ayar Y: %{s.OffsetYPercent:0}";
            MaxWidthLabel.Text = $"Azami Genişlik: %{s.MaxWidthPercent:0}";
            MaxLinesLabel.Text = $"Azami Satır: {s.MaxLines}";
            AnimDurLabel.Text = $"Animasyon Süresi: {s.AnimationDurationMs} ms";
            LingerLabel.Text = $"Kaybolma Gecikmesi: {s.LingerAfterDisappearMs} ms";
            MinDisplayLabel.Text = $"En Az Gösterim: {s.MinDisplayDurationMs} ms";
            OrigSizeLabel.Text = $"Orijinal Boyut: {s.OriginalFontSize:0} px";
            OrigOpacityLabel.Text = $"Orijinal Opaklık: %{s.OriginalTextOpacity:0}";
        }
    }

    private void UpdateDebounceLabel()
    {
        DebounceLabel.Text = IsEnglishUi
            ? $"Typewriter Debounce (wait N ms): {_settings.TypewriterDebounceMs} ms"
            : $"Daktilo Beklemesi (N ms değişmeyince çevir): {_settings.TypewriterDebounceMs} ms";
    }

    private void UpdateOcrConfidenceUi()
    {
        int pct = (int)Math.Clamp(Math.Round(_settings.OcrMinConfidence * 100.0), 0, 90);
        OcrConfidenceLabel.Text = IsEnglishUi
            ? $"OneOCR Confidence Threshold (Noise Filter): {pct}%"
            : $"OneOCR Güven Eşiği (Gürültü Filtresi): %{pct}";
        OcrConfidencePanel.Visibility = _settings.OcrEngine == OcrEngineKind.OneOcr
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateSimulateVramUi()
    {
        double limitGb = Math.Clamp(_settings.SimulatedVramLimitGb, 6.0, 16.0);
        SimulateVramLabel.Text = IsEnglishUi
            ? $"Simulated GPU VRAM Limit: {limitGb:0.#} GB ({limitGb * 1024:0} MB)"
            : $"Simüle Edilen Kart VRAM Limiti: {limitGb:0.#} GB ({limitGb * 1024:0} MB)";
        SimulateVramPanel.Visibility = _settings.SimulateVramLimitEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!_settings.SimulateVramLimitEnabled)
        {
            SimulateVramPreviewText.Text = string.Empty;
            return;
        }

        if (_llamaManager.IsRunning && _llamaManager.ActiveGpuOffloadPlan is { } activePlan)
        {
            var effectiveDuringRun = PerformanceMonitorService.QueryEffectiveVramBudget(true, limitGb);
            SimulateVramPreviewText.Text = IsEnglishUi
                ? $"Active running plan: {activePlan.Summary} • Stop & Start to re-measure VRAM and apply {limitGb:0.#} GB cap ({effectiveDuringRun.UsedMb:0}/{effectiveDuringRun.TotalMb:0} MB incl. active model)."
                : $"Şu an çalışan plan: {activePlan.Summary} • {limitGb:0.#} GB sınırını temiz bellekle uygulamak için Durdurup tekrar Başlatın (aktif model dahil {effectiveDuringRun.UsedMb:0}/{effectiveDuringRun.TotalMb:0} MB).";
            return;
        }

        var vram = PerformanceMonitorService.QueryEffectiveVramBudget(true, limitGb);
        if (!vram.Available)
        {
            SimulateVramPreviewText.Text = IsEnglishUi
                ? "NVIDIA NVML not detected — simulation requires an NVIDIA GPU."
                : "NVIDIA NVML algılanmadı — simülasyon NVIDIA GPU gerektirir.";
            return;
        }

        var preview = LlamaServerManager.ComputeGpuOffloadPlan(_settings.LocalModel, _settings.LocalModelQuantization, vram);
        SimulateVramPreviewText.Text = IsEnglishUi
            ? $"Simulated available headroom: {vram.FreeMb:0} MB free ({vram.UsedMb:0}/{vram.TotalMb:0} MB used) → Estimated placement: {preview.Summary}"
            : $"Simüle boş bütçe: {vram.FreeMb:0} MB boş ({vram.UsedMb:0}/{vram.TotalMb:0} MB dolu) → Tahmini yerleşim: {preview.Summary}";
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
        _currentPipelineState = state;
        StatusDetailText.Text = message;
        ApplyPipelineStatusLabels(state);
    }

    private void ApplyPipelineStatusLabels(PipelineState state)
    {
        switch (state)
        {
            case PipelineState.Running:
                StatusTitleText.Text = IsEnglishUi ? "Running" : "Çalışıyor";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                StartStopBtn.Content = IsEnglishUi ? "■  Stop" : "■  Durdur";
                StartStopBtn.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                break;
            case PipelineState.Initializing:
                StatusTitleText.Text = IsEnglishUi ? "Loading model…" : "Model yükleniyor…";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                StartStopBtn.Content = IsEnglishUi ? "Please wait…" : "Bekleyin…";
                break;
            case PipelineState.Error:
                StatusTitleText.Text = IsEnglishUi ? "Error" : "Hata";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                StartStopBtn.Content = IsEnglishUi ? "▶  Start" : "▶  Başlat";
                StartStopBtn.Background = new SolidColorBrush(Color.FromRgb(2, 132, 199));
                break;
            default:
                StatusTitleText.Text = IsEnglishUi ? "Ready" : "Hazır";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                StartStopBtn.Content = IsEnglishUi ? "▶  Start" : "▶  Başlat";
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
                IsEnglishUi
                    ? "You have not selected a capture region yet.\nClick 'Select Region' first and draw a rectangle over the on-screen subtitles."
                    : "Henüz ekrandan altyazı bölgesi seçmediniz.\nÖnce 'Çevrilecek Alanı Seç' düğmesine basarak altyazıların çıktığı dikdörtgeni işaretleyin.",
                IsEnglishUi ? "No Region Selected" : "Alan Seçilmedi",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _isStartingOrSwitching = true;
        StartStopBtn.IsEnabled = false;
        try
        {
            await _pipeline.StartAsync();
            UpdateVramWarningBanner();
        }
        catch (Exception ex)
        {
            UpdateVramWarningBanner();
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

        UpdateVramWarningBanner();
        UpdateSimulateVramUi();

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
                Label = IsEnglishUi ? "🖥 Full Screen (Desktop / Monitor Capture)" : "🖥 Tüm Ekran (Masaüstü / Monitör Yakalama)",
                Window = null
            }
        };

        foreach (var win in windows)
        {
            items.Add(new CaptureTargetOption
            {
                Label = win.IsMinimized
                    ? (IsEnglishUi ? $"🎯 {win.DisplayName} [Minimized]" : $"🎯 {win.DisplayName} [Simge Durumunda]")
                    : $"🎯 {win.DisplayName}",
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
            StatusDetailText.Text = IsEnglishUi
                ? $"Target window selected: [{win.ProcessName}] {win.Title}. Click 'Select Region' to draw the subtitle area inside it."
                : $"Hedef pencere seçildi: [{win.ProcessName}] {win.Title}. Şimdi 'Çevrilecek Alanı Seç' ile pencere içindeki altyazı alanını işaretleyin.";
        }
        else
        {
            StatusDetailText.Text = IsEnglishUi
                ? "Full Screen (Monitor) mode selected. Use 'Select Region' to mark a capture box."
                : "Tüm Ekran (Monitör) modu seçildi. Yeni alan seçmek için 'Çevrilecek Alanı Seç' düğmesini kullanın.";
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
                RegionSummaryText.Text = IsEnglishUi
                    ? $"🎯 Window Locked: [{r.TargetWindowProcessName}] {r.TargetWindowTitle}  •  Offset: ({r.WindowRelativeX}, {r.WindowRelativeY})  •  Size: {r.Width} × {r.Height} px"
                    : $"🎯 Pencereye Kilitli: [{r.TargetWindowProcessName}] {r.TargetWindowTitle}  •  İç Konum: ({r.WindowRelativeX}, {r.WindowRelativeY})  •  Boyut: {r.Width} × {r.Height} px";
            }
            else
            {
                RegionSummaryText.Text = IsEnglishUi
                    ? $"🖥 Screen Region: ({r.X}, {r.Y})  •  Size: {r.Width} × {r.Height} px  ({r.MonitorDeviceName})"
                    : $"🖥 Ekran Bölgesi: ({r.X}, {r.Y})  •  Boyut: {r.Width} × {r.Height} px  ({r.MonitorDeviceName})";
            }
        }
        else
        {
            RegionSummaryText.Text = IsEnglishUi
                ? "No region selected yet — click 'Select Region' above."
                : "Henüz alan seçilmedi — yukarıdaki 'Çevrilecek Alanı Seç' düğmesine tıklayın.";
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
            ToggleScreenOverlayBtn.Content = IsEnglishUi
                ? (show ? "👁  Screen Subtitle: On" : "🚫  Screen Subtitle: Off (OBS)")
                : (show ? "👁  Ekran Altyazısı: Açık" : "🚫  Ekran Altyazısı: Kapalı (OBS)");
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

        var previousOcrEngine = _settings.OcrEngine;
        var previousModel = _settings.LocalModel;

        if (Enum.TryParse<LocalModelKind>(GetComboTag(LocalModelCombo), out var model))
            _settings.LocalModel = model;

        if (_settings.LocalModel != previousModel)
        {
            _settings.LocalModelQuantization = ModelCatalog.ResolveQuantization(_settings.LocalModel, _settings.LocalModelQuantization);
            PopulateQuantComboForCurrentModel();
        }
        else if (Enum.TryParse<ModelQuantization>(GetComboTag(QuantSelectionCombo), out var q))
        {
            _settings.LocalModelQuantization = ModelCatalog.ResolveQuantization(_settings.LocalModel, q);
        }

        if (Enum.TryParse<LocalBackendKind>(GetComboTag(BackendCombo), out var b))
            _settings.LocalBackend = b;
        if (int.TryParse(GetComboTag(StabilityFramesCombo), out int sf))
            _settings.StabilityFrames = sf;
        if (Enum.TryParse<OcrEngineKind>(GetComboTag(OcrEngineCombo), out var ocrEngine))
            _settings.OcrEngine = ocrEngine;
        if (int.TryParse(GetComboTag(ThreadsCombo), out int th))
            _settings.LlamaThreads = th;
        if (int.TryParse(GetComboTag(ContextMaxLinesCombo), out int ctxLines))
        {
            _settings.ContextMaxLines = Math.Clamp(ctxLines, 4, 12);
            _settings.ContextTrimToLines = Math.Max(2, _settings.ContextMaxLines / 2);
        }
        if (int.TryParse(GetComboTag(ContextIdleResetCombo), out int ctxIdle))
            _settings.ContextIdleResetSeconds = Math.Clamp(ctxIdle, 15, 600);

        _settingsService.Save(_settings);

        if (_settings.OcrEngine != previousOcrEngine)
        {
            _ocrService.ConfigureEngine(_settings.OcrEngine, _settings.OcrMinConfidence, _settings.SourceLanguage);
            UpdateOcrConfidenceUi();
            RefreshLanguageCombos();
            CheckEnvironmentWarnings();
        }

        if (_settings.LocalModel != previousModel)
        {
            UpdateLocalEngineCardLabels();
            UpdateContextControlsState();
            UpdateContextMemoryBadge(_pipeline.ContextLineCount);
            if (_pipeline.State == PipelineState.Running)
            {
                StatusDetailText.Text = IsEnglishUi
                    ? "Model changed — stop and start again to load the selected model."
                    : "Model değiştirildi — yeni modelin yüklenmesi için Durdurup tekrar Başlatın.";
            }
        }

        UpdateVramWarningBanner();
        UpdateSimulateVramUi();
        _pipeline.ApplyLiveSettings(_settings);
    }

    private void PopulateQuantComboForCurrentModel()
    {
        bool en = IsEnglishUi;
        _uiSuppressCount++;
        try
        {
            QuantSelectionCombo.Items.Clear();
            if (_settings.LocalModel == LocalModelKind.HyMt2_7B)
            {
                _settings.LocalModelQuantization = ModelQuantization.Q6_K;
                QuantSelectionCombo.Items.Add(new ComboBoxItem
                {
                    Tag = "Q6_K",
                    Content = en
                        ? "Q6_K (~5.74 GB — Optimized for 10 GB RTX 3080)"
                        : "Q6_K (~5.74 GB — 10 GB RTX 3080 için Optimize)",
                    IsSelected = true
                });
                QuantSelectionCombo.SelectedIndex = 0;
                QuantSelectionCombo.IsEnabled = false;
            }
            else
            {
                _settings.LocalModelQuantization = ModelCatalog.ResolveQuantization(
                    LocalModelKind.TranslateGemma4B,
                    _settings.LocalModelQuantization);
                var q8 = new ComboBoxItem
                {
                    Tag = "Q8_0",
                    Content = en
                        ? "Q8_0 (~3.85 GB — High Quality)"
                        : "Q8_0 (~3.85 GB — Yüksek Kalite)"
                };
                var q4 = new ComboBoxItem
                {
                    Tag = "Q4_K_M",
                    Content = en
                        ? "Q4_K_M (~2.32 GB — Faster & Lighter)"
                        : "Q4_K_M (~2.32 GB — Daha Hızlı ve Hafif)"
                };
                QuantSelectionCombo.Items.Add(q8);
                QuantSelectionCombo.Items.Add(q4);
                QuantSelectionCombo.SelectedItem = _settings.LocalModelQuantization == ModelQuantization.Q4_K_M ? q4 : q8;
                QuantSelectionCombo.IsEnabled = true;
            }
        }
        finally
        {
            _uiSuppressCount--;
        }
    }

    private void AdvancedSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsUiSuppressed)
            return;

        _settings.TypewriterDebounceMs = (int)DebounceSlider.Value;
        _settings.OcrMinConfidence = Math.Clamp(OcrConfidenceSlider.Value / 100.0, 0.0, 0.90);
        UpdateDebounceLabel();
        UpdateOcrConfidenceUi();
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

    private void SimulateVramCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (IsUiSuppressed)
            return;

        _settings.SimulateVramLimitEnabled = SimulateVramCheck.IsChecked == true;
        _settingsService.Save(_settings);
        UpdateSimulateVramUi();
        UpdateVramWarningBanner();

        if (_pipeline.State == PipelineState.Running && _settings.TranslationEngine == TranslationEngineKind.Local)
        {
            StatusDetailText.Text = IsEnglishUi
                ? "VRAM simulation changed — stop and start again to reload the model under the simulated cap."
                : "VRAM simülasyonu değiştirildi — modelin yeni limitle yüklenmesi için Durdurup tekrar Başlatın.";
        }
    }

    private void SimulateVramSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsUiSuppressed)
            return;

        _settings.SimulatedVramLimitGb = Math.Clamp(Math.Round(SimulateVramSlider.Value * 2.0) / 2.0, 6.0, 16.0);
        _settingsService.Save(_settings);
        UpdateSimulateVramUi();
        UpdateVramWarningBanner();
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
            ApplyUiLanguage();
        }
        else
        {
            // Sihirbaz içinde TR/EN değiştirilmiş olabilir
            _settingsService.Save(_settings);
            ApplyUiLanguage();
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
        var records = _diagnostics.GetSnapshot();
        if (s.SampleCount == 0)
        {
            DiagnosticsSummaryText.Text = IsEnglishUi ? "No measurements yet." : "Henüz ölçüm yok.";
        }
        else
        {
            var localWithTimings = records.Where(r => !r.CacheHit && r.PromptTokens > 0).ToList();
            string kvPart = string.Empty;
            if (localWithTimings.Count > 0)
            {
                double avgCachedPct = localWithTimings.Average(r => Math.Min(100.0, r.CachedTokens * 100.0 / r.PromptTokens));
                double avgTps = localWithTimings.Where(r => r.TokensPerSecond > 0).Select(r => r.TokensPerSecond).DefaultIfEmpty(0).Average();
                kvPart = IsEnglishUi
                    ? $" | KV Hit: {avgCachedPct:F0}% ({avgTps:F1} tok/s)"
                    : $" | KV İsabet: %{avgCachedPct:F0} ({avgTps:F1} tok/s)";
            }

            if (IsEnglishUi)
            {
                DiagnosticsSummaryText.Text =
                    $"Samples: {s.SampleCount}/50 (Cache Hits: {s.CacheHits}) | " +
                    $"Capture Avg/P95: {s.Capture.AverageMs}/{s.Capture.P95Ms} ms | " +
                    $"OCR: {s.Ocr.AverageMs}/{s.Ocr.P95Ms} ms | " +
                    $"1st Token: {s.FirstToken.AverageMs}/{s.FirstToken.P95Ms} ms | " +
                    $"Translate: {s.TotalTranslate.AverageMs}/{s.TotalTranslate.P95Ms} ms | " +
                    $"Render: {s.Render.AverageMs}/{s.Render.P95Ms} ms | " +
                    $"End-to-End: {s.EndToEnd.AverageMs}/{s.EndToEnd.P95Ms} ms{kvPart}";
            }
            else
            {
                DiagnosticsSummaryText.Text =
                    $"Örnek: {s.SampleCount}/50 (Önbellek: {s.CacheHits}) | " +
                    $"Yakalama Ort/P95: {s.Capture.AverageMs}/{s.Capture.P95Ms} ms | " +
                    $"OCR: {s.Ocr.AverageMs}/{s.Ocr.P95Ms} ms | " +
                    $"1.Token: {s.FirstToken.AverageMs}/{s.FirstToken.P95Ms} ms | " +
                    $"Çeviri: {s.TotalTranslate.AverageMs}/{s.TotalTranslate.P95Ms} ms | " +
                    $"Gösterim: {s.Render.AverageMs}/{s.Render.P95Ms} ms | " +
                    $"Uçtan Uca: {s.EndToEnd.AverageMs}/{s.EndToEnd.P95Ms} ms{kvPart}";
            }
        }

        DiagnosticsGrid.ItemsSource = records.Reverse().ToList();
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
        string appLabel = IsEnglishUi ? "bitiq (UI + OCR)" : "bitiq (Arayüz + OCR)";
        string llamaOffLabel = IsEnglishUi ? "llama-server (Model): Off" : "llama-server (Model): Kapalı";

        PerfTotalCpuText.Text = IsEnglishUi ? $"{snap.TotalCpuPercent:0.0}%" : $"%{snap.TotalCpuPercent:0.0}";
        PerfCpuBar.Value = Math.Clamp(snap.TotalCpuPercent, 0, 100);
        PerfAppCpuText.Text = IsEnglishUi
            ? $"{appLabel}: {snap.AppCpuPercent:0.0}%"
            : $"{appLabel}: %{snap.AppCpuPercent:0.0}";
        PerfLlamaCpuText.Text = snap.IsLlamaRunning
            ? (IsEnglishUi
                ? $"llama-server (PID {snap.LlamaPid}): {snap.LlamaCpuPercent:0.0}%"
                : $"llama-server (PID {snap.LlamaPid}): %{snap.LlamaCpuPercent:0.0}")
            : llamaOffLabel;

        PerfTotalRamText.Text = $"{snap.TotalRamMb:0} MB";
        PerfRamBar.Value = Math.Clamp(snap.TotalRamMb, 0, PerfRamBar.Maximum);
        PerfAppRamText.Text = $"{appLabel}: {snap.AppRamMb:0} MB";
        PerfLlamaRamText.Text = snap.IsLlamaRunning
            ? $"llama-server (Model): {snap.LlamaRamMb:0} MB"
            : llamaOffLabel;

        if (snap.HasGpuMetrics)
        {
            PerfGpuLoadText.Text = IsEnglishUi
                ? $"{snap.GpuUtilizationPercent}% GPU"
                : $"%{snap.GpuUtilizationPercent} GPU";
            PerfGpuBar.Value = Math.Clamp(snap.GpuUtilizationPercent, 0, 100);
            var effectiveVram = PerformanceMonitorService.ApplySimulatedVramLimit(
                new VramBudgetSnapshot(
                    true,
                    snap.GpuName,
                    snap.TotalVramCapacityMb,
                    snap.TotalVramUsedMb,
                    Math.Max(0, snap.TotalVramCapacityMb - snap.TotalVramUsedMb)),
                _settings.SimulateVramLimitEnabled,
                _settings.SimulatedVramLimitGb);

            PerfGpuNameText.Text = effectiveVram.GpuName;

            if (snap.HasPerProcessVram)
            {
                PerfVramDetailText.Text = IsEnglishUi
                    ? $"App + Model VRAM: {snap.AppAndLlamaVramMb:0} MB\nTotal GPU VRAM: {effectiveVram.UsedMb:0} / {effectiveVram.TotalMb:0} MB"
                    : $"Uygulama + Model VRAM: {snap.AppAndLlamaVramMb:0} MB\nToplam Kart VRAM: {effectiveVram.UsedMb:0} / {effectiveVram.TotalMb:0} MB";
            }
            else
            {
                string gpuTag = snap.IsLlamaOnGpu
                    ? (IsEnglishUi ? "Model active on GPU • " : "Model GPU'da aktif • ")
                    : string.Empty;
                PerfVramDetailText.Text = IsEnglishUi
                    ? $"{gpuTag}Total GPU VRAM: {effectiveVram.UsedMb:0} / {effectiveVram.TotalMb:0} MB"
                    : $"{gpuTag}Toplam Kart VRAM: {effectiveVram.UsedMb:0} / {effectiveVram.TotalMb:0} MB";
            }

            double remainingMb = Math.Max(0, effectiveVram.TotalMb - effectiveVram.UsedMb);
            double usedRatio = effectiveVram.TotalMb > 0 ? effectiveVram.UsedMb / effectiveVram.TotalMb : 0;
            if (remainingMb < 350.0)
            {
                PerfVramSafetyText.Foreground = BrushInfoWarn;
                PerfVramSafetyText.Text = IsEnglishUi
                    ? $"⚠ Low VRAM headroom ({remainingMb:0} MB left • {usedRatio * 100:0}% used) — shared-memory spillover can cause OBS lag"
                    : $"⚠ Boş VRAM çok az ({remainingMb:0} MB kaldı • %{usedRatio * 100:0} kullanım) — paylaşılan belleğe taşma OBS drop yaratabilir";
            }
            else
            {
                PerfVramSafetyText.Foreground = BrushInfoOk;
                PerfVramSafetyText.Text = IsEnglishUi
                    ? $"✓ VRAM headroom safe ({remainingMb:0} MB free • {usedRatio * 100:0}% used, target ≥ 400 MB free)"
                    : $"✓ VRAM bütçesi güvenli ({remainingMb:0} MB boş • %{usedRatio * 100:0} kullanım, hedef ≥ 400 MB boş)";
            }
        }
        else
        {
            PerfGpuLoadText.Text = IsEnglishUi ? "Unavailable" : "Kullanılamıyor";
            PerfGpuBar.Value = 0;
            PerfGpuNameText.Text = IsEnglishUi ? "NVIDIA NVML not found" : "NVIDIA NVML bulunamadı";
            PerfVramDetailText.Text = IsEnglishUi
                ? "No NVIDIA NVML driver found; reporting CPU/RAM only."
                : "AMD/Intel GPU veya NVML sürücüsü yok; yalnızca CPU/RAM okunuyor.";
            PerfVramSafetyText.Text = string.Empty;
        }

        if (snap.IsLlamaRunning)
        {
            string backendLabel = string.IsNullOrWhiteSpace(snap.ActiveBackend)
                ? (IsEnglishUi ? "unknown" : "bilinmiyor")
                : snap.ActiveBackend;
            string offloadTag = _llamaManager.ActiveGpuOffloadPlan is { } plan
                ? $" • {plan.Summary}"
                : string.Empty;
            PerfEngineStatusText.Text = IsEnglishUi
                ? $"Status: Active (PID {snap.LlamaPid} • Backend: {backendLabel}{offloadTag}) — {_llamaManager.LastStatusMessage}"
                : $"Durum: Aktif (PID {snap.LlamaPid} • Arka uç: {backendLabel}{offloadTag}) — {_llamaManager.LastStatusMessage}";
            UnloadLlamaBtn.IsEnabled = !_isStartingOrSwitching;
        }
        else
        {
            PerfEngineStatusText.Text = IsEnglishUi
                ? "Status: Off (consuming zero RAM/VRAM)"
                : "Durum: Kapalı (RAM ve VRAM tüketmiyor)";
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
            if (_pipeline.State == PipelineState.Running &&
                _settings.TranslationEngine == TranslationEngineKind.Local)
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
            UpdateSimulateVramUi();
        }
    }

    // ==================== DİL AYARLARI & ARAYÜZ DİLİ (TR / EN) ====================

    private void UiLangTrBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.Equals(_settings.UiLanguage, "tr", StringComparison.OrdinalIgnoreCase))
            return;

        _settings.UiLanguage = "tr";
        _settingsService.Save(_settings);
        ApplyUiLanguage();
    }

    private void UiLangEnBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.Equals(_settings.UiLanguage, "en", StringComparison.OrdinalIgnoreCase))
            return;

        _settings.UiLanguage = "en";
        _settingsService.Save(_settings);
        ApplyUiLanguage();
    }

    private void RefreshLanguageCombos(string? pendingMissingLangCode = null)
    {
        bool en = IsEnglishUi;
        bool oneOcrReady = _settings.OcrEngine == OcrEngineKind.OneOcr && OneOcrNativeEngine.CheckAvailability().IsAvailable;
        var sourceItems = new List<SourceLanguageItem>(LanguageCatalog.SupportedLanguages.Count);
        foreach (var lang in LanguageCatalog.SupportedLanguages)
        {
            bool installed = oneOcrReady || WindowsOcrService.IsLanguageOcrInstalled(lang.Code, out _);
            sourceItems.Add(new SourceLanguageItem
            {
                Code = lang.Code,
                DisplayName = LanguageCatalog.GetDisplayName(lang.Code, en),
                IsInstalled = installed,
                StatusText = installed
                    ? (oneOcrReady ? (en ? "✓ OneOCR" : "✓ OneOCR") : (en ? "✓ Installed" : "✓ Kurulu"))
                    : (en ? "No OCR pack" : "Paketi yok"),
                NameBrush = installed ? BrushInstalledName : BrushMissingName,
                NameWeight = installed ? FontWeights.SemiBold : FontWeights.Normal,
                StatusBrush = installed ? BrushInstalledStatus : BrushMissingStatus,
                DownloadButtonText = en ? "⬇ Install" : "⬇ İndir",
                DownloadTooltip = en
                    ? $"Download & install Windows OCR language pack ({lang.OcrCapabilityTag})"
                    : $"Windows OCR dil paketini indir ve kur ({lang.OcrCapabilityTag})",
                DownloadButtonVisibility = installed ? Visibility.Collapsed : Visibility.Visible
            });
        }

        var targetItems = LanguageCatalog.SupportedLanguages
            .Select(lang => new TargetLanguageItem
            {
                Code = lang.Code,
                DisplayName = LanguageCatalog.GetDisplayName(lang.Code, en)
            })
            .ToList();

        string activeSrc = LanguageCatalog.GetByCode(_settings.SourceLanguage).Code;
        string activeTgt = LanguageCatalog.GetByCode(_settings.TargetLanguage).Code;

        _uiSuppressCount++;
        try
        {
            SourceLangCombo.ItemsSource = sourceItems;
            SourceLangCombo.SelectedItem = sourceItems.FirstOrDefault(x =>
                    string.Equals(x.Code, activeSrc, StringComparison.OrdinalIgnoreCase))
                ?? sourceItems.FirstOrDefault();

            TargetLangCombo.ItemsSource = targetItems;
            TargetLangCombo.SelectedItem = targetItems.FirstOrDefault(x =>
                    string.Equals(x.Code, activeTgt, StringComparison.OrdinalIgnoreCase))
                ?? targetItems.FirstOrDefault();
        }
        finally
        {
            _uiSuppressCount--;
        }

        if (!string.IsNullOrWhiteSpace(pendingMissingLangCode))
        {
            var missing = LanguageCatalog.GetByCode(pendingMissingLangCode);
            string missingName = LanguageCatalog.GetShortName(missing.Code, en);
            QuickInstallOcrText.Text = en
                ? $"⚠ Windows OCR pack for {missingName} is not installed."
                : $"⚠ {missingName} için Windows OCR paketi kurulu değil.";
            QuickInstallOcrBtn.Content = en
                ? $"⬇ Download & Install {missingName} OCR"
                : $"⬇ {missingName} OCR Paketini İndir ve Kur";
            QuickInstallOcrBtn.Tag = missing.Code;
            QuickInstallOcrPanel.Visibility = Visibility.Visible;
        }
        else
        {
            QuickInstallOcrPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateLanguagePairLabels()
    {
        bool en = IsEnglishUi;
        string srcShort = LanguageCatalog.GetShortName(_settings.SourceLanguage, en);
        string tgtShort = LanguageCatalog.GetShortName(_settings.TargetLanguage, en);

        RegionSummaryHeaderText.Text = en
            ? $"Selected Capture Region ({srcShort} → {tgtShort})"
            : $"Seçili Yakalama Bölgesi ({srcShort} → {tgtShort})";
        LiveSourceLabelText.Text = $"{srcShort} (OCR):";
        LiveTargetLabelText.Text = en ? $"{tgtShort} (Translation):" : $"{tgtShort} (Çeviri):";
    }

    private void ApplySelectedSourceLanguage(string langCode)
    {
        string normalized = LanguageCatalog.GetByCode(langCode).Code;
        if (!string.Equals(_settings.SourceLanguage, normalized, StringComparison.OrdinalIgnoreCase))
        {
            _settings.SourceLanguage = normalized;
            _settingsService.Save(_settings);
        }

        var ocrRes = _ocrService.SetSourceLanguage(normalized);
        _pipeline.ApplyLiveSettings(_settings);
        CheckEnvironmentWarnings();
        UpdateLanguagePairLabels();
        QuickInstallOcrPanel.Visibility = Visibility.Collapsed;

        if (ocrRes.IsAvailable)
        {
            SourceOcrPackInfoText.Foreground = BrushInfoMuted;
            SourceOcrPackInfoText.Text = IsEnglishUi
                ? $"Active OCR language: {LanguageCatalog.GetShortName(normalized, true)}. Languages marked '⬇ Install' become selectable once installed."
                : $"Aktif OCR dili: {LanguageCatalog.GetShortName(normalized, false)}. Yanında '⬇ İndir' görünen diller kurulum bittiğinde aktif olur.";
        }
        else
        {
            SourceOcrPackInfoText.Foreground = BrushInfoWarn;
            SourceOcrPackInfoText.Text = ocrRes.ErrorMessage;
        }
    }

    private void SourceLangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsUiSuppressed || SourceLangCombo.SelectedItem is not SourceLanguageItem item)
            return;

        if (!item.IsInstalled)
        {
            // Kurulu olmayan dil aktif seçime alınmaz; mevcut dile dönüp hızlı kurulum düğmesini göster
            RefreshLanguageCombos(pendingMissingLangCode: item.Code);
            SourceOcrPackInfoText.Foreground = BrushInfoWarn;
            SourceOcrPackInfoText.Text = IsEnglishUi
                ? $"'{item.DisplayName}' requires its Windows OCR pack before activation. Click '⬇ Install' next to it or below."
                : $"'{item.DisplayName}' dilini aktif etmek için Windows OCR paketi kurulmalıdır. Sağdaki '⬇ İndir' veya aşağıdaki düğmeyle yükleyin.";
            return;
        }

        ApplySelectedSourceLanguage(item.Code);
    }

    private void TargetLangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsUiSuppressed || TargetLangCombo.SelectedItem is not TargetLanguageItem item)
            return;

        string normalized = LanguageCatalog.GetByCode(item.Code).Code;
        if (string.Equals(_settings.TargetLanguage, normalized, StringComparison.OrdinalIgnoreCase))
            return;

        _settings.TargetLanguage = normalized;
        _settingsService.Save(_settings);
        _pipeline.ApplyLiveSettings(_settings);
        UpdateLanguagePairLabels();
        UpdateProfilePromptPreview();
    }

    private void SwapLangsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstallingOcr)
            return;

        string newSource = LanguageCatalog.GetByCode(_settings.TargetLanguage).Code;
        string newTarget = LanguageCatalog.GetByCode(_settings.SourceLanguage).Code;
        if (string.Equals(newSource, newTarget, StringComparison.OrdinalIgnoreCase))
            return;

        bool oneOcrReady = _settings.OcrEngine == OcrEngineKind.OneOcr && OneOcrNativeEngine.CheckAvailability().IsAvailable;
        if (!oneOcrReady && !WindowsOcrService.IsLanguageOcrInstalled(newSource, out _))
        {
            RefreshLanguageCombos(pendingMissingLangCode: newSource);
            string newSrcName = LanguageCatalog.GetShortName(newSource, IsEnglishUi);
            SourceOcrPackInfoText.Foreground = BrushInfoWarn;
            SourceOcrPackInfoText.Text = IsEnglishUi
                ? $"Cannot swap: Windows OCR pack for {newSrcName} is not installed yet. Install it first below."
                : $"Diller değiştirilemedi: {newSrcName} için Windows OCR paketi henüz kurulu değil. Önce aşağıdan kurun.";
            return;
        }

        _settings.SourceLanguage = newSource;
        _settings.TargetLanguage = newTarget;
        _settingsService.Save(_settings);

        ApplySelectedSourceLanguage(newSource);
        RefreshLanguageCombos();
    }

    private async void InstallOcrLangBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstallingOcr)
            return;

        if (sender is not FrameworkElement { Tag: string langCode } || string.IsNullOrWhiteSpace(langCode))
            return;

        SourceLangCombo.IsDropDownOpen = false;
        var desc = LanguageCatalog.GetByCode(langCode);
        string langName = LanguageCatalog.GetShortName(desc.Code, IsEnglishUi);

        _isInstallingOcr = true;
        SourceLangCombo.IsEnabled = false;
        SwapLangsBtn.IsEnabled = false;
        QuickInstallOcrBtn.IsEnabled = false;
        OcrInstallProgressBar.Visibility = Visibility.Visible;
        SourceOcrPackInfoText.Foreground = BrushInfoAccent;
        SourceOcrPackInfoText.Text = IsEnglishUi
            ? $"Installing Windows OCR pack for {langName}… Approve the UAC prompt if requested."
            : $"{langName} Windows OCR paketi kuruluyor… Yönetici onayı penceresi çıkarsa onaylayın (15–60 sn sürebilir).";

        try
        {
            var result = await WindowsOcrService.InstallOcrLanguagePackAsync(desc.Code, IsEnglishUi);
            if (result.Success)
            {
                ApplySelectedSourceLanguage(desc.Code);
                RefreshLanguageCombos();
                SourceOcrPackInfoText.Foreground = BrushInfoOk;
                SourceOcrPackInfoText.Text = $"✓ {result.Message}";
                StatusDetailText.Text = result.Message;
            }
            else
            {
                RefreshLanguageCombos(pendingMissingLangCode: desc.Code);
                SourceOcrPackInfoText.Foreground = BrushInfoErr;
                SourceOcrPackInfoText.Text = $"⚠ {result.Message}";
            }
        }
        finally
        {
            _isInstallingOcr = false;
            SourceLangCombo.IsEnabled = true;
            SwapLangsBtn.IsEnabled = true;
            QuickInstallOcrBtn.IsEnabled = true;
            OcrInstallProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyUiLanguage()
    {
        bool en = IsEnglishUi;

        // Toggle buton görünümü
        UiLangTrBtn.Background = en ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(2, 132, 199));
        UiLangTrBtn.Foreground = en ? new SolidColorBrush(Color.FromRgb(148, 163, 184)) : new SolidColorBrush(Color.FromRgb(248, 250, 252));
        UiLangEnBtn.Background = en ? new SolidColorBrush(Color.FromRgb(2, 132, 199)) : Brushes.Transparent;
        UiLangEnBtn.Foreground = en ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(148, 163, 184));

        // Sekme başlıkları
        MainTabGeneral.Header = en ? "General" : "Genel";
        MainTabProfiles.Header = en ? "Game Profile" : "Oyun Profili";
        MainTabStyle.Header = en ? "Appearance" : "Görünüm";
        MainTabObs.Header = "OBS";
        MainTabAdvanced.Header = en ? "Advanced" : "Gelişmiş";
        PerformanceTab.Header = en ? "Performance" : "Performans";

        // Üst durum & uyarı & eylem düğmeleri
        ApplyPipelineStatusLabels(_currentPipelineState);
        if (_currentPipelineState == PipelineState.Stopped &&
            (StatusDetailText.Text.Contains("'Çevrilecek Alanı Seç'", StringComparison.Ordinal) ||
             StatusDetailText.Text.Contains("'Select Region'", StringComparison.Ordinal)))
        {
            StatusDetailText.Text = en
                ? "To start, click 'Select Region' and then press 'Start'."
                : "Başlatmak için 'Çevrilecek Alanı Seç' ardından 'Başlat'a basın.";
        }

        OverlapWarningText.Text = en
            ? "⚠ Warning: The subtitle overlay overlaps the OCR capture region! Move the overlay with 'Reposition' or pick a different capture box so the app doesn't read its own translation."
            : "⚠ Uyarı: Altyazı penceresi OCR yakalama bölgesiyle çakışıyor! Uygulamanın kendi çevirisini okumaması için altyazı katmanını 'Konumlandır' ile taşıyın veya başka bir bölge seçin.";

        SelectRegionBtn.Content = en ? "🎯  Select Region" : "🎯  Çevrilecek Alanı Seç";
        PositionOverlayBtn.Content = en ? "✥  Reposition Overlay" : "✥  Altyazıyı Konumlandır";
        OpenTestWindowBtn.Content = en ? "🎬  Open Test Window" : "🎬  Test Penceresi Aç";
        SyncScreenOverlayControls(_settings.ShowScreenOverlay);

        // Sekme 1: Genel
        EngineSelectorHeaderText.Text = en ? "Translation Engine Selection" : "Çeviri Motoru Seçimi";
        UpdateLocalEngineCardLabels();
        EngineGoogleTitleText.Text = en ? "Google Translate: lightweight & fast" : "Google Translate: hafif ve hızlı";
        EngineGoogleDescText.Text = en
            ? "Uses zero VRAM and minimal RAM. Unloads the local model immediately when selected."
            : "Ekran kartı ve bellek kullanmaz. Seçildiğinde yerel model VRAM/RAM'den tamamen boşaltılır.";

        ContextCardHeaderText.Text = en ? "🧠 Context & Game Profile" : "🧠 Bağlam (Context) ve Oyun Profili";
        ContextEnabledCheck.Content = en
            ? "Enable context-aware translation (prior dialogue + game glossary, Hy-MT2 7B Q6_K)"
            : "Bağlam duyarlı çeviriyi etkinleştir (önceki replikler + oyun sözlüğü, Hy-MT2 7B Q6_K)";
        EditGameProfileBtn.Content = en ? "⚙ Edit Profile" : "⚙ Profili Düzenle";
        ResetContextBtn.Content = en ? "🔄 Reset Context" : "🔄 Bağlamı Sıfırla";
        UpdateContextMemoryBadge(_pipeline.ContextLineCount);
        UpdateContextControlsState();

        // Sekme 2: Oyun Profili
        ProfileEditSelectLabelText.Text = en ? "Profile:" : "Profil:";
        NewGameProfileBtn.Content = en ? "+ New" : "+ Yeni";
        SaveGameProfileBtn.Content = en ? "Save" : "Kaydet";
        DeleteGameProfileBtn.Content = en ? "Delete" : "Sil";
        ExportGameProfileBtn.Content = en ? "Export JSON" : "JSON Dışa Aktar";
        ImportGameProfileBtn.Content = en ? "Import JSON" : "JSON İçe Aktar";
        ProfileMetaHeaderText.Text = en ? "Game Details & Tone" : "Oyun Bilgileri ve Üslup";
        GameTitleLabelText.Text = en ? "Game Title" : "Oyun Adı";
        GameSynopsisLabelText.Text = en ? "Short Synopsis / Genre (1-2 sentences)" : "Kısa Özet / Tür (1-2 cümle)";
        GameToneLabelText.Text = en
            ? "Tone & Register Note (e.g. 'Natural spoken Turkish; close friends use informal address')"
            : "Üslup ve Hitap Notu (ör. 'Doğal konuşma dili; yakın arkadaşlar sen diye hitap eder')";
        CharactersHeaderText.Text = en ? "Characters (Name / Gender-Role)" : "Karakterler (Ad / Cinsiyet-Rol)";
        GlossaryHeaderText.Text = en ? "Glossary / Terms (X → Y)" : "Sözlük / Terimler (X → Y)";
        AddCharacterBtn.Content = en ? "+ Add" : "+ Ekle";
        RemoveCharacterBtn.Content = en ? "Delete" : "Sil";
        AddGlossaryBtn.Content = en ? "+ Add" : "+ Ekle";
        RemoveGlossaryBtn.Content = en ? "Delete" : "Sil";
        ColCharName.Header = en ? "Name" : "Ad";
        ColCharNote.Header = en ? "Note (gender/role)" : "Not (cinsiyet/rol)";
        ColGlossarySrc.Header = en ? "Source Term" : "Kaynak Terim";
        ColGlossaryTgt.Header = en ? "Target Translation" : "Türkçe / Hedef";
        ProfilePreviewExpander.Header = en
            ? "Hy-MT2 Prompt Context Preview ([Background Information])"
            : "Hy-MT2 Prompt Bağlam Önizlemesi ([Background Information])";
        RefreshGameProfileDropdowns();
        UpdateProfilePromptPreview();

        LangSettingsHeaderText.Text = en ? "🌐 Language Settings (On-Screen Language → Translation)" : "🌐 Dil Ayarları (Ekrandaki Dil → Çeviri Dili)";
        SourceLangLabelText.Text = en ? "On-Screen Language (OCR):" : "Ekrandaki Dil (OCR):";
        TargetLangLabelText.Text = en ? "Translate To (Subtitles):" : "Çevrilecek Dil (Altyazı):";
        SwapLangsBtn.ToolTip = en ? "Swap languages" : "Dilleri değiştir";

        CaptureSourceHeaderText.Text = en ? "🎯 Capture Source (Screen or Window Lock)" : "🎯 Yakalama Kaynağı (Ekran veya Pencereye Kilitle)";
        RefreshWindowsBtn.Content = en ? "🔄 Refresh" : "🔄 Yenile";
        CaptureSourceHintText.Text = en
            ? "💡 Tip: Choose a window and click 'Select Region' to lock capture strictly to that window. Even if overlays or other windows cover it, only the locked window is captured."
            : "💡 İpucu: Bir pencere seçip 'Çevrilecek Alanı Seç' derseniz yakalama ve çeviri o pencereye kilitlenir. Üstüne başka bir pencere veya altyazı kutusu gelse bile yalnızca seçilen pencere yakalanır.";

        ShowScreenOverlayMainCheck.Content = en
            ? "Show transparent on-screen subtitle overlay (turn off to send only to OBS source)"
            : "Ekranda şeffaf altyazı kutusunu göster (kapatılırsa yalnızca OBS kaynağına gönderilir)";
        MergeIncompleteCheck.Content = en
            ? "Merge incomplete sentences before translating (append next line if no terminal punctuation)"
            : "Bitmemiş cümleleri birleştirerek çevir (nokta ile bitmeyen altyazılara sonraki satırı ekle)";
        StreamingMainCheck.Content = en
            ? "Stream text with typewriter effect (turn off to display full translation at once)"
            : "Yazıyı daktilo (typewriter) efektiyle akışlı göster (kapatılırsa cümle tek seferde anında görünür)";
        LivePanelHeaderText.Text = en ? "Live Detected & Translated Text" : "Canlı Algılanan & Çevrilen Metin";

        // Sekme 2: Görünüm
        StyleLivePreviewLabel.Text = en ? "LIVE PREVIEW" : "CANLI ÖNİZLEME";
        PresetLabelText.Text = en ? "Preset Style:" : "Hazır Stil:";
        SaveProfileBtn.Content = en ? "Save" : "Kaydet";
        SaveAsProfileBtn.Content = en ? "Save As" : "Farklı Kaydet";
        RenameProfileBtn.Content = en ? "Rename" : "Yeniden Adlandır";
        DeleteProfileBtn.Content = en ? "Delete" : "Sil";
        ExportJsonBtn.Content = en ? "Export JSON" : "JSON Dışa Aktar";
        ImportJsonBtn.Content = en ? "Import JSON" : "JSON İçe Aktar";
        BasicStyleHeaderText.Text = en ? "Basic Style Settings (Live Preview)" : "Temel Stil Ayarları (Anında Yansır)";
        FontFamilyLabelText.Text = en ? "1. Font Family (Search & Pick)" : "1. Yazı Tipi (Ara ve Seç)";
        FontSearchBox.ToolTip = en ? "Search fonts" : "Yazı tipi ara";
        TextColorLabelText.Text = en ? "3. Text Color" : "3. Yazı Rengi";
        BgColorLabelText.Text = en ? "4. Background Color" : "4. Arka Plan Rengi";
        PositionLabelText.Text = en ? "6. Position" : "6. Konum";
        AdvancedStyleExpander.Header = en
            ? "Advanced Style (Outline, Shadow, Background Mode, Layout, Behavior, Original Text)"
            : "Gelişmiş Stil (Kontur, Gölge, Arka Plan Modu, Yerleşim, Davranış, Orijinal Metin)";
        StyleFontDetailsHeaderText.Text = en ? "Typography" : "Yazı Ayrıntıları";
        FontWeightLabelText.Text = en ? "Weight" : "Kalınlık";
        TextAlignLabelText.Text = en ? "Alignment" : "Hizalama";
        ItalicCheck.Content = en ? "Italic" : "İtalik";
        UppercaseCheck.Content = en ? "UPPERCASE" : "BÜYÜK HARF";
        StyleColorOutlineHeaderText.Text = en ? "Color, Outline & Shadow" : "Renk, Kontur ve Gölge";
        OutlineColorLabelText.Text = en ? "Outline Color" : "Kontur Rengi";
        ShadowColorLabelText.Text = en ? "Shadow Color" : "Gölge Rengi";
        StyleBgBorderHeaderText.Text = en ? "Background & Border" : "Arka Plan ve Kenarlık";
        BgModeLabelText.Text = en ? "Background Mode" : "Arka Plan Modu";
        BorderColorLabelText.Text = en ? "Border Color" : "Kenarlık Rengi";
        StyleLayoutHeaderText.Text = en ? "Layout & Overflow" : "Yerleşim ve Taşma";
        OverflowLabelText.Text = en ? "Overflow Mode" : "Taşma Modu";
        StyleBehaviorHeaderText.Text = en ? "Behavior & Original Text" : "Davranış ve Orijinal Metin";
        AnimLabelText.Text = en ? "Animation" : "Animasyon";
        ShowOriginalCheck.Content = en ? "Show original source text in a smaller secondary line" : "Orijinal kaynak metni küçük ve ayrı stille göster";
        OrigColorLabelText.Text = en ? "Original Color" : "Orijinal Renk";
        OrigItalicCheck.Content = en ? "Original Italic" : "Orijinal İtalik";

        LocalizeComboItems(PositionPresetCombo, en
            ? new Dictionary<string, string> { ["BottomCenter"] = "Bottom Center", ["TopCenter"] = "Top Center", ["Center"] = "Center", ["BottomLeft"] = "Bottom Left", ["BottomRight"] = "Bottom Right", ["TopLeft"] = "Top Left", ["TopRight"] = "Top Right" }
            : new Dictionary<string, string> { ["BottomCenter"] = "Alt Orta", ["TopCenter"] = "Üst Orta", ["Center"] = "Orta", ["BottomLeft"] = "Alt Sol", ["BottomRight"] = "Alt Sağ", ["TopLeft"] = "Üst Sol", ["TopRight"] = "Üst Sağ" });
        LocalizeComboItems(FontWeightCombo, en
            ? new Dictionary<string, string> { ["300"] = "Light (300)", ["400"] = "Regular (400)", ["500"] = "Medium (500)", ["600"] = "SemiBold (600)", ["700"] = "Bold (700)", ["800"] = "ExtraBold (800)", ["900"] = "Black (900)" }
            : new Dictionary<string, string> { ["300"] = "İnce (300)", ["400"] = "Normal (400)", ["500"] = "Orta (500)", ["600"] = "Yarı Kalın (600)", ["700"] = "Kalın (700)", ["800"] = "Çok Kalın (800)", ["900"] = "Siyah (900)" });
        LocalizeComboItems(TextAlignCombo, en
            ? new Dictionary<string, string> { ["Center"] = "Center", ["Left"] = "Left", ["Right"] = "Right" }
            : new Dictionary<string, string> { ["Center"] = "Orta", ["Left"] = "Sol", ["Right"] = "Sağ" });
        LocalizeComboItems(BgModeCombo, en
            ? new Dictionary<string, string> { ["None"] = "None", ["PerLineBox"] = "Per-Line Box", ["SingleBlock"] = "Single Block", ["FullWidthBar"] = "Full-Width Bar" }
            : new Dictionary<string, string> { ["None"] = "Kapalı", ["PerLineBox"] = "Satır Başına Kutu", ["SingleBlock"] = "Tek Blok", ["FullWidthBar"] = "Tam Genişlik Şerit" });
        LocalizeComboItems(OverflowCombo, en
            ? new Dictionary<string, string> { ["ShrinkToFit"] = "Shrink to Fit", ["Clip"] = "Clip" }
            : new Dictionary<string, string> { ["ShrinkToFit"] = "Küçült", ["Clip"] = "Kes" });
        LocalizeComboItems(AnimTypeCombo, en
            ? new Dictionary<string, string> { ["None"] = "None", ["Fade"] = "Fade", ["Slide"] = "Slide" }
            : new Dictionary<string, string> { ["None"] = "Yok", ["Fade"] = "Solma", ["Slide"] = "Kayma" });

        // Sekme 3: OBS
        ObsPrimaryHeaderText.Text = en
            ? "Primary Method: OBS Browser Source (Transparent Background, Live SSE)"
            : "Birincil Yöntem: OBS Tarayıcı Kaynağı (Şeffaf Arka Plan, Anlık SSE)";
        CopyObsUrlBtn.Content = en ? "Copy" : "Kopyala";
        OpenObsBrowserBtn.Content = en ? "Open in Browser" : "Tarayıcıda Aç";
        ObsSetupTitleText.Text = en ? "OBS Setup in 4 Steps:" : "4 Adımda OBS Kurulumu:";
        ObsSetupStepsText.Text = en
            ? "1. In OBS Studio Sources, click '+' (Add Source).\n2. Choose 'Browser' and click 'OK'.\n3. Paste the URL above into the URL field.\n4. Match Width / Height to your OBS canvas size (e.g. 1920 × 1080)."
            : "1. OBS Studio'da Kaynaklar bölümünden '+' (Kaynak Ekle) düğmesine basın.\n2. 'Tarayıcı' (Browser) kaynağını seçip 'Tamam' deyin.\n3. URL kutusuna yukarıdaki adresi yapıştırın.\n4. Genişlik / Yükseklik değerlerini OBS yayın tuvalinizin boyutuyla aynı yapın (ör. 1920 × 1080).";
        ObsSetupTipText.Text = en
            ? "Tip: If capture card video lags behind subtitles, add a 'Render Delay' filter to your video source in OBS."
            : "İpucu: Capture card gecikmesi nedeniyle görüntü altyazıdan geç geliyorsa OBS'te video yakalama kaynağınıza 'Render Delay' (İşleme Gecikmesi) filtresi ekleyebilirsiniz.";
        ObsScreenMonitorHeaderText.Text = en
            ? "On-Screen Overlay (For Viewing on Your Own Monitor)"
            : "Ekran Altyazısı (Kendi Monitörünüzde İzleme İçin)";
        ShowScreenOverlayCheck.Content = en
            ? "Show transparent on-screen subtitle overlay (clicks pass through to the game)"
            : "Ekranda şeffaf altyazı katmanını göster (tıklamalar oyuna geçer)";
        ObsChromaHeaderText.Text = en
            ? "Fallback Method: OBS Window (Window Capture + Chroma Key)"
            : "Yedek Yöntem: OBS Penceresi (Pencere Yakalama + Chroma Key)";
        ShowChromaWindowCheck.Content = en
            ? "Open solid-color OBS Chroma Key window"
            : "Düz renkli OBS Chroma Key penceresini aç";
        ObsChromaColorLabelText.Text = en ? "Chroma Background Color:" : "Chroma Arka Plan Rengi:";
        ObsDropFreeHeaderText.Text = en
            ? "RTX 3080 10 GB / Drop-Free 1080p Streaming Checklist"
            : "RTX 3080 10 GB / Dropsuz 1080p Yayın Kontrol Listesi";
        ObsDropFreeChecklistText.Text = en
            ? "1. Run OBS Studio as Administrator (raises OBS GPU scheduling priority).\n2. OBS Output → Encoder: NVIDIA NVENC (Preset P5), keep 'Look-ahead' and 'Psycho Visual Tuning' OFF (leaves CUDA cores to the LLM).\n3. NVIDIA Control Panel → Manage 3D Settings → Program Settings → llama-server.exe → 'CUDA - Sysmem Fallback Policy: Prefer No Sysmem Fallback'.\n4. Click 'Start' in bitiq BEFORE going live to pre-load Hy-MT2 7B Q6_K into VRAM (never swap models mid-stream)."
            : "1. OBS Studio'yu Yönetici Olarak çalıştırın (OBS'in GPU zamanlama önceliğini artırır).\n2. OBS Çıkış → Kodlayıcı: NVIDIA NVENC (Preset P5), 'Look-ahead' ve 'Psycho Visual Tuning' KAPALI (CUDA çekirdeklerini LLM'e bırakır).\n3. NVIDIA Denetim Masası → 3D Ayarları → Program Ayarları → llama-server.exe → 'CUDA - Sysmem Fallback Policy: Prefer No Sysmem Fallback'.\n4. Yayına başlamadan önce bitiq'te 'Başlat'a basıp Hy-MT2 7B Q6_K modelini VRAM'e ön-yükleyin (yayın ortasında model değiştirmeyin).";

        // Sekme 4: Gelişmiş & Tanı
        AdvLocalHeaderText.Text = en ? "Local Model & Latency Settings" : "Yerel Model ve Gecikme Ayarları";
        AdvModelLabelText.Text = en ? "Local Translation Model" : "Yerel Çeviri Modeli";
        AdvQuantLabelText.Text = en ? "Model Quantization (Quality ↔ Speed)" : "Model Niceleme (Kalite ↔ Hız)";
        AdvBackendLabelText.Text = en ? "GPU / Backend" : "GPU / Arka Uç";
        AdvStabilityLabelText.Text = en ? "Speed ↔ Stability (Frames)" : "Hız ↔ Kararlılık (Kare)";
        AdvOcrEngineLabelText.Text = en ? "OCR Engine" : "OCR Motoru";
        AdvThreadsLabelText.Text = en ? "CPU Thread Count" : "CPU Thread Sayısı";
        AdvContextLinesLabelText.Text = en ? "Context History (Lines)" : "Bağlam Geçmişi (Replik)";
        AdvContextIdleLabelText.Text = en ? "Context Idle Reset" : "Bağlam Boşta Sıfırlama";
        LocalizeComboItems(LocalModelCombo, en
            ? new Dictionary<string, string> { ["HyMt2_7B"] = "Hy-MT2 7B (Q6_K — Context-Aware, Recommended)", ["TranslateGemma4B"] = "TranslateGemma 4B (Fast / Context-Free)" }
            : new Dictionary<string, string> { ["HyMt2_7B"] = "Hy-MT2 7B (Q6_K — Bağlamlı, Önerilen)", ["TranslateGemma4B"] = "TranslateGemma 4B (Hızlı / Bağlamsız)" });
        LocalizeComboItems(OcrEngineCombo, en
            ? new Dictionary<string, string> { ["WindowsOcr"] = "Windows OCR (Built-in)", ["OneOcr"] = "OneOCR (Win11 Smart ONNX • Multi-Lang)" }
            : new Dictionary<string, string> { ["WindowsOcr"] = "Windows OCR (Yerleşik)", ["OneOcr"] = "OneOCR (Win11 Akıllı ONNX • Çoklu Dil)" });
        PopulateQuantComboForCurrentModel();
        LocalizeComboItems(ContextMaxLinesCombo, en
            ? new Dictionary<string, string> { ["4"] = "4 Lines (Light)", ["6"] = "6 Lines", ["8"] = "8 Lines (Recommended)", ["12"] = "12 Lines (Long)" }
            : new Dictionary<string, string> { ["4"] = "4 Replik (Hafif)", ["6"] = "6 Replik", ["8"] = "8 Replik (Önerilen)", ["12"] = "12 Replik (Uzun)" });
        LocalizeComboItems(ContextIdleResetCombo, en
            ? new Dictionary<string, string> { ["45"] = "45 s", ["90"] = "90 s (Recommended)", ["180"] = "180 s", ["300"] = "300 s" }
            : new Dictionary<string, string> { ["45"] = "45 sn", ["90"] = "90 sn (Önerilen)", ["180"] = "180 sn", ["300"] = "300 sn" });
        LocalizeComboItems(BackendCombo, en
            ? new Dictionary<string, string> { ["Auto"] = "Auto (CUDA → Vulkan → CPU)", ["Cuda"] = "CUDA", ["Vulkan"] = "Vulkan (Fallback)", ["Cpu"] = "CPU (Fallback)" }
            : new Dictionary<string, string> { ["Auto"] = "Otomatik (CUDA → Vulkan → CPU)", ["Cuda"] = "CUDA", ["Vulkan"] = "Vulkan (Yedek)", ["Cpu"] = "CPU (Yedek)" });
        LocalizeComboItems(StabilityFramesCombo, en
            ? new Dictionary<string, string> { ["1"] = "1 Frame (Fastest)", ["2"] = "2 Frames (Balanced)", ["3"] = "3 Frames (Most Stable)" }
            : new Dictionary<string, string> { ["1"] = "1 Kare (En Hızlı)", ["2"] = "2 Kare (Dengeli)", ["3"] = "3 Kare (En Kararlı)" });

        StreamingCheck.Content = en ? "Stream text with typewriter effect" : "Daktilo (typewriter) efektiyle akışlı göster";
        PreprocessCheck.Content = en ? "Smart Background Cleanup (recommended for outline-less subtitles)" : "Akıllı Arka Plan Temizleme (Arka plansız altyazılar için önerilen)";
        MergeIncompleteAdvCheck.Content = en ? "Merge incomplete sentences before translating" : "Bitmemiş cümleleri birleştirerek çevir";
        SimulateVramCheck.Content = en ? "Simulate VRAM Limit (emulate a smaller GPU VRAM cap)" : "VRAM Simüle Et (Farklı ekran kartı limitini test et)";
        OpenWizardBtn.Content = en ? "Open Setup Wizard" : "Kurulum Sihirbazını Aç";
        OpenDataFolderBtn.Content = en ? "Open Settings & Model Folder" : "Ayar & Model Klasörünü Aç";
        DiagHeaderText.Text = en ? "Diagnostics (Last 50 Lines Avg & P95)" : "Tanı (Son 50 Satır Ortalama ve P95 Süreleri)";
        ClearDiagnosticsBtn.Content = en ? "Clear" : "Temizle";

        ColDiagEngine.Header = en ? "Engine" : "Motor";
        ColDiagSource.Header = en ? "Source" : "Kaynak";
        ColDiagCache.Header = en ? "Cache" : "Önbellek";
        ColDiagCapture.Header = en ? "Capture (ms)" : "Yakalama (ms)";
        ColDiagOcr.Header = "OCR (ms)";
        ColDiagFirstToken.Header = en ? "1st Token (ms)" : "1. Token (ms)";
        ColDiagTranslate.Header = en ? "Translate (ms)" : "Çeviri (ms)";
        ColDiagCachedTok.Header = en ? "KV Hit" : "KV İsabet";
        ColDiagTokSec.Header = "tok/s";
        ColDiagRender.Header = en ? "Render (ms)" : "Gösterim (ms)";
        ColDiagTotal.Header = en ? "Total (ms)" : "Toplam (ms)";

        // Sekme 5: Performans
        PerfNoticeText.Text = en
            ? "ℹ Zero Background Overhead: Metrics are sampled every 1.5s from in-process Windows & NVML APIs ONLY while this Performance tab is visible. Switching away stops the timer completely (0% extra CPU)."
            : "ℹ Sıfır Arka Plan Yükü: Ölçümler yalnızca bu 'Performans' sekmesi açıkken her 1.5 saniyede bir doğrudan süreç içi (in-process) Windows & NVML API'lerinden okunur. Başka sekmeye geçildiğinde ölçüm zamanlayıcısı tamamen durdurulur (%0 ekstra CPU).";
        PerfCpuCardHeaderText.Text = en ? "CPU Usage" : "CPU Kullanımı";
        PerfRamCardHeaderText.Text = en ? "RAM (Memory) Usage" : "RAM (Bellek) Kullanımı";
        PerfGpuCardHeaderText.Text = en ? "GPU & VRAM Usage" : "GPU & VRAM Kullanımı";
        PerfLlamaStatusHeaderText.Text = en ? "Local Model Process Status (llama-server)" : "Yerel Model Süreç Durumu (llama-server)";
        UnloadLlamaBtn.Content = en ? "🧹 Unload Local Model from Memory" : "🧹 Yerel Modeli Bellekten Boşalt";

        // Dinamik içerikler
        UpdateStyleSliderLabels(_settings.ActiveStyle);
        UpdateDebounceLabel();
        UpdateOcrConfidenceUi();
        UpdateSimulateVramUi();
        RefreshLanguageCombos();
        UpdateLanguagePairLabels();
        if (!_isInstallingOcr)
        {
            SourceOcrPackInfoText.Foreground = BrushInfoMuted;
            SourceOcrPackInfoText.Text = en
                ? $"Active OCR language: {LanguageCatalog.GetShortName(_settings.SourceLanguage, true)}. Languages marked '⬇ Install' become selectable once Windows installs the OCR pack."
                : $"Aktif OCR dili: {LanguageCatalog.GetShortName(_settings.SourceLanguage, false)}. Yanında '⬇ İndir' görünen diller Windows OCR paketi kurulduktan sonra aktif olur.";
        }
        RefreshCaptureTargetList();
        UpdateRegionSummary();
        CheckEnvironmentWarnings();
        RefreshDiagnosticsUi();

        if (Equals(MainTabs.SelectedItem, PerformanceTab))
        {
            RefreshPerformanceUi();
        }
        else
        {
            PerfAppCpuText.Text = en ? "bitiq (UI + OCR): 0.0%" : "bitiq (Arayüz + OCR): %0.0";
            PerfLlamaCpuText.Text = en ? "llama-server (Model): Off" : "llama-server (Model): Kapalı";
            PerfAppRamText.Text = en ? "bitiq (UI + OCR): 0 MB" : "bitiq (Arayüz + OCR): 0 MB";
            PerfLlamaRamText.Text = en ? "llama-server (Model): Off" : "llama-server (Model): Kapalı";
            PerfEngineStatusText.Text = en ? "Status: Off (consuming zero RAM/VRAM)" : "Durum: Kapalı (RAM ve VRAM tüketmiyor)";
        }
    }

    private void LocalizeComboItems(ComboBox combo, IReadOnlyDictionary<string, string> labelsByTag)
    {
        _uiSuppressCount++;
        try
        {
            int selectedIdx = combo.SelectedIndex;
            foreach (var item in combo.Items.OfType<ComboBoxItem>())
            {
                string tag = item.Tag?.ToString() ?? string.Empty;
                if (labelsByTag.TryGetValue(tag, out string? label))
                    item.Content = label;
            }
            if (selectedIdx >= 0)
            {
                combo.SelectedIndex = -1;
                combo.SelectedIndex = selectedIdx;
            }
        }
        finally
        {
            _uiSuppressCount--;
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

    // ==================== BAĞLAM (CONTEXT) VE OYUN PROFİLİ YÖNETİMİ ====================

    private sealed class ActiveGameProfileOption
    {
        public string? Id { get; init; }
        public string Label { get; init; } = string.Empty;
        public override string ToString() => Label;
    }

    private void UpdateLocalEngineCardLabels()
    {
        bool en = IsEnglishUi;
        if (_settings.LocalModel == LocalModelKind.HyMt2_7B)
        {
            EngineLocalTitleText.Text = en
                ? "Local AI (Hy-MT2 7B Q6_K): offline, context-aware"
                : "Yerel AI (Hy-MT2 7B Q6_K): internetsiz, bağlamlı";
            EngineLocalDescText.Text = en
                ? "Runs on RTX GPU (~5.74 GB). Uses recent dialogue, characters & glossary for natural subtitles."
                : "RTX GPU üzerinde çalışır (~5.74 GB). Önceki replikleri, karakterleri ve sözlüğü bağlam olarak kullanır.";
        }
        else
        {
            EngineLocalTitleText.Text = en
                ? "Local AI (TranslateGemma 4B): offline, fast"
                : "Yerel AI (TranslateGemma 4B): internetsiz, hızlı";
            EngineLocalDescText.Text = en
                ? "Runs on RTX GPU. Lightweight sentence-by-sentence translation without context memory."
                : "RTX GPU üzerinde çalışır. Bağlamsız, hafif ve hızlı cümle bazlı çeviri yapar.";
        }
    }

    private void UpdateContextMemoryBadge(int count)
    {
        bool en = IsEnglishUi;
        ContextMemoryBadgeText.Text = en
            ? $"In memory: {count}/{_settings.ContextMaxLines} lines"
            : $"Bellekte: {count}/{_settings.ContextMaxLines} replik";
    }

    private void UpdateContextControlsState()
    {
        bool en = IsEnglishUi;
        bool isHyMt2 = _settings.LocalModel == LocalModelKind.HyMt2_7B;
        bool contextActive = _settings.ContextEnabled && isHyMt2;

        ContextEnabledCheck.IsEnabled = isHyMt2;
        ActiveGameProfileCombo.IsEnabled = contextActive;
        ResetContextBtn.IsEnabled = contextActive;

        if (!isHyMt2)
        {
            ProfileModelNoticeBanner.Background = new SolidColorBrush(Color.FromRgb(69, 26, 3));
            ProfileModelNoticeBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            ProfileModelNoticeText.Foreground = BrushInfoWarn;
            ProfileModelNoticeText.Text = en
                ? "⚠ TranslateGemma 4B is currently selected and does not use context/profiles. Switch to Hy-MT2 7B (Q6_K) in Advanced tab to enable context-aware translation."
                : "⚠ Şu anda TranslateGemma 4B seçili olduğu için bağlam/profil kullanılmaz. Bağlamlı çeviri için Gelişmiş sekmesinden Hy-MT2 7B (Q6_K) modelini seçin.";
        }
        else
        {
            ProfileModelNoticeBanner.Background = new SolidColorBrush(Color.FromRgb(15, 23, 42));
            ProfileModelNoticeBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85));
            ProfileModelNoticeText.Foreground = BrushInfoMuted;
            ProfileModelNoticeText.Text = en
                ? "ℹ The active game profile and sliding dialogue history are automatically injected into Hy-MT2 7B (Q6_K) via KV prefix caching."
                : "ℹ Aktif oyun profili ve kayan diyalog geçmişi Hy-MT2 7B (Q6_K) seçiliyken ön ek KV önbelleğiyle otomatik olarak modele iletilir.";
        }
    }

    private void ReloadGameProfiles()
    {
        _gameProfiles = _gameProfileService.LoadAll();
        RefreshCachedActiveProfile();
        RefreshGameProfileDropdowns();

        if (_editingProfile is null || !_gameProfiles.Any(p => p.Id == _editingProfile.Id))
        {
            var initial = _gameProfiles.FirstOrDefault(p => p.Id == _settings.ActiveGameProfileId)
                ?? _gameProfiles.FirstOrDefault();
            LoadGameProfileIntoEditor(initial);
        }
    }

    private void RefreshCachedActiveProfile()
    {
        if (string.IsNullOrWhiteSpace(_settings.ActiveGameProfileId))
        {
            _cachedActiveProfile = null;
            return;
        }

        var match = _gameProfiles.FirstOrDefault(p => string.Equals(p.Id, _settings.ActiveGameProfileId, StringComparison.Ordinal));
        _cachedActiveProfile = match?.Clone();
    }

    private void RefreshGameProfileDropdowns()
    {
        bool en = IsEnglishUi;
        _uiSuppressCount++;
        try
        {
            var activeItems = new List<ActiveGameProfileOption>
            {
                new()
                {
                    Id = null,
                    Label = en ? "(No Profile — Dialogue History Only)" : "(Profil Yok — Yalnızca Diyalog Geçmişi)"
                }
            };
            foreach (var p in _gameProfiles)
            {
                activeItems.Add(new ActiveGameProfileOption
                {
                    Id = p.Id,
                    Label = string.IsNullOrWhiteSpace(p.Title) ? p.Id : p.Title
                });
            }

            ActiveGameProfileCombo.ItemsSource = activeItems;
            ActiveGameProfileCombo.SelectedItem = activeItems.FirstOrDefault(x =>
                string.Equals(x.Id, _settings.ActiveGameProfileId, StringComparison.Ordinal)) ?? activeItems[0];

            var editItems = _gameProfiles.ToList();
            GameProfileEditCombo.ItemsSource = null;
            GameProfileEditCombo.ItemsSource = editItems;
            GameProfileEditCombo.DisplayMemberPath = nameof(GameProfile.Title);
            if (_editingProfile is not null)
            {
                var editMatch = editItems.FirstOrDefault(p => string.Equals(p.Id, _editingProfile.Id, StringComparison.Ordinal));
                if (editMatch is not null)
                    GameProfileEditCombo.SelectedItem = editMatch;
            }
        }
        finally
        {
            _uiSuppressCount--;
        }
    }

    private void LoadGameProfileIntoEditor(GameProfile? profile)
    {
        _uiSuppressCount++;
        try
        {
            _editingProfile = profile?.Clone() ?? new GameProfile();
            GameTitleBox.Text = _editingProfile.Title;
            GameSynopsisBox.Text = _editingProfile.Synopsis;
            GameToneBox.Text = _editingProfile.ToneNote;

            _editingCharacters.Clear();
            foreach (var c in _editingProfile.Characters)
                _editingCharacters.Add(c.Clone());

            _editingGlossary.Clear();
            foreach (var g in _editingProfile.Glossary)
                _editingGlossary.Add(g.Clone());
        }
        finally
        {
            _uiSuppressCount--;
        }

        UpdateProfilePromptPreview();
    }

    private GameProfile CollectEditorProfile()
    {
        CharactersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        GlossaryGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var p = _editingProfile is not null ? _editingProfile.Clone() : new GameProfile();
        if (string.IsNullOrWhiteSpace(p.Id))
            p.Id = Guid.NewGuid().ToString("N");

        p.Title = GameTitleBox.Text.Trim();
        p.Synopsis = GameSynopsisBox.Text.Trim();
        p.ToneNote = GameToneBox.Text.Trim();
        p.Characters = _editingCharacters
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new CharacterEntry(c.Name.Trim(), c.Note?.Trim()))
            .ToList();
        p.Glossary = _editingGlossary
            .Where(g => !string.IsNullOrWhiteSpace(g.Source) && !string.IsNullOrWhiteSpace(g.Target))
            .Select(g => new GlossaryEntry(g.Source.Trim(), g.Target.Trim()))
            .ToList();
        return p;
    }

    private void UpdateProfilePromptPreview()
    {
        var p = new GameProfile
        {
            Id = _editingProfile?.Id ?? "preview",
            Title = GameTitleBox.Text.Trim(),
            Synopsis = GameSynopsisBox.Text.Trim(),
            ToneNote = GameToneBox.Text.Trim(),
            Characters = _editingCharacters.Where(c => !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Clone()).ToList(),
            Glossary = _editingGlossary.Where(g => !string.IsNullOrWhiteSpace(g.Source) && !string.IsNullOrWhiteSpace(g.Target)).Select(g => g.Clone()).ToList()
        };

        string preview = HyMt2PromptBuilder.BuildBackgroundPreview(p, _settings.TargetLanguage);
        ProfilePromptPreviewBox.Text = string.IsNullOrWhiteSpace(preview)
            ? (IsEnglishUi ? "(Fill in fields above to preview the context injected into Hy-MT2)" : "(Hy-MT2'ye iletilecek bağlamı görmek için yukarıdaki alanları doldurun)")
            : preview;
    }

    private void ContextEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (IsUiSuppressed)
            return;

        _settings.ContextEnabled = ContextEnabledCheck.IsChecked == true;
        _settingsService.Save(_settings);
        UpdateContextControlsState();
        _pipeline.ApplyLiveSettings(_settings);
    }

    private void ActiveGameProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsUiSuppressed || ActiveGameProfileCombo.SelectedItem is not ActiveGameProfileOption opt)
            return;

        _settings.ActiveGameProfileId = opt.Id;
        _settingsService.Save(_settings);
        RefreshCachedActiveProfile();
        _pipeline.ApplyLiveSettings(_settings);
    }

    private void EditGameProfileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_settings.ActiveGameProfileId))
        {
            var match = _gameProfiles.FirstOrDefault(p => p.Id == _settings.ActiveGameProfileId);
            if (match is not null)
                LoadGameProfileIntoEditor(match);
        }
        MainTabs.SelectedItem = MainTabProfiles;
    }

    private void ResetContextBtn_Click(object sender, RoutedEventArgs e)
    {
        _pipeline.ResetContext();
        StatusDetailText.Text = IsEnglishUi
            ? "Dialogue context memory cleared."
            : "Diyalog bağlam geçmişi sıfırlandı.";
    }

    private void GameProfileEditCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsUiSuppressed || GameProfileEditCombo.SelectedItem is not GameProfile selected)
            return;

        LoadGameProfileIntoEditor(selected);
    }

    private void NewGameProfile_Click(object sender, RoutedEventArgs e)
    {
        LoadGameProfileIntoEditor(new GameProfile
        {
            Title = IsEnglishUi ? "New Game" : "Yeni Oyun"
        });
        GameTitleBox.Focus();
        GameTitleBox.SelectAll();
    }

    private void SaveGameProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = CollectEditorProfile();
        if (string.IsNullOrWhiteSpace(profile.Title))
        {
            MessageBox.Show(
                IsEnglishUi ? "Please enter a game title before saving." : "Kaydetmeden önce lütfen bir oyun adı girin.",
                IsEnglishUi ? "Game Profile" : "Oyun Profili",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _gameProfileService.Save(profile);
        _settings.ActiveGameProfileId = profile.Id;
        _settingsService.Save(_settings);

        _editingProfile = profile.Clone();
        ReloadGameProfiles();
        LoadGameProfileIntoEditor(profile);
        _pipeline.ApplyLiveSettings(_settings);

        StatusDetailText.Text = IsEnglishUi
            ? $"Game profile saved and activated: {profile.Title}"
            : $"Oyun profili kaydedildi ve aktifleştirildi: {profile.Title}";
    }

    private void DeleteGameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_editingProfile is null || !_gameProfiles.Any(p => p.Id == _editingProfile.Id))
        {
            LoadGameProfileIntoEditor(null);
            return;
        }

        string idToDelete = _editingProfile.Id;
        _gameProfileService.Delete(idToDelete);
        if (string.Equals(_settings.ActiveGameProfileId, idToDelete, StringComparison.Ordinal))
        {
            _settings.ActiveGameProfileId = null;
            _settingsService.Save(_settings);
        }

        _editingProfile = null;
        ReloadGameProfiles();
        _pipeline.ApplyLiveSettings(_settings);
    }

    private void ExportGameProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = CollectEditorProfile();
        string safeName = string.IsNullOrWhiteSpace(profile.Title) ? "game_profile" : profile.Title.Replace(' ', '_');
        var sfd = new SaveFileDialog
        {
            Filter = "JSON Oyun Profili (*.json)|*.json",
            FileName = $"{safeName}.json"
        };
        if (sfd.ShowDialog() == true)
        {
            _gameProfileService.ExportToFile(profile, sfd.FileName);
        }
    }

    private void ImportGameProfile_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Filter = "JSON Oyun Profili (*.json)|*.json"
        };
        if (ofd.ShowDialog() == true)
        {
            try
            {
                var imported = _gameProfileService.ImportFromFile(ofd.FileName);
                _settings.ActiveGameProfileId = imported.Id;
                _settingsService.Save(_settings);
                _editingProfile = imported.Clone();
                ReloadGameProfiles();
                LoadGameProfileIntoEditor(imported);
                _pipeline.ApplyLiveSettings(_settings);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    IsEnglishUi ? $"Could not import profile: {ex.Message}" : $"Profil içe aktarılamadı: {ex.Message}",
                    IsEnglishUi ? "Error" : "Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private void AddCharacterRow_Click(object sender, RoutedEventArgs e)
    {
        var item = IsEnglishUi
            ? new CharacterEntry("Character", "note")
            : new CharacterEntry("Karakter", "not");
        _editingCharacters.Add(item);
        CharactersGrid.SelectedItem = item;
        UpdateProfilePromptPreview();
    }

    private void RemoveCharacterRow_Click(object sender, RoutedEventArgs e)
    {
        if (CharactersGrid.SelectedItem is CharacterEntry item)
        {
            _editingCharacters.Remove(item);
            UpdateProfilePromptPreview();
        }
    }

    private void AddGlossaryRow_Click(object sender, RoutedEventArgs e)
    {
        var item = IsEnglishUi
            ? new GlossaryEntry("Source term", "Translation")
            : new GlossaryEntry("Terim", "Çevirisi");
        _editingGlossary.Add(item);
        GlossaryGrid.SelectedItem = item;
        UpdateProfilePromptPreview();
    }

    private void RemoveGlossaryRow_Click(object sender, RoutedEventArgs e)
    {
        if (GlossaryGrid.SelectedItem is GlossaryEntry item)
        {
            _editingGlossary.Remove(item);
            UpdateProfilePromptPreview();
        }
    }

    private void ProfileField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsUiSuppressed)
            return;
        UpdateProfilePromptPreview();
    }

    private void ProfileGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(UpdateProfilePromptPreview);
    }
}
