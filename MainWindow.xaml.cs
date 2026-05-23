using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
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
    private const double SkillCardVerticalBreakpoint = 320;
    private const double MaxResultFontSize = 24;
    private const double MinResultFontSize = 10;
    private const double MaxResultLineGap = 12;
    private const double MinResultLineGap = 4;
    private static readonly Thickness MaxResultTextPadding = new(14, 12, 14, 10);
    private static readonly Thickness MinResultTextPadding = new(6, 6, 6, 4);
    private static readonly Thickness VisibleFrameBorderThickness = new(1);
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
    private SettingsWindow? _settingsWindow;
    private DisplayedResultState? _displayedResult;
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
        _statusClearTimer.Tick += StatusClearTimer_Tick;
        AppLogger.Event("app_start", new
        {
            appDirectory = AppPaths.AppDirectory,
            targetLanguage = _config.TargetLanguage,
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
        var nextWidth = Math.Max(MinWidth, _resizeStartWindowSize.Width + delta.X);
        var nextHeight = Math.Max(MinHeight, _resizeStartWindowSize.Height + delta.Y);

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
        _displayedResult = null;
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
            searchButton.TargetGame,
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
        var targetGame = RocoGame.DisplayName;
        var searchPrefix = RocoGame.SearchPrefix;
        var operationId = Guid.NewGuid().ToString("N")[..12];
        var stopwatch = Stopwatch.StartNew();
        var captureWidth = 0;
        var captureHeight = 0;
        var model = AppConfig.NormalizeModel(operationConfig.Model);
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
                targetGame,
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
            var capturePath = SaveCapture(operationId, bitmap);

            var result = await GeminiClient.TranslateImageToTextStreamingAsync(
                    bitmap,
                    operationConfig,
                    model,
                    operationId,
                    partialText => Dispatcher.Invoke(
                        () =>
                        {
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                streamedTextShown = true;
                                SetStreamingResultText(partialText);
                            }
                        },
                        DispatcherPriority.Background),
                    cancellationToken)
                .ConfigureAwait(true);
            providerRequestId = result.ProviderRequestId;
            responseUsage = result.Usage;
            stopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();
            SetResultText(
                operationId,
                result.Text,
                result.SearchQueries,
                targetGame,
                searchPrefix);
            AppLogger.TextResult(new TextResultLogEntry(
                DateTimeOffset.Now,
                operationId,
                model,
                operationConfig.TargetLanguage,
                targetGame,
                capturePath,
                result.Text,
                result.SearchQueries));
            ClearStatus();
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
                    targetGame,
                    nameof(OperationCanceledException),
                    "Cancelled");
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppLogger.Error("Translate failed.", ex);
            var shortError = ToShortUserError(ex);
            if (streamedTextShown)
            {
                ClearFailedStreamingResult();
            }

            SetStatus(shortError);
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
                targetGame,
                ex.GetType().Name,
                shortError);
        }
        finally
        {
            SetBusy(false);
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
        var model = AppConfig.NormalizeModel(operationConfig.Model);
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
            var capturePath = SaveCapture(operationId, bitmap);

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
            SetSkillResult(lookup, operationConfig.TargetLanguage);
            AppLogger.Event("skill_check_result", new
            {
                operationId,
                model,
                targetLanguage = operationConfig.TargetLanguage,
                targetGame,
                capturePath,
                extracted = result.SkillNames,
                matched = lookup.Matched.Select(skill => new { skill.Id, skill.NameZh }).ToArray(),
                unmatched = lookup.Unmatched
            });
            ClearStatus();
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

            SetStatus(shortError);
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
            SetSkillBusy(false);
            if (ReferenceEquals(_skillCancellation, cancellationSource))
            {
                _skillCancellation.Dispose();
                _skillCancellation = null;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.Event("app_closed", new { reason = "window_closed" });
        _translationCancellation?.Cancel();
        _translationCancellation?.Dispose();
        _translationCancellation = null;
        _skillCancellation?.Cancel();
        _skillCancellation?.Dispose();
        _skillCancellation = null;
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
        _settingsWindow = new SettingsWindow(settingsConfig)
        {
            Owner = this
        };

        try
        {
            if (_settingsWindow.ShowDialog() == true)
            {
                try
                {
                    AppConfigStore.Save(settingsConfig);
                    _config = settingsConfig;
                    AppLogger.Event("settings_saved", new
                    {
                        targetLanguage = _config.TargetLanguage
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or CryptographicException)
                {
                    MessageBox.Show(this, ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        finally
        {
            _settingsWindow = null;
        }
    }

    private async Task<Drawing.Bitmap> CaptureFrameWithoutOverlayAsync(CancellationToken cancellationToken)
    {
        var bounds = ScreenCaptureService.GetVisualScreenBounds(this, FrameBorder);
        var previousVisibility = Visibility;
        var previousHitTestVisible = IsHitTestVisible;
        Visibility = Visibility.Hidden;
        IsHitTestVisible = false;

        try
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            return ScreenCaptureService.CaptureScreenBounds(bounds);
        }
        finally
        {
            Visibility = previousVisibility;
            IsHitTestVisible = previousHitTestVisible;
        }
    }

    private void SetBusy(bool busy)
    {
        _isTranslating = busy;
        TranslateButton.IsEnabled = !busy;
        SkillButton.IsEnabled = !busy && !_isCheckingSkills;
        TranslateSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TranslateIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;

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
        _isCheckingSkills = busy;
        SkillButton.IsEnabled = !busy;
        TranslateButton.IsEnabled = !busy && !_isTranslating;
        SkillSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SkillIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;

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
        _displayedResult = null;
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
        IReadOnlyList<SearchQueryResult> searchQueries,
        string targetGame,
        string searchPrefix)
    {
        _displayedResult = new DisplayedResultState(
            operationId,
            searchQueries,
            targetGame,
            searchPrefix);
        _isShowingSkillResult = false;
        UseCaptureFramePresentation();
        SkillResultContent.Children.Clear();
        SetResultTextLines(text);
        ResultTextPanel.Visibility = Visibility.Visible;
        SetSearchButtons(operationId, searchQueries, targetGame, searchPrefix);

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
        _displayedResult = null;
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
                FontFamily = FontFamily,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
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
            names = unmatched
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
            Background = new SolidColorBrush(Color.FromArgb(110, 34, 34, 34)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 198, 95)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };

        bool? isVertical = null;
        void UpdateLayout(double width)
        {
            var useVertical = ShouldUseVerticalSkillCardLayout(width);
            if (isVertical == useVertical)
            {
                return;
            }

            isVertical = useVertical;
            card.Child = CreateSkillCardContent(skill, targetLanguage, useVertical);
        }

        UpdateLayout(GetSkillCardLayoutWidth(card));
        card.SizeChanged += (_, e) => UpdateLayout(e.NewSize.Width);
        return card;
    }

    private FrameworkElement CreateSkillCardContent(SkillEntry skill, string targetLanguage, bool vertical)
    {
        var image = new Image
        {
            Width = 60,
            Height = 60,
            Margin = vertical ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 14, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Stretch = Stretch.UniformToFill,
            SnapsToDevicePixels = true
        };
        TrySetSkillImage(image, skill.Icon);

        var textPanel = new StackPanel();
        var localizedName = SkillDatabase.GetLocalizedName(skill, targetLanguage);
        textPanel.Children.Add(new TextBlock
        {
            Text = localizedName,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 198, 95)),
            FontFamily = FontFamily,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        });

        textPanel.Children.Add(CreateSkillInfoLine(skill, targetLanguage));

        textPanel.Children.Add(new TextBlock
        {
            Text = SkillDatabase.GetLocalizedDescription(skill, targetLanguage),
            Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
            FontFamily = FontFamily,
            FontSize = 14,
            LineHeight = 18,
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        if (vertical)
        {
            var panel = new StackPanel();
            panel.Children.Add(image);
            panel.Children.Add(textPanel);
            return panel;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(image);
        Grid.SetColumn(textPanel, 1);
        grid.Children.Add(textPanel);
        return grid;
    }

    private bool ShouldUseVerticalSkillCardLayout(double cardWidth) =>
        cardWidth > 0
            ? cardWidth < SkillCardVerticalBreakpoint
            : GetSkillCardLayoutWidth(null) < SkillCardVerticalBreakpoint;

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

    private FrameworkElement CreateSkillInfoLine(SkillEntry skill, string targetLanguage)
    {
        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var elementIcon = new Image
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
            ToolTip = SkillDatabase.GetElementLabel(skill.Element, targetLanguage)
        };
        TrySetSkillImage(elementIcon, skill.ElementIcon);
        panel.Children.Add(elementIcon);

        var categoryIcon = CreateVectorIcon(GetSkillCategoryIconPath(skill.Category), 20, new Thickness(0, 0, 8, 0));
        categoryIcon.ToolTip = SkillDatabase.GetCategoryLabel(skill.Category, targetLanguage);
        panel.Children.Add(categoryIcon);

        panel.Children.Add(CreateVectorIcon("/Resources/EnergyStar.xaml", 15, new Thickness(0, 0, 3, 0)));
        panel.Children.Add(CreateSkillInfoText(skill.Energy?.ToString() ?? "-", new Thickness(0, 0, 8, 0)));

        panel.Children.Add(CreateVectorIcon("/Resources/SkillMeta/Power.xaml", 15, new Thickness(0, 0, 3, 0)));
        panel.Children.Add(CreateSkillInfoText(skill.Power?.ToString() ?? "-", new Thickness(0, 0, 8, 0)));

        if (skill.Accuracy is not null)
        {
            panel.Children.Add(CreateVectorIcon("/Resources/SkillMeta/Accuracy.xaml", 15, new Thickness(0, 0, 3, 0)));
            panel.Children.Add(CreateSkillInfoText(skill.Accuracy.ToString() ?? "-", new Thickness(0, 0, 8, 0)));
        }

        if (skill.Priority is not null)
        {
            panel.Children.Add(CreateVectorIcon("/Resources/SkillMeta/Priority.xaml", 15, new Thickness(0, 0, 3, 0)));
            panel.Children.Add(CreateSkillInfoText(skill.Priority.ToString() ?? "-", new Thickness(0, 0, 8, 0)));
        }

        return panel;
    }

    private TextBlock CreateSkillInfoText(string text, Thickness margin)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
            FontFamily = FontFamily,
            FontSize = 13,
            Margin = margin,
            TextWrapping = TextWrapping.Wrap
        };
    }

    private static Image CreateVectorIcon(string resourcePath, double size, Thickness margin)
    {
        var image = new Image
        {
            Width = size,
            Height = size,
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform
        };

        try
        {
            image.Source = (ImageSource)Application.LoadComponent(new Uri(resourcePath, UriKind.Relative));
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }

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
        _statusClearTimer.Stop();
        StatusText.Text = text;
        StatusLabel.Visibility = Visibility.Visible;
        UpdateClearButtonVisibility();
        _statusClearTimer.Start();
    }

    private void ClearStatus()
    {
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
        _displayedResult = null;
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
        IReadOnlyList<SearchQueryResult> searchQueries,
        string targetGame,
        string searchPrefix)
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

            var searchUrl = BuildBilibiliSearchUrl(searchQuery.Query, searchPrefix);
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
                targetGame,
                searchUrl);
            buttonIndex++;
        }

        SearchButtonsPanel.Visibility = _searchButtons.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string BuildBilibiliSearchUrl(string searchQuery, string searchPrefix)
    {
        var keyword = searchQuery.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return string.Empty;
        }

        var prefixedKeyword = string.IsNullOrWhiteSpace(searchPrefix) ||
            keyword.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase)
                ? keyword
                : $"{searchPrefix} {keyword}";
        return AppConfig.BilibiliSearchUrlPrefix + Uri.EscapeDataString(prefixedKeyword);
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
            SetStatus("Could not open link");
        }
    }

    private string? SaveCapture(string operationId, Drawing.Bitmap bitmap)
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
                FrameBorder.ActualWidth,
                FrameBorder.ActualHeight));
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

        if (exception is TimeoutException ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Timed out";
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
            Model = AppConfig.NormalizeModel(config.Model),
            TargetLanguage = AppConfig.NormalizeTargetLanguage(config.TargetLanguage),
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
    string TargetGame,
    string Url);

internal sealed record DisplayedResultState(
    string OperationId,
    IReadOnlyList<SearchQueryResult> SearchQueries,
    string TargetGame,
    string SearchPrefix);
