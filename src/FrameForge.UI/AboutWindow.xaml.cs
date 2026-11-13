using System.Windows;
using FrameForge.Core;

namespace FrameForge.UI;

public partial class AboutWindow : Window
{
    readonly bool _ownsUpdates;
    public AboutWindow(AppUpdateViewModel? updates = null)
    {
        InitializeComponent();
        _ownsUpdates = updates == null;
        DataContext = updates ?? new AppUpdateViewModel();
        Version? version = typeof(AboutWindow).Assembly.GetName().Version;
        VersionText.Text = Loc.Format("Version", version?.ToString(3) ?? "1.0.0");
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_ownsUpdates) ((AppUpdateViewModel)DataContext).Dispose();
        base.OnClosed(e);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
