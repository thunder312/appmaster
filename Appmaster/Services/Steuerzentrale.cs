using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Appmaster.Models;
using Appmaster.ViewModels;
using Appmaster.Views;

namespace Appmaster.Services;

/// <summary>
/// Das Herz von Appmaster: kennt Apps, Ressourcen und Rechner, prüft laufend Erreichbarkeit und Messwerte,
/// weckt Rechner per Wake-on-LAN, startet beim App-Start die benötigten Ressourcen, achtet auf Konfliktgruppen
/// (z. B. GPU-Speicher), warnt vor dem Stoppen genutzter Ressourcen und fährt nicht mehr gebrauchte herunter.
/// </summary>
public sealed class Steuerzentrale
{
    private readonly DispatcherTimer _takt = new();
    private bool _prueft;

    public Konfiguration Konfig { get; }
    public ObservableCollection<AppVm> Apps { get; } = new();
    public ObservableCollection<RessourceVm> Ressourcen { get; } = new();
    public ObservableCollection<RechnerVm> Rechner { get; } = new();

    public Steuerzentrale()
    {
        Konfig = KonfigStore.Laden();
        foreach (var r in Konfig.Rechner) Rechner.Add(new RechnerVm(r));
        foreach (var r in Konfig.Ressourcen) Ressourcen.Add(new RessourceVm(r));
        foreach (var a in Konfig.Apps) Apps.Add(new AppVm(a));
        AlleNeuBeschriften();

        _takt.Interval = TimeSpan.FromSeconds(Math.Max(2, Konfig.Einstellungen.PruefIntervallSekunden));
        _takt.Tick += async (_, _) => await ZustandAktualisierenAsync();
        _takt.Start();
        _ = ZustandAktualisierenAsync();
    }

    private static Window? Fenster => Application.Current?.MainWindow;

    private RechnerVm? RechnerVon(Ressource r) => Rechner.FirstOrDefault(x => x.Modell.Id == r.RechnerId);

    // ---------------------------------------------------------------- Konfiguration

    public void Speichern()
    {
        Konfig.Rechner = Rechner.Select(r => r.Modell).ToList();
        Konfig.Ressourcen = Ressourcen.Select(r => r.Modell).ToList();
        Konfig.Apps = Apps.Select(a => a.Modell).ToList();
        try { KonfigStore.Speichern(Konfig); }
        catch (Exception ex) { Dlg.Hinweis(Fenster, "Speichern fehlgeschlagen", ex.Message); }
    }

    /// <summary>Rechnernamen und Ressourcen-Zusammenfassungen neu berechnen (nach Änderungen).</summary>
    public void AlleNeuBeschriften()
    {
        foreach (var r in Ressourcen)
            r.RechnerName = Rechner.FirstOrDefault(x => x.Modell.Id == r.Modell.RechnerId)?.Name ?? "—";
        foreach (var a in Apps)
        {
            var namen = a.Modell.RessourcenIds
                .Select(id => Ressourcen.FirstOrDefault(r => r.Modell.Id == id)?.Name)
                .Where(n => n != null);
            a.RessourcenText = string.Join(" · ", namen);
            a.ModellGeaendert();
        }
        foreach (var r in Ressourcen) r.ModellGeaendert();
        foreach (var r in Rechner) r.ModellGeaendert();
        NutzerAktualisieren();
    }

    public void AppHinzufuegen(AppEintrag a) { Apps.Add(new AppVm(a)); AlleNeuBeschriften(); Speichern(); }
    public void RessourceHinzufuegen(Ressource r) { Ressourcen.Add(new RessourceVm(r)); AlleNeuBeschriften(); Speichern(); _ = ZustandAktualisierenAsync(); }
    public void RechnerHinzufuegen(Rechner r) { Rechner.Add(new RechnerVm(r)); AlleNeuBeschriften(); Speichern(); _ = ZustandAktualisierenAsync(); }
    public void AppEntfernen(AppVm a) { Apps.Remove(a); Speichern(); }

    public bool RessourceEntfernen(RessourceVm r)
    {
        var verwender = Apps.Where(a => a.Modell.RessourcenIds.Contains(r.Modell.Id)).Select(a => a.Name).ToList();
        if (verwender.Count > 0)
        {
            Dlg.Hinweis(Fenster, "Ressource wird noch gebraucht", $"„{r.Name}“ ist noch bei diesen Apps eingetragen: {string.Join(", ", verwender)}.");
            return false;
        }
        Ressourcen.Remove(r);
        Speichern();
        return true;
    }

    public bool RechnerEntfernen(RechnerVm r)
    {
        var verwender = Ressourcen.Where(x => x.Modell.RechnerId == r.Modell.Id).Select(x => x.Name).ToList();
        if (verwender.Count > 0)
        {
            Dlg.Hinweis(Fenster, "Rechner wird noch gebraucht", $"Auf „{r.Name}“ liegen noch Ressourcen: {string.Join(", ", verwender)}.");
            return false;
        }
        Rechner.Remove(r);
        Speichern();
        return true;
    }

    // ---------------------------------------------------------------- Zustand

    private async Task ZustandAktualisierenAsync()
    {
        if (_prueft) return;
        _prueft = true;
        try
        {
            await Task.WhenAll(Rechner.ToList().Select(RechnerAktualisierenAsync));
            var liste = Ressourcen.ToList();
            var ergebnisse = await Task.WhenAll(liste.Select(async r => (r, z: await Gesundheit.PruefenAsync(r.Modell.PruefUrl))));
            foreach (var (r, z) in ergebnisse)
                if (r.Zustand != Zustand.Startet) r.Zustand = z;
        }
        finally { _prueft = false; }
    }

    private static async Task RechnerAktualisierenAsync(RechnerVm v)
    {
        var pingOk = await Erreichbar.PingAsync(v.Modell);
        var daten = AgentClient.Eingerichtet(v.Modell) ? await AgentClient.StatusAsync(v.Modell) : null;
        // Manche Rechner (Webhosting, Firewall) antworten nicht auf Ping, aber auf den Agent.
        bool online = pingOk || daten != null;
        if (v.Zustand == RechnerZustand.Startet && !online) return;   // wacht gerade auf
        v.Zustand = online ? RechnerZustand.Online : RechnerZustand.Offline;
        v.Daten = daten;
    }

    private void NutzerAktualisieren()
    {
        foreach (var r in Ressourcen)
            r.Nutzer = string.Join(", ", Apps.Where(a => a.Laeuft && a.Modell.RessourcenIds.Contains(r.Modell.Id)).Select(a => a.Name));
    }

    // ---------------------------------------------------------------- Rechner steuern

    /// <summary>Schickt das Magic Packet und wartet, bis der Rechner (und sein Agent) antwortet.</summary>
    public async Task<bool> RechnerAufweckenAsync(RechnerVm v, Action<string>? status = null)
    {
        v.Beschaeftigt = true;
        v.Zustand = RechnerZustand.Startet;
        try
        {
            status?.Invoke($"Wecke {v.Name} auf …");
            await WakeOnLan.SendenAsync(v.Modell.MacAdresse, v.Modell.Broadcast);
            var bis = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < bis)
            {
                await Task.Delay(3000);
                status?.Invoke($"Warte auf {v.Name} …");
                var daten = AgentClient.Eingerichtet(v.Modell) ? await AgentClient.StatusAsync(v.Modell) : null;
                bool bereit = AgentClient.Eingerichtet(v.Modell) ? daten != null : await Erreichbar.PingAsync(v.Modell);
                if (bereit)
                {
                    v.Zustand = RechnerZustand.Online;
                    v.Daten = daten;
                    return true;
                }
            }
            v.Zustand = RechnerZustand.Offline;
            Dlg.Hinweis(Fenster, $"{v.Name} antwortet nicht",
                "Das Aufwecken hat nach 5 Minuten nichts bewirkt. Ist Wake-on-LAN im BIOS und am Netzwerkadapter aktiv, und stimmen MAC- und Broadcast-Adresse?");
            return false;
        }
        finally { v.Beschaeftigt = false; }
    }

    public async Task RechnerHerunterfahrenAsync(RechnerVm v)
    {
        var aktiv = Ressourcen.Where(r => r.Modell.RechnerId == v.Modell.Id && r.IstBenutzt).ToList();
        var warnung = aktiv.Count > 0
            ? $"Diese Ressourcen werden gerade von laufenden Apps genutzt: {string.Join(", ", aktiv.Select(r => r.Name))}.\n\n"
            : "";
        if (Dlg.Frage(Fenster, $"{v.Name} herunterfahren?", warnung + "Laufende Dienste auf dem Rechner werden beendet. Zum Wiederstarten kannst du ihn hier per Wake-on-LAN aufwecken.",
                "Herunterfahren", "Abbrechen") != 0) return;
        v.Beschaeftigt = true;
        try
        {
            var (ok, meldung) = await AgentClient.HerunterfahrenAsync(v.Modell);
            if (!ok) Dlg.Hinweis(Fenster, "Herunterfahren fehlgeschlagen", meldung);
        }
        finally { v.Beschaeftigt = false; }
    }

    /// <summary>Stellt sicher, dass der Rechner der Ressource läuft; fragt vor dem Aufwecken. false = abbrechen.</summary>
    private async Task<bool> RechnerBereitAsync(Ressource r, Action<string>? status)
    {
        var v = RechnerVon(r);
        if (v == null || v.Zustand == RechnerZustand.Online || v.Modell.IstLokal) return true;
        if (v.Zustand == RechnerZustand.Startet) return true;
        if (!WakeOnLan.MacGueltig(v.Modell.MacAdresse))
        {
            // Ohne MAC-Adresse bleibt nur die Warnung; Start der Ressource scheitert dann ggf. selbst.
            return true;
        }
        var antwort = Dlg.Frage(Fenster, $"{v.Name} ist aus",
            $"Für „{r.Name}“ muss {v.Name} laufen. Soll Appmaster den Rechner jetzt per Wake-on-LAN aufwecken?",
            "Aufwecken", "Nicht aufwecken", "Abbrechen");
        if (antwort == 0) return await RechnerAufweckenAsync(v, status);
        return antwort == 1;
    }

    // ---------------------------------------------------------------- Ressourcen steuern

    private async Task<(bool Ok, string Meldung)> StartAusfuehrenAsync(RessourceVm r)
    {
        if (r.Modell.Steuerung == Steuerung.Agent)
        {
            var v = RechnerVon(r.Modell);
            return v == null ? (false, "Rechner nicht gefunden.") : await AgentClient.DockerAsync(v.Modell, r.Modell.AgentContainer, "start");
        }
        var (code, ausgabe) = await BefehlsLauf.AusfuehrenAsync(r.Modell.StartBefehl);
        return (code == 0, $"Der Startbefehl endete mit Code {code}.\n\n{ausgabe}");
    }

    private async Task<(bool Ok, string Meldung)> StopAusfuehrenAsync(RessourceVm r)
    {
        if (r.Modell.Steuerung == Steuerung.Agent)
        {
            var v = RechnerVon(r.Modell);
            return v == null ? (false, "Rechner nicht gefunden.") : await AgentClient.DockerAsync(v.Modell, r.Modell.AgentContainer, "stop");
        }
        var (code, ausgabe) = await BefehlsLauf.AusfuehrenAsync(r.Modell.StopBefehl);
        return (code == 0, $"Der Stoppbefehl endete mit Code {code}.\n\n{ausgabe}");
    }

    /// <summary>
    /// Prüft Konfliktgruppen vor dem Start von <paramref name="r"/>. <paramref name="mitgebrauchte"/> sind Ressourcen,
    /// die dieselbe App ohnehin zusammen braucht. false = Start abbrechen.
    /// </summary>
    private async Task<bool> KonflikteLoesenAsync(RessourceVm r, ICollection<string> mitgebrauchte)
    {
        var gruppe = r.Modell.Konfliktgruppe.Trim();
        if (gruppe.Length == 0) return true;
        var andere = Ressourcen.Where(x => x != r && x.Zustand == Zustand.Laeuft && !mitgebrauchte.Contains(x.Modell.Id)
            && string.Equals(x.Modell.Konfliktgruppe.Trim(), gruppe, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var x in andere)
        {
            if (x.IstBenutzt)
            {
                var a = Dlg.Frage(Fenster, "Ressourcen-Konflikt",
                    $"{x.Name} läuft bereits und wird von {x.Nutzer} genutzt. {x.Name} und {r.Name} teilen sich „{gruppe}“ und laufen " +
                    $"vermutlich nicht gleichzeitig (zu wenig Speicher). {x.Name} wird nicht angetastet.",
                    $"{r.Name} trotzdem starten", "Abbrechen");
                if (a != 0) return false;
            }
            else if (x.StopMoeglich && x.KannSteuern)
            {
                var a = Dlg.Frage(Fenster, "Ressourcen-Konflikt",
                    $"{x.Name} läuft, wird aber von keiner App gebraucht. {x.Name} und {r.Name} teilen sich „{gruppe}“ – am besten läuft nur eine.",
                    $"{x.Name} stoppen, dann {r.Name} starten", "Beide laufen lassen", "Abbrechen");
                if (a == 0) { if (!await RessourceStoppenAsync(x, still: true)) return false; }
                else if (a != 1) return false;
            }
            else
            {
                var a = Dlg.Frage(Fenster, "Ressourcen-Konflikt",
                    $"{x.Name} läuft und teilt sich „{gruppe}“ mit {r.Name}. Appmaster kann {x.Name} nicht selbst stoppen – bitte von Hand beenden.",
                    $"{r.Name} trotzdem starten", "Abbrechen");
                if (a != 0) return false;
            }
        }
        return true;
    }

    public async Task<bool> RessourceStartenAsync(RessourceVm r, Action<string>? status = null, ICollection<string>? mitgebrauchte = null)
    {
        if (!r.StartMoeglich) return false;
        if (!await KonflikteLoesenAsync(r, mitgebrauchte ?? Array.Empty<string>())) return false;
        if (!await RechnerBereitAsync(r.Modell, status)) return false;

        r.Beschaeftigt = true;
        r.Zustand = Zustand.Startet;
        try
        {
            status?.Invoke($"Starte {r.Name} …");
            var (ok, meldung) = await StartAusfuehrenAsync(r);
            if (!ok)
            {
                r.Zustand = Zustand.Gestoppt;
                Dlg.Hinweis(Fenster, $"{r.Name} konnte nicht gestartet werden", meldung);
                return false;
            }
            var bis = DateTime.UtcNow.AddSeconds(Math.Max(5, r.Modell.StartWartezeit));
            while (DateTime.UtcNow < bis)
            {
                status?.Invoke($"Warte auf {r.Name} …");
                if (await Gesundheit.PruefenAsync(r.Modell.PruefUrl) == Zustand.Laeuft)
                {
                    r.Zustand = Zustand.Laeuft;
                    return true;
                }
                await Task.Delay(1500);
            }
            r.Zustand = Zustand.Gestoppt;
            return false;
        }
        finally { r.Beschaeftigt = false; }
    }

    /// <summary>Stoppt eine Ressource; warnt, wenn laufende Apps sie noch brauchen (außer <paramref name="still"/>).</summary>
    public async Task<bool> RessourceStoppenAsync(RessourceVm r, bool still = false)
    {
        if (!r.StopMoeglich) return false;
        if (!still && r.IstBenutzt)
        {
            var antwort = Dlg.Frage(Fenster, $"{r.Name} wird gerade genutzt",
                $"{r.NutzerText}. Wenn du {r.Name} jetzt stoppst, können diese Apps nicht mehr arbeiten.",
                "Trotzdem stoppen", "Abbrechen");
            if (antwort != 0) return false;
        }
        r.Beschaeftigt = true;
        try
        {
            var (ok, meldung) = await StopAusfuehrenAsync(r);
            if (!ok)
            {
                Dlg.Hinweis(Fenster, $"{r.Name} konnte nicht gestoppt werden", meldung);
                return false;
            }
            await Task.Delay(800);
            r.Zustand = await Gesundheit.PruefenAsync(r.Modell.PruefUrl);
            return true;
        }
        finally { r.Beschaeftigt = false; }
    }

    // ---------------------------------------------------------------- Apps steuern

    public async Task AppStartenAsync(AppVm app)
    {
        if (app.Laeuft || app.Beschaeftigt) return;
        app.Beschaeftigt = true;
        try
        {
            var benoetigt = app.Modell.RessourcenIds.Select(id => Ressourcen.FirstOrDefault(x => x.Modell.Id == id)).Where(x => x != null).Select(x => x!).ToList();

            // Braucht die App selbst zwei Ressourcen aus einer Konfliktgruppe? Einmal klar warnen.
            foreach (var g in benoetigt.Where(x => x.Modell.Konfliktgruppe.Trim().Length > 0).GroupBy(x => x.Modell.Konfliktgruppe.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var a = Dlg.Frage(Fenster, "Ressourcen-Konflikt in der App",
                    $"„{app.Name}“ braucht {string.Join(" und ", g.Select(x => x.Name))}, die sich „{g.Key}“ teilen und vermutlich nicht gleichzeitig laufen.",
                    "Trotzdem starten", "Abbrechen");
                if (a != 0) return;
            }
            var gemeinsam = app.Modell.RessourcenIds;

            foreach (var r in benoetigt)
            {
                r.Zustand = r.Zustand == Zustand.Startet ? Zustand.Startet : await Gesundheit.PruefenAsync(r.Modell.PruefUrl);
                if (r.Zustand == Zustand.Laeuft) continue;

                bool ok = false;
                if (r.StartMoeglich) ok = await RessourceStartenAsync(r, s => app.Status = s, gemeinsam);
                if (ok) continue;

                var antwort = Dlg.Frage(Fenster, $"{r.Name} ist nicht erreichbar",
                    r.StartMoeglich
                        ? $"„{app.Name}“ braucht {r.Name}, aber der Start hat nicht geklappt."
                        : $"„{app.Name}“ braucht {r.Name}. Appmaster darf diese Ressource nicht selbst starten – bitte starte sie von Hand.",
                    "Trotzdem starten", "Abbrechen");
                if (antwort != 0) { app.Status = ""; return; }
            }

            app.Status = "Starte …";
            if (app.Modell.Typ == AppTyp.Lokal) LokalStarten(app);
            else await WebStartenAsync(app);

            app.Laeuft = true;
            app.Status = "";
            NutzerAktualisieren();
        }
        catch (Exception ex)
        {
            app.Status = "";
            Dlg.Hinweis(Fenster, $"{app.Name} konnte nicht gestartet werden", ex.Message);
        }
        finally { app.Beschaeftigt = false; app.Status = ""; }
    }

    private void LokalStarten(AppVm app)
    {
        var a = app.Modell;
        if (string.IsNullOrWhiteSpace(a.StartBefehl)) throw new InvalidOperationException("Kein Startbefehl eingetragen.");
        var p = ProzessAnlegen(a);
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => Application.Current.Dispatcher.InvokeAsync(() => _ = AppEndeAsync(app));
        p.Start();
        app.Prozess = p;
    }

    private async Task WebStartenAsync(AppVm app)
    {
        var a = app.Modell;
        if (!string.IsNullOrWhiteSpace(a.StartBefehl))
        {
            var p = ProzessAnlegen(a);
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => Application.Current.Dispatcher.InvokeAsync(() => _ = AppEndeAsync(app));
            p.Start();
            app.Prozess = p;
            // Lokalen Server abwarten, bevor der Browser aufgeht.
            if (!string.IsNullOrWhiteSpace(a.Url))
                for (int i = 0; i < 40 && await Gesundheit.PruefenAsync(a.Url) != Zustand.Laeuft; i++)
                {
                    app.Status = "Warte auf Server …";
                    await Task.Delay(750);
                }
        }
        if (!string.IsNullOrWhiteSpace(a.Url))
            Process.Start(new ProcessStartInfo(a.Url) { UseShellExecute = true });
    }

    private static Process ProzessAnlegen(AppEintrag a)
    {
        var endung = Path.GetExtension(a.StartBefehl).ToLowerInvariant();
        var psi = new ProcessStartInfo(a.StartBefehl, a.Argumente)
        {
            // .exe/.com/Befehle aus dem PATH direkt, alles andere (bat, lnk, Dokumente) über die Shell.
            UseShellExecute = endung is not ("" or ".exe" or ".com"),
            WorkingDirectory = Directory.Exists(a.Arbeitsordner) ? a.Arbeitsordner : "",
        };
        if (!psi.UseShellExecute && !string.IsNullOrWhiteSpace(a.Datenpfad))
            psi.Environment["APPMASTER_DATA"] = a.Datenpfad;
        return new Process { StartInfo = psi };
    }

    public async Task AppBeendenAsync(AppVm app)
    {
        if (!app.Laeuft) return;
        try
        {
            if (app.Prozess is { HasExited: false } p)
            {
                p.CloseMainWindow();
                if (!p.WaitForExit(3000)) p.Kill(entireProcessTree: true);
            }
        }
        catch { /* schon beendet */ }
        await AppEndeAsync(app);
    }

    /// <summary>Beendet alle von Appmaster gestarteten Apps (Ressourcen mit „automatisch stoppen“ fahren dabei herunter).</summary>
    public async Task AlleAppsBeendenAsync()
    {
        foreach (var a in Apps.Where(x => x.Laeuft).ToList()) await AppBeendenAsync(a);
    }

    public bool LaufendeApps => Apps.Any(a => a.Laeuft);

    private async Task AppEndeAsync(AppVm app)
    {
        if (!app.Laeuft) return;
        app.Laeuft = false;
        app.Prozess = null;
        NutzerAktualisieren();

        // Ressourcen, die niemand mehr braucht und die sich selbst abschalten sollen
        foreach (var id in app.Modell.RessourcenIds)
        {
            var r = Ressourcen.FirstOrDefault(x => x.Modell.Id == id);
            if (r is { Modell: { AutomatischStoppen: true }, IstBenutzt: false, Zustand: Zustand.Laeuft } && r.StopMoeglich)
                await RessourceStoppenAsync(r, still: true);
        }
    }
}
