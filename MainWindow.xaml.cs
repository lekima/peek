using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
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
using Forms = System.Windows.Forms;
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
    private static readonly Uri AppIconUri = new("pack://application:,,,/Resources/AppIcon.ico", UriKind.Absolute);

    private AppConfig _config = AppConfigStore.Load();
    private CancellationTokenSource? _translationCancellation;
    private Point? _dragStartCursor;
    private Point? _dragStartWindowPosition;
    private Point? _resizeStartCursor;
    private Size _resizeStartWindowSize;
    private bool _dragButtonDragged;
    private bool _resizeButtonDragged;
    private bool _resizeExpandedDuringInteraction;
    private bool _isTranslating;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Drawing.Icon? _trayIconImage;
    private SettingsWindow? _settingsWindow;
    private readonly Dictionary<System.Windows.Controls.Button, SearchButtonState> _searchButtons = [];
    private readonly DoubleAnimation _spinnerAnimation = new(0, 360, TimeSpan.FromMilliseconds(700))
    {
        RepeatBehavior = RepeatBehavior.Forever
    };

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();
        AppLogger.Event("app_start", new
        {
            appDirectory = AppPaths.AppDirectory,
            resultFormat = _config.ResultFormat.ToString(),
            targetLanguage = _config.TargetLanguage,
            targetGame = TargetGames.GetDisplayName(_config.TargetGame),
            searchProfile = AppConfig.SearchProfile,
            searchSource = AppConfig.SearchSource,
            searchLanguage = AppConfig.SearchLanguage,
            searchPrefix = TargetGames.GetSearchPrefix(_config.TargetGame),
            totalCostUsd = _config.TotalCostUsd
        });
    }

    [SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "Peek is the product name shown in the tray.")]
    private void InitializeTrayIcon()
    {
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        _trayMenu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(Close));

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Peek",
            Icon = CreateTrayIcon(),
            ContextMenuStrip = _trayMenu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() =>
        {
            Show();
            Activate();
        });
    }

    private Drawing.Icon CreateTrayIcon()
    {
        _trayIconImage?.Dispose();

        var streamInfo = System.Windows.Application.GetResourceStream(AppIconUri) ??
            throw new InvalidOperationException("Application icon resource is missing.");
        using var stream = streamInfo.Stream;
        using var icon = new Drawing.Icon(stream);

        _trayIconImage = (Drawing.Icon)icon.Clone();
        return _trayIconImage;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            OpenSettings();
        }
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!Win32.SetWindowDisplayAffinity(handle, Win32.WdaExcludeFromCapture))
        {
            AppLogger.Info($"SetWindowDisplayAffinity failed error={Win32.GetLastError()}");
        }

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
            IsPointInside(SearchButtonsPanel, windowPoint);
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
        FitResultText();
    }

    private void CollapseFrame()
    {
        FrameBorder.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
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

        if (ResizeRowButton.IsMouseCaptured)
        {
            ResizeRowButton.ReleaseMouseCapture();
        }

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
            searchButton.SearchProfile,
            searchButton.SearchSource,
            searchButton.SearchLanguage,
            searchButton.SearchPrefix,
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

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            OpenSettings();
            if (string.IsNullOrWhiteSpace(_config.ApiKey))
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
        var searchContext = CreateSearchContext(operationConfig);
        var operationId = Guid.NewGuid().ToString("N")[..12];
        var stopwatch = Stopwatch.StartNew();
        var captureWidth = 0;
        var captureHeight = 0;
        var resultFormat = operationConfig.ResultFormat;
        var model = AppConfig.GetModel(resultFormat);
        string? providerRequestId = null;
        decimal responseCost = 0;
        var responseUsage = new TokenUsage(0, 0, 0);
        var usageTracked = false;

        try
        {
            AppLogger.Event("translate_start", new
            {
                operationId,
                model,
                resultFormat = resultFormat.ToString(),
                targetLanguage = operationConfig.TargetLanguage,
                targetGame = searchContext.TargetGame,
                searchProfile = searchContext.Profile,
                searchSource = searchContext.Source,
                searchLanguage = searchContext.Language,
                searchPrefix = searchContext.SearchPrefix,
                windowLeft = Left,
                windowTop = Top,
                frameWidth = FrameBorder.ActualWidth,
                frameHeight = FrameBorder.ActualHeight
            });
            SetBusy(true);
            ClearStatus();

            using var bitmap = ScreenCaptureService.CaptureVisualBounds(this, FrameBorder);
            captureWidth = bitmap.Width;
            captureHeight = bitmap.Height;
            var capturePath = SaveCapture(operationId, resultFormat, bitmap);

            if (resultFormat == ResultFormat.Image)
            {
                var imageResult = await OpenRouterClient.TranslateImageToEditedImageAsync(bitmap, operationConfig, model, operationId, cancellationToken).ConfigureAwait(true);
                providerRequestId = imageResult.ProviderRequestId;
                responseCost = imageResult.CostUsd;
                responseUsage = imageResult.Usage;
                stopwatch.Stop();
                cancellationToken.ThrowIfCancellationRequested();
                SetResultImage(imageResult.ImageData, operationId);
                TrackUsage(
                    operationId,
                    providerRequestId,
                    true,
                    responseCost,
                    captureWidth,
                    captureHeight,
                    stopwatch.ElapsedMilliseconds,
                    responseUsage,
                    model,
                    operationConfig,
                    searchContext,
                    null,
                    null);
                usageTracked = true;
                return;
            }

            var result = await OpenRouterClient.TranslateImageToTextAsync(bitmap, operationConfig, model, operationId, cancellationToken).ConfigureAwait(true);
            providerRequestId = result.ProviderRequestId;
            responseCost = result.CostUsd;
            responseUsage = result.Usage;
            stopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();
            SetResultText(operationId, result.Text, result.SearchQueries, searchContext);
            AppLogger.TextResult(new TextResultLogEntry(
                DateTimeOffset.Now,
                operationId,
                model,
                operationConfig.TargetLanguage,
                searchContext.TargetGame,
                searchContext.Profile,
                searchContext.Source,
                searchContext.Language,
                searchContext.SearchPrefix,
                capturePath,
                result.Text,
                result.SearchQueries));
            ClearStatus();
            TrackUsage(
                operationId,
                providerRequestId,
                true,
                responseCost,
                captureWidth,
                captureHeight,
                stopwatch.ElapsedMilliseconds,
                responseUsage,
                model,
                operationConfig,
                searchContext,
                null,
                null);
            usageTracked = true;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            AppLogger.Info($"operation={operationId} translate.cancelled");
            if (!usageTracked && (responseCost > 0 || providerRequestId is not null))
            {
                TrackUsage(
                    operationId,
                    providerRequestId,
                    false,
                    responseCost,
                    captureWidth,
                    captureHeight,
                    stopwatch.ElapsedMilliseconds,
                    responseUsage,
                    model,
                    operationConfig,
                    searchContext,
                    nameof(OperationCanceledException),
                    "Cancelled");
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppLogger.Error("Translate failed.", ex);
            var shortError = ToShortUserError(ex);
            SetStatus(shortError);
            AppLogger.Info($"operation={operationId} user_error={shortError}");
            TrackUsage(
                operationId,
                providerRequestId,
                false,
                responseCost,
                captureWidth,
                captureHeight,
                stopwatch.ElapsedMilliseconds,
                responseUsage,
                model,
                operationConfig,
                searchContext,
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

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.Event("app_closed", new { reason = "window_closed" });
        _translationCancellation?.Cancel();
        _translationCancellation?.Dispose();
        _translationCancellation = null;
        if (_trayIcon is not null)
        {
            _trayIcon.ContextMenuStrip = null;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;

        _trayIconImage?.Dispose();
        _trayIconImage = null;

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
            resultFormat = _config.ResultFormat.ToString(),
            targetLanguage = _config.TargetLanguage,
            targetGame = TargetGames.GetDisplayName(_config.TargetGame),
            searchProfile = AppConfig.SearchProfile,
            searchSource = AppConfig.SearchSource,
            searchLanguage = AppConfig.SearchLanguage,
            searchPrefix = TargetGames.GetSearchPrefix(_config.TargetGame),
            startupEnabled = StartupService.IsEnabled()
        });

        _settingsWindow = new SettingsWindow(_config)
        {
            Owner = this
        };

        try
        {
            if (_settingsWindow.ShowDialog() == true)
            {
                try
                {
                    AppConfigStore.Save(_config);
                    AppLogger.Event("settings_saved", new
                    {
                        resultFormat = _config.ResultFormat.ToString(),
                        targetLanguage = _config.TargetLanguage,
                        targetGame = TargetGames.GetDisplayName(_config.TargetGame),
                        searchProfile = AppConfig.SearchProfile,
                        searchSource = AppConfig.SearchSource,
                        searchLanguage = AppConfig.SearchLanguage,
                        searchPrefix = TargetGames.GetSearchPrefix(_config.TargetGame),
                        startupEnabled = StartupService.IsEnabled(),
                        totalCostUsd = _config.TotalCostUsd
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

    private void SetResultText(
        string operationId,
        string text,
        IReadOnlyList<SearchQueryResult> searchQueries,
        SearchContext searchContext)
    {
        ResultImage.Source = null;
        ResultImage.Visibility = Visibility.Collapsed;
        SetResultTextLines(text);
        ResultTextPanel.Visibility = Visibility.Visible;
        SetSearchButtons(operationId, searchQueries, searchContext);
        ResultPanel.Padding = MaxResultTextPadding;
        ResultPanel.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE6, 0x11, 0x11, 0x11));
        ResultPanel.Visibility = Visibility.Visible;
        UpdateResultPanelClip();
        FitResultText();
        Dispatcher.BeginInvoke(FitResultText, DispatcherPriority.Loaded);
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusLabel.Visibility = Visibility.Visible;
    }

    private void ClearStatus()
    {
        StatusText.Text = string.Empty;
        StatusLabel.Visibility = Visibility.Collapsed;
    }

    private void SetResultImage(string imageDataUrl, string operationId)
    {
        ResultTextPanel.Children.Clear();
        ResultTextPanel.Visibility = Visibility.Collapsed;
        ClearSearchButtons();
        var resultPath = SaveImageDataUrl(operationId, imageDataUrl);
        AppLogger.Event("image_result", new
        {
            operationId,
            path = resultPath
        });
        ResultImage.Source = LoadImageDataUrl(imageDataUrl);
        ResultImage.Visibility = Visibility.Visible;
        ResultPanel.Padding = new Thickness(0);
        ResultPanel.Background = System.Windows.Media.Brushes.Transparent;
        ResultPanel.Visibility = Visibility.Visible;
        UpdateResultPanelClip();
        ClearStatus();
    }

    private void ClearResult()
    {
        ResultTextPanel.Children.Clear();
        ResultTextPanel.Visibility = Visibility.Collapsed;
        ClearSearchButtons();
        ResultImage.Source = null;
        ResultImage.Visibility = Visibility.Collapsed;
        ClearStatus();
        ResultPanel.Visibility = Visibility.Collapsed;
        CollapseFrame();
    }

    private static BitmapImage LoadImageDataUrl(string dataUrl)
    {
        var commaIndex = dataUrl.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex < 0)
        {
            throw new InvalidOperationException("Image response was not a data URL.");
        }

        var bytes = Convert.FromBase64String(dataUrl[(commaIndex + 1)..]);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void SetSearchButtons(string operationId, IReadOnlyList<SearchQueryResult> searchQueries, SearchContext searchContext)
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

            var searchUrl = BuildSearchUrl(searchQuery.Query, searchContext);
            if (string.IsNullOrWhiteSpace(searchUrl))
            {
                continue;
            }

            var button = buttons[buttonIndex];
            button.ToolTip = string.IsNullOrWhiteSpace(searchQuery.Intent)
                ? AppConfig.SearchSource
                : searchQuery.Intent;
            button.Visibility = Visibility.Visible;
            _searchButtons[button] = new SearchButtonState(
                operationId,
                buttonIndex + 1,
                searchQuery,
                searchContext.TargetGame,
                searchContext.Profile,
                searchContext.Source,
                searchContext.Language,
                searchContext.SearchPrefix,
                searchUrl);
            buttonIndex++;
        }

        SearchButtonsPanel.Visibility = _searchButtons.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string BuildSearchUrl(string searchQuery, SearchContext searchContext)
    {
        var keyword = searchQuery.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return string.Empty;
        }

        var prefixedKeyword = string.IsNullOrWhiteSpace(searchContext.SearchPrefix) ||
            keyword.StartsWith(searchContext.SearchPrefix, StringComparison.OrdinalIgnoreCase)
                ? keyword
                : $"{searchContext.SearchPrefix} {keyword}";
        return string.Format(
            CultureInfo.InvariantCulture,
            searchContext.UrlTemplate,
            Uri.EscapeDataString(prefixedKeyword));
    }

    private void ClearSearchButtons()
    {
        _searchButtons.Clear();
        SearchButtonsPanel.Visibility = Visibility.Collapsed;
        SearchButton1.Visibility = Visibility.Collapsed;
        SearchButton2.Visibility = Visibility.Collapsed;
        SearchButton3.Visibility = Visibility.Collapsed;
        var searchLabel = $"Search {AppConfig.SearchSource}";
        SearchButton1.ToolTip = searchLabel;
        SearchButton2.ToolTip = searchLabel;
        SearchButton3.ToolTip = searchLabel;
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

    private string? SaveCapture(string operationId, ResultFormat resultFormat, Drawing.Bitmap bitmap)
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
                resultFormat.ToString(),
                path,
                bitmap.Width,
                bitmap.Height,
                FrameBorder.ActualWidth,
                FrameBorder.ActualHeight));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ExternalException)
        {
            AppLogger.Error("Could not save capture.", ex);
            return null;
        }
    }

    private static string? SaveImageDataUrl(string operationId, string dataUrl)
    {
        try
        {
            var commaIndex = dataUrl.IndexOf(',', StringComparison.Ordinal);
            if (commaIndex < 0)
            {
                return null;
            }

            var bytes = Convert.FromBase64String(dataUrl[(commaIndex + 1)..]);
            var directory = Path.Combine(AppPaths.DataDirectory, "results");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{operationId}.png");
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or FormatException)
        {
            AppLogger.Error("Could not save image result.", ex);
            return null;
        }
    }

    private void TrackUsage(
        string operationId,
        string? providerRequestId,
        bool success,
        decimal cost,
        int width,
        int height,
        long elapsedMilliseconds,
        TokenUsage usage,
        string model,
        AppConfig operationConfig,
        SearchContext searchContext,
        string? errorKind,
        string? errorMessage)
    {
        if (cost > 0)
        {
            _config.TotalCostUsd += cost;
            try
            {
                AppConfigStore.Save(_config);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or CryptographicException)
            {
                AppLogger.Error("Could not save total cost.", ex);
            }
        }

        var entry = new UsageLogEntry(
            DateTimeOffset.Now,
            operationId,
            providerRequestId,
            model,
            operationConfig.ResultFormat.ToString(),
            operationConfig.TargetLanguage,
            searchContext.TargetGame,
            searchContext.Profile,
            searchContext.Source,
            searchContext.Language,
            searchContext.SearchPrefix,
            success,
            cost,
            _config.TotalCostUsd,
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
            $"cost={OpenRouterClient.FormatCost(cost)} " +
            $"total={OpenRouterClient.FormatCost(_config.TotalCostUsd)} " +
            $"duration_ms={elapsedMilliseconds} " +
            $"tokens={usage.TotalTokens} " +
            $"capture={width}x{height}");
    }

    private static string ToShortUserError(Exception exception)
    {
        var message = exception.Message;

        if (message.Contains("API key", StringComparison.OrdinalIgnoreCase))
        {
            return "API key required";
        }

        if (message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("403", StringComparison.OrdinalIgnoreCase))
        {
            return "API key rejected";
        }

        if (message.Contains("429", StringComparison.OrdinalIgnoreCase))
        {
            return "Rate limited";
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
            ResultFormat = config.ResultFormat,
            TargetLanguage = config.TargetLanguage,
            TargetGame = config.TargetGame,
            TotalCostUsd = config.TotalCostUsd
        };

    private static SearchContext CreateSearchContext(AppConfig config) =>
        new(
            AppConfig.SearchProfile,
            AppConfig.SearchSource,
            AppConfig.SearchLanguage,
            TargetGames.GetDisplayName(config.TargetGame),
            TargetGames.GetSearchPrefix(config.TargetGame),
            AppConfig.SearchUrlTemplate);

    private void FitResultText()
    {
        if (!ResultPanel.IsVisible ||
            ResultImage.Visibility == Visibility.Visible ||
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
    string SearchProfile,
    string SearchSource,
    string SearchLanguage,
    string SearchPrefix,
    string Url);

internal sealed record SearchContext(
    string Profile,
    string Source,
    string Language,
    string TargetGame,
    string SearchPrefix,
    string UrlTemplate);
