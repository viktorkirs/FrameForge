using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FrameForge.Capture;
using FrameForge.Core;

namespace FrameForge.UI;

public sealed class RegionWindow : Window
{
    const double BarGap = 8;
    const double BarMargin = 8;
    const uint SwpShowWindow = 0x0040;
    const int WmGetMinMaxInfo = 0x0024;
    const int WmWindowPosChanging = 0x0046;

    readonly RegionCanvas _canvas;
    readonly Canvas _overlay;
    readonly Border _plate;
    readonly StackPanel _stack;
    readonly StackPanel _buttons;
    readonly TextBlock _help;
    readonly Button _confirm;
    readonly Button _move;
    readonly ScreenRect _pending;
    int _monLeft;
    int _monTop;
    int _monWidth;
    int _monHeight;
    uint _dpiX = 96;
    uint _dpiY = 96;
    int _x;
    int _y;
    int _width;
    int _height;
    int _anchorX;
    int _anchorY;
    int _originX;
    int _originY;
    int _originWidth;
    int _originHeight;
    double _barX;
    double _barY;
    double _barW;
    double _barH;
    bool _buttonsNearTop = true;
    bool _buttonsBelow = true;
    bool _fitting;
    bool _placeQueued;
    DragKind _drag = DragKind.None;

    RegionWindow(ScreenRect current, MonitorSpan monitor)
    {
        _pending = current;
        ApplyMonitor(monitor);

        Title = Loc.Get("Region");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Cursor = Cursors.Cross;

        _canvas = new RegionCanvas(this);
        _help = new TextBlock
        {
            Text = Loc.Get("RegionHelp"),
            Foreground = Brushes.White,
            FontSize = 13,
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 380
        };
        var cancel = new Button
        {
            Content = Loc.Get("Cancel"),
            MinWidth = 108,
            Height = 36,
            Padding = new Thickness(16, 0, 16, 0),
            Margin = new Thickness(0, 0, 8, 0),
            Background = (Brush)FindResource("CardBrush"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 14
        };
        cancel.Click += (_, _) => DialogResult = false;
        _confirm = new Button
        {
            Content = Loc.Get("UseRegion"),
            MinWidth = 160,
            Height = 36,
            Padding = new Thickness(16, 0, 16, 0),
            Background = (Brush)FindResource("AccentBrush"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 14,
            IsEnabled = false
        };
        _confirm.Click += (_, _) => Confirm();
        _move = new Button
        {
            Content = "↔ " + Loc.Get("MoveRegion"),
            Height = 36,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.SizeAll,
            IsEnabled = false,
            ToolTip = Loc.Get("RegionMoveHint")
        };
        _move.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_width < 16 || _height < 16)
                return;
            (int x, int y) = CursorPixels();
            BeginDrag(DragKind.Move, x, y);
            e.Handled = true;
        };
        _buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _buttons.Children.Add(_move);
        _buttons.Children.Add(cancel);
        _buttons.Children.Add(_confirm);
        _stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _help.Margin = new Thickness(0, 8, 0, 0);
        _stack.Children.Add(_buttons);
        _stack.Children.Add(_help);
        var plateFill = new SolidColorBrush(Color.FromArgb(236, 16, 20, 28));
        plateFill.Freeze();
        var plateStroke = new SolidColorBrush(Color.FromRgb(52, 60, 78));
        plateStroke.Freeze();
        _plate = new Border
        {
            Background = plateFill,
            BorderBrush = plateStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            SnapsToDevicePixels = true,
            Cursor = Cursors.Arrow,
            Child = _stack
        };
        _overlay = new Canvas { Background = null };
        _overlay.Children.Add(_plate);

        var grid = new Grid();
        grid.Children.Add(_canvas);
        grid.Children.Add(_overlay);
        Content = grid;

        AdoptPending();
        _confirm.IsEnabled = _width >= 16 && _height >= 16;
        _move.IsEnabled = _confirm.IsEnabled;
        ApplyDipBounds();
        DpiChanged += (_, _) => Refit();
        SizeChanged += (_, _) =>
        {
            if (_fitting || _placeQueued)
                return;
            _placeQueued = true;
            Dispatcher.BeginInvoke(() =>
            {
                _placeQueued = false;
                PlaceBar();
            }, DispatcherPriority.Render);
        };
        Loaded += (_, _) =>
        {
            Refit();
            Activate();
            Focus();
        };
        ContentRendered += (_, _) => PlaceBar();
    }

    public ScreenRect Result { get; private set; }

    public static bool TrySelect(Window? owner, ScreenRect current, out ScreenRect rect)
    {
        MonitorSpan monitor = ResolveMonitor(owner, current);
        var window = new RegionWindow(current, monitor);
        if (owner != null && owner.WindowState != WindowState.Minimized)
            window.Owner = owner;
        bool accepted = window.ShowDialog() == true && !window.Result.IsEmpty;
        rect = window.Result;
        return accepted;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        SetWindowDisplayAffinity(hwnd, 0x00000011);
        if (HwndSource.FromHwnd(hwnd) is { } source)
            source.AddHook(KeepOnMonitor);
        Refit();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
    }

    internal void Draw(DrawingContext context, FrameworkElement visual)
    {
        double width = Math.Max(0, visual.ActualWidth);
        double height = Math.Max(0, visual.ActualHeight);
        var full = new Rect(0, 0, width, height);
        var dim = new SolidColorBrush(Color.FromArgb(150, 8, 10, 16));
        dim.Freeze();
        Rect? hole = SelectionDip(visual);
        if (hole == null)
        {
            context.DrawRectangle(dim, null, full);
            return;
        }

        var geometry = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(full),
            new RectangleGeometry(hole.Value));
        context.DrawGeometry(dim, null, geometry);
        var pen = new Pen(Brushes.White, 2);
        pen.Freeze();
        context.DrawRectangle(null, pen, hole.Value);
        DrawHandles(context, hole.Value);

        double pixelsPerDip = VisualTreeHelper.GetDpi(visual).PixelsPerDip;
        var label = new FormattedText(
            $"{_width} × {_height}",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            16,
            Brushes.White,
            pixelsPerDip);
        if (!TryLabelOrigin(hole.Value, label.Width, label.Height, width, height, out double textX, out double textY))
            return;

        var labelBg = new SolidColorBrush(Color.FromArgb(180, 14, 18, 26));
        labelBg.Freeze();
        context.DrawRoundedRectangle(
            labelBg,
            null,
            new Rect(textX - 6, textY - 2, label.Width + 12, label.Height + 4),
            4,
            4);
        context.DrawText(label, new Point(textX, textY));
    }

    internal void OnSurfaceDown(MouseButtonEventArgs e)
    {
        (int x, int y) = CursorPixels();
        BeginDrag(HitTest(x, y), x, y);
        e.Handled = true;
    }

    void BeginDrag(DragKind kind, int x, int y)
    {
        _drag = kind;
        _anchorX = x;
        _anchorY = y;
        _originX = _x;
        _originY = _y;
        _originWidth = _width;
        _originHeight = _height;
        if (_drag == DragKind.Create)
        {
            _x = x;
            _y = y;
            _width = 0;
            _height = 0;
        }

        _canvas.CaptureMouse();
    }

    internal void OnSurfaceMove(MouseEventArgs e)
    {
        (int x, int y) = CursorPixels();
        if (e.LeftButton != MouseButtonState.Pressed || _drag == DragKind.None)
        {
            Cursor = CursorFor(HitTest(x, y));
            return;
        }

        MoveSelection(x, y);
    }

    void MoveSelection(int x, int y)
    {
        int dx = x - _anchorX;
        int dy = y - _anchorY;
        switch (_drag)
        {
            case DragKind.Create:
                _x = Math.Min(_anchorX, x);
                _y = Math.Min(_anchorY, y);
                _width = Math.Abs(x - _anchorX);
                _height = Math.Abs(y - _anchorY);
                break;
            case DragKind.Move:
                _x = Clamp(_originX + dx, _monLeft, _monLeft + _monWidth - _originWidth);
                _y = Clamp(_originY + dy, _monTop, _monTop + _monHeight - _originHeight);
                _width = _originWidth;
                _height = _originHeight;
                break;
            default:
                Resize(dx, dy);
                break;
        }

        ClampToMonitor();
        _confirm.IsEnabled = _width >= 16 && _height >= 16;
        _move.IsEnabled = _confirm.IsEnabled;
        PlaceBar();
        _canvas.InvalidateVisual();
    }

    internal void OnSurfaceUp(MouseButtonEventArgs e)
    {
        _drag = DragKind.None;
        if (_canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();
        _confirm.IsEnabled = _width >= 16 && _height >= 16;
        _move.IsEnabled = _confirm.IsEnabled;
        PlaceBar();
        _canvas.InvalidateVisual();
        e.Handled = true;
    }

    void Confirm()
    {
        if (_width < 16 || _height < 16)
            return;

        int width = Math.Max(16, _width) & ~1;
        int height = Math.Max(16, _height) & ~1;
        Result = new ScreenRect(_x, _y, width, height);
        DialogResult = true;
    }

    void Resize(int dx, int dy)
    {
        int left = _originX;
        int top = _originY;
        int right = _originX + _originWidth;
        int bottom = _originY + _originHeight;
        if (_drag is DragKind.West or DragKind.NorthWest or DragKind.SouthWest)
            left = _originX + dx;
        if (_drag is DragKind.East or DragKind.NorthEast or DragKind.SouthEast)
            right = _originX + _originWidth + dx;
        if (_drag is DragKind.North or DragKind.NorthWest or DragKind.NorthEast)
            top = _originY + dy;
        if (_drag is DragKind.South or DragKind.SouthWest or DragKind.SouthEast)
            bottom = _originY + _originHeight + dy;

        if (right < left)
            (left, right) = (right, left);
        if (bottom < top)
            (top, bottom) = (bottom, top);

        _x = left;
        _y = top;
        _width = Math.Max(1, right - left);
        _height = Math.Max(1, bottom - top);
    }

    void ClampToMonitor()
    {
        int maxRight = _monLeft + _monWidth;
        int maxBottom = _monTop + _monHeight;
        if (_x < _monLeft)
        {
            _width -= _monLeft - _x;
            _x = _monLeft;
        }

        if (_y < _monTop)
        {
            _height -= _monTop - _y;
            _y = _monTop;
        }

        if (_x + _width > maxRight)
            _width = maxRight - _x;
        if (_y + _height > maxBottom)
            _height = maxBottom - _y;
        if (_width < 1)
            _width = 1;
        if (_height < 1)
            _height = 1;
    }

    DragKind HitTest(int x, int y)
    {
        if (_width < 2 || _height < 2)
            return DragKind.Create;

        int margin = HitMargin();
        bool nearLeft = Math.Abs(x - _x) <= margin;
        bool nearRight = Math.Abs(x - (_x + _width)) <= margin;
        bool nearTop = Math.Abs(y - _y) <= margin;
        bool nearBottom = Math.Abs(y - (_y + _height)) <= margin;
        bool inside = x >= _x - margin && y >= _y - margin && x <= _x + _width + margin && y <= _y + _height + margin;
        if (!inside)
            return DragKind.Create;
        if (nearTop && nearLeft)
            return DragKind.NorthWest;
        if (nearTop && nearRight)
            return DragKind.NorthEast;
        if (nearBottom && nearLeft)
            return DragKind.SouthWest;
        if (nearBottom && nearRight)
            return DragKind.SouthEast;
        if (nearLeft)
            return DragKind.West;
        if (nearRight)
            return DragKind.East;
        if (nearTop)
            return DragKind.North;
        if (nearBottom)
            return DragKind.South;
        if (x >= _x && y >= _y && x <= _x + _width && y <= _y + _height)
            return DragKind.Move;
        return DragKind.Create;
    }

    int HitMargin()
    {
        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (scale <= 0)
            scale = _dpiX / 96.0;
        if (scale <= 0)
            scale = 1;
        return Math.Max(8, (int)Math.Round(8 * scale));
    }

    static Cursor CursorFor(DragKind kind) => kind switch
    {
        DragKind.Move => Cursors.SizeAll,
        DragKind.West or DragKind.East => Cursors.SizeWE,
        DragKind.North or DragKind.South => Cursors.SizeNS,
        DragKind.NorthWest or DragKind.SouthEast => Cursors.SizeNWSE,
        DragKind.NorthEast or DragKind.SouthWest => Cursors.SizeNESW,
        _ => Cursors.Cross
    };

    Rect? SelectionDip(FrameworkElement visual)
    {
        if (_width < 2 || _height < 2)
            return null;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || PresentationSource.FromVisual(visual)?.CompositionTarget is not { } target)
            return null;

        var leftTop = new NativePoint { X = _x, Y = _y };
        var rightBottom = new NativePoint { X = _x + _width, Y = _y + _height };
        ScreenToClient(hwnd, ref leftTop);
        ScreenToClient(hwnd, ref rightBottom);
        Matrix toDip = target.TransformFromDevice;
        Point start = toDip.Transform(new Point(leftTop.X, leftTop.Y));
        Point end = toDip.Transform(new Point(rightBottom.X, rightBottom.Y));
        return new Rect(start, end);
    }

    static void DrawHandles(DrawingContext context, Rect hole)
    {
        var brush = Brushes.White;
        double size = 8;
        Point[] points =
        [
            hole.TopLeft,
            new Point(hole.Left + (hole.Width / 2), hole.Top),
            hole.TopRight,
            new Point(hole.Right, hole.Top + (hole.Height / 2)),
            hole.BottomRight,
            new Point(hole.Left + (hole.Width / 2), hole.Bottom),
            hole.BottomLeft,
            new Point(hole.Left, hole.Top + (hole.Height / 2))
        ];
        foreach (Point point in points)
        {
            context.DrawRectangle(brush, null, new Rect(point.X - (size / 2), point.Y - (size / 2), size, size));
        }
    }

    bool TryLabelOrigin(Rect hole, double labelW, double labelH, double hostW, double hostH, out double textX, out double textY)
    {
        textX = hole.X + 8;
        textY = hole.Y + 6;
        bool inside = hole.Height >= labelH + 14 && hole.Width >= labelW + 16;
        if (!inside)
        {
            textX = hole.X;
            textY = _buttonsBelow ? hole.Y - labelH - 6 : hole.Bottom + 6;
        }

        textX = ClampD(textX, 8, Math.Max(8, hostW - labelW - 8));
        textY = ClampD(textY, 8, Math.Max(8, hostH - labelH - 8));
        var text = new Rect(textX - 6, textY - 2, labelW + 12, labelH + 4);
        var bar = new Rect(_barX, _barY, Math.Max(_barW, 1), Math.Max(_barH, 1));
        if (_barW > 1 && text.IntersectsWith(bar))
        {
            textY = _buttonsBelow
                ? ClampD(hole.Y - labelH - 6, 8, Math.Max(8, hostH - labelH - 8))
                : ClampD(hole.Bottom + 6, 8, Math.Max(8, hostH - labelH - 8));
            text = new Rect(textX - 6, textY - 2, labelW + 12, labelH + 4);
            if (text.IntersectsWith(bar))
                return false;
        }

        return true;
    }

    void PlaceBar()
    {
        double hostW = _overlay.ActualWidth;
        double hostH = _overlay.ActualHeight;
        if (hostW < 1 || hostH < 1)
        {
            hostW = ActualWidth > 1 ? ActualWidth : Width;
            hostH = ActualHeight > 1 ? ActualHeight : Height;
        }

        if (double.IsNaN(hostW) || double.IsNaN(hostH) || hostW < 1 || hostH < 1)
            return;

        double availableW = Math.Max(1, hostW - (BarMargin * 2));
        double availableH = Math.Max(1, hostH - (BarMargin * 2));
        _help.MaxWidth = Math.Min(380, Math.Max(80, availableW - 20));
        _plate.MaxWidth = availableW;

        Rect? hole = SelectionDip(_canvas);
        bool hasRect = hole.HasValue;
        Rect rect = hole ?? Rect.Empty;
        _plate.Measure(new Size(availableW, availableH));
        double barW = Math.Min(availableW, Math.Max(1, _plate.DesiredSize.Width));
        double barH = Math.Min(availableH, Math.Max(1, _plate.DesiredSize.Height));
        bool below = !hasRect || rect.Bottom + BarGap + barH <= hostH - BarMargin;
        if (below != _buttonsNearTop)
        {
            SetBarSide(below);
            _plate.Measure(new Size(availableW, availableH));
            barW = Math.Min(availableW, Math.Max(1, _plate.DesiredSize.Width));
            barH = Math.Min(availableH, Math.Max(1, _plate.DesiredSize.Height));
            bool belowNow = !hasRect || rect.Bottom + BarGap + barH <= hostH - BarMargin;
            if (belowNow != below)
            {
                below = belowNow;
                SetBarSide(below);
                _plate.Measure(new Size(availableW, availableH));
                barW = Math.Min(availableW, Math.Max(1, _plate.DesiredSize.Width));
                barH = Math.Min(availableH, Math.Max(1, _plate.DesiredSize.Height));
            }
        }

        BarSlot slot = LayoutBar(hasRect, rect, barW, barH, hostW, hostH);
        if (slot.ButtonsBelow != _buttonsNearTop)
            SetBarSide(slot.ButtonsBelow);

        Canvas.SetLeft(_plate, slot.X);
        Canvas.SetTop(_plate, slot.Y);
        _buttonsBelow = hole.HasValue && slot.ButtonsBelow;
        _barX = slot.X;
        _barY = slot.Y;
        _barW = barW;
        _barH = barH;
    }

    void SetBarSide(bool buttonsBelow)
    {
        if (_buttonsNearTop == buttonsBelow && _stack.Children.Count == 2)
            return;
        _buttonsNearTop = buttonsBelow;
        _stack.Children.Clear();
        if (buttonsBelow)
        {
            _plate.Padding = new Thickness(10, 8, 10, 10);
            _help.Margin = new Thickness(0, 8, 0, 0);
            _stack.Children.Add(_buttons);
            _stack.Children.Add(_help);
        }
        else
        {
            _plate.Padding = new Thickness(10, 10, 10, 8);
            _help.Margin = new Thickness(0, 0, 0, 8);
            _stack.Children.Add(_help);
            _stack.Children.Add(_buttons);
        }
    }

    static BarSlot LayoutBar(bool hasRect, Rect rect, double barW, double barH, double hostW, double hostH)
    {
        double maxX = Math.Max(BarMargin, hostW - barW - BarMargin);
        double maxY = Math.Max(BarMargin, hostH - barH - BarMargin);
        if (!hasRect)
        {
            double idleX = ClampD((hostW - barW) / 2, BarMargin, maxX);
            double idleY = ClampD(hostH - barH - BarMargin, BarMargin, maxY);
            return new BarSlot(idleX, idleY, true);
        }

        bool below = rect.Bottom + BarGap + barH <= hostH - BarMargin;
        double x = rect.X;
        double y = below ? rect.Bottom + BarGap : rect.Y - BarGap - barH;
        return new BarSlot(ClampD(x, BarMargin, maxX), ClampD(y, BarMargin, maxY), below);
    }

    void ApplyMonitor(MonitorSpan monitor)
    {
        _monLeft = monitor.Left;
        _monTop = monitor.Top;
        _monWidth = Math.Max(1, monitor.Width);
        _monHeight = Math.Max(1, monitor.Height);
        _dpiX = monitor.DpiX == 0 ? 96 : monitor.DpiX;
        _dpiY = monitor.DpiY == 0 ? 96 : monitor.DpiY;
    }

    void AdoptPending()
    {
        if (_pending.Width < 2 || _pending.Height < 2)
            return;

        int left = Math.Max(_pending.X, _monLeft);
        int top = Math.Max(_pending.Y, _monTop);
        int right = Math.Min(_pending.X + _pending.Width, _monLeft + _monWidth);
        int bottom = Math.Min(_pending.Y + _pending.Height, _monTop + _monHeight);
        int width = right - left;
        int height = bottom - top;
        if (width < 16 || height < 16)
            return;

        _x = left;
        _y = top;
        _width = width;
        _height = height;
    }

    void Refit()
    {
        if (_fitting)
            return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _monWidth < 1 || _monHeight < 1)
            return;

        _fitting = true;
        try
        {
            SetWindowPos(hwnd, new IntPtr(-1), _monLeft, _monTop, _monWidth, _monHeight, SwpShowWindow);
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi > 0)
            {
                _dpiX = dpi;
                _dpiY = dpi;
            }
            else if (IsLoaded)
            {
                DpiScale scale = VisualTreeHelper.GetDpi(this);
                if (scale.PixelsPerInchX > 0)
                    _dpiX = (uint)Math.Round(scale.PixelsPerInchX);
                if (scale.PixelsPerInchY > 0)
                    _dpiY = (uint)Math.Round(scale.PixelsPerInchY);
            }

            ApplyDipBounds();
            SetWindowPos(hwnd, new IntPtr(-1), _monLeft, _monTop, _monWidth, _monHeight, SwpShowWindow);
        }
        finally
        {
            _fitting = false;
        }

        PlaceBar();
    }

    void ApplyDipBounds()
    {
        double scaleX = _dpiX / 96.0;
        double scaleY = _dpiY / 96.0;
        if (scaleX <= 0)
            scaleX = 1;
        if (scaleY <= 0)
            scaleY = 1;
        SetIfDiff(Left, _monLeft / scaleX, value => Left = value);
        SetIfDiff(Top, _monTop / scaleY, value => Top = value);
        SetIfDiff(Width, _monWidth / scaleX, value => Width = value);
        SetIfDiff(Height, _monHeight / scaleY, value => Height = value);
    }

    static void SetIfDiff(double current, double value, Action<double> apply)
    {
        if (double.IsNaN(current) || Math.Abs(current - value) >= 0.5)
            apply(value);
    }

    IntPtr KeepOnMonitor(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (lParam == IntPtr.Zero || _monWidth < 1 || _monHeight < 1)
            return IntPtr.Zero;

        if (msg == WmGetMinMaxInfo)
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MaxSize = new NativePoint { X = _monWidth, Y = _monHeight };
            info.MaxPosition = new NativePoint { X = _monLeft, Y = _monTop };
            info.MinTrackSize = new NativePoint { X = 1, Y = 1 };
            info.MaxTrackSize = new NativePoint { X = _monWidth, Y = _monHeight };
            Marshal.StructureToPtr(info, lParam, false);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WmWindowPosChanging)
            return IntPtr.Zero;

        var pos = Marshal.PtrToStructure<WindowPos>(lParam);
        const uint noMove = 0x0002;
        const uint noSize = 0x0001;
        const uint hideWindow = 0x0080;
        if ((pos.Flags & hideWindow) != 0)
            return IntPtr.Zero;
        bool move = (pos.Flags & noMove) == 0;
        bool size = (pos.Flags & noSize) == 0;
        if ((!move || (pos.X == _monLeft && pos.Y == _monTop)) &&
            (!size || (pos.Cx == _monWidth && pos.Cy == _monHeight)))
            return IntPtr.Zero;

        pos.X = _monLeft;
        pos.Y = _monTop;
        pos.Cx = _monWidth;
        pos.Cy = _monHeight;
        pos.Flags &= ~noMove;
        pos.Flags &= ~noSize;
        Marshal.StructureToPtr(pos, lParam, false);
        return IntPtr.Zero;
    }

    (int X, int Y) CursorPixels()
    {
        GetCursorPos(out NativePoint point);
        return (point.X, point.Y);
    }

    static MonitorSpan ResolveMonitor(Window? owner, ScreenRect current)
    {
        nint hint = IntPtr.Zero;
        if (!current.IsEmpty)
            hint = MonitorFromPoint(new NativePoint { X = current.X + current.Width / 2, Y = current.Y + current.Height / 2 }, 2);
        if (hint == IntPtr.Zero && owner != null)
        {
            IntPtr hwnd = new WindowInteropHelper(owner).Handle;
            if (hwnd != IntPtr.Zero)
                hint = MonitorFromWindow(hwnd, 2);
        }

        if (hint == IntPtr.Zero && GetCursorPos(out NativePoint cursor))
            hint = MonitorFromPoint(cursor, 2);

        MonitorInfo[] monitors = [];
        try
        {
            monitors = MonitorEnumerator.GetMonitors().ToArray();
        }
        catch (Exception)
        {
            monitors = [];
        }

        if (monitors.Length > 0)
        {
            MonitorInfo? chosen = hint != IntPtr.Zero
                ? monitors.FirstOrDefault(monitor => monitor.Handle == hint)
                : null;
            chosen ??= monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
            if (TryReadMonitor(chosen.Handle, out int left, out int top, out int width, out int height, out uint dpiX, out uint dpiY))
                return new MonitorSpan(left, top, width, height, dpiX, dpiY);
            return new MonitorSpan(chosen.Left, chosen.Top, Math.Max(1, chosen.Width), Math.Max(1, chosen.Height), 96, 96);
        }

        if (hint != IntPtr.Zero && TryReadMonitor(hint, out int winLeft, out int winTop, out int winWidth, out int winHeight, out uint winDpiX, out uint winDpiY))
            return new MonitorSpan(winLeft, winTop, winWidth, winHeight, winDpiX, winDpiY);

        return new MonitorSpan(0, 0, Math.Max(1, GetSystemMetrics(0)), Math.Max(1, GetSystemMetrics(1)), 96, 96);
    }

    static bool TryReadMonitor(nint handle, out int left, out int top, out int width, out int height, out uint dpiX, out uint dpiY)
    {
        left = top = width = height = 0;
        dpiX = dpiY = 96;
        if (handle == 0)
            return false;

        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(handle, ref info))
            return false;

        width = info.Right - info.Left;
        height = info.Bottom - info.Top;
        if (width < 100 || height < 100)
            return false;

        left = info.Left;
        top = info.Top;
        try
        {
            if (GetDpiForMonitor(handle, 0, out uint x, out uint y) == 0 && x > 0 && y > 0)
            {
                dpiX = x;
                dpiY = y;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return true;
    }

    static int Clamp(int value, int min, int max)
    {
        if (max < min)
            return min;
        if (value < min)
            return min;
        if (value > max)
            return max;
        return value;
    }

    static double ClampD(double value, double min, double max)
    {
        if (max < min)
            return min;
        if (value < min)
            return min;
        if (value > max)
            return max;
        return value;
    }

    [DllImport("user32.dll")]
    static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    static extern nint MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfoEx
    {
        public int Size;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int WorkLeft;
        public int WorkTop;
        public int WorkRight;
        public int WorkBottom;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    readonly record struct MonitorSpan(int Left, int Top, int Width, int Height, uint DpiX, uint DpiY);

    readonly record struct BarSlot(double X, double Y, bool ButtonsBelow);

    enum DragKind
    {
        None,
        Create,
        Move,
        North,
        South,
        East,
        West,
        NorthEast,
        NorthWest,
        SouthEast,
        SouthWest
    }

    sealed class RegionCanvas : Border
    {
        readonly RegionWindow _owner;

        public RegionCanvas(RegionWindow owner)
        {
            _owner = owner;
            Background = Brushes.Transparent;
        }

        protected override void OnRender(DrawingContext drawingContext) => _owner.Draw(drawingContext, this);

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) => _owner.OnSurfaceDown(e);

        protected override void OnMouseMove(MouseEventArgs e) => _owner.OnSurfaceMove(e);

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => _owner.OnSurfaceUp(e);
    }
}
