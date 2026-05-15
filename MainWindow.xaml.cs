using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Peek;

public partial class MainWindow : Window
{
    private const double CollapsedWidth = 84;
    private const double CollapsedHeight = 24;
    private const double FrameRevealHeight = 48;
    private const double MaxResultFontSize = 24;
    private const double MinResultFontSize = 8;
    private const int WmNcHitTest = 0x0084;
    private const nint HtClient = 1;
    private const nint HtTransparent = -1;

    private readonly ScreenCaptureService _screenCapture = new();
    private readonly OpenRouterClient _openRouter = new();
    private AppConfig _config = AppConfigStore.Load();
    private CancellationTokenSource? _translationCancellation;
    private Point? _dragButtonPressPoint;
    private Point? _dragStartCursor;
    private Point? _lockedDragOffset;
    private Point? _resizeStartCursor;
    private Vector _resizeCursorToBottomRightOffset;
    private bool _dragButtonDragged;
    private bool _resizeButtonDragged;
    private bool _isDragLocked;
    private bool _isResizeLocked;
    private bool _resizeExpandedDuringInteraction;
    private bool _isTranslating;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Drawing.Icon? _trayIconImage;
    private SettingsWindow? _settingsWindow;
    private readonly DispatcherTimer _lockedDragTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(16)
    };
    private readonly DispatcherTimer _lockedResizeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(16)
    };
    private readonly DoubleAnimation _spinnerAnimation = new(0, 360, TimeSpan.FromMilliseconds(700))
    {
        RepeatBehavior = RepeatBehavior.Forever
    };

    public MainWindow()
    {
        InitializeComponent();
        _lockedDragTimer.Tick += (_, _) => UpdateLockedDragPosition();
        _lockedResizeTimer.Tick += (_, _) => UpdateLockedResize();
        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        _trayMenu = new Forms.ContextMenuStrip();
        var startupItem = new Forms.ToolStripMenuItem("Run on startup")
        {
            CheckOnClick = true
        };
        startupItem.Click += (_, _) => Dispatcher.Invoke(() => ToggleStartupFromTray(startupItem));
        _trayMenu.Opening += (_, _) => startupItem.Checked = StartupService.IsEnabled();

        _trayMenu.Items.Add(startupItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
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

    private void ToggleStartupFromTray(Forms.ToolStripMenuItem item)
    {
        try
        {
            StartupService.SetEnabled(item.Checked);
        }
        catch (Exception ex)
        {
            item.Checked = StartupService.IsEnabled();
            MessageBox.Show(this, ex.Message, "Startup setting", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Drawing.Icon CreateTrayIcon()
    {
        _trayIconImage?.Dispose();

        using var bitmap = new Drawing.Bitmap(32, 32);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Drawing.Color.Transparent);

        using var buttonPath = RoundedRectangle(2, 2, 28, 28, 6);
        using var buttonBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xDD, 0xFD, 0xD8, 0x4D));
        graphics.FillPath(buttonBrush, buttonPath);

        using var dotBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xCC, 0x16, 0x16, 0x16));
        graphics.FillEllipse(dotBrush, 9, 9, 4, 4);
        graphics.FillEllipse(dotBrush, 19, 9, 4, 4);
        graphics.FillEllipse(dotBrush, 9, 19, 4, 4);
        graphics.FillEllipse(dotBrush, 19, 19, 4, 4);

        var handle = bitmap.GetHicon();
        try
        {
            _trayIconImage = (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            Win32.DestroyIcon(handle);
        }

        return _trayIconImage;
    }

    private static Drawing.Drawing2D.GraphicsPath RoundedRectangle(int x, int y, int width, int height, int radius)
    {
        var path = new Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;

        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
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
        Win32.SetWindowDisplayAffinity(handle, Win32.WdaExcludeFromCapture);

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
        return IsPointInside(TranslateButton, windowPoint) ||
               IsPointInside(DragButton, windowPoint) ||
               IsPointInside(ClearButton, windowPoint) ||
               IsPointInside(ResizeRowButton, windowPoint) ||
               IsPointInside(ResizeCornerButton, windowPoint);
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
        FitResultText();
    }

    private void DragButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isDragLocked)
        {
            StopLockedDrag();
            e.Handled = true;
            return;
        }

        _dragButtonPressPoint = e.GetPosition(this);
        _dragStartCursor = GetCursorDipPoint();
        _lockedDragOffset = _dragButtonPressPoint;
        _dragButtonDragged = false;
        DragButton.CaptureMouse();
        _lockedDragTimer.Start();
        e.Handled = true;
    }

    private void DragButton_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragLocked)
        {
            UpdateLockedDragPosition();
            e.Handled = true;
            return;
        }

        if (_dragButtonPressPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateLockedDragPosition();
        e.Handled = true;
    }

    private void DragButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragButtonPressPoint is null)
        {
            return;
        }

        if (!_dragButtonDragged)
        {
            StartLockedDrag(_dragButtonPressPoint.Value);
            _dragButtonPressPoint = null;
            e.Handled = true;
            return;
        }

        DragButton.ReleaseMouseCapture();
        _dragButtonPressPoint = null;
        _dragStartCursor = null;
        _lockedDragOffset = null;
        _dragButtonDragged = false;
        _lockedDragTimer.Stop();
        e.Handled = true;
    }

    private void StartLockedDrag(Point offset)
    {
        _lockedDragOffset = offset;
        _dragButtonDragged = false;
        _isDragLocked = true;
        DragButton.CaptureMouse();
        _lockedDragTimer.Start();
    }

    private void StopLockedDrag()
    {
        _isDragLocked = false;
        _lockedDragOffset = null;
        _dragButtonPressPoint = null;
        _dragStartCursor = null;
        _dragButtonDragged = false;
        DragButton.ReleaseMouseCapture();
        _lockedDragTimer.Stop();
    }

    private void ResizeButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isResizeLocked)
        {
            StopLockedResize();
            e.Handled = true;
            return;
        }

        if (sender == ResizeRowButton)
        {
            ResizeRowButton.Visibility = Visibility.Collapsed;
            ResizeCornerButton.Visibility = Visibility.Visible;
            StartCursorAnchoredResize();
            e.Handled = true;
            return;
        }

        StartCursorAnchoredResize();
        e.Handled = true;
    }

    private void StartCursorAnchoredResize()
    {
        _resizeStartCursor = GetCursorDipPoint();
        _resizeCursorToBottomRightOffset = new Vector(
            Width - (_resizeStartCursor.Value.X - Left),
            Height - (_resizeStartCursor.Value.Y - Top));
        _resizeExpandedDuringInteraction = FrameBorder.Visibility == Visibility.Visible;
        _resizeButtonDragged = false;
        ResizeCornerButton.CaptureMouse();
        _lockedResizeTimer.Start();
    }

    private void ResizeButton_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isResizeLocked)
        {
            UpdateLockedResize();
            e.Handled = true;
            return;
        }

        if (_resizeStartCursor is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateLockedResize();
        e.Handled = true;
    }

    private void ResizeButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizeStartCursor is null)
        {
            return;
        }

        if (!_resizeButtonDragged)
        {
            StartLockedResize();
            e.Handled = true;
            return;
        }

        ResizeCornerButton.ReleaseMouseCapture();
        if (!_resizeExpandedDuringInteraction)
        {
            CollapseFrame();
        }

        _resizeStartCursor = null;
        _resizeButtonDragged = false;
        _lockedResizeTimer.Stop();
        e.Handled = true;
    }

    private void StartLockedResize()
    {
        _isResizeLocked = true;
        _resizeButtonDragged = false;
        ResizeCornerButton.CaptureMouse();
        _lockedResizeTimer.Start();
    }

    private void StopLockedResize()
    {
        _isResizeLocked = false;
        _resizeStartCursor = null;
        _resizeButtonDragged = false;
        ResizeCornerButton.ReleaseMouseCapture();
        _lockedResizeTimer.Stop();

        if (FrameBorder.Visibility != Visibility.Visible)
        {
            CollapseFrame();
        }
    }

    private void UpdateLockedResize()
    {
        if (_resizeStartCursor is null)
        {
            return;
        }

        var cursor = GetCursorDipPoint();
        var delta = cursor - _resizeStartCursor.Value;

        if (!_isResizeLocked)
        {
            if (!_resizeButtonDragged &&
                Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _resizeButtonDragged = true;
        }

        ApplyResizeToCursor(cursor);
    }

    private void ApplyResizeToCursor(Point cursor)
    {
        var nextWidth = Math.Max(MinWidth, cursor.X - Left + _resizeCursorToBottomRightOffset.X);
        var nextHeight = Math.Max(MinHeight, cursor.Y - Top + _resizeCursorToBottomRightOffset.Y);

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
        ResizeCornerButton.Visibility = Visibility.Collapsed;
        ResizeRowButton.Visibility = Visibility.Visible;
        Width = CollapsedWidth;
        Height = CollapsedHeight;
    }

    private void FinishResizeInteraction()
    {
        _isResizeLocked = false;
        _resizeStartCursor = null;
        _resizeButtonDragged = false;
        _resizeExpandedDuringInteraction = false;
        _lockedResizeTimer.Stop();

        if (ResizeRowButton.IsMouseCaptured)
        {
            ResizeRowButton.ReleaseMouseCapture();
        }

        if (ResizeCornerButton.IsMouseCaptured)
        {
            ResizeCornerButton.ReleaseMouseCapture();
        }
    }

    private void UpdateLockedDragPosition()
    {
        if (_lockedDragOffset is null ||
            !Win32.GetCursorPos(out var cursorPosition) ||
            PresentationSource.FromVisual(this)?.CompositionTarget is not { } compositionTarget)
        {
            return;
        }

        var screenPoint = compositionTarget.TransformFromDevice.Transform(new Point(cursorPosition.X, cursorPosition.Y));

        if (!_isDragLocked && _dragStartCursor is not null)
        {
            var delta = screenPoint - _dragStartCursor.Value;
            if (!_dragButtonDragged &&
                Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _dragButtonDragged = true;
        }

        Left = screenPoint.X - _lockedDragOffset.Value.X;
        Top = screenPoint.Y - _lockedDragOffset.Value.Y;
    }

    private Point GetCursorDipPoint()
    {
        if (!Win32.GetCursorPos(out var cursorPosition) ||
            PresentationSource.FromVisual(this)?.CompositionTarget is not { } compositionTarget)
        {
            return new Point(Left, Top);
        }

        return compositionTarget.TransformFromDevice.Transform(new Point(cursorPosition.X, cursorPosition.Y));
    }

    private async void TranslateButton_Click(object sender, RoutedEventArgs e)
    {
        await TranslateCurrentAreaAsync();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ClearResult();
    }

    private void SettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private void StartupMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item)
        {
            try
            {
                StartupService.SetEnabled(item.IsChecked);
            }
            catch (Exception ex)
            {
                item.IsChecked = StartupService.IsEnabled();
                MessageBox.Show(this, ex.Message, "Startup setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void WindowMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Header is string header &&
                string.Equals(header, "Run on startup", StringComparison.Ordinal))
            {
                item.IsChecked = StartupService.IsEnabled();
                return;
            }
        }
    }

    private void QuitMenu_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

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
            AppLogger.Info("Translate skipped: frame is collapsed.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            OpenSettings();
            if (string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                AppLogger.Error("Translate skipped: API key required.");
                return;
            }
        }

        _translationCancellation?.Dispose();
        _translationCancellation = new CancellationTokenSource();
        var cancellationSource = _translationCancellation;
        var cancellationToken = cancellationSource.Token;
        var operationId = Guid.NewGuid().ToString("N")[..12];
        var stopwatch = Stopwatch.StartNew();
        var captureWidth = 0;
        var captureHeight = 0;

        try
        {
            AppLogger.Info($"operation={operationId} translate.start bounds=({Left:0},{Top:0},{FrameBorder.ActualWidth:0}x{FrameBorder.ActualHeight:0}) model={_config.Model}");
            SetBusy(true);

            using var bitmap = _screenCapture.CaptureVisualBounds(this, FrameBorder);
            captureWidth = bitmap.Width;
            captureHeight = bitmap.Height;

            var result = await _openRouter.TranslateImageToTextAsync(bitmap, _config, operationId, cancellationToken);
            stopwatch.Stop();
            SetResultText(result.Text);
            TrackUsage(
                operationId,
                result.ProviderRequestId,
                true,
                result.CostUsd,
                captureWidth,
                captureHeight,
                stopwatch.ElapsedMilliseconds,
                result.Usage,
                null,
                null);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            AppLogger.Info($"operation={operationId} translate.cancelled");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppLogger.Error("Translate failed.", ex);
            var shortError = ToShortUserError(ex);
            AppLogger.Info($"operation={operationId} user_error={shortError}");
            TrackUsage(
                operationId,
                null,
                false,
                0,
                captureWidth,
                captureHeight,
                stopwatch.ElapsedMilliseconds,
                new TokenUsage(0, 0, 0),
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
        _translationCancellation?.Cancel();
        _translationCancellation?.Dispose();
        _translationCancellation = null;
        _lockedDragTimer.Stop();
        _lockedResizeTimer.Stop();
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
                }
                catch (Exception ex)
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

    private void SetResultText(string text)
    {
        ResultText.Text = text;
        ResultPanel.Visibility = Visibility.Visible;
        FitResultText();
        Dispatcher.BeginInvoke(FitResultText, DispatcherPriority.Loaded);
    }

    private void ClearResult()
    {
        ResultText.Text = string.Empty;
        ResultText.FontSize = MaxResultFontSize;
        ResultPanel.Visibility = Visibility.Collapsed;
        CollapseFrame();
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
            catch (Exception ex)
            {
                AppLogger.Error("Could not save total cost.", ex);
            }
        }

        var entry = new UsageLogEntry(
            DateTimeOffset.Now,
            operationId,
            providerRequestId,
            _config.Model,
            _config.FromLanguage,
            _config.ToLanguage,
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
            $"model={_config.Model} " +
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
            return "API key required.";
        }

        if (message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("403", StringComparison.OrdinalIgnoreCase))
        {
            return "API key rejected.";
        }

        if (message.Contains("429", StringComparison.OrdinalIgnoreCase))
        {
            return "Rate limited.";
        }

        if (message.Contains("400", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            return "Request failed.";
        }

        if (message.Contains("No translation", StringComparison.OrdinalIgnoreCase))
        {
            return "No translation.";
        }

        return "Translation failed.";
    }

    private void FitResultText()
    {
        if (!ResultPanel.IsVisible ||
            string.IsNullOrWhiteSpace(ResultText.Text) ||
            ResultPanel.ActualWidth <= 0 ||
            ResultPanel.ActualHeight <= 0)
        {
            return;
        }

        ResultText.FontSize = MaxResultFontSize;
        ResultText.TextTrimming = TextTrimming.None;

        var availableWidth = Math.Max(1, ResultPanel.ActualWidth - ResultPanel.Padding.Left - ResultPanel.Padding.Right);
        var availableHeight = Math.Max(1, ResultPanel.ActualHeight - ResultPanel.Padding.Top - ResultPanel.Padding.Bottom);

        for (var fontSize = MaxResultFontSize; fontSize >= MinResultFontSize; fontSize -= 0.5)
        {
            ResultText.FontSize = fontSize;
            ResultText.Measure(new Size(availableWidth, double.PositiveInfinity));

            if (ResultText.DesiredSize.Width <= availableWidth &&
                ResultText.DesiredSize.Height <= availableHeight)
            {
                ResultText.TextTrimming = TextTrimming.None;
                return;
            }
        }

        ResultText.FontSize = MinResultFontSize;
        ResultText.TextTrimming = TextTrimming.CharacterEllipsis;
    }
}
