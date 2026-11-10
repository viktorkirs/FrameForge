using System.ComponentModel;
using FrameForge.Core;

namespace FrameForge.UI;

public sealed class TextTable : INotifyPropertyChanged
{
    public static TextTable Instance { get; } = new();

    TextTable()
    {
        Loc.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public string this[string key] => Loc.Get(key);

    public event PropertyChangedEventHandler? PropertyChanged;
}
