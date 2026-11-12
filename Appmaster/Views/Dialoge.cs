using System.Windows;
using System.Windows.Controls;
using EulenwerkCI;
using Microsoft.Win32;

namespace Appmaster.Views;

/// <summary>Einfache Meldungs- und Rückfragefenster im Eulenwerk-Design.</summary>
public static class Dlg
{
    public static Window Grundfenster(Window? besitzer, string titel, double breite)
    {
        var w = new Window
        {
            Title = titel, Width = breite, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Owner = besitzer,
            WindowStartupLocation = besitzer != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Style = (Style)Application.Current.FindResource("EwWindow"),
        };
        if (besitzer?.Icon != null) w.Icon = besitzer.Icon;
        EulenwerkTheme.TitelleisteAnpassen(w);
        return w;
    }

    /// <summary>Zeigt Text und Knöpfe; gibt den Index des gedrückten Knopfes zurück (-1 = Fenster geschlossen).</summary>
    public static int Frage(Window? besitzer, string titel, string text, params string[] knoepfe) =>
        FrageHaken(besitzer, titel, text, null, out _, knoepfe);

    /// <summary>Wie <see cref="Frage"/>, zusätzlich mit Ankreuzfeld (z. B. „Nicht mehr fragen“).</summary>
    public static int FrageHaken(Window? besitzer, string titel, string text, string? haken, out bool angehakt, params string[] knoepfe)
    {
        var w = Grundfenster(besitzer, titel, 460);
        int ergebnis = -1;
        var reihe = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        for (int i = 0; i < knoepfe.Length; i++)
        {
            int index = i;
            var b = new Button { Content = knoepfe[i], Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0), IsDefault = i == 0, IsCancel = i == knoepfe.Length - 1 && knoepfe.Length > 1 };
            if (i == 0) b.Style = (Style)Application.Current.FindResource("EwButtonPrimary");
            b.Click += (_, _) => { ergebnis = index; w.Close(); };
            reihe.Children.Add(b);
        }
        var inhalt = new StackPanel { Margin = new Thickness(24) };
        inhalt.Children.Add(new TextBlock { Text = titel, Style = (Style)Application.Current.FindResource("EwH3") });
        inhalt.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("EwMuted") });
        CheckBox? box = null;
        if (haken != null)
        {
            box = new CheckBox { Content = haken, Margin = new Thickness(0, 16, 0, 0) };
            inhalt.Children.Add(box);
        }
        inhalt.Children.Add(reihe);
        w.Content = inhalt;
        w.ShowDialog();
        angehakt = box?.IsChecked == true;
        return ergebnis;
    }

    public static void Hinweis(Window? besitzer, string titel, string text) => Frage(besitzer, titel, text, "OK");
}

/// <summary>Baukasten für die Bearbeiten-Fenster: Beschriftung + Feld untereinander, unten Speichern/Abbrechen.</summary>
public sealed class Formular
{
    private readonly Window _fenster;
    private readonly StackPanel _felder = new();

    public Formular(Window? besitzer, string titel)
    {
        _fenster = Dlg.Grundfenster(besitzer, titel, 560);
        _fenster.SizeToContent = SizeToContent.Manual;
        _fenster.Height = 640;
        _fenster.ResizeMode = ResizeMode.CanResizeWithGrip;
        _fenster.MinHeight = 380;
    }

    private static Style Stil(string key) => (Style)Application.Current.FindResource(key);

    private void Zeile(string beschriftung, UIElement feld, string? hinweis = null)
    {
        _felder.Children.Add(new TextBlock { Text = beschriftung, Style = Stil("EwLabel") });
        feld.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, hinweis == null ? 16 : 4));
        _felder.Children.Add(feld);
        if (hinweis != null)
            _felder.Children.Add(new TextBlock { Text = hinweis, Style = Stil("EwMuted"), FontSize = 12, Margin = new Thickness(0, 0, 0, 16) });
    }

    public TextBox Text(string beschriftung, string wert, string? hinweis = null, bool mehrzeilig = false)
    {
        var t = new TextBox { Text = wert };
        if (mehrzeilig) { t.AcceptsReturn = true; t.MinHeight = 64; t.TextWrapping = TextWrapping.Wrap; }
        Zeile(beschriftung, t, hinweis);
        return t;
    }

    public ComboBox Auswahl<T>(string beschriftung, IEnumerable<T> werte, T gewaehlt, Func<T, string>? anzeige = null)
    {
        var c = new ComboBox { DisplayMemberPath = null };
        foreach (var w in werte) c.Items.Add(new ComboBoxItem { Content = anzeige?.Invoke(w) ?? w?.ToString(), Tag = w });
        c.SelectedItem = c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, gewaehlt)) ?? (c.Items.Count > 0 ? c.Items[0] : null);
        Zeile(beschriftung, c);
        return c;
    }

    public static T? Gewaehlt<T>(ComboBox c) => c.SelectedItem is ComboBoxItem { Tag: T t } ? t : default;

    public CheckBox Haken(string beschriftung, bool wert, string? hinweis = null)
    {
        var h = new CheckBox { Content = beschriftung, IsChecked = wert };
        Zeile("", h, hinweis);
        return h;
    }

    public TextBox Pfad(string beschriftung, string wert, bool ordner, string? hinweis = null, string filter = "Alle Dateien|*.*")
    {
        var t = new TextBox { Text = wert };
        var b = new Button { Content = "Durchsuchen …", Margin = new Thickness(8, 0, 0, 0), Style = Stil("EwButtonGhost") };
        b.Click += (_, _) =>
        {
            if (ordner)
            {
                var d = new OpenFolderDialog { InitialDirectory = t.Text };
                if (d.ShowDialog(_fenster) == true) t.Text = d.FolderName;
            }
            else
            {
                var d = new OpenFileDialog { FileName = t.Text, Filter = filter };
                if (d.ShowDialog(_fenster) == true) t.Text = d.FileName;
            }
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(b, 1);
        g.Children.Add(t);
        g.Children.Add(b);
        Zeile(beschriftung, g, hinweis);
        return t;
    }

    public List<(CheckBox Haken, string Id)> Mehrfach(string beschriftung, IEnumerable<(string Id, string Name)> eintraege, ICollection<string> gewaehlt, string? hinweis = null)
    {
        var liste = new List<(CheckBox, string)>();
        var sp = new StackPanel();
        foreach (var (id, name) in eintraege)
        {
            var h = new CheckBox { Content = name, IsChecked = gewaehlt.Contains(id), Margin = new Thickness(0, 0, 0, 6) };
            sp.Children.Add(h);
            liste.Add((h, id));
        }
        if (liste.Count == 0)
            sp.Children.Add(new TextBlock { Text = "Noch keine Ressourcen angelegt.", Style = Stil("EwMuted") });
        Zeile(beschriftung, sp, hinweis);
        return liste;
    }

    /// <summary>Zeigt das Fenster; true = Speichern, false = Abbrechen. <paramref name="pruefen"/> darf einen Fehlertext liefern.</summary>
    public bool Zeigen(Func<string?>? pruefen = null)
    {
        bool ok = false;
        var speichern = new Button { Content = "Speichern", Style = Stil("EwButtonPrimary"), IsDefault = true };
        var abbrechen = new Button { Content = "Abbrechen", Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var fehler = new TextBlock { Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("EwDanger"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        speichern.Click += (_, _) =>
        {
            var f = pruefen?.Invoke();
            if (f != null) { fehler.Text = f; return; }
            ok = true;
            _fenster.Close();
        };
        var fuss = new DockPanel { Margin = new Thickness(24, 12, 24, 16) };
        var knoepfe = new StackPanel { Orientation = Orientation.Horizontal };
        knoepfe.Children.Add(speichern);
        knoepfe.Children.Add(abbrechen);
        DockPanel.SetDock(knoepfe, Dock.Right);
        fuss.Children.Add(knoepfe);
        fuss.Children.Add(fehler);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _felder, Padding = new Thickness(24, 20, 24, 0) };
        var wurzel = new DockPanel();
        DockPanel.SetDock(fuss, Dock.Bottom);
        wurzel.Children.Add(fuss);
        wurzel.Children.Add(scroll);
        _fenster.Content = wurzel;
        _fenster.ShowDialog();
        return ok;
    }
}
