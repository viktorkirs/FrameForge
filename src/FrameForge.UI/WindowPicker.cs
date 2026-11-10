using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using FrameForge.Capture;
using FrameForge.Core;

namespace FrameForge.UI;

public sealed class WindowPicker : Window
{
    readonly WrapPanel _cards = new() { ItemHeight = 232 };
    readonly TextBox _search = new();
    readonly TextBlock _count = new();
    readonly TextBlock _empty = new();
    readonly ScrollViewer _scroll;
    readonly List<WindowThumbnail> _thumbnails = [];
    readonly DispatcherTimer _timer;
    IReadOnlyList<WindowChoice> _windows;
    bool _updateQueued;
    bool _closed;

    public WindowChoice? Choice { get; private set; }

    WindowPicker(IReadOnlyList<WindowChoice> windows)
    {
        _windows = windows;
        Title = Loc.Get("WindowPickerTitle");
        Width = Math.Min(900, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(720, SystemParameters.WorkArea.Height - 24);
        MinWidth = 540;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Background = ResourceBrush("BgBrush");
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0) });

        var grid = new Grid();
        foreach (GridLength height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18), Cursor = Cursors.SizeAll };
        header.Children.Add(new TextBlock { Text = Title, FontSize = 21, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = Loc.Get("WindowPickerHint"), Foreground = ResourceBrush("MutedBrush"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        header.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        grid.Children.Add(header);

        var searchPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        searchPanel.Children.Add(new TextBlock { Text = Loc.Get("WindowSearch"), Foreground = ResourceBrush("MutedBrush"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
        _search.Name = "WindowSearch";
        AutomationProperties.SetAutomationId(_search, "WindowSearch");
        AutomationProperties.SetName(_search, Loc.Get("WindowSearch"));
        _search.Padding = new Thickness(12, 8, 12, 8);
        _search.TextChanged += (_, _) => FillCards();
        searchPanel.Children.Add(_search);
        Grid.SetRow(searchPanel, 1);
        grid.Children.Add(searchPanel);

        _count.Foreground = ResourceBrush("MutedBrush");
        _count.FontSize = 12;
        _count.Margin = new Thickness(6, 0, 0, 8);
        Grid.SetRow(_count, 2);
        grid.Children.Add(_count);

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Content = _cards, CanContentScroll = false
        };
        _scroll.ScrollChanged += (_, _) => { ResizeCards(); UpdateThumbnails(); };
        _scroll.SizeChanged += (_, _) => { ResizeCards(); QueueThumbnails(); };
        var body = new Grid();
        body.Children.Add(_scroll);
        _empty.Foreground = ResourceBrush("MutedBrush");
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.IsHitTestVisible = false;
        body.Children.Add(_empty);
        Grid.SetRow(body, 3);
        grid.Children.Add(body);

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        var refresh = new Button { Content = "↻  " + Loc.Get("RefreshWindows"), MinWidth = 140, Height = 36, Padding = new Thickness(12, 0, 12, 0) };
        refresh.Click += (_, _) => { _windows = WindowCatalog.VisibleWindows(); FillCards(); };
        DockPanel.SetDock(refresh, Dock.Left);
        footer.Children.Add(refresh);
        var cancel = new Button { Content = Loc.Get("Cancel"), Width = 120, Height = 36, IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(cancel);
        Grid.SetRow(footer, 4);
        grid.Children.Add(footer);

        Content = new Border { BorderBrush = ResourceBrush("BorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(20), Child = grid };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => UpdateThumbnails();
        Loaded += (_, _) => { ResizeCards(); UpdateThumbnails(); _timer.Start(); _search.Focus(); };
        LayoutUpdated += (_, _) => QueueThumbnails();
        FillCards();
    }

    Brush ResourceBrush(string key) => (Brush)FindResource(key);

    void FillCards()
    {
        ReleaseThumbnails();
        _cards.Children.Clear();
        WindowChoice[] shown = _windows.Where(choice => choice.Matches(_search.Text)).ToArray();
        _count.Text = Loc.Format("WindowCount", shown.Length, _windows.Count);
        _empty.Text = _windows.Count == 0 ? Loc.Get("WindowPickerEmpty") : Loc.Get("WindowSearchEmpty");
        _empty.Visibility = shown.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (WindowChoice choice in shown)
        {
            var button = new Button
            {
                Margin = new Thickness(5), Padding = new Thickness(10),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
                ToolTip = choice.Label, Tag = choice
            };
            AutomationProperties.SetName(button, choice.Label);
            var content = new StackPanel();
            var placeholder = new TextBlock
            {
                Text = Loc.Get("ThumbnailUnavailable"), Foreground = ResourceBrush("MutedBrush"), FontSize = 12,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12)
            };
            var preview = new Border { Height = 136, Background = ResourceBrush("PreviewBrush"), CornerRadius = new CornerRadius(6), Child = placeholder, IsHitTestVisible = false };
            content.Children.Add(preview);
            content.Children.Add(new TextBlock
            {
                Text = choice.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 38, Margin = new Thickness(0, 10, 0, 3)
            });
            content.Children.Add(new TextBlock
            {
                Text = choice.ProcessName, Foreground = ResourceBrush("MutedBrush"), FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            button.Content = content;
            button.Click += (_, _) =>
            {
                if (!WindowCatalog.IsAlive(choice.Handle))
                {
                    _windows = WindowCatalog.VisibleWindows();
                    FillCards();
                    _count.Text = Loc.Get("WindowGone");
                    return;
                }
                Choice = choice;
                DialogResult = true;
            };
            _cards.Children.Add(button);
            _thumbnails.Add(new WindowThumbnail(this, preview, _scroll, placeholder, choice.Handle));
        }
        ResizeCards();
        _scroll.ScrollToTop();
        QueueThumbnails();
    }

    void ResizeCards()
    {
        double width = _scroll.ViewportWidth > 1 ? _scroll.ViewportWidth : Math.Max(1, _scroll.ActualWidth - 18);
        int columns = Math.Clamp((int)(width / 245), 1, 4);
        double itemWidth = Math.Max(1, Math.Floor(width / columns));
        if (double.IsNaN(_cards.ItemWidth) || Math.Abs(_cards.ItemWidth - itemWidth) > 0.1)
            _cards.ItemWidth = itemWidth;
    }

    void QueueThumbnails()
    {
        if (_closed || _updateQueued || !IsLoaded)
            return;
        _updateQueued = true;
        Dispatcher.BeginInvoke(() => { _updateQueued = false; UpdateThumbnails(); }, DispatcherPriority.Render);
    }

    void UpdateThumbnails()
    {
        if (_closed)
            return;
        foreach (WindowThumbnail thumbnail in _thumbnails)
            thumbnail.Update();
    }

    void ReleaseThumbnails()
    {
        foreach (WindowThumbnail thumbnail in _thumbnails)
            thumbnail.Dispose();
        _thumbnails.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _timer.Stop();
        ReleaseThumbnails();
        base.OnClosed(e);
    }

    public static bool TryPick(Window? owner, out WindowChoice choice)
    {
        var dialog = new WindowPicker(WindowCatalog.VisibleWindows());
        if (owner != null)
            dialog.Owner = owner;
        bool accepted = dialog.ShowDialog() == true && dialog.Choice != null;
        choice = dialog.Choice!;
        return accepted;
    }
}
