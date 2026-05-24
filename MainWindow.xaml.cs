using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.ComponentModel;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using Drawing = System.Drawing;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Peek;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF Window lifetime cleanup is performed in OnClosed.")]
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by WPF from App.xaml StartupUri.")]
internal sealed partial class MainWindow : Window
{
    private const double CollapsedWidth = 106;
    private const double CollapsedHeight = 24;
    private const double FrameRevealHeight = 48;
    private const double FrameHorizontalNonCaptureWidth = 4;
    private const double FrameVerticalNonCaptureHeight = 44;
    private const int MaxCapturePixelWidth = 2560;
    private const int MaxCapturePixelHeight = 1440;
    private const long MaxCapturePixels = (long)MaxCapturePixelWidth * MaxCapturePixelHeight;
    private const double SkillCardStackedBreakpoint = 260;
    private const double SkillIconSize = 52;
    private const double StackedSkillIconSize = 40;
    private const double SkillBadgeHeight = 24;
    private const double SkillBadgeElementIconSize = 17;
    private const double SkillBadgeEnergyIconSize = 12;
    private const double WideSkillTitleFontSize = 18;
    private const double WideSkillDescriptionFontSize = 16;
    private const double WideSkillDescriptionLineHeight = 20;
    private const double StackedSkillTitleFontSize = 16;
    private const double StackedSkillDescriptionFontSize = 14;
    private const double StackedSkillDescriptionLineHeight = 18;
    private static readonly Color SkillBadgeNeutralBackground = Color.FromRgb(78, 78, 78);
    private const double MaxResultFontSize = 24;
    private const double MinResultFontSize = 10;
    private const double MaxResultLineGap = 12;
    private const double MinResultLineGap = 4;
    private static readonly Thickness MaxResultTextPadding = new(14, 12, 14, 10);
    private static readonly Thickness MinResultTextPadding = new(6, 6, 6, 4);
    private static readonly Thickness VisibleFrameBorderThickness = new(1);
    private static readonly IReadOnlyDictionary<string, Color> ElementColors = new Dictionary<string, Color>(StringComparer.Ordinal)
    {
        ["bug"] = Color.FromRgb(159, 207, 34),
        ["dark"] = Color.FromRgb(208, 71, 123),
        ["dragon"] = Color.FromRgb(233, 76, 98),
        ["electric"] = Color.FromRgb(231, 205, 13),
        ["fairy"] = Color.FromRgb(254, 129, 181),
        ["fighting"] = Color.FromRgb(254, 154, 59),
        ["fire"] = Color.FromRgb(224, 88, 34),
        ["flying"] = Color.FromRgb(65, 200, 203),
        ["ghost"] = Color.FromRgb(148, 70, 236),
        ["grass"] = Color.FromRgb(79, 189, 116),
        ["ground"] = Color.FromRgb(152, 125, 68),
        ["ice"] = Color.FromRgb(100, 174, 218),
        ["illusion"] = Color.FromRgb(157, 169, 255),
        ["light"] = Color.FromRgb(79, 193, 255),
        ["normal"] = Color.FromRgb(63, 137, 181),
        ["poison"] = Color.FromRgb(187, 98, 224),
        ["steel"] = Color.FromRgb(37, 185, 164),
        ["water"] = Color.FromRgb(99, 169, 254)
    };
    private const int WmNcHitTest = 0x0084;
    private const nint HtClient = 1;
    private const nint HtTransparent = -1;

    private AppConfig _config = AppConfigStore.Load();
    private CancellationTokenSource? _translationCancellation;
    private CancellationTokenSource? _skillCancellation;
    private Point? _dragStartCursor;
    private Point? _dragStartWindowPosition;
    private Point? _resizeStartCursor;
    private Size _resizeStartWindowSize;
    private bool _dragButtonDragged;
    private bool _resizeButtonDragged;
    private bool _resizeExpandedDuringInteraction;
    private bool _isTranslating;
    private bool _isCheckingSkills;
    private bool _isShowingSkillResult;
    private bool _isClosing;
    private SettingsWindow? _settingsWindow;
    private readonly DispatcherTimer _statusClearTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2.5)
    };
    private readonly Dictionary<System.Windows.Controls.Button, SearchButtonState> _searchButtons = [];
    private readonly DoubleAnimation _spinnerAnimation = new(0, 360, TimeSpan.FromMilliseconds(700))
    {
        RepeatBehavior = RepeatBehavior.Forever
    };

    public MainWindow()
    {
        InitializeComponent();
        AppLogger.IncludeSensitiveData = _config.DiagnosticsEnabled;
        if (!_config.DiagnosticsEnabled)
        {
            AppDataMaintenance.ClearSensitiveData();
        }

        _statusClearTimer.Tick += StatusClearTimer_Tick;
        AppLogger.Event("app_start", new
        {
            appDirectory = AppPaths.AppDirectory,
            targetLanguage = _config.TargetLanguage,
            diagnosticsEnabled = _config.DiagnosticsEnabled,
            targetGame = RocoGame.DisplayName
        });
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!AppConfig.HasGeminiApiKey(_config.ApiKey))
        {
            OpenSettings();
        }
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (HwndSource.FromHwnd(handle) is { } source)
        {
            source.AddHook(WindowMessageHook);
        }
    }

    private nint WindowMessageHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WmNcHitTest)
        {
            return 0;
        }

        var screenPoint = GetScreenPoint(lParam);
        var windowPoint = PointFromScreen(screenPoint);
        handled = true;

        return IsInteractivePoint(windowPoint) ? HtClient : HtTransparent;
    }

    private bool IsInteractivePoint(Point windowPoint)
    {
        return IsPointInside(DragButton, windowPoint) ||
            IsPointInside(TranslateButton, windowPoint) ||
            IsPointInside(SkillButton, windowPoint) ||
            IsPointInside(ClearButton, windowPoint) ||
            IsPointInside(ResizeRowButton, windowPoint) ||
            IsPointInside(ResizeCornerButton, windowPoint) ||
            IsPointInside(SearchButtonsPanel, windowPoint) ||
            IsPointInside(SkillResultPanel, windowPoint);
    }

    private bool IsPointInside(FrameworkElement element, Point windowPoint)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        var bounds = element.TransformToAncestor(this)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Contains(windowPoint);
    }

    private static Point GetScreenPoint(nint lParam)
    {
        var value = lParam.ToInt64();
        var x = (short)(value & 0xFFFF);
        var y = (short)((value >> 16) & 0xFFFF);
        return new Point(x, y);
    }

    private void ResultPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResultPanelClip();
        FitResultText();
    }

    private void UpdateResultPanelClip()
    {
        if (ResultPanel.ActualWidth <= 0 || ResultPanel.ActualHeight <= 0)
        {
            ResultPanel.Clip = null;
            return;
        }

        var radius = Math.Max(0, ResultPanel.CornerRadius.TopLeft);
        ResultPanel.Clip = new RectangleGeometry(
            new Rect(0, 0, ResultPanel.ActualWidth, ResultPanel.ActualHeight),
            radius,
            radius);
    }

    private void DragButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartCursor = GetScreenDipPoint(e.GetPosition(this));
        _dragStartWindowPosition = new Point(Left, Top);
        _dragButtonDragged = false;
        DragButton.CaptureMouse();
        e.Handled = true;
    }

    private void DragButton_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStartCursor is null ||
            _dragStartWindowPosition is null ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var cursor = GetScreenDipPoint(e.GetPosition(this));
        var delta = cursor - _dragStartCursor.Value;
        if (!_dragButtonDragged &&
            Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragButtonDragged = true;
        Left = _dragStartWindowPosition.Value.X + delta.X;
        Top = _dragStartWindowPosition.Value.Y + delta.Y;
        e.Handled = true;
    }

    private void DragButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStartCursor is null)
        {
            return;
        }

        DragButton.ReleaseMouseCapture();
        _dragStartCursor = null;
        _dragStartWindowPosition = null;
        _dragButtonDragged = false;
        e.Handled = true;
    }

    private void ResizeButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender == ResizeRowButton)
        {
            ResizeRowButton.Visibility = Visibility.Collapsed;
            ResizeCornerButton.Visibility = Visibility.Visible;
            StartCursorAnchoredResize(e);
            e.Handled = true;
            return;
        }

        StartCursorAnchoredResize(e);
        e.Handled = true;
    }

    private void StartCursorAnchoredResize(MouseButtonEventArgs e)
    {
        _resizeStartCursor = GetScreenDipPoint(e.GetPosition(this));
        _resizeStartWindowSize = new Size(Width, Height);
        _resizeExpandedDuringInteraction = FrameBorder.Visibility == Visibility.Visible;
        _resizeButtonDragged = false;
        ResizeCornerButton.CaptureMouse();
    }

    private void ResizeButton_MouseMove(object sender, MouseEventArgs e)
    {
        if (_resizeStartCursor is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var cursor = GetScreenDipPoint(e.GetPosition(this));
        var delta = cursor - _resizeStartCursor.Value;
        if (!_resizeButtonDragged &&
            Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _resizeButtonDragged = true;
        ApplyResizeDelta(delta);
        e.Handled = true;
    }

    private void ResizeButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizeStartCursor is null)
        {
            return;
        }

        var wasExpanded = _resizeExpandedDuringInteraction;
        FinishResizeInteraction();
        if (!wasExpanded)
        {
            CollapseFrame();
        }

        e.Handled = true;
    }

    private void ApplyResizeDelta(Vector delta)
    {
        var maxWindowSize = GetMaxCaptureWindowSize();
        var nextWidth = Math.Min(
            maxWindowSize.Width,
            Math.Max(MinWidth, _resizeStartWindowSize.Width + delta.X));
        var nextHeight = Math.Min(
            maxWindowSize.Height,
            Math.Max(MinHeight, _resizeStartWindowSize.Height + delta.Y));

        if (nextHeight <= FrameRevealHeight)
        {
            if (_resizeExpandedDuringInteraction)
            {
                CollapseFrame();
                FinishResizeInteraction();
            }

            return;
        }

        _resizeExpandedDuringInteraction = true;
        FrameBorder.Visibility = Visibility.Visible;
        if (!_isShowingSkillResult)
        {
            UseCaptureFramePresentation();
        }

        ResizeRowButton.Visibility = Visibility.Collapsed;
        ResizeCornerButton.Visibility = Visibility.Visible;
        Width = Math.Max(CollapsedWidth, nextWidth);
        Height = nextHeight;
        FitResultText();
    }

    private void CollapseFrame()
    {
        FrameBorder.Visibility = Visibility.Collapsed;
        UseCaptureFramePresentation();
        SkillResultPanel.Visibility = Visibility.Collapsed;
        SkillResultContent.Children.Clear();
        ResultPanel.Visibility = Visibility.Collapsed;
        _isShowingSkillResult = false;
        ClearButton.Visibility = Visibility.Collapsed;
        SetActionButtonsVisible(true);
        ClearSearchButtons();
        ResizeCornerButton.Visibility = Visibility.Collapsed;
        ResizeRowButton.Visibility = Visibility.Visible;
        Width = CollapsedWidth;
        Height = CollapsedHeight;
    }

    private void FinishResizeInteraction()
    {
        _resizeStartCursor = null;
        _resizeButtonDragged = false;
        _resizeExpandedDuringInteraction = false;

        if (ResizeCornerButton.IsMouseCaptured)
        {
            ResizeCornerButton.ReleaseMouseCapture();
        }
    }

    private Point GetScreenDipPoint(Point windowPoint)
    {
        var screenPoint = PointToScreen(windowPoint);
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } compositionTarget)
        {
            return screenPoint;
        }

        return compositionTarget.TransformFromDevice.Transform(screenPoint);
    }

    private Size GetMaxCaptureWindowSize()
    {
        var maxFrameSize = new Point(MaxCapturePixelWidth, MaxCapturePixelHeight);
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } compositionTarget)
        {
            maxFrameSize = compositionTarget.TransformFromDevice.Transform(maxFrameSize);
        }

        return new Size(
            Math.Max(
                CollapsedWidth,
                Math.Min(SystemParameters.VirtualScreenWidth, maxFrameSize.X + FrameHorizontalNonCaptureWidth)),
            Math.Max(
                MinHeight,
                Math.Min(SystemParameters.VirtualScreenHeight, maxFrameSize.Y + FrameVerticalNonCaptureHeight)));
    }

    private async void TranslateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isShowingSkillResult)
        {
            RestoreCaptureFrameFromSkillResult();
        }

        await TranslateCurrentAreaAsync().ConfigureAwait(true);
    }

    private async void SkillButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isShowingSkillResult)
        {
            RestoreCaptureFrameFromSkillResult();
        }

        await CheckVisibleSkillsAsync().ConfigureAwait(true);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTranslating)
        {
            AppLogger.Event("translate_cancel_requested", new { reason = "clear_button" });
            _translationCancellation?.Cancel();
        }

        if (_isCheckingSkills)
        {
            AppLogger.Event("skill_check_cancel_requested", new { reason = "clear_button" });
            _skillCancellation?.Cancel();
        }

        AppLogger.Event("result_clear", new { hadResult = ResultPanel.Visibility == Visibility.Visible });
        ClearResult();
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            !_searchButtons.TryGetValue(button, out var searchButton) ||
            string.IsNullOrWhiteSpace(searchButton.Url))
        {
            return;
        }

        AppLogger.SearchClick(new SearchClickLogEntry(
            DateTimeOffset.Now,
            searchButton.OperationId,
            searchButton.Index,
            searchButton.Query.Label,
            searchButton.Query.Query,
            searchButton.Query.Intent,
            RocoGame.DisplayName,
            searchButton.Url));
        OpenUrl(searchButton.Url);
    }

    private void SettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private void QuitMenu_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "This is the UI operation boundary; failures are logged and shown as a short status.")]
    private async Task TranslateCurrentAreaAsync()
    {
        if (_isTranslating || _isCheckingSkills)
        {
            return;
        }

        if (FrameBorder.Visibility != Visibility.Visible ||
            FrameBorder.ActualWidth < 8 ||
            FrameBorder.ActualHeight < 8)
        {
            AppLogger.Event("translate_skipped", new
            {
                reason = "frame_collapsed_or_too_small",
                frameWidth = FrameBorder.ActualWidth,
                frameHeight = FrameBorder.ActualHeight
            });
            return;
        }

        if (!AppConfig.HasGeminiApiKey(_config.ApiKey))
        {
            OpenSettings();
            if (!AppConfig.HasGeminiApiKey(_config.ApiKey))
            {
                AppLogger.Event("translate_skipped", new { reason = "api_key_required" });
                return;
            }
        }

        _translationCancellation?.Dispose();
        _translationCancellation = new CancellationTokenSource();
        var cancellationSource = _translationCancellation;
        var cancellationToken = cancellationSource.Token;
        var operationConfig = CloneConfig(_config);
        var operationId = Guid.NewGuid().ToString("N")[..12];
        var stopwatch = Stopwatch.StartNew();
        var captureWidth = 0;
        var captureHeight = 0;
        var model = AppConfig.DefaultModel;
        string? providerRequestId = null;
        var responseUsage = new TokenUsage(0, 0, 0);
        var usageTracked = false;
        var streamedTextShown = false;

        try
        {
            AppLogger.Event("translate_start", new
            {
                operationId,
                model,
                targetLanguage = operationConfig.TargetLanguage,
                RocoGame.DisplayName,
                windowLeft = Left,
                windowTop = Top,
                frameWidth = FrameBorder.ActualWidth,
                frameHeight = FrameBorder.ActualHeight
            });
            SetBusy(true);
            ClearStatus();

            using var bitmap = await CaptureFrameWithoutOverlayAsync(cancellationToken).ConfigureAwait(true);
            captureWidth = bitmap.Width;
            captureHeight = bitmap.Height;
            var capturePath = await SaveCaptureAsync(
                    operationId,
                    bitmap,
                    operationConfig.DiagnosticsEnabled,
                    FrameBorder.ActualWidth,
                    FrameBorder.ActualHeight,
                    cancellationToken)
                .ConfigureAwait(true);

            var result = await GeminiClient.TranslateImageToTextStreamingAsync(
                    bitmap,
                    operationConfig,
                    model,
                    operationId,
                    partialText => InvokeUiDuringOperation(
                        () =>
                        {
                            streamedTextShown = true;
                            SetStreamingResultText(partialText);
                        },
                        cancellationToken,
                        DispatcherPriority.Background),
                    cancellationToken)
                .ConfigureAwait(true);
            providerRequestId = result.ProviderRequestId;
            responseUsage = result.Usage;
            stopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_isClosing)
            {
                SetResultText(
                    operationId,
                    result.Text,
                    result.SearchQueries);
            }

            AppLogger.TextResult(new TextResultLogEntry(
                DateTimeOffset.Now,
                operationId,
                model,
                operationConfig.TargetLanguage,
                RocoGame.DisplayName,
                capturePath,
                result.Text,
                result.SearchQueries));
            if (!_isClosing)
            {
                ClearStatus();
            }

            TrackUsage(
                operationId,
                providerRequestId,
                true,
                captureWidth,
                captureHeight,
                stopwatch.ElapsedMilliseconds,
                responseUsage,
                model,
                operationConfig,
                RocoGame.DisplayName,
                null,
                null);
            usageTracked = true;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            AppLogger.Info($"operation={operationId} translate.cancelled");
            if (!usageTracked && providerRequestId is not null)
            {
                TrackUsage(
                    operationId,
                    providerRequestId,
                    false,
                    captureWidth,
                    captureHeight,
                    stopwatch.ElapsedMilliseconds,
                    responseUsage,
                    model,
                    operationConfig,
                    RocoGame.DisplayName,
                    nameof(OperationCanceledException),
                    "Cancelled");
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppLogger.Error("Translate failed.", ex);
            var shortError = ToShortUserError(ex);
            if (!_isClosing && streamedTextShown)
            {
                ClearFailedStreamingResult();
            }

            if (!_isClosing)
            {
                SetStatus(shortError);
            }

            AppLogger.Info($"operation={operationId} user_error={shortError}");
            TrackUsage(
                operationId,
                providerRequestId,
                false,
                captureWidth,
                captureHeight,
                stopwatch.ElapsedMilliseconds,
                responseUsage,
                model,
                operationConfig,
                RocoGame.DisplayName,
                ex.GetType().Name,
                shortError);
        }
        finally
        {
            if (!_isClosing)
            {
                SetBusy(false);
            }

            if (ReferenceEquals(_translationCancellation, cancellationSource))
            {
                _translationCancellation.Dispose();
                _translationCancellation = null;
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "This is the UI operation boundary; failures are logged and shown as a short status.")]
    private async Task CheckVisibleSkillsAsync()
    {
        if (_isTranslating || _isCheckingSkills)
        {
            return;
        }

        if (FrameBorder.Visibility != Visibility.Visible ||
            FrameBorder.ActualWidth < 8 ||
            FrameBorder.ActualHeight < 8)
        {
            AppLogger.Event("skill_check_skipped", new
            {
                reason = "frame_collapsed_or_too_small",
                frameWidth = FrameBorder.ActualWidth,
                frameHeight = FrameBorder.ActualHeight
            });
            return;
        }

        if (!AppConfig.HasGeminiApiKey(_config.ApiKey))
        {
            OpenSettings();
            if (!AppConfig.HasGeminiApiKey(_config.ApiKey))
            {
                AppLogger.Event("skill_check_skipped", new { reason = "api_key_required" });
                return;
            }
        }

        _skillCancellation?.Dispose();
        _skillCancellation = new CancellationTokenSource();
        var cancellationSource = _skillCancellation;
        var cancellationToken = cancellationSource.Token;
        var operationConfig = CloneConfig(_config);
        var targetGame = RocoGame.DisplayName;
        var operationId = Guid.NewGuid().ToString("N")[..12];
        var stopwatch = Stopwatch.StartNew();
        var captureWidth = 0;
        var captureHeight = 0;
        var model = AppConfig.DefaultModel;
        string? providerRequestId = null;
        var responseUsage = new TokenUsage(0, 0, 0);
        var usageTracked = false;

        try
        {
            AppLogger.Event("skill_check_start", new
            {
                operationId,
                model,
                targetLanguage = operationConfig.TargetLanguage,
                targetGame,
                windowLeft = Left,
                windowTop = Top,
                frameWidth = FrameBorder.ActualWidth,
                frameHeight = FrameBorder.ActualHeight
            });
            SetSkillBusy(true);
            ClearStatus();

            using var bitmap = await CaptureFrameWithoutOverlayAsync(cancellationToken).ConfigureAwait(true);
            captureWidth = bitmap.Width;
            captureHeight = bitmap.Height;
            var capturePath = await SaveCaptureAsync(
                    operationId,
                    bitmap,
                    operationConfig.DiagnosticsEnabled,
                    FrameBorder.ActualWidth,
                    FrameBorder.ActualHeight,
                    cancellationToken)
                .ConfigureAwait(true);

            var result = await GeminiClient.ExtractVisibleSkillNamesStreamingAsync(
                    bitmap,
                    operationConfig,
                    model,
                    operationId,
                    cancellationToken)
                .ConfigureAwait(true);
            providerRequestId = result.ProviderRequestId;
            responseUsage = result.Usage;
            stopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();

            var lookup = SkillDatabase.Lookup(result.SkillNames);
            if (!_isClosing)
            {
                SetSkillResult(lookup, operationConfig.TargetLanguage);
            }

            AppLogger.Event("skill_check_result", new
            {
                operationId,
                model,
                targetLanguage = operationConfig.TargetLanguage,
                targetGame,
                capturePath = AppLogger.IncludeSensitiveData ? capturePath : null,
                extracted = AppLogger.IncludeSensitiveData ? result.SkillNames : null,
                extractedCount = result.SkillNames.Count,
                matched = AppLogger.IncludeSensitiveData
                    ? lookup.Matched.Select(skill => new { skill.Id, skill.NameZh }).Cast<object>().ToArray()
                    : lookup.Matched.Select(skill => new { skill.Id }).Cast<object>().ToArray(),
                unmatched = AppLogger.IncludeSensitiveData ? lookup.Unmatched : null,
                unmatchedCount = lookup.Unmatched.Count
            });
            if (!_isClosing)
            {
                ClearStatus();
            }

            TrackUsage(
                operationId,
                providerRequestId,
                true,
                captureWidth,
                captureHeight,
                stopwatch.ElapsedMilliseconds,
                responseUsage,
                model,
                operationConfig,
                targetGame,
                null,
                null);
            usageTracked = true;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            AppLogger.Info($"operation={operationId} skill_check.cancelled");
            if (!usageTracked && providerRequestId is not null)
            {
                TrackUsage(
                    operationId,
                    providerRequestId,
                    false,
                    captureWidth,
                    captureHeight,
                    stopwatch.ElapsedMilliseconds,
                    responseUsage,
                    model,
                    operationConfig,
                    targetGame,
                    nameof(OperationCanceledException),
                    "Cancelled");
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppLogger.Error("Skill check failed.", ex);
            var shortError = ToShortUserError(ex);
            if (string.Equals(shortError, "Translation failed", StringComparison.Ordinal))
            {
                shortError = "Skill check failed";
            }

            if (!_isClosing)
            {
                SetStatus(shortError);
            }

            AppLogger.Info($"operation={operationId} skill_check.user_error={shortError}");
            TrackUsage(
                operationId,
                providerRequestId,
                false,
                captureWidth,
                captureHeight,
                stopwatch.ElapsedMilliseconds,
                responseUsage,
                model,
                operationConfig,
                targetGame,
                ex.GetType().Name,
                shortError);
        }
        finally
        {
            if (!_isClosing)
            {
                SetSkillBusy(false);
            }

            if (ReferenceEquals(_skillCancellation, cancellationSource))
            {
                _skillCancellation.Dispose();
                _skillCancellation = null;
            }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        _translationCancellation?.Cancel();
        _skillCancellation?.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.Event("app_closed", new { reason = "window_closed" });
        _translationCancellation?.Cancel();
        _skillCancellation?.Cancel();
        _statusClearTimer.Stop();
        _statusClearTimer.Tick -= StatusClearTimer_Tick;

        base.OnClosed(e);
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        AppLogger.Event("settings_opened", new
        {
            targetLanguage = _config.TargetLanguage
        });

        var settingsConfig = CloneConfig(_config);
        while (true)
        {
            _settingsWindow = new SettingsWindow(settingsConfig)
            {
                Owner = this
            };

            try
            {
                if (_settingsWindow.ShowDialog() != true)
                {
                    return;
                }

                AppConfigStore.Save(settingsConfig);
                _config = settingsConfig;
                AppLogger.IncludeSensitiveData = _config.DiagnosticsEnabled;
                if (!_config.DiagnosticsEnabled)
                {
                    AppDataMaintenance.ClearSensitiveData();
                }

                AppLogger.Event("settings_saved", new
                {
                    targetLanguage = _config.TargetLanguage,
                    diagnosticsEnabled = _config.DiagnosticsEnabled
                });
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or CryptographicException)
            {
                MessageBox.Show(this, ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _settingsWindow = null;
            }
        }
    }

    private async Task<Drawing.Bitmap> CaptureFrameWithoutOverlayAsync(CancellationToken cancellationToken)
    {
        var bounds = ScreenCaptureService.GetVisualScreenBounds(this, FrameBorder);
        ValidateCaptureBounds(bounds);
        var previousVisibility = Visibility;
        var previousHitTestVisible = IsHitTestVisible;
        Visibility = Visibility.Hidden;
        IsHitTestVisible = false;

        try
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var bitmap = ScreenCaptureService.CaptureScreenBounds(bounds);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            bitmap.Dispose();
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        return bitmap;
                    },
                    cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            if (!_isClosing)
            {
                Visibility = previousVisibility;
                IsHitTestVisible = previousHitTestVisible;
            }
        }
    }

    private void InvokeUiDuringOperation(
        Action action,
        CancellationToken cancellationToken,
        DispatcherPriority priority = DispatcherPriority.Normal)
    {
        if (_isClosing ||
            cancellationToken.IsCancellationRequested ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            if (Dispatcher.CheckAccess())
            {
                if (!_isClosing && !cancellationToken.IsCancellationRequested)
                {
                    action();
                }

                return;
            }

            Dispatcher.Invoke(
                () =>
                {
                    if (!_isClosing && !cancellationToken.IsCancellationRequested)
                    {
                        action();
                    }
                },
                priority);
        }
        catch (Exception ex) when (
            (ex is InvalidOperationException or TaskCanceledException) ||
            _isClosing ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
        }
    }

    private static void ValidateCaptureBounds(Rect bounds)
    {
        if (bounds.Width > MaxCapturePixelWidth ||
            bounds.Height > MaxCapturePixelHeight ||
            bounds.Width * bounds.Height > MaxCapturePixels)
        {
            throw new InvalidOperationException("Capture area is too large.");
        }
    }

    private void SetBusy(bool busy)
    {
        if (_isClosing)
        {
            return;
        }

        _isTranslating = busy;
        TranslateButton.IsEnabled = !busy;
        SkillButton.IsEnabled = !busy && !_isCheckingSkills;
        TranslateSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TranslateIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        UpdateClearButtonVisibility();

        if (busy)
        {
            TranslateSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, _spinnerAnimation);
        }
        else
        {
            TranslateSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            TranslateSpinnerRotate.Angle = 0;
        }
    }

    private void SetSkillBusy(bool busy)
    {
        if (_isClosing)
        {
            return;
        }

        _isCheckingSkills = busy;
        SkillButton.IsEnabled = !busy;
        TranslateButton.IsEnabled = !busy && !_isTranslating;
        SkillSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SkillIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        UpdateClearButtonVisibility();

        if (busy)
        {
            SkillSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, _spinnerAnimation);
        }
        else
        {
            SkillSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            SkillSpinnerRotate.Angle = 0;
        }
    }

    private void SetStreamingResultText(string text)
    {
        _isShowingSkillResult = false;
        UseCaptureFramePresentation();
        SkillResultContent.Children.Clear();

        if (SearchButtonsPanel.Visibility == Visibility.Visible)
        {
            ClearSearchButtons();
        }

        SetResultTextLines(text);
        ResultTextPanel.Visibility = Visibility.Visible;
        ResultPanel.Padding = MaxResultTextPadding;
        ResultPanel.Visibility = Visibility.Visible;
        ClearButton.Visibility = Visibility.Visible;
        SetActionButtonsVisible(true);
        UpdateResultPanelClip();
        FitResultText();
        Dispatcher.BeginInvoke(FitResultText, DispatcherPriority.Loaded);
    }

    private void ClearFailedStreamingResult()
    {
        _isShowingSkillResult = false;
        UseCaptureFramePresentation();
        SkillResultContent.Children.Clear();
        ResultTextPanel.Children.Clear();
        ResultTextPanel.Visibility = Visibility.Collapsed;
        ClearSearchButtons();
        ResultPanel.Visibility = Visibility.Collapsed;
        UpdateClearButtonVisibility();
    }

    private void SetResultText(
        string operationId,
        string text,
        IReadOnlyList<SearchQueryResult> searchQueries)
    {
        _isShowingSkillResult = false;
        UseCaptureFramePresentation();
        SkillResultContent.Children.Clear();
        SetResultTextLines(text);
        ResultTextPanel.Visibility = Visibility.Visible;
        SetSearchButtons(operationId, searchQueries);

        ResultPanel.Padding = MaxResultTextPadding;
        ResultPanel.Visibility = Visibility.Visible;
        ClearButton.Visibility = Visibility.Visible;
        SetActionButtonsVisible(true);
        UpdateResultPanelClip();
        FitResultText();
        Dispatcher.BeginInvoke(FitResultText, DispatcherPriority.Loaded);
    }

    private void SetSkillResult(SkillLookupResult lookup, string targetLanguage)
    {
        _isShowingSkillResult = true;
        UseSkillResultPresentation();
        ClearSearchButtons();
        ResultTextPanel.Children.Clear();
        ResultTextPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        ClearButton.Visibility = Visibility.Visible;
        SetActionButtonsVisible(true);
        SkillResultContent.Children.Clear();

        if (lookup.Matched.Count == 0)
        {
            SkillResultContent.Children.Add(new TextBlock
            {
                Text = lookup.Unmatched.Count == 0
                    ? "No visible skills found."
                    : "No matching skills found.",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 198, 95)),
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap
            });
        }

        foreach (var skill in lookup.Matched)
        {
            SkillResultContent.Children.Add(CreateSkillCard(skill, targetLanguage));
        }

        LogUnmatchedSkills(lookup.Unmatched);

        TrimLastSkillResultChildMargin();
    }

    private static void LogUnmatchedSkills(IReadOnlyList<string> unmatched)
    {
        if (unmatched.Count == 0)
        {
            return;
        }

        AppLogger.Event("skills_unmatched", new
        {
            count = unmatched.Count,
            names = AppLogger.IncludeSensitiveData ? unmatched : null
        });
    }

    private void UseCaptureFramePresentation()
    {
        FrameBorder.BorderThickness = VisibleFrameBorderThickness;
        ResultPanel.CornerRadius = new CornerRadius(3);
        SkillResultPanel.Visibility = Visibility.Collapsed;
    }

    private void UseSkillResultPresentation()
    {
        FrameBorder.Visibility = Visibility.Visible;
        FrameBorder.BorderThickness = VisibleFrameBorderThickness;
        ResultPanel.Visibility = Visibility.Collapsed;
        ResizeCornerButton.Visibility = Visibility.Visible;
        ResizeRowButton.Visibility = Visibility.Collapsed;
        SkillResultPanel.Visibility = Visibility.Visible;
    }

    private void TrimLastSkillResultChildMargin()
    {
        if (SkillResultContent.Children.Count == 0 ||
            SkillResultContent.Children[^1] is not FrameworkElement element)
        {
            return;
        }

        element.Margin = new Thickness(
            element.Margin.Left,
            element.Margin.Top,
            element.Margin.Right,
            0);
    }

    private void RestoreCaptureFrameFromSkillResult()
    {
        _isShowingSkillResult = false;
        SkillResultContent.Children.Clear();
        SkillResultPanel.Visibility = Visibility.Collapsed;
        UseCaptureFramePresentation();
        FrameBorder.Visibility = Visibility.Visible;
        ResizeCornerButton.Visibility = Visibility.Visible;
        ResizeRowButton.Visibility = Visibility.Collapsed;
        SetActionButtonsVisible(true);
    }

    private FrameworkElement CreateSkillCard(SkillEntry skill, string targetLanguage)
    {
        var card = new Border
        {
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
            Background = new SolidColorBrush(Color.FromArgb(145, 50, 50, 50)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6)
        };
        SkillCardLayoutMode? currentMode = null;
        void UpdateLayout(double width)
        {
            var mode = GetSkillCardLayoutMode(width);
            if (currentMode == mode)
            {
                return;
            }

            currentMode = mode;
            card.Child = CreateSkillCardContent(skill, targetLanguage, mode);
        }

        UpdateLayout(GetSkillCardLayoutWidth(card));
        card.SizeChanged += (_, e) => UpdateLayout(e.NewSize.Width);
        return card;
    }

    private FrameworkElement CreateSkillCardContent(SkillEntry skill, string targetLanguage, SkillCardLayoutMode mode)
    {
        var localizedName = SkillDatabase.GetLocalizedName(skill, targetLanguage);

        return mode switch
        {
            SkillCardLayoutMode.Stacked => CreateStackedSkillCardContent(skill, targetLanguage, localizedName),
            _ => CreateWideSkillCardContent(skill, targetLanguage, localizedName)
        };
    }

    private FrameworkElement CreateWideSkillCardContent(SkillEntry skill, string targetLanguage, string localizedName)
    {
        var panel = new StackPanel();
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        topRow.Children.Add(CreateSkillImageHost(skill, new Thickness(0, 0, 12, 0)));

        var metadataBlock = CreateSkillMetadataBlock(localizedName, skill, targetLanguage, WideSkillTitleFontSize);
        Grid.SetColumn(metadataBlock, 1);
        topRow.Children.Add(metadataBlock);

        panel.Children.Add(topRow);
        panel.Children.Add(CreateSkillDescriptionBlock(
            skill,
            targetLanguage,
            new Thickness(0, 8, 0, 0),
            WideSkillDescriptionFontSize,
            WideSkillDescriptionLineHeight));
        return panel;
    }

    private FrameworkElement CreateStackedSkillCardContent(SkillEntry skill, string targetLanguage, string localizedName)
    {
        var panel = new StackPanel();
        panel.Children.Add(CreateSkillImageHost(skill, new Thickness(0, 0, 0, 8), StackedSkillIconSize));
        panel.Children.Add(CreateSkillMetadataBlock(localizedName, skill, targetLanguage, StackedSkillTitleFontSize));
        panel.Children.Add(CreateSkillDescriptionBlock(
            skill,
            targetLanguage,
            new Thickness(0, 8, 0, 0),
            StackedSkillDescriptionFontSize,
            StackedSkillDescriptionLineHeight));
        return panel;
    }

    private FrameworkElement CreateSkillImageHost(SkillEntry skill, Thickness margin, double size = SkillIconSize)
    {
        var host = new Grid
        {
            Width = size,
            Height = size,
            Margin = margin,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        var image = new Image
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Stretch = Stretch.UniformToFill,
            SnapsToDevicePixels = true
        };
        TrySetSkillImage(image, skill.Icon.Path);
        host.Children.Add(image);

        host.Children.Add(CreateSkillEnergyBadge(skill.Energy));
        return host;
    }

    private FrameworkElement CreateSkillTitleLine(string localizedName, SkillEntry skill, double fontSize)
    {
        var title = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(255, 198, 95)),
            FontSize = fontSize,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap
        };

        var titleText = new Run(localizedName);
        titleText.Cursor = Cursors.Hand;
        title.Inlines.Add(titleText);
        title.Inlines.Add(new Run(" "));
        var icon = CreateVectorIcon(
            GetSkillCategoryIconPath(skill.Category),
            SkillBadgeElementIconSize,
            new Thickness(0));
        title.Inlines.Add(new InlineUIContainer(icon)
        {
            BaselineAlignment = BaselineAlignment.Center
        });
        titleText.MouseEnter += (_, _) => titleText.TextDecorations = CreateDottedUnderline();
        titleText.MouseLeave += (_, _) => titleText.TextDecorations = null;
        titleText.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenSkillSearch(skill);
        };

        return title;
    }

    private FrameworkElement CreateSkillMetadataBlock(
        string localizedName,
        SkillEntry skill,
        string targetLanguage,
        double titleFontSize)
    {
        var panel = new StackPanel();
        panel.Children.Add(CreateSkillTitleLine(localizedName, skill, titleFontSize));
        panel.Children.Add(CreateSkillBadgeLine(skill, targetLanguage));
        return panel;
    }

    private TextBlock CreateSkillDescriptionBlock(
        SkillEntry skill,
        string targetLanguage,
        Thickness margin,
        double fontSize,
        double lineHeight)
    {
        var description = GetSkillDescriptionText(skill, targetLanguage, out var isPlaceholder);
        return new TextBlock
        {
            Text = description,
            Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
            Opacity = isPlaceholder ? 0.5 : 1.0,
            FontSize = fontSize,
            FontWeight = FontWeights.Medium,
            FontStyle = isPlaceholder ? FontStyles.Italic : FontStyles.Normal,
            LineHeight = lineHeight,
            Margin = margin,
            TextWrapping = TextWrapping.Wrap
        };
    }

    private static string GetSkillDescriptionText(
        SkillEntry skill,
        string targetLanguage,
        out bool isPlaceholder)
    {
        var description = SkillDatabase.GetLocalizedDescription(skill, targetLanguage).Trim();
        if (!string.IsNullOrWhiteSpace(description))
        {
            isPlaceholder = false;
            return description;
        }

        isPlaceholder = true;
        return string.Equals(AppConfig.NormalizeTargetLanguage(targetLanguage), "Vietnamese", StringComparison.Ordinal)
            ? "Kỹ năng này không có mô tả."
            : "This skill doesn't have a description.";
    }

    private static TextDecorationCollection CreateDottedUnderline()
    {
        var decoration = new TextDecoration
        {
            Location = TextDecorationLocation.Underline,
            Pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 198, 95)), 1)
            {
                DashStyle = DashStyles.Dot
            }
        };
        decoration.Freeze();

        var decorations = new TextDecorationCollection { decoration };
        decorations.Freeze();
        return decorations;
    }

    private FrameworkElement CreateSkillBadgeLine(SkillEntry skill, string targetLanguage)
    {
        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0)
        };
        panel.Children.Add(CreateSkillDamageBadge(skill, targetLanguage));
        return panel;
    }

    private SkillCardLayoutMode GetSkillCardLayoutMode(double cardWidth)
    {
        var width = cardWidth > 0
            ? cardWidth
            : GetSkillCardLayoutWidth(null);

        if (width < SkillCardStackedBreakpoint)
        {
            return SkillCardLayoutMode.Stacked;
        }

        return SkillCardLayoutMode.Wide;
    }

    private enum SkillCardLayoutMode
    {
        Wide,
        Stacked
    }


    private double GetSkillCardLayoutWidth(FrameworkElement? card)
    {
        if (card?.ActualWidth > 0)
        {
            return card.ActualWidth;
        }

        if (SkillResultPanel.ActualWidth > 0)
        {
            return SkillResultPanel.ActualWidth;
        }

        return FrameBorder.ActualWidth;
    }

    private FrameworkElement CreateSkillDamageBadge(SkillEntry skill, string targetLanguage)
    {
        var elementColor = DarkenColor(GetElementColor(skill.Element));
        var badge = new Border
        {
            Background = new SolidColorBrush(elementColor),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(5, 2, 9, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Height = SkillBadgeHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = SkillDatabase.GetElementLabel(skill.Element, targetLanguage)
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var elementIcon = new Image
        {
            Width = SkillBadgeElementIconSize,
            Height = SkillBadgeElementIconSize,
            Margin = new Thickness(0, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform
        };
        SetVectorIcon(elementIcon, GetElementIconPath(skill.Element), Brushes.White, true);
        panel.Children.Add(elementIcon);
        panel.Children.Add(new TextBlock
        {
            Text = skill.Power is > 0 ? skill.Power.Value.ToString(CultureInfo.InvariantCulture) : "--",
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        badge.Child = panel;
        return badge;
    }

    private FrameworkElement CreateSkillEnergyBadge(int? energy)
    {
        var badge = new Border
        {
            Background = new SolidColorBrush(SkillBadgeNeutralBackground),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(4, 1, 5, 1),
            MinWidth = 24,
            Height = 19,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-5, -5, 0, 0),
            ToolTip = "Energy"
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(CreateVectorIcon("/Resources/EnergyStar.xaml", SkillBadgeEnergyIconSize, new Thickness(0, 0, 4, 0)));
        panel.Children.Add(new TextBlock
        {
            Text = energy?.ToString(CultureInfo.InvariantCulture) ?? "--",
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        badge.Child = panel;
        return badge;
    }

    private static Image CreateVectorIcon(string resourcePath, double size, Thickness margin, Brush? tintBrush = null)
    {
        var image = new Image
        {
            Width = size,
            Height = size,
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform
        };

        SetVectorIcon(image, resourcePath, tintBrush);
        return image;
    }

    private static string GetSkillCategoryIconPath(string category)
    {
        return category switch
        {
            "physical" => "/Resources/SkillMeta/Physical.xaml",
            "special" => "/Resources/SkillMeta/Magic.xaml",
            "defense" => "/Resources/SkillMeta/Defense.xaml",
            _ => "/Resources/SkillMeta/Status.xaml"
        };
    }

    private static string GetElementIconPath(string element)
    {
        return $"/Resources/Elements/{element}.xaml";
    }

    private static Color GetElementColor(string element)
    {
        return ElementColors[element];
    }

    private static Color DarkenColor(Color color)
    {
        const double factor = 0.62;
        return Color.FromRgb(
            (byte)Math.Round(color.R * factor),
            (byte)Math.Round(color.G * factor),
            (byte)Math.Round(color.B * factor));
    }

    private static void SetVectorIcon(Image image, string resourcePath, Brush? tintBrush = null, bool skipFirstVisibleGeometry = false)
    {
        var source = (ImageSource)Application.LoadComponent(new Uri(resourcePath, UriKind.Relative));
        if (tintBrush is not null && source is DrawingImage drawingImage)
        {
            var tinted = drawingImage.CloneCurrentValue();
            var skippedFirstVisibleGeometry = false;
            TintDrawing(tinted.Drawing, tintBrush, skipFirstVisibleGeometry, ref skippedFirstVisibleGeometry);
            source = tinted;
        }

        image.Source = source;
    }

    private static void TintDrawing(
        System.Windows.Media.Drawing? drawing,
        Brush brush,
        bool skipFirstVisibleGeometry,
        ref bool skippedFirstVisibleGeometry)
    {
        switch (drawing)
        {
            case GeometryDrawing geometryDrawing:
                if (!IsTransparentBrush(geometryDrawing.Brush))
                {
                    if (skipFirstVisibleGeometry && !skippedFirstVisibleGeometry)
                    {
                        geometryDrawing.Brush = Brushes.Transparent;
                        skippedFirstVisibleGeometry = true;
                    }
                    else
                    {
                        geometryDrawing.Brush = brush;
                    }
                }

                break;
            case DrawingGroup drawingGroup:
                foreach (var child in drawingGroup.Children)
                {
                    TintDrawing(child, brush, skipFirstVisibleGeometry, ref skippedFirstVisibleGeometry);
                }

                break;
        }
    }

    private static bool IsTransparentBrush(Brush? brush)
    {
        return brush is SolidColorBrush solidColorBrush && solidColorBrush.Color.A == 0;
    }

    private static void TrySetSkillImage(Image image, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            image.Source = new BitmapImage(new Uri($"pack://application:,,,/{path}", UriKind.Absolute));
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void SetStatus(string text)
    {
        if (_isClosing)
        {
            return;
        }

        _statusClearTimer.Stop();
        StatusText.Text = text;
        StatusLabel.Visibility = Visibility.Visible;
        UpdateClearButtonVisibility();
        _statusClearTimer.Start();
    }

    private void ClearStatus()
    {
        if (_isClosing)
        {
            return;
        }

        _statusClearTimer.Stop();
        StatusText.Text = string.Empty;
        StatusLabel.Visibility = Visibility.Collapsed;
        UpdateClearButtonVisibility();
    }

    private void StatusClearTimer_Tick(object? sender, EventArgs e)
    {
        ClearStatus();
    }

    private void ClearResult()
    {
        ResultTextPanel.Children.Clear();
        ResultTextPanel.Visibility = Visibility.Collapsed;
        SkillResultContent.Children.Clear();
        SkillResultPanel.Visibility = Visibility.Collapsed;
        _isShowingSkillResult = false;
        UseCaptureFramePresentation();
        ClearStatus();
        CollapseFrame();
    }

    private void UpdateClearButtonVisibility()
    {
        ClearButton.Visibility =
            _isTranslating ||
            _isCheckingSkills ||
            ResultPanel.Visibility == Visibility.Visible ||
            SkillResultPanel.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void SetActionButtonsVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TranslateButton.Visibility = visibility;
        SkillButton.Visibility = visibility;
    }

    private void SetSearchButtons(
        string operationId,
        IReadOnlyList<SearchQueryResult> searchQueries)
    {
        ClearSearchButtons();

        var buttons = new[] { SearchButton1, SearchButton2, SearchButton3 };
        var buttonIndex = 0;
        foreach (var searchQuery in searchQueries)
        {
            if (buttonIndex >= buttons.Length)
            {
                break;
            }

            var searchUrl = BuildBilibiliSearchUrl(searchQuery.Query);
            if (string.IsNullOrWhiteSpace(searchUrl))
            {
                continue;
            }

            var button = buttons[buttonIndex];
            button.ToolTip = string.IsNullOrWhiteSpace(searchQuery.Intent)
                ? "Search Bilibili"
                : searchQuery.Intent;
            button.Visibility = Visibility.Visible;
            _searchButtons[button] = new SearchButtonState(
                operationId,
                buttonIndex + 1,
                searchQuery,
                searchUrl);
            buttonIndex++;
        }

        SearchButtonsPanel.Visibility = _searchButtons.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string BuildBilibiliSearchUrl(string searchQuery)
    {
        var keyword = searchQuery.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return string.Empty;
        }

        var prefixedKeyword = keyword.StartsWith(RocoGame.SearchPrefix, StringComparison.OrdinalIgnoreCase)
                ? keyword
                : $"{RocoGame.SearchPrefix} {keyword}";
        return AppConfig.BilibiliSearchUrlPrefix + Uri.EscapeDataString(prefixedKeyword);
    }

    private void OpenSkillSearch(SkillEntry skill)
    {
        var searchUrl = BuildBilibiliSearchUrl(skill.NameZh);
        if (string.IsNullOrWhiteSpace(searchUrl))
        {
            return;
        }

        AppLogger.Event("skill_search_click", new
        {
            skillId = skill.Id,
            skillNameZh = AppLogger.IncludeSensitiveData ? skill.NameZh : null,
            searchPrefix = RocoGame.SearchPrefix,
            searchUrl = AppLogger.IncludeSensitiveData ? searchUrl : null
        });
        OpenUrl(searchUrl);
    }

    private void ClearSearchButtons()
    {
        _searchButtons.Clear();
        SearchButtonsPanel.Visibility = Visibility.Collapsed;
        SearchButton1.Visibility = Visibility.Collapsed;
        SearchButton2.Visibility = Visibility.Collapsed;
        SearchButton3.Visibility = Visibility.Collapsed;
        SearchButton1.ToolTip = "Search Bilibili";
        SearchButton2.ToolTip = "Search Bilibili";
        SearchButton3.ToolTip = "Search Bilibili";
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLogger.Error("Could not open search link.", ex);
            if (!_isClosing)
            {
                SetStatus("Could not open link");
            }
        }
    }

    private static Task<string?> SaveCaptureAsync(
        string operationId,
        Drawing.Bitmap bitmap,
        bool persistCapture,
        double frameWidth,
        double frameHeight,
        CancellationToken cancellationToken)
    {
        if (!persistCapture)
        {
            AppLogger.Event("capture", new
            {
                operationId,
                saved = false,
                width = bitmap.Width,
                height = bitmap.Height,
                frameWidth,
                frameHeight
            });
            return Task.FromResult<string?>(null);
        }

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return SaveCapture(operationId, bitmap, frameWidth, frameHeight);
            },
            cancellationToken);
    }

    private static string? SaveCapture(
        string operationId,
        Drawing.Bitmap bitmap,
        double frameWidth,
        double frameHeight)
    {
        try
        {
            var directory = Path.Combine(AppPaths.DataDirectory, "captures");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{operationId}.png");
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            AppLogger.Capture(new CaptureLogEntry(
                DateTimeOffset.Now,
                operationId,
                path,
                bitmap.Width,
                bitmap.Height,
                frameWidth,
                frameHeight));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or System.Runtime.InteropServices.ExternalException)
        {
            AppLogger.Error("Could not save capture.", ex);
            return null;
        }
    }

    private static void TrackUsage(
        string operationId,
        string? providerRequestId,
        bool success,
        int width,
        int height,
        long elapsedMilliseconds,
        TokenUsage usage,
        string model,
        AppConfig operationConfig,
        string targetGame,
        string? errorKind,
        string? errorMessage)
    {
        var entry = new UsageLogEntry(
            DateTimeOffset.Now,
            operationId,
            providerRequestId,
            model,
            operationConfig.TargetLanguage,
            targetGame,
            success,
            elapsedMilliseconds,
            width,
            height,
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.TotalTokens,
            errorKind,
            errorMessage);

        AppLogger.Usage(entry);
        AppLogger.Info(
            $"operation={operationId} usage.summary " +
            $"success={success} " +
            $"model={model} " +
            $"duration_ms={elapsedMilliseconds} " +
            $"tokens={usage.TotalTokens} " +
            $"capture={width}x{height}");
    }

    private static string ToShortUserError(Exception exception)
    {
        var message = exception.Message;

        if (message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("API key expired", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("API key not valid", StringComparison.OrdinalIgnoreCase))
        {
            return "API key rejected";
        }

        if (message.Contains("API key", StringComparison.OrdinalIgnoreCase))
        {
            return "API key required";
        }

        if (message.Contains("429", StringComparison.OrdinalIgnoreCase))
        {
            return "Rate limited";
        }

        if (message.Contains("MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
        {
            return "Response too long";
        }

        if (message.Contains("too large", StringComparison.OrdinalIgnoreCase))
        {
            return "Area too large";
        }

        if (message.Contains("outside the screen", StringComparison.OrdinalIgnoreCase))
        {
            return "Area off screen";
        }

        if (exception is TimeoutException ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Timed out";
        }

        if (message.Contains("prompt blocked", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("SAFETY", StringComparison.OrdinalIgnoreCase))
        {
            return "Content blocked";
        }

        if (message.Contains("RECITATION", StringComparison.OrdinalIgnoreCase))
        {
            return "Response blocked";
        }

        if (message.Contains("400", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            return "Request failed";
        }

        if (message.Contains("No translation", StringComparison.OrdinalIgnoreCase))
        {
            return "No translation";
        }

        return "Translation failed";
    }

    private static AppConfig CloneConfig(AppConfig config) =>
        new()
        {
            ApiKey = config.ApiKey,
            TargetLanguage = AppConfig.NormalizeTargetLanguage(config.TargetLanguage),
            DiagnosticsEnabled = config.DiagnosticsEnabled
        };

    private void FitResultText()
    {
        if (!ResultPanel.IsVisible ||
            ResultTextPanel.Children.Count == 0 ||
            ResultPanel.ActualWidth <= 0 ||
            ResultPanel.ActualHeight <= 0)
        {
            return;
        }

        ApplyResultTextLayout(MaxResultFontSize, TextTrimming.None);

        for (var fontSize = MaxResultFontSize; fontSize >= MinResultFontSize; fontSize -= 0.5)
        {
            ResultPanel.Padding = GetResultTextPadding(fontSize);
            ApplyResultTextLayout(fontSize, TextTrimming.None);
            var availableWidth = Math.Max(1, ResultPanel.ActualWidth - ResultPanel.Padding.Left - ResultPanel.Padding.Right);
            var availableHeight = Math.Max(1, ResultPanel.ActualHeight - ResultPanel.Padding.Top - ResultPanel.Padding.Bottom);

            ResultTextPanel.Measure(new Size(availableWidth, double.PositiveInfinity));

            if (ResultTextPanel.DesiredSize.Width <= availableWidth &&
                ResultTextPanel.DesiredSize.Height <= availableHeight)
            {
                return;
            }
        }

        ResultPanel.Padding = MinResultTextPadding;
        ApplyResultTextLayout(MinResultFontSize, TextTrimming.CharacterEllipsis);
    }

    private void SetResultTextLines(string text)
    {
        ResultTextPanel.Children.Clear();

        var lines = text
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            ResultTextPanel.Children.Add(new TextBlock
            {
                Text = line
            });
        }
    }

    private void ApplyResultTextLayout(double fontSize, TextTrimming textTrimming)
    {
        var lineGap = GetResultLineGap(fontSize);
        var lines = ResultTextPanel.Children.OfType<TextBlock>().ToArray();

        for (var index = 0; index < lines.Length; index++)
        {
            lines[index].FontSize = fontSize;
            lines[index].TextTrimming = textTrimming;
            lines[index].Margin = index + 1 < lines.Length
                ? new Thickness(0, 0, 0, lineGap)
                : new Thickness(0);
        }
    }

    private static Thickness GetResultTextPadding(double fontSize)
    {
        var ratio = Math.Clamp(
            (fontSize - MinResultFontSize) / (MaxResultFontSize - MinResultFontSize),
            0,
            1);
        return new Thickness(
            Lerp(MinResultTextPadding.Left, MaxResultTextPadding.Left, ratio),
            Lerp(MinResultTextPadding.Top, MaxResultTextPadding.Top, ratio),
            Lerp(MinResultTextPadding.Right, MaxResultTextPadding.Right, ratio),
            Lerp(MinResultTextPadding.Bottom, MaxResultTextPadding.Bottom, ratio));
    }

    private static double GetResultLineGap(double fontSize)
    {
        var ratio = Math.Clamp(
            (fontSize - MinResultFontSize) / (MaxResultFontSize - MinResultFontSize),
            0,
            1);
        return Lerp(MinResultLineGap, MaxResultLineGap, ratio);
    }

    private static double Lerp(double start, double end, double amount) =>
        start + (end - start) * amount;
}

internal sealed record SearchButtonState(
    string OperationId,
    int Index,
    SearchQueryResult Query,
    string Url);
