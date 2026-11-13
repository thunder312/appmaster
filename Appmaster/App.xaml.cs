using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Appmaster.Services;
using Appmaster.Views;
using EulenwerkCI;

namespace Appmaster;

public partial class App : Application
{
    public const string Name = "Appmaster";
    public const string Version = "0.5.0";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        EulenwerkTheme.Initialisieren("Administration", Name);

        // Entwicklerhilfe: "Appmaster.exe --icon <ziel.ico>" schreibt das Logo als .ico und beendet sich.
        if (e.Args.Length == 2 && e.Args[0] == "--icon")
        {
            IcoSchreiben(e.Args[1]);
            Shutdown();
            return;
        }

        // Entwicklerhilfe: "Appmaster.exe --docker-test <ziel.txt>" schreibt gefundenen Docker-Desktop-Pfad und Engine-Zustand und beendet sich.
        if (e.Args.Length == 2 && e.Args[0] == "--docker-test")
        {
            var test = new Models.Rechner { DockerDesktopStarten = true };
            File.WriteAllText(e.Args[1],
                $"ExePfad: {DockerDesktop.ExePfad(test) ?? "(nicht gefunden)"}\nEngineLaeuft: {Task.Run(DockerDesktop.EngineLaeuftAsync).GetAwaiter().GetResult()}\n" +
                $"IstDockerBefehl: {DockerDesktop.IstDockerBefehl("docker start bildki-queue")} {DockerDesktop.IstDockerBefehl("ssh athene docker start x")} " +
                $"{DockerDesktop.IstDockerBefehl("ollama serve")} {DockerDesktop.IstDockerBefehl("dockerize.exe run")} {DockerDesktop.IstDockerBefehl("cmd /c docker compose up")}\n");
            Shutdown();
            return;
        }

        // Entwicklerhilfe: "Appmaster.exe --suche-test <ziel.txt> [port …]" sucht auf diesem PC (Arbeitsrechner) und auf localhost (Webserver, angegebene Ports).
        if (e.Args.Length >= 2 && e.Args[0] == "--suche-test")
        {
            var ports = e.Args.Skip(2).Where(x => int.TryParse(x, out _)).Select(int.Parse).ToList();
            var hosts = e.Args.Skip(2).Where(x => !int.TryParse(x, out _)).ToList();
            var ziel = new List<SuchRechner> { new("t", "Test", "", true, Models.RechnerKategorie.Arbeitsrechner | Models.RechnerKategorie.Webserver, ports, hosts) };
            var funde = Task.Run(() => EulenwerkSuche.SuchenAsync(ziel)).GetAwaiter().GetResult();
            File.WriteAllText(e.Args[1], string.Join("\n", funde.Select(f =>
                $"{f.Typ} | {f.Name} | {f.Kategorie} | v{f.Version} | {f.Start}{f.Url} | symbol={(f.Symbol?.Length ?? 0)} | ressourcen={f.Manifest?.Ressourcen.Count ?? 0} | {f.Beschreibung}")) + $"\n({funde.Count} Funde)\n");
            for (int i = 0; i < funde.Count; i++)
                if (funde[i].Symbol is { } png) File.WriteAllBytes($"{e.Args[1]}.{i}.png", png);   // Symbole zur Sichtprüfung
            Shutdown();
            return;
        }

        // Entwicklerhilfe: "Appmaster.exe --ki-test <ziel.txt> <host>" sucht KI-Dienste (Ollama, sd-server) auf dem Rechner.
        if (e.Args.Length == 3 && e.Args[0] == "--ki-test")
        {
            var r = new SuchRechner("t", "Test", e.Args[2] == "localhost" ? "" : e.Args[2], e.Args[2] == "localhost", Models.RechnerKategorie.GpuRechner, new List<int>());
            var funde = Task.Run(() => KiSuche.SuchenAsync(r, new List<ContainerInfo>())).GetAwaiter().GetResult();
            File.WriteAllText(e.Args[1], string.Join(Environment.NewLine, funde.Select(f => $"{f.Name} | {f.PruefUrl} | läuft={f.Laeuft} | {f.Beschreibung}")) + $"{Environment.NewLine}({funde.Count} Funde)");
            Shutdown();
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--sicherung-test")
        {
            File.WriteAllText(e.Args[1], Task.Run(SicherungSelbsttest.Ausfuehren).GetAwaiter().GetResult());
            Shutdown();
            return;
        }

        if (Ort.Erststart && e.Args.Length == 0)
        {
            // Der Dialog ist das einzige Fenster – ohne das würde Schließen die App beenden.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ErststartAbfragen();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        var fenster = new MainWindow();
        MainWindow = fenster;
        fenster.Show();

        // Entwicklerhilfe: "--shot <ziel.png> [apps|ressourcen|rechner] [hell]" rendert das Fenster und beendet sich.
        if (e.Args.Length >= 2 && e.Args[0] == "--shot")
        {
            if (e.Args.Contains("hell")) EulenwerkTheme.Setzen(EwDesign.Hell);
            fenster.SeiteWaehlen(e.Args.Length > 2 ? e.Args[2] : "apps");
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            t.Tick += (_, _) =>
            {
                var bmp = new RenderTargetBitmap((int)fenster.ActualWidth, (int)fenster.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var grund = new System.Windows.Media.DrawingVisual();
                using (var dc = grund.RenderOpen()) dc.DrawRectangle(fenster.Background, null, new Rect(0, 0, fenster.ActualWidth, fenster.ActualHeight));
                bmp.Render(grund);
                bmp.Render((System.Windows.Media.Visual)fenster.Content);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var f = File.Create(e.Args[1]);
                enc.Save(f);
                Shutdown();
            };
            t.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SshTunnel.AlleBeenden();
        Sicherung.Abschliessen();
        base.OnExit(e);
    }

    /// <summary>Beim ersten Start: wo soll die Konfiguration liegen (nur dieser PC, Dropbox, eigener Ordner)?</summary>
    private static void ErststartAbfragen()
    {
        var dropbox = Ort.DropboxOrdner();
        var dropboxZiel = dropbox == null ? null : Path.Combine(dropbox, "Appmaster");
        var optionen = new List<string> { "Nur auf diesem PC" };
        if (dropboxZiel != null) optionen.Add("In der Dropbox (gemeinsam mit anderen PCs)");
        optionen.Add("In einem anderen Ordner");

        var f = new Formular(null, "Willkommen bei Appmaster");
        var wahl = f.Auswahl("Wo soll Appmaster seine Einstellungen speichern?", optionen, optionen[dropboxZiel != null ? 1 : 0]);
        var eigener = f.Pfad("Ordner (nur bei „anderer Ordner“)", "", ordner: true,
            hinweis: "Apps, Rechner und Ressourcen stehen in config.json in diesem Ordner. Zugangsschlüssel der Agenten bleiben aus Sicherheitsgründen immer lokal auf diesem PC. " +
                     "Alles lässt sich später unter „Einstellungen“ ändern.");
        var vorschlag = dropboxZiel != null && File.Exists(Path.Combine(dropboxZiel, Sicherung.DateiName)) ? Path.Combine(dropboxZiel, Sicherung.DateiName) : "";
        var sicherung = f.Pfad("Sicherungsdatei zum Wiederherstellen (optional)", vorschlag, ordner: false, filter: SicherungsDialoge.Filter,
            hinweis: "Appmaster neu installiert? Wähle die Datei appmaster-sicherung.enc, um Rechner, Ressourcen, Apps und Zugangsdaten wiederherzustellen. " +
                     "Leer lassen für eine neue, leere Konfiguration.");
        var sicherungPw = f.Passwort("Passwort der Sicherung (nur zum Wiederherstellen)");
        SicherungsInhalt? wiederherstellen = null;
        if (!f.Zeigen(() =>
        {
            if (Formular.Gewaehlt<string>(wahl) == optionen[^1] && string.IsNullOrWhiteSpace(eigener.Text)) return "Bitte einen Ordner wählen.";
            if (string.IsNullOrWhiteSpace(sicherung.Text)) return null;
            if (!File.Exists(sicherung.Text.Trim())) return "Die Sicherungsdatei existiert nicht.";
            try { wiederherstellen = Sicherung.Lesen(sicherung.Text.Trim(), sicherungPw.Password); return null; }
            catch (Exception ex) { return ex.Message; }
        }, "Weiter"))
        {
            Ort.Setzen(Ort.LokalerOrdner);
            return;
        }
        var gewaehlt = Formular.Gewaehlt<string>(wahl);
        var ziel = gewaehlt == optionen[0] ? Ort.LokalerOrdner
                 : gewaehlt == optionen[^1] ? eigener.Text.Trim()
                 : dropboxZiel!;
        try { Directory.CreateDirectory(ziel); Ort.Setzen(ziel); }
        catch { Ort.Setzen(Ort.LokalerOrdner); }

        if (wiederherstellen != null)
        {
            try { Sicherung.Wiederherstellen(wiederherstellen, sicherungPw.Password); }
            catch (Exception ex) { Dlg.Hinweis(null, "Wiederherstellen fehlgeschlagen", ex.Message); }
        }
    }

    /// <summary>ICO mit PNG-Bildern (16–256 px), wie es ab Windows Vista üblich ist.</summary>
    private static void IcoSchreiben(string ziel)
    {
        int[] groessen = [16, 24, 32, 48, 64, 128, 256];
        var bilder = groessen.Select(g =>
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(AppLogo.Rendern(g)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }).ToList();

        using var w = new BinaryWriter(File.Create(ziel));
        w.Write((short)0); w.Write((short)1); w.Write((short)bilder.Count);
        int offset = 6 + 16 * bilder.Count;
        for (int i = 0; i < bilder.Count; i++)
        {
            int g = groessen[i];
            w.Write((byte)(g >= 256 ? 0 : g)); w.Write((byte)(g >= 256 ? 0 : g));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(bilder[i].Length); w.Write(offset);
            offset += bilder[i].Length;
        }
        foreach (var b in bilder) w.Write(b);
    }
}
