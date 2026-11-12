using System.Windows;
using System.Windows.Controls;
using EulenwerkCI;

namespace Appmaster.Views;

/// <summary>Über-Dialog nach Eulenwerk-Vorlage: Logo, Name, Version, Beschreibung, Entwickler, Technik, Eulenwerk-Fuß.</summary>
public sealed class UeberFenster : Window
{
    public UeberFenster()
    {
        Title = "Über Appmaster";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)Application.Current.FindResource("EwWindow");
        EulenwerkTheme.TitelleisteAnpassen(this);

        var schliessen = new Button { Content = "Schließen", IsDefault = true, IsCancel = true };
        schliessen.Click += (_, _) => Close();

        var sp = new StackPanel { Margin = new Thickness(28, 26, 28, 22) };
        sp.Children.Add(Eulenwerk.Kopf(new AppLogo { Width = 64, Height = 64 }, "Appmaster", App.Version));
        sp.Children.Add(new TextBlock
        {
            Text = "Die Schaltzentrale für alle Eulenwerk-Apps: startet Windows- und Web-Apps, hält die benötigten KI-Dienste " +
                   "(Athene, lokale Modelle) bereit, warnt vor dem Stoppen genutzter Ressourcen und fährt nicht mehr gebrauchte herunter.",
            Style = (Style)Application.Current.FindResource("EwMuted"),
        });
        sp.Children.Add(Eulenwerk.Abschnitt("Entwickelt von"));
        sp.Children.Add(Eulenwerk.EntwicklerKarten(null));
        sp.Children.Add(Eulenwerk.Abschnitt("Technik"));
        sp.Children.Add(Eulenwerk.Technik("C#", ".NET 8", "WPF", "JSON-Konfiguration"));
        sp.Children.Add(Eulenwerk.Fuss(schliessen));
        Content = sp;
    }
}
