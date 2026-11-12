using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Appmaster.ViewModels;

public abstract class Beobachtbar : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Melden([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Setze<T>(ref T feld, T wert, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(feld, wert)) return false;
        feld = wert;
        Melden(name);
        return true;
    }
}
