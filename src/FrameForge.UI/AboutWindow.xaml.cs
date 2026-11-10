using System.Windows;
using FrameForge.Core;

namespace FrameForge.UI;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Version? version = typeof(AboutWindow).Assembly.GetName().Version;
        VersionText.Text = Loc.Format("Version", version?.ToString(3) ?? "1.0.0");
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
