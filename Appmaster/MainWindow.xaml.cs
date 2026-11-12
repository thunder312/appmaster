using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Appmaster.Models;
using Appmaster.Services;
using Appmaster.ViewModels;
using Appmaster.Views;
using EulenwerkCI;

namespace Appmaster;

public partial class MainWindow : Window
{
    private readonly Steuerzentrale _z = new();

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppLogo.Rendern(64);
        EulenwerkTheme.TitelleisteAnpassen(this);

        ListeApps.ItemsSource = _z.Apps;
        ListeRessourcen.ItemsSource = _z.Ressourcen;
        ListeRechner.ItemsSource = _z.Rechner;
        _z.Apps.CollectionChanged += (_, _) => LeerApps.Visibility = _z.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LeerApps.Visibility = _z.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _z.Ressourcen.CollectionChanged += (_, _) => LeerRessourcen.Visibility = _z.Ressourcen.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LeerRessourcen.Visibility = _z.Ressourcen.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _z.Rechner.CollectionChanged += (_, _) => LeerRechner.Visibility = _z.Rechner.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LeerRechner.Visibility = _z.Rechner.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Closing += Fenster_Closing;

        // Fuß: Eule + "Ein Eulenwerk-Projekt" (Link)
        Fuss.Margin = new Thickness(0);
        Fuss.Children.Add(Eulenwerk.Eule(18));
        var tb = new TextBlock { Margin = new Thickness(8, 0, 0, 0), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        tb.Inlines.Add(Eulenwerk.Link("Ein Eulenwerk-Projekt", Eulenwerk.Homepage));
        Fuss.Children.Add(tb);

        ThemaIconAktualisieren();
        EulenwerkTheme.Geaendert += _ => ThemaIconAktualisieren();
    }

    // ---------------------------------------------------------------- Kopf

    private void ThemaIconAktualisieren() =>
        ThemaIcon.Data = (Geometry)FindResource(EulenwerkTheme.IstHell ? "Mond" : "Sonne");

    private void Thema_Click(object sender, RoutedEventArgs e) => EulenwerkTheme.Umschalten();

    // ---------------------------------------------------------------- Schließen & Einstellungen

    private bool _beendeNun;

    /// <summary>Läuft noch eine App, fragt Appmaster: Standard ist „Laufen lassen“.</summary>
    private async void Fenster_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_beendeNun || !_z.LaufendeApps) return;
        var aktion = _z.Konfig.Einstellungen.BeimSchliessen;
        if (aktion == SchliessenAktion.Fragen)
        {
            var antwort = Dlg.FrageHaken(this, "Appmaster schließen?",
                "Es laufen noch Apps, die Appmaster gestartet hat. Sie und die dafür gestarteten KI-Dienste können weiterlaufen. " +
                "Appmaster verwaltet sie dann aber erst nach dem nächsten Start wieder.",
                "Meine Wahl merken (unter „Einstellungen“ änderbar)", out var merken,
                "Laufen lassen", "Alles beenden", "Abbrechen");
            if (antwort is < 0 or 2) { e.Cancel = true; return; }
            aktion = antwort == 0 ? SchliessenAktion.LaufenLassen : SchliessenAktion.Beenden;
            if (merken) { _z.Konfig.Einstellungen.BeimSchliessen = aktion; _z.Speichern(); }
        }
        if (aktion == SchliessenAktion.Beenden)
        {
            e.Cancel = true;
            IsEnabled = false;
            await _z.AlleAppsBeendenAsync();
            _beendeNun = true;
            Close();
        }
    }

    private void Einstellungen_Click(object sender, RoutedEventArgs e)
    {
        var f = new Formular(this, "Einstellungen");
        var schliessen = f.Auswahl("Beim Schließen von Appmaster, wenn Apps laufen", Enum.GetValues<SchliessenAktion>(),
            _z.Konfig.Einstellungen.BeimSchliessen,
            a => a switch { SchliessenAktion.Fragen => "Nachfragen (Standard: Laufen lassen)", SchliessenAktion.LaufenLassen => "Immer laufen lassen", _ => "Immer alles beenden" });
        var takt = f.Text("Prüfintervall (Sekunden)", _z.Konfig.Einstellungen.PruefIntervallSekunden.ToString(), "Wie oft Appmaster Erreichbarkeit und Leistungsdaten abfragt (wirksam nach Neustart).");
        var dropbox = Ort.DropboxOrdner();
        var ort = f.Pfad("Speicherort der Konfiguration", Ort.Ordner, ordner: true,
            hinweis: (dropbox != null ? $"Tipp: {System.IO.Path.Combine(dropbox, "Appmaster")} (Dropbox) teilt die Einstellungen zwischen deinen PCs. " : "") +
                     "Zugangsschlüssel der Agenten bleiben immer lokal auf diesem PC. Wirksam nach Neustart.");
        if (!f.Zeigen(() => int.TryParse(takt.Text, out var t) && t >= 2 ? null : "Prüfintervall: eine Zahl ab 2.")) return;

        _z.Konfig.Einstellungen.BeimSchliessen = Formular.Gewaehlt<SchliessenAktion>(schliessen);
        _z.Konfig.Einstellungen.PruefIntervallSekunden = int.Parse(takt.Text);
        _z.Speichern();

        var neu = ort.Text.Trim();
        if (neu.Length > 0 && !string.Equals(neu, Ort.Ordner, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                System.IO.Directory.CreateDirectory(neu);
                var ziel = System.IO.Path.Combine(neu, "config.json");
                if (!System.IO.File.Exists(ziel)) System.IO.File.Copy(KonfigStore.Datei, ziel);
                Ort.Setzen(neu);
                Dlg.Hinweis(this, "Speicherort geändert", "Ab dem nächsten Start von Appmaster wird die Konfiguration aus dem neuen Ordner geladen.");
            }
            catch (Exception ex) { Dlg.Hinweis(this, "Speicherort konnte nicht geändert werden", ex.Message); }
        }
    }

    private void AgentHilfe_Click(object sender, RoutedEventArgs e)
    {
        var ordner = System.IO.Path.Combine(AppContext.BaseDirectory, "agent");
        var antwort = Dlg.Frage(this, "Agent installieren",
            "Der Appmaster-Agent ist ein kleines Programm für den Rechner, den Appmaster überwachen soll.\n\n" +
            "• Linux-Rechner (Athene, vServer): agent/appmaster-agent.py + install.sh – liefert CPU, RAM, GPU, Temperatur, Docker-Container und startet/stoppt Container.\n" +
            "• Webhosting (nur PHP): agent/webhosting/status.php – nur lesend (Speicherplatz, Last). Prozesse steuern geht dort nicht.\n\n" +
            "Eine Anleitung steht in agent/README.md. Danach trägst du Adresse und Token beim Rechner ein.",
            "Ordner öffnen", "Schließen");
        if (antwort == 0 && System.IO.Directory.Exists(ordner)) System.Diagnostics.Process.Start("explorer.exe", ordner);
    }

    private void Ueber_Click(object sender, RoutedEventArgs e) => new UeberFenster { Owner = this }.ShowDialog();

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (SeiteApps == null) return; // während InitializeComponent
        SeiteApps.Visibility = NavApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SeiteRessourcen.Visibility = NavRessourcen.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SeiteRechner.Visibility = NavRechner.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NeuText.Text = NavApps.IsChecked == true ? "App" : NavRessourcen.IsChecked == true ? "Ressource" : "Rechner";
    }

    public void SeiteWaehlen(string seite)
    {
        (seite switch { "ressourcen" => NavRessourcen, "rechner" => NavRechner, _ => NavApps }).IsChecked = true;
    }

    private void Neu_Click(object sender, RoutedEventArgs e)
    {
        if (NavApps.IsChecked == true) { var a = AppBearbeiten(new AppEintrag(), neu: true); if (a != null) _z.AppHinzufuegen(a); }
        else if (NavRessourcen.IsChecked == true) { var r = RessourceBearbeiten(new Ressource(), neu: true); if (r != null) _z.RessourceHinzufuegen(r); }
        else { var r = RechnerBearbeiten(new Rechner(), neu: true); if (r != null) _z.RechnerHinzufuegen(r); }
    }

    private static T Kontext<T>(object sender) => (T)((FrameworkElement)sender).DataContext;

    // ---------------------------------------------------------------- Apps

    private async void AppStart_Click(object sender, RoutedEventArgs e) => await _z.AppStartenAsync(Kontext<AppVm>(sender));
    private async void AppEnde_Click(object sender, RoutedEventArgs e) => await _z.AppBeendenAsync(Kontext<AppVm>(sender));

    private void AppBearbeiten_Click(object sender, RoutedEventArgs e)
    {
        var vm = Kontext<AppVm>(sender);
        if (AppBearbeiten(vm.Modell, neu: false) != null) { _z.AlleNeuBeschriften(); _z.Speichern(); }
    }

    private async void AppEntfernen_Click(object sender, RoutedEventArgs e)
    {
        var vm = Kontext<AppVm>(sender);
        if (Dlg.Frage(this, "App entfernen?", $"„{vm.Name}“ wird aus Appmaster entfernt. Die App selbst und ihre Daten bleiben unberührt.", "Entfernen", "Abbrechen") != 0) return;
        if (vm.Laeuft) await _z.AppBeendenAsync(vm);
        _z.AppEntfernen(vm);
    }

    /// <summary>Bearbeiten-Fenster für eine App; gibt das (geänderte) Modell zurück oder null bei Abbruch.</summary>
    private AppEintrag? AppBearbeiten(AppEintrag a, bool neu)
    {
        var f = new Formular(this, neu ? "Neue App" : "App bearbeiten");
        var name = f.Text("Name", a.Name);
        var beschr = f.Text("Beschreibung", a.Beschreibung);
        var kat = f.Auswahl("Kategorie (Farbe)", Enum.GetValues<Kategorie>(), a.Kategorie, KategorieFarben.Anzeige);
        var typ = f.Auswahl("Art", Enum.GetValues<AppTyp>(), a.Typ, t => t == AppTyp.Lokal ? "Windows-Programm" : "Web-Anwendung");
        var start = f.Pfad("Startprogramm / Befehl", a.StartBefehl, ordner: false,
            hinweis: "Windows: Programm oder Befehl (z. B. dotnet). Web: optional – startet einen lokalen Server.",
            filter: "Programme|*.exe;*.bat;*.cmd;*.lnk|Alle Dateien|*.*");
        var args = f.Text("Argumente", a.Argumente);
        var ordner = f.Pfad("Arbeitsordner", a.Arbeitsordner, ordner: true);
        var url = f.Text("Web-Adresse", a.Url, hinweis: "Nur für Web-Anwendungen: wird im Browser geöffnet.");
        var daten = f.Pfad("Datenspeicherpfad", a.Datenpfad, ordner: true,
            hinweis: "Wird der App als Umgebungsvariable APPMASTER_DATA mitgegeben.");
        var ress = f.Mehrfach("Benötigte Ressourcen", _z.Ressourcen.Select(r => (r.Modell.Id, r.Name)), a.RessourcenIds,
            "Diese werden vor dem Start geprüft und – wenn möglich – gestartet.");

        bool ok = f.Zeigen(() =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return "Bitte einen Namen eintragen.";
            var t = Formular.Gewaehlt<AppTyp>(typ);
            if (t == AppTyp.Lokal && string.IsNullOrWhiteSpace(start.Text)) return "Bitte ein Startprogramm oder einen Befehl eintragen.";
            if (t == AppTyp.Web && string.IsNullOrWhiteSpace(url.Text)) return "Bitte die Web-Adresse eintragen.";
            return null;
        });
        if (!ok) return null;

        a.Name = name.Text.Trim();
        a.Beschreibung = beschr.Text.Trim();
        a.Kategorie = Formular.Gewaehlt<Kategorie>(kat);
        a.Typ = Formular.Gewaehlt<AppTyp>(typ);
        a.StartBefehl = start.Text.Trim();
        a.Argumente = args.Text.Trim();
        a.Arbeitsordner = ordner.Text.Trim();
        a.Url = url.Text.Trim();
        a.Datenpfad = daten.Text.Trim();
        a.RessourcenIds = ress.Where(x => x.Haken.IsChecked == true).Select(x => x.Id).ToList();
        return a;
    }

    // ---------------------------------------------------------------- Ressourcen

    private async void RessStart_Click(object sender, RoutedEventArgs e) => await _z.RessourceStartenAsync(Kontext<RessourceVm>(sender));
    private async void RessStopp_Click(object sender, RoutedEventArgs e) => await _z.RessourceStoppenAsync(Kontext<RessourceVm>(sender));

    private void RessBearbeiten_Click(object sender, RoutedEventArgs e)
    {
        var vm = Kontext<RessourceVm>(sender);
        if (RessourceBearbeiten(vm.Modell, neu: false) != null) { _z.AlleNeuBeschriften(); _z.Speichern(); }
    }

    private void RessEntfernen_Click(object sender, RoutedEventArgs e)
    {
        var vm = Kontext<RessourceVm>(sender);
        if (Dlg.Frage(this, "Ressource entfernen?", $"„{vm.Name}“ wird aus Appmaster entfernt. Der Dienst selbst bleibt unberührt.", "Entfernen", "Abbrechen") == 0)
            _z.RessourceEntfernen(vm);
    }

    private Ressource? RessourceBearbeiten(Ressource r, bool neu)
    {
        if (_z.Rechner.Count == 0)
        {
            Dlg.Hinweis(this, "Erst einen Rechner anlegen", "Eine Ressource läuft immer auf einem Rechner. Lege zuerst auf der Seite „Rechner“ einen an (z. B. Athene).");
            return null;
        }
        var f = new Formular(this, neu ? "Neue Ressource" : "Ressource bearbeiten");
        var name = f.Text("Name", r.Name, "z. B. Ollama, sd-server FLUX, Postgres");
        var beschr = f.Text("Beschreibung", r.Beschreibung);
        var rechner = f.Auswahl("Läuft auf", _z.Rechner.Select(x => x.Modell).ToList(),
            _z.Rechner.Select(x => x.Modell).FirstOrDefault(x => x.Id == r.RechnerId) ?? _z.Rechner[0].Modell,
            x => string.IsNullOrWhiteSpace(x.Adresse) ? $"{x.Name} (dieser PC)" : x.Name);
        var pruef = f.Text("Prüfadresse (HTTP)", r.PruefUrl, "Antwortet sie, gilt die Ressource als „läuft“. Beispiel: http://athene:11434/api/version");
        var steuer = f.Auswahl("Steuerung", Enum.GetValues<Steuerung>(), r.Steuerung, s => s switch
        {
            Steuerung.Agent => "Docker-Container über den Agent des Rechners",
            Steuerung.Befehl => "Eigene Start-/Stoppbefehle",
            _ => "Nur überwachen",
        });
        var container = f.Text("Docker-Container (bei „über den Agent“)", r.AgentContainer, "Name wie in „docker ps“, z. B. ollama");
        var startBef = f.Text("Startbefehl (bei „Eigene Befehle“)", r.StartBefehl, "Läuft über cmd.exe, z. B. ssh athene docker start ollama");
        var stopBef = f.Text("Stoppbefehl (bei „Eigene Befehle“)", r.StopBefehl);
        var gruppe = f.Text("Konfliktgruppe", r.Konfliktgruppe,
            "Ressourcen mit gleicher Gruppe passen nicht zusammen in den Speicher, z. B. „GPU Athene“ für FLUX, Pony und Ollama. Appmaster warnt dann und bietet an, die andere zu stoppen.");
        var auto = f.Haken("Automatisch stoppen, wenn keine App sie mehr braucht", r.AutomatischStoppen);
        var warte = f.Text("Wartezeit nach dem Start (Sekunden)", r.StartWartezeit.ToString());

        bool ok = f.Zeigen(() =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return "Bitte einen Namen eintragen.";
            var st = Formular.Gewaehlt<Steuerung>(steuer);
            if (st == Steuerung.Agent && string.IsNullOrWhiteSpace(container.Text)) return "Bitte den Docker-Container-Namen eintragen.";
            if (st == Steuerung.Befehl && string.IsNullOrWhiteSpace(startBef.Text)) return "Bitte einen Startbefehl eintragen.";
            if (!int.TryParse(warte.Text, out var s) || s < 5) return "Wartezeit: eine Zahl ab 5.";
            return null;
        });
        if (!ok) return null;

        r.Name = name.Text.Trim();
        r.Beschreibung = beschr.Text.Trim();
        r.RechnerId = Formular.Gewaehlt<Rechner>(rechner)?.Id ?? r.RechnerId;
        r.PruefUrl = pruef.Text.Trim();
        r.Steuerung = Formular.Gewaehlt<Steuerung>(steuer);
        r.AgentContainer = container.Text.Trim();
        r.StartBefehl = startBef.Text.Trim();
        r.StopBefehl = stopBef.Text.Trim();
        r.Konfliktgruppe = gruppe.Text.Trim();
        r.AutomatischStoppen = auto.IsChecked == true;
        r.StartWartezeit = int.Parse(warte.Text);
        return r;
    }

    // ---------------------------------------------------------------- Rechner

    private async void RechAufwecken_Click(object sender, RoutedEventArgs e)
    {
        var vm = Kontext<RechnerVm>(sender);
        await _z.RechnerAufweckenAsync(vm, s => vm.Meldung = s);
    }

    private async void RechAus_Click(object sender, RoutedEventArgs e) => await _z.RechnerHerunterfahrenAsync(Kontext<RechnerVm>(sender));

    private void RechBearbeiten_Click(object sender, RoutedEventArgs e)
    {
        var vm = Kontext<RechnerVm>(sender);
        if (RechnerBearbeiten(vm.Modell, neu: false) != null)
        {
            _z.AlleNeuBeschriften();
            _z.Speichern();
        }
    }

    private void RechEntfernen_Click(object sender, RoutedEventArgs e)
    {
        var r = Kontext<RechnerVm>(sender);
        if (Dlg.Frage(this, "Rechner entfernen?", $"„{r.Name}“ wird aus Appmaster entfernt.", "Entfernen", "Abbrechen") == 0)
            _z.RechnerEntfernen(r);
    }

    private Rechner? RechnerBearbeiten(Rechner r, bool neu)
    {
        var f = new Formular(this, neu ? "Neuer Rechner" : "Rechner bearbeiten");
        var name = f.Text("Name", r.Name, "z. B. Athene, GPU-PC, vServer");
        var adr = f.Text("Adresse", r.Adresse, "Hostname oder IP. Leer lassen für diesen PC.");
        var mac = f.Text("MAC-Adresse (Wake-on-LAN)", r.MacAdresse, "aa:bb:cc:dd:ee:ff – nur bei Rechnern im selben Netzwerk, die per Kabel angeschlossen sind. Leer = nicht aufweckbar.");
        var bc = f.Text("Broadcast-Adresse", r.Broadcast, "Meist 255.255.255.255, oder z. B. 192.168.1.255 für das Heimnetz.");
        var art = f.Auswahl("Überwachung / Steuerung", Enum.GetValues<AgentArt>(), r.AgentArt, a => a switch
        {
            AgentArt.LinuxAgent => "Appmaster-Agent (Linux): Leistungsdaten + Docker",
            AgentArt.PhpStatus => "status.php (Webhosting): nur lesend",
            _ => "Keine (nur Ping)",
        });
        var url = f.Text("Agent-Adresse", r.AgentUrl, "Linux: http://athene:8765 · Webhosting: https://deine-seite.de/appmaster/status.php");
        var token = f.Text("Agent-Token", r.AgentToken, "Wird nur auf diesem PC gespeichert, nicht in der (Dropbox-)Konfiguration.");
        var docker = f.Haken("Docker Desktop bei Bedarf starten (nur dieser PC)", r.DockerDesktopStarten,
            "Läuft die Docker-Engine nicht, startet Appmaster Docker Desktop vor einem Docker-Befehl (z. B. docker start …) und wartet, bis sie bereit ist. Nur wirksam, wenn die Adresse leer ist.");
        var dockerPfad = f.Pfad("Docker Desktop.exe (optional)", r.DockerDesktopPfad, ordner: false,
            hinweis: "Leer = automatisch suchen (über docker.exe bzw. C:\\Program Files\\Docker). Wird nur auf diesem PC gespeichert.", filter: "Docker Desktop|Docker Desktop.exe|Programme|*.exe");
        var tunnel = f.Haken("Agent über SSH-Tunnel erreichen", r.TunnelAktiv,
            "Für Server im Internet: Der Agent lauscht dort nur auf 127.0.0.1 (install.sh --bind 127.0.0.1). Appmaster baut den Tunnel selbst auf und beendet ihn beim Schließen. Die Agent-Adresse oben wird dann ignoriert.");
        var tZiel = f.Text("Tunnel: SSH-Ziel", r.TunnelZiel, "z. B. root@82.165.153.177 oder ein Name aus ~/.ssh/config");
        var tSchl = f.Pfad("Tunnel: privater Schlüssel", r.TunnelSchluessel, ordner: false,
            hinweis: "OpenSSH-Format (z. B. C:\\Users\\du\\.ssh\\id_ed25519), keine .ppk-Datei. Wird nur auf diesem PC gespeichert. Leer = Standardschlüssel bzw. ssh-Konfiguration.");
        var tLokal = f.Text("Tunnel: lokaler Port", r.TunnelLokalPort.ToString(), "Port an diesem PC, z. B. 8766 (je Server ein anderer).");
        var tServer = f.Text("Tunnel: Agent-Port auf dem Server", r.TunnelServerPort.ToString(), "Standard 8765.");

        bool ok = f.Zeigen(() =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return "Bitte einen Namen eintragen.";
            if (mac.Text.Trim().Length > 0 && !WakeOnLan.MacGueltig(mac.Text)) return "Die MAC-Adresse ist ungültig (Beispiel: aa:bb:cc:dd:ee:ff).";
            if (tunnel.IsChecked == true)
            {
                if (Formular.Gewaehlt<AgentArt>(art) != AgentArt.LinuxAgent) return "Der SSH-Tunnel funktioniert nur mit „Appmaster-Agent (Linux)“. Bitte oben „Überwachung / Steuerung“ entsprechend wählen.";
                if (string.IsNullOrWhiteSpace(tZiel.Text)) return "Bitte das SSH-Ziel für den Tunnel eintragen (z. B. root@82.165.153.177).";
                if (!int.TryParse(tLokal.Text, out var lp) || lp is < 1024 or > 65535) return "Der lokale Tunnel-Port muss zwischen 1024 und 65535 liegen.";
                if (!int.TryParse(tServer.Text, out var sp) || sp is < 1 or > 65535) return "Der Agent-Port auf dem Server ist ungültig.";
                if (tSchl.Text.Trim().EndsWith(".ppk", StringComparison.OrdinalIgnoreCase)) return "OpenSSH kann keine .ppk-Schlüssel lesen. Nimm die Datei ohne Endung (OpenSSH-Format) oder exportiere sie mit PuTTYgen.";
            }
            else if (Formular.Gewaehlt<AgentArt>(art) != AgentArt.Keiner && string.IsNullOrWhiteSpace(url.Text)) return "Bitte die Agent-Adresse eintragen.";
            return null;
        });
        if (!ok) return null;

        r.Name = name.Text.Trim();
        r.Adresse = adr.Text.Trim();
        r.MacAdresse = mac.Text.Trim();
        r.Broadcast = string.IsNullOrWhiteSpace(bc.Text) ? "255.255.255.255" : bc.Text.Trim();
        r.AgentArt = Formular.Gewaehlt<AgentArt>(art);
        r.AgentUrl = url.Text.Trim();
        r.AgentToken = token.Text.Trim();
        r.DockerDesktopStarten = docker.IsChecked == true;
        r.DockerDesktopPfad = dockerPfad.Text.Trim();
        r.TunnelAktiv = tunnel.IsChecked == true;
        r.TunnelZiel = tZiel.Text.Trim();
        r.TunnelSchluessel = tSchl.Text.Trim();
        if (int.TryParse(tLokal.Text, out var lport)) r.TunnelLokalPort = lport;
        if (int.TryParse(tServer.Text, out var sport)) r.TunnelServerPort = sport;
        SshTunnel.Beenden(r.Id);   // mit geänderten Einstellungen neu aufbauen
        return r;
    }
}
