using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace FrameForge.UI;

/// <summary>Dark frosted glass behind modal windows, including nested native file dialogs.</summary>
public static class ModalBackdrop
{
    static readonly Dictionary<Window, Surface> Surfaces = new();

    public static bool? ShowDialog(Window dialog)
    {
        using var backdrop = Begin(dialog.Owner);
        return dialog.ShowDialog();
    }

    public static bool? ShowDialog(CommonDialog dialog, Window owner)
    {
        using var backdrop = Begin(owner);
        return dialog.ShowDialog(owner);
    }

    public static IDisposable Begin(Window? owner)
    {
        var acquired = new List<Surface>();
        try
        {
            for (Window? window = owner; window != null; window = window.Owner)
            {
                if (!window.IsVisible || window.Content is not UIElement content) continue;
                if (!Surfaces.TryGetValue(window, out Surface? surface))
                { surface = new(window, content); Surfaces.Add(window, surface); }
                surface.References++; acquired.Add(surface);
            }
            return new Scope(acquired);
        }
        catch { new Scope(acquired).Dispose(); throw; }
    }

    sealed class Scope(List<Surface> acquired) : IDisposable
    {
        bool _disposed;
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            foreach (Surface surface in acquired)
                if (--surface.References == 0) { Surfaces.Remove(surface.Window); surface.Dispose(); }
        }
    }

    sealed class Surface : IDisposable
    {
        public Window Window { get; }
        public int References;
        readonly UIElement _content;
        readonly Effect? _previousEffect;
        readonly AdornerLayer _layer;
        readonly FrostedGlass _glass;
        readonly AdornerDecorator? _decorator;

        public Surface(Window window, UIElement content)
        {
            Window = window; _content = content; _previousEffect = content.Effect;
            AdornerLayer? layer = AdornerLayer.GetAdornerLayer(content);
            if (layer == null)
            {
                window.Content = null;
                _decorator = new AdornerDecorator { Child = content }; window.Content = _decorator;
                window.UpdateLayout(); layer = _decorator.AdornerLayer;
            }
            _layer = layer; _glass = new FrostedGlass(content);
            content.SetCurrentValue(UIElement.EffectProperty, new BlurEffect { Radius = 10, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance });
            _layer.Add(_glass);
        }

        public void Dispose()
        {
            _layer.Remove(_glass); _content.SetCurrentValue(UIElement.EffectProperty, _previousEffect);
            if (_decorator != null && Window.Content == _decorator)
            { _decorator.Child = null; Window.Content = _content; }
        }
    }

    sealed class FrostedGlass(UIElement content) : Adorner(content)
    {
        protected override void OnRender(DrawingContext dc)
        {
            var tint = new LinearGradientBrush(Color.FromArgb(165, 22, 31, 46), Color.FromArgb(200, 8, 14, 24), 90);
            var sheen = new LinearGradientBrush(Color.FromArgb(22, 215, 228, 244), Color.FromArgb(0, 125, 149, 180), 115);
            var bounds = new Rect(AdornedElement.RenderSize);
            dc.DrawRectangle(tint, null, bounds); dc.DrawRectangle(sheen, null, bounds);
        }
    }
}
