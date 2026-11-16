using System.Windows;
using System.Windows.Input;
using FrameForge.Core;

namespace FrameForge.UI;

public partial class AppDialog : Window
{
    MessageBoxResult _result = MessageBoxResult.Cancel;
    bool _saveChanges;

    public AppDialog() => InitializeComponent();

    public static MessageBoxResult ConfirmSave(Window owner)
    {
        var dialog = new AppDialog { _saveChanges = true, Title = Loc.Get("EditorTitle") };
        dialog.DialogTitle.Text = Loc.Get("EditorSaveChangesTitle");
        dialog.DialogMessage.Text = Loc.Get("EditorSaveChanges");
        dialog.PrimaryAction.Content = Loc.Get("EditorSaveProject");
        dialog.DiscardAction.Content = Loc.Get("EditorDiscardChanges");
        dialog.CancelAction.Content = Loc.Get("Cancel");
        dialog.AttachOwner(owner); dialog.ShowDialog(); return dialog._result;
    }

    public static void ShowError(Window? owner, string message, string title)
    {
        var dialog = new AppDialog { Title = title };
        dialog.DialogTitle.Text = title; dialog.DialogMessage.Text = message; dialog.DialogSymbol.Text = "!";
        dialog.PrimaryAction.Content = Loc.Get("DialogOK");
        dialog.DiscardAction.Visibility = dialog.CancelAction.Visibility = Visibility.Collapsed;
        dialog.AttachOwner(owner); dialog.ShowDialog();
    }

    void AttachOwner(Window? owner)
    {
        Window? target = owner?.IsVisible == true ? owner : Application.Current.MainWindow;
        if (target?.IsVisible == true && target != this) Owner = target;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    void Primary_Click(object sender, RoutedEventArgs e) { _result = _saveChanges ? MessageBoxResult.Yes : MessageBoxResult.OK; Close(); }
    void Discard_Click(object sender, RoutedEventArgs e) { _result = MessageBoxResult.No; Close(); }
    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    void Dialog_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
    void Title_Down(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button) DragMove(); }
}
