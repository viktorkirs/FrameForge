using System.IO;
using System.Windows;
using System.Windows.Controls;
using FrameForge.Core;
using FrameForge.UI;

internal static partial class Program
{
    static void RunPreferencesTests()
    {
        Test("first run saves each language, keeps other preferences and skips the next prompt", () =>
        {
            foreach (AppLanguage language in Enum.GetValues<AppLanguage>())
            {
                SettingsStore.Save(new AppSettings { Fps = 60, OutputDirectory = _output, SystemAudio = false });
                var settings = SettingsStore.Load();
                Check(settings.Language is null, "A language was chosen before the first-run dialog");
                Check(CompleteFirstRun(settings, language, accept: true), "Language selection was rejected");
                AppSettings loaded = SettingsStore.Load();
                Check(loaded.Language == language && Loc.Language == language, "Selected language was not saved or applied");
                Check(loaded.Fps == 60 && loaded.OutputDirectory == _output, "First run overwrote existing preferences");
                Loc.Set(language == AppLanguage.English ? AppLanguage.German : AppLanguage.English);
                Check(LanguageWindow.ChooseIfNeeded(loaded) && Loc.Language == language, "Saved language was not reused");
                Check(!Application.Current.Windows.OfType<LanguageWindow>().Any(), "First-run dialog reappeared");
            }
        });
        Test("closing first-run selection keeps the language unset so it is asked again", () =>
        {
            SettingsStore.Save(new AppSettings { OutputDirectory = _output, SystemAudio = false });
            var settings = SettingsStore.Load();
            Check(!CompleteFirstRun(settings, AppLanguage.Russian, accept: false), "Close accepted a language");
            Check(settings.Language is null && SettingsStore.Load().Language is null, "Close saved a language");
            Check(CompleteFirstRun(SettingsStore.Load(), AppLanguage.English, accept: true), "The next launch could not choose a language");
        });
        Test("settings save language changes and the shared close button cancels the preview", () =>
        {
            SettingsStore.Save(new AppSettings { Language = AppLanguage.English, OutputDirectory = _output, SystemAudio = false });
            Loc.Set(AppLanguage.English);
            var main = new MainWindow();
            Window? previousMain = Application.Current.MainWindow;
            Application.Current.MainWindow = main;
            main.Show();
            try
            {
                Wait(Task.Delay(80));
                SaveWindow(main, "main-without-language-buttons.png");
                var model = (MainViewModel)main.DataContext;
                foreach (bool accept in new[] { false, true })
                {
                    Exception? interactionError = null;
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var dialog = Application.Current.Windows.OfType<SettingsWindow>().Single();
                        try
                        {
                            ((TabControl)dialog.FindName("SettingsTabs")).SelectedIndex = 3;
                            ((ComboBox)dialog.FindName("LanguageBox")).SelectedValue = AppLanguage.German;
                            Check(Loc.Language == AppLanguage.German, "Settings did not preview the selected language");
                            dialog.UpdateLayout();
                            SaveWindow(dialog, "settings-language.png");
                            var button = (Button)dialog.FindName(accept ? "SaveButton" : "CloseWindowButton");
                            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        catch (Exception ex) { interactionError = ex; dialog.Close(); }
                    }));
                    model.SettingsCommand.Execute(null);
                    if (interactionError != null) throw interactionError;
                    AppLanguage expected = accept ? AppLanguage.German : AppLanguage.English;
                    Check(Loc.Language == expected && model.Settings.Language == expected && SettingsStore.Load().Language == expected,
                        accept ? "Save did not persist the language" : "Close did not discard the language preview");
                }
                ((Button)main.FindName("CloseWindowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (int attempt = 0; attempt < 50 && main.IsVisible; attempt++) Wait(Task.Delay(100));
                Check(!main.IsVisible, "The shared close button did not close the main window");
            }
            finally
            {
                if (main.IsVisible)
                {
                    main.Close();
                    for (int attempt = 0; attempt < 50 && main.IsVisible; attempt++) Wait(Task.Delay(100));
                }
                Application.Current.MainWindow = previousMain;
                SettingsStore.Save(new AppSettings { Language = AppLanguage.Russian, OutputDirectory = _output, SystemAudio = false, Fps = 30 });
            }
        });
    }

    static bool CompleteFirstRun(AppSettings settings, AppLanguage language, bool accept)
    {
        Exception? interactionError = null;
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<LanguageWindow>().Single();
            try
            {
                ((ComboBox)dialog.FindName("LanguageBox")).SelectedValue = language;
                Check(Loc.Language == language, "First-run text did not switch languages");
                Check((string)((Button)dialog.FindName("ContinueButton")).Content == Loc.Get("Continue"), "Continue button was not translated");
                dialog.UpdateLayout();
                SaveWindow(dialog, $"first-run-{language.ToString().ToLowerInvariant()}.png");
                ((Button)dialog.FindName(accept ? "ContinueButton" : "CloseWindowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { interactionError = ex; dialog.Close(); }
        }));
        bool accepted = LanguageWindow.ChooseIfNeeded(settings);
        if (interactionError != null) throw interactionError;
        return accepted;
    }
}
