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
    private const double CollapsedWidth = 84;
    private const double CollapsedHeight = 24;
    private const double FrameRevealHeight = 48;
    private const double MaxResultFontSize = 24;
    private const double MinResultFontSize = 10;
    private const double MaxResultLineGap = 12;
    private const double MinResultLineGap = 4;
    private static readonly Thickness MaxResultTextPadding = new(14, 12, 14, 10);
    private static readonly Thickness MinResultTextPadding = new(6, 6, 6, 4);
    private const int WmNcHitTest = 0x0084;
    private const nint HtClient = 1;
    private const nint HtTransparent = -1;

    private AppConfig _config = AppConfigStore.Load();
    private CancellationTokenSource? _translationCancellation;
    private CancellationTokenSource? _replyCancellation;
    private Point? _dragStartCursor;
    private Point? _dragStartWindowPosition;
    private Point? _resizeStartCursor;
    private Size _resizeStartWindowSize;
    private bool _dragButtonDragged;
    private bool _resizeButtonDragged;
    private bool _resizeExpandedDuringInteraction;
    private bool _isTranslating;
    private bool _isReplyTranslating;
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
        InitializeTargetGameMenu();
        InitializeAppModeMenu();
        AppLogger.Event("app_start", new
        {
            appDirectory = AppPaths.AppDirectory,
            targetLanguage = _config.TargetLanguage,
            targetGame = TargetGames.GetDisplayName(_config.TargetGame),
            mode = AppModes.GetDisplayName(_config.Mode)
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
            IsPointInside(ClearButton, windowPoint) ||
            IsPointInside(ResizeRowButton, windowPoint) ||
            IsPointInside(ResizeCornerButton, windowPoint) ||
            IsPointInside(SearchButtonsPanel, windowPoint) ||
            IsPointInside(ReplyComposerPanel, windowPoint);
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
        ResizeRowButton.Visibility = Visibility.Collapsed;
        ResizeCornerButton.Visibility = Visibility.Visible;
        Width = Math.Max(CollapsedWidth, nextWidth);
        Height = nextHeight;
        ShowDefaultChatComposerIfNeeded(false);
        FitResultText();
    }

    private void CollapseFrame()
    {
        FrameBorder.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        _displayedResult = null;
        ClearSearchButtons();
        HideReplyComposer();
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
        await TranslateCurrentAreaAsync().ConfigureAwait(true);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTranslating)
        {
            AppLogger.Event("translate_cancel_requested", new { reason = "clear_button" });
            _translationCancellation?.Cancel();
        }

        if (_isReplyTranslating)
        {
            AppLogger.Event("reply_translate_cancel_requested", new { reason = "clear_button" });
            _replyCancellation?.Cancel();
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

    private async void ReplySendButton_Click(object sender, RoutedEventArgs e)
    {
        await TranslateReplyToChineseAsync().ConfigureAwait(true);
    }

    private async void ReplyTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await TranslateReplyToChineseAsync().ConfigureAwait(true);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideReplyComposer();
        }
    }

    private void InitializeAppModeMenu()
    {
        if (DragButton.ContextMenu is not { } menu)
        {
            return;
        }

        foreach (var mode in AppModes.MenuModes.Reverse())
        {
            var item = new MenuItem
            {
                Header = AppModes.GetDisplayName(mode),
                IsCheckable = true,
                Tag = mode.ToString()
            };
            item.Click += AppModeMenu_Click;
            menu.Items.Insert(0, item);
        }

        menu.Items.Insert(AppModes.MenuModes.Count, new Separator());
    }

    private void InitializeTargetGameMenu()
    {
        if (DragButton.ContextMenu is not { } menu)
        {
            return;
        }

        var insertIndex = 0;
        while (insertIndex < menu.Items.Count && menu.Items[insertIndex] is not Separator)
        {
            insertIndex++;
        }

        foreach (var game in TargetGames.MenuGames.Reverse())
        {
            var item = new MenuItem
            {
                Header = TargetGames.GetDisplayName(game),
                IsCheckable = true,
                Tag = game.ToString()
            };
            item.Click += TargetGameMenu_Click;
            menu.Items.Insert(insertIndex, item);
        }
    }

    private void WindowMenu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateAppModeMenuChecks();
        UpdateTargetGameMenuChecks();
    }

    private void AppModeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } ||
            !Enum.TryParse<AppMode>(tag, true, out var mode) ||
            !Enum.IsDefined(mode) ||
            _config.Mode == mode)
        {
            UpdateAppModeMenuChecks();
            return;
        }

        var previousMode = _config.Mode;
        try
        {
            _config.Mode = mode;
            AppConfigStore.Save(_config);
            ApplyModeToVisibleResult();
            AppLogger.Event("mode_changed", new
            {
                mode = AppModes.GetDisplayName(_config.Mode)
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or CryptographicException)
        {
            _config.Mode = previousMode;
            AppLogger.Error("Could not save mode.", ex);
            MessageBox.Show(this, ex.Message, "Mode", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateAppModeMenuChecks();
        }
    }

    private void TargetGameMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } ||
            !Enum.TryParse<TargetGame>(tag, true, out var targetGame) ||
            !Enum.IsDefined(targetGame) ||
            _config.TargetGame == targetGame)
        {
            UpdateTargetGameMenuChecks();
            return;
        }

        var previousTargetGame = _config.TargetGame;
        try
        {
            _config.TargetGame = targetGame;
            AppConfigStore.Save(_config);
            AppLogger.Event("target_game_changed", new
            {
                targetGame = TargetGames.GetDisplayName(_config.TargetGame)
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or CryptographicException)
        {
            _config.TargetGame = previousTargetGame;
            AppLogger.Error("Could not save target game.", ex);
            MessageBox.Show(this, ex.Message, "Target game", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateTargetGameMenuChecks();
        }
    }

    private void UpdateAppModeMenuChecks()
    {
        if (DragButton.ContextMenu is not { } menu)
        {
            return;
        }

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is string tag &&
                Enum.TryParse<AppMode>(tag, true, out var mode) &&
                Enum.IsDefined(mode))
            {
                var isSelected = _config.Mode == mode;
                item.IsChecked = isSelected;
                item.FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal;
            }
        }
    }

    private void UpdateTargetGameMenuChecks()
    {
        if (DragButton.ContextMenu is not { } menu)
        {
            return;
        }

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is string tag &&
                Enum.TryParse<TargetGame>(tag, true, out var targetGame) &&
                Enum.IsDefined(targetGame))
            {
                var isSelected = _config.TargetGame == targetGame;
                item.IsChecked = isSelected;
                item.FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal;
            }
        }
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
        if (_isTranslating)
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
        var targetGame = TargetGames.GetDisplayName(operationConfig.TargetGame);
        var searchPrefix = TargetGames.GetSearchPrefix(operationConfig.TargetGame);
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
                                SetStreamingResultText(
                                    partialText,
                                    operationConfig.Mode,
                                    operationConfig.TargetLanguage);
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
                searchPrefix,
                operationConfig.Mode,
                operationConfig.TargetLanguage);
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
    private async Task TranslateReplyToChineseAsync()
    {
        if (_isReplyTranslating)
        {
            return;
        }

        var replyText = ReplyTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(replyText))
        {
            SetStatus("Reply is empty");
            return;
        }

        if (!AppConfig.HasGeminiApiKey(_config.ApiKey))
        {
            OpenSettings();
            if (!AppConfig.HasGeminiApiKey(_config.ApiKey))
            {
                AppLogger.Event("reply_translate_skipped", new { reason = "api_key_required" });
                return;
            }
        }

        _replyCancellation?.Dispose();
        _replyCancellation = new CancellationTokenSource();
        var cancellationSource = _replyCancellation;
        var cancellationToken = cancellationSource.Token;
        var operationConfig = CloneConfig(_config);
        var operationId = Guid.NewGuid().ToString("N")[..12];
        var model = AppConfig.NormalizeModel(operationConfig.Model);

        try
        {
            AppLogger.Event("reply_translate_start", new
            {
                operationId,
                model,
                sourceLanguage = operationConfig.TargetLanguage,
                targetLanguage = "Chinese Simplified"
            });

            SetReplyBusy(true);
            ClearStatus();

            var result = await GeminiClient.TranslateReplyToChineseStreamingAsync(
                    replyText,
                    operationConfig,
                    model,
                    operationId,
                    _ => { },
                    cancellationToken)
                .ConfigureAwait(true);

            cancellationToken.ThrowIfCancellationRequested();
            Clipboard.SetText(result.Text);
            ReplyTextBox.Clear();
            SetStatus("Copied to clipboard ✓");
            AppLogger.Event("reply_translate_copied", new
            {
                operationId,
                model,
                sourceLanguage = operationConfig.TargetLanguage,
                translation = result.Text,
                promptTokens = result.Usage.PromptTokens,
                completionTokens = result.Usage.CompletionTokens,
                totalTokens = result.Usage.TotalTokens
            });
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info($"operation={operationId} reply_translate.cancelled");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Reply translation failed.", ex);
            var shortError = ToShortUserError(ex);
            SetStatus(shortError);
            AppLogger.Info($"operation={operationId} reply_translate.user_error={shortError}");
        }
        finally
        {
            SetReplyBusy(false);
            if (ReferenceEquals(_replyCancellation, cancellationSource))
            {
                _replyCancellation.Dispose();
                _replyCancellation = null;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.Event("app_closed", new { reason = "window_closed" });
        _translationCancellation?.Cancel();
        _translationCancellation?.Dispose();
        _translationCancellation = null;
        _replyCancellation?.Cancel();
        _replyCancellation?.Dispose();
        _replyCancellation = null;
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
            targetLanguage = _config.TargetLanguage,
            mode = AppModes.GetDisplayName(_config.Mode)
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
                        targetLanguage = _config.TargetLanguage,
                        mode = AppModes.GetDisplayName(_config.Mode)
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

    private void SetReplyBusy(bool busy)
    {
        _isReplyTranslating = busy;
        ReplySendButton.IsEnabled = !busy;
        ReplySpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ReplySendIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;

        if (busy)
        {
            ReplySpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, _spinnerAnimation);
        }
        else
        {
            ReplySpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            ReplySpinnerRotate.Angle = 0;
        }
    }

    private void SetStreamingResultText(
        string text,
        AppMode operationMode,
        string operationTargetLanguage)
    {
        if (SearchButtonsPanel.Visibility == Visibility.Visible)
        {
            ClearSearchButtons();
        }

        if (operationMode == AppMode.Chat)
        {
            ShowReplyComposer(false, operationTargetLanguage);
        }
        else
        {
            HideReplyComposer();
        }

        SetResultTextLines(text);
        ResultTextPanel.Visibility = Visibility.Visible;
        ResultPanel.Padding = MaxResultTextPadding;
        ResultPanel.Visibility = Visibility.Visible;
        UpdateResultPanelClip();
        FitResultText();
        Dispatcher.BeginInvoke(FitResultText, DispatcherPriority.Loaded);
    }

    private void ClearFailedStreamingResult()
    {
        _displayedResult = null;
        ResultTextPanel.Children.Clear();
        ResultTextPanel.Visibility = Visibility.Collapsed;
        ClearSearchButtons();
        HideReplyComposer();
        ResultPanel.Visibility = Visibility.Collapsed;
    }

    private void SetResultText(
        string operationId,
        string text,
        IReadOnlyList<SearchQueryResult> searchQueries,
        string targetGame,
        string searchPrefix,
        AppMode operationMode,
        string operationTargetLanguage)
    {
        _displayedResult = new DisplayedResultState(
            operationMode,
            operationTargetLanguage,
            string.Empty,
            Array.Empty<SearchQueryResult>(),
            targetGame,
            searchPrefix);
        SetResultTextLines(text);
        ResultTextPanel.Visibility = Visibility.Visible;
        if (operationMode == AppMode.Chat)
        {
            ClearSearchButtons();
            ShowReplyComposer(true, operationTargetLanguage);
        }
        else
        {
            HideReplyComposer();
            SetSearchButtons(operationId, searchQueries, targetGame, searchPrefix);
        }

        ResultPanel.Padding = MaxResultTextPadding;
        ResultPanel.Visibility = Visibility.Visible;
        UpdateResultPanelClip();
        FitResultText();
        Dispatcher.BeginInvoke(FitResultText, DispatcherPriority.Loaded);
    }

    private void ApplyModeToVisibleResult()
    {
        if (ResultPanel.Visibility != Visibility.Visible || ResultTextPanel.Visibility != Visibility.Visible)
        {
            if (_config.Mode == AppMode.Chat && FrameBorder.Visibility == Visibility.Visible)
            {
                ClearSearchButtons();
                ShowReplyComposer(true);
            }
            else
            {
                HideReplyComposer();
                ResultPanel.Visibility = Visibility.Collapsed;
            }

            return;
        }

        if (_displayedResult is null)
        {
            HideReplyComposer();
            return;
        }

        if (_displayedResult.Mode == AppMode.Chat)
        {
            ClearSearchButtons();
            ShowReplyComposer(true, _displayedResult.TargetLanguage);
            return;
        }

        HideReplyComposer();
        SetSearchButtons(
            _displayedResult.OperationId,
            _displayedResult.SearchQueries,
            _displayedResult.TargetGame,
            _displayedResult.SearchPrefix);
    }

    private void ShowDefaultChatComposerIfNeeded(bool focus)
    {
        if (_config.Mode != AppMode.Chat || ReplyComposerPanel.Visibility == Visibility.Visible)
        {
            return;
        }

        ClearSearchButtons();
        ShowReplyComposer(focus);
    }

    private void ShowReplyComposer(bool focus = true, string? targetLanguage = null)
    {
        ResultPanel.Padding = MaxResultTextPadding;
        ResultPanel.Visibility = Visibility.Visible;
        ReplyComposerPanel.Visibility = Visibility.Visible;
        ReplyTextBox.ToolTip = $"Type in {AppConfig.NormalizeTargetLanguage(targetLanguage ?? _config.TargetLanguage)}";
        UpdateReplyComposerMargin();
        FitResultText();
        if (!focus)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                ReplyTextBox.Focus();
                Keyboard.Focus(ReplyTextBox);
                FitResultText();
            },
            DispatcherPriority.Loaded);
    }

    private void HideReplyComposer()
    {
        ReplyComposerPanel.Visibility = Visibility.Collapsed;
        ReplyTextBox.Clear();
        if (ResultTextPanel.Visibility != Visibility.Visible || ResultTextPanel.Children.Count == 0)
        {
            ResultPanel.Visibility = Visibility.Collapsed;
        }

        FitResultText();
    }

    private void SetStatus(string text)
    {
        _statusClearTimer.Stop();
        StatusText.Text = text;
        StatusLabel.Visibility = Visibility.Visible;
        _statusClearTimer.Start();
    }

    private void ClearStatus()
    {
        _statusClearTimer.Stop();
        StatusText.Text = string.Empty;
        StatusLabel.Visibility = Visibility.Collapsed;
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
        HideReplyComposer();
        ClearStatus();
        CollapseFrame();
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
            TargetGame = config.TargetGame,
            Mode = config.Mode,
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
            UpdateReplyComposerMargin();
            ApplyResultTextLayout(fontSize, TextTrimming.None);
            var availableWidth = Math.Max(1, ResultPanel.ActualWidth - ResultPanel.Padding.Left - ResultPanel.Padding.Right);
            var availableHeight = Math.Max(1, ResultPanel.ActualHeight - ResultPanel.Padding.Top - ResultPanel.Padding.Bottom);
            if (ReplyComposerPanel.Visibility == Visibility.Visible)
            {
                availableHeight = Math.Max(
                    1,
                    availableHeight - ReplyComposerPanel.ActualHeight - ReplyComposerPanel.Margin.Top);
            }

            ResultTextPanel.Measure(new Size(availableWidth, double.PositiveInfinity));

            if (ResultTextPanel.DesiredSize.Width <= availableWidth &&
                ResultTextPanel.DesiredSize.Height <= availableHeight)
            {
                return;
            }
        }

        ResultPanel.Padding = MinResultTextPadding;
        UpdateReplyComposerMargin();
        ApplyResultTextLayout(MinResultFontSize, TextTrimming.CharacterEllipsis);
    }

    private void UpdateReplyComposerMargin()
    {
        var horizontalOffset = ResultPanel.Padding.Bottom - ResultPanel.Padding.Left;
        ReplyComposerPanel.Margin = new Thickness(
            horizontalOffset,
            ReplyComposerPanel.Margin.Top,
            horizontalOffset,
            0);
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
    AppMode Mode,
    string TargetLanguage,
    string OperationId,
    IReadOnlyList<SearchQueryResult> SearchQueries,
    string TargetGame,
    string SearchPrefix);
