// Eulenwerk · Hell/Dunkel-Umschaltung für WPF (gehört zu Eulenwerk.Theme.xaml).
//
// Voraussetzungen:
//   * App.xaml bindet wie gewohnt Eulenwerk.<Kategorie>.xaml und danach Eulenwerk.Theme.xaml ein.
//   * Eulenwerk.Hell.xaml und Eulenwerk.<Kategorie>.Hell.xaml liegen im selben Ordner (Build-Aktion: Page).
//   * Eigene Styles/XAML verwenden für Farben {DynamicResource EwText} usw. – StaticResource-Verweise
//     werden beim Start einmal aufgelöst und wechseln später nicht mehr mit.
//
// Benutzung:
//   // App.xaml.cs, OnStartup – vor dem ersten Fenster:
//   EulenwerkTheme.Initialisieren("Kreativ", "BildKI");        // lädt die gespeicherte Wahl (Standard: Dunkel)
//   // Umschalter (z. B. Menüeintrag oder Button im Header):
//   EulenwerkTheme.Umschalten();                               // Hell <-> Dunkel, wird gespeichert
//   EulenwerkTheme.Setzen(EwDesign.System);                    // folgt der Windows-Einstellung
//   // Fenster: Titelleiste passend färben (Windows 10 20H1+ / 11)
//   EulenwerkTheme.TitelleisteAnpassen(this);                  // im Konstruktor nach InitializeComponent()
//
// Gespeichert wird unter %APPDATA%\Eulenwerk\<App>\design.txt.

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace EulenwerkCI;

public enum EwDesign { Dunkel, Hell, System }

public static class EulenwerkTheme
{
    private static string _kategorie = "Organisation";
    private static string _app = "App";

    /// <summary>Gespeicherte Wahl des Benutzers (Dunkel, Hell oder System).</summary>
    public static EwDesign Wahl { get; private set; } = EwDesign.Dunkel;

    /// <summary>Tatsächlich aktives Design.</summary>
    public static bool IstHell { get; private set; }

    /// <summary>Wird nach jedem Wechsel ausgelöst (z. B. um Grafiken neu zu zeichnen).</summary>
    public static event Action<bool>? Geaendert;

    public static void Initialisieren(string kategorie, string appName, EwDesign standard = EwDesign.Dunkel)
    {
        _kategorie = kategorie;
        _app = appName;
        Wahl = Laden() ?? standard;
        Anwenden();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (Wahl == EwDesign.System && e.Category == UserPreferenceCategory.General)
                Application.Current?.Dispatcher.Invoke(Anwenden);
        };
    }

    public static void Setzen(EwDesign wahl)
    {
        Wahl = wahl;
        Speichern();
        Anwenden();
    }

    public static void Umschalten() => Setzen(IstHell ? EwDesign.Dunkel : EwDesign.Hell);

    private static void Anwenden()
    {
        bool hell = Wahl == EwDesign.Hell || (Wahl == EwDesign.System && WindowsIstHell());
        var md = Application.Current.Resources.MergedDictionaries;

        foreach (var d in md.Where(d => d.Source?.OriginalString.EndsWith(".Hell.xaml", StringComparison.OrdinalIgnoreCase) == true
                                     || d.Source?.OriginalString.EndsWith("Eulenwerk.Hell.xaml", StringComparison.OrdinalIgnoreCase) == true).ToList())
            md.Remove(d);

        if (hell)
        {
            var theme = md.FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Eulenwerk.Theme.xaml", StringComparison.OrdinalIgnoreCase) == true)
                        ?? throw new InvalidOperationException("Eulenwerk.Theme.xaml ist in App.xaml nicht eingebunden.");
            string basis = theme.Source.OriginalString[..^"Eulenwerk.Theme.xaml".Length];
            md.Add(new ResourceDictionary { Source = new Uri(basis + "Eulenwerk.Hell.xaml", UriKind.RelativeOrAbsolute) });
            md.Add(new ResourceDictionary { Source = new Uri(basis + $"Eulenwerk.{_kategorie}.Hell.xaml", UriKind.RelativeOrAbsolute) });
        }

        IstHell = hell;
        foreach (Window w in Application.Current.Windows)
            TitelleisteAnpassen(w);
        Geaendert?.Invoke(hell);
    }

    private static bool WindowsIstHell()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    private static string Datei => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Eulenwerk", _app, "design.txt");

    private static EwDesign? Laden()
    {
        try { return Enum.TryParse(File.ReadAllText(Datei).Trim(), out EwDesign d) ? d : null; }
        catch { return null; }
    }

    private static void Speichern()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Datei)!);
            File.WriteAllText(Datei, Wahl.ToString());
        }
        catch { /* nicht kritisch */ }
    }

    // ---- Titelleiste (DWM) ----
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Färbt die Windows-Titelleiste passend zum aktiven Design.</summary>
    public static void TitelleisteAnpassen(Window fenster)
    {
        void Setzen()
        {
            var hwnd = new WindowInteropHelper(fenster).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dunkel = IstHell ? 0 : 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref dunkel, sizeof(int)) != 0)   // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(hwnd, 19, ref dunkel, sizeof(int));        // ältere Windows-10-Builds
        }
        if (new WindowInteropHelper(fenster).Handle == IntPtr.Zero) fenster.SourceInitialized += (_, _) => Setzen();
        else Setzen();
    }
}
