using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using FrameForge.Core;

namespace FrameForge.UI;

public sealed class AudioLevelIndicator : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(AudioLevelIndicator), new PropertyMetadata(0d, Changed));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(AudioLevelIndicator), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty SignalColorProperty = DependencyProperty.Register(nameof(SignalColor), typeof(Color), typeof(AudioLevelIndicator), new FrameworkPropertyMetadata(Color.FromRgb(112, 167, 255), FrameworkPropertyMetadataOptions.AffectsRender));
    bool _subscribed;
    double _shown;
    long _last;

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public Color SignalColor { get => (Color)GetValue(SignalColorProperty); set => SetValue(SignalColorProperty, value); }

    public AudioLevelIndicator()
    {
        Width = Height = 24;
        Loaded += (_, _) => { Loc.Changed += UpdateText; Refresh(); };
        Unloaded += (_, _) => { Loc.Changed -= UpdateText; Unsubscribe(); };
    }

    static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs _) => ((AudioLevelIndicator)owner).Refresh();

    void Refresh()
    {
        UpdateText();
        if (IsLoaded && !_subscribed && (IsActive || _shown > .001))
        {
            _subscribed = true;
            _last = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += Animate;
        }
        InvalidateVisual();
    }

    void UpdateText()
    {
        string text = Loc.Get(!IsActive ? "AudioLevelOff" : Level > .01 ? "AudioLevelSignal" : "AudioLevelQuiet");
        ToolTip = text;
        AutomationProperties.SetHelpText(this, text);
    }

    void Animate(object? sender, EventArgs args)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Clamp((now - _last) / (double)Stopwatch.Frequency, 0, .1);
        _last = now;
        double target = IsActive && double.IsFinite(Level) ? Math.Clamp(Level, 0, 1) : 0;
        double previous = _shown;
        _shown += (target - _shown) * (1 - Math.Exp(-dt / (target > _shown ? .055 : .22)));
        if (Math.Abs(_shown - target) < .001) _shown = target;
        if (Math.Abs(previous - _shown) > .0001) InvalidateVisual();
        if (!IsActive && _shown == 0) Unsubscribe();
    }

    void Unsubscribe()
    {
        if (!_subscribed) return;
        CompositionTarget.Rendering -= Animate;
        _subscribed = false;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext drawing)
    {
        Point center = new(ActualWidth / 2, ActualHeight / 2);
        var track = new SolidColorBrush(Color.FromRgb(62, 74, 94));
        Color color = IsActive ? SignalColor : Color.FromRgb(96, 108, 128);
        var signal = new SolidColorBrush(color);
        double radius = 7.2 + 2.1 * _shown;
        drawing.DrawEllipse(null, new Pen(track, 1.3), center, 9.4, 9.4);
        drawing.PushOpacity(IsActive ? .35 + .65 * _shown : .45);
        drawing.DrawEllipse(null, new Pen(signal, 1.5), center, radius, radius);
        drawing.Pop();
        drawing.PushOpacity(IsActive ? .3 + .65 * _shown : .35);
        drawing.DrawEllipse(signal, null, center, 2 + 4.6 * _shown, 2 + 4.6 * _shown);
        drawing.Pop();
    }
}
