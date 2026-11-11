using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FrameForge.Core;

namespace FrameForge.UI;

public partial class LanguageWindow : Window
{
    readonly AppLanguage _originalLanguage = Loc.Language;

    public LanguageWindow()
    {
        InitializeComponent();
        LanguageBox.ItemsSource = LanguageChoice.All;
        LanguageBox.SelectedValue = Loc.Language;
        Loaded += (_, _) => LanguageBox.Focus();
    }

    public static bool ChooseIfNeeded(AppSettings settings)
    {
        Loc.Set(settings.Language ?? Loc.FromCulture());
        if (settings.Language is not null)
            return true;

        var window = new LanguageWindow();
        if (window.ShowDialog() != true)
            return false;

        settings.Language = (AppLanguage)window.LanguageBox.SelectedValue;
        SettingsStore.Save(settings);
        return true;
    }

    void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedValue is AppLanguage language)
            Loc.Set(language);
    }

    void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true)
            Loc.Set(_originalLanguage);
        base.OnClosed(e);
    }
}
