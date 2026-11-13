using System.IO;
using Appmaster.Models;
using Appmaster.ViewModels;

namespace Appmaster.Services;

/// <summary>Automatisches Finden und Anlegen von Eulenwerk-Apps samt ihrer Ressourcen (siehe <see cref="EulenwerkSuche"/>).</summary>
public sealed partial class Steuerzentrale
{
    private bool _sucht;
    private bool _ersteSucheGestartet;
    private int _sucheTakt;

    /// <summary>Wird nach jeder Suche gemeldet: Anzahl der neu angelegten Apps (0 = nichts Neues).</summary>
    public event Action<int>? SucheBeendet;

    public static string SymbolOrdner => Path.Combine(Ort.LokalerOrdner, "icons");
    public static string SymbolDatei(string appId) => Path.Combine(SymbolOrdner, appId + ".png");

    /// <summary>Alle 15 Minuten, aber mindestens im Prüftakt – die Suche ist leicht, soll aber nicht ständig laufen.</summary>
    private int SucheAlleTakte => Math.Max(1, 900 / Math.Max(2, Konfig.Einstellungen.PruefIntervallSekunden));

    private void SucheBeiTaktAnstossen()
    {
        if (_ersteSucheGestartet && ++_sucheTakt < SucheAlleTakte) return;
        _ersteSucheGestartet = true;
        _sucheTakt = 0;
        _ = EulenwerkSuchenAsync();
    }

    /// <summary>Nach dem Anlegen oder Ändern eines Rechners: Zustand holen (Container-Ports vom Agent), dann suchen.</summary>
    public async Task NachRechnerAenderungAsync()
    {
        await ZustandAktualisierenAsync();
        await EulenwerkSuchenAsync();
    }

    public async Task<int> EulenwerkSuchenAsync()
    {
        if (_sucht) return 0;
        _sucht = true;
        try
        {
            var ziele = Rechner
                .Where(v => v.Modell.Kategorien.HasFlag(RechnerKategorie.Arbeitsrechner) || v.Modell.Kategorien.HasFlag(RechnerKategorie.Webserver))
                .Select(v => new SuchRechner(v.Modell.Id, v.Name, v.Modell.Adresse.Trim(), v.Modell.IstLokal, v.Modell.Kategorien,
                    v.Daten?.Container?.Where(c => c.Zustand == "running").SelectMany(c => c.Ports ?? new List<int>()).ToList() ?? new List<int>()))
                .ToList();
            if (ziele.Count == 0) return 0;

            var funde = await EulenwerkSuche.SuchenAsync(ziele);   // läuft im Hintergrund, danach wieder auf dem UI-Thread
            var neu = FundeUebernehmen(funde);
            SucheBeendet?.Invoke(neu);
            return neu;
        }
        catch { return 0; }
        finally { _sucht = false; }
    }

    private static bool Gleich(string? a, string? b) => string.Equals(a?.Trim().TrimEnd('/'), b?.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private int FundeUebernehmen(List<EulenwerkFund> funde)
    {
        int neu = 0;
        bool geaendert = false;
        foreach (var f in funde)
        {
            if (Konfig.AbgelehnteAutoApps.Contains(f.Schluessel, StringComparer.OrdinalIgnoreCase)) continue;

            var vm = Apps.FirstOrDefault(a => Gleich(a.Modell.AutoSchluessel, f.Schluessel))
                     ?? Apps.FirstOrDefault(a => f.Typ == AppTyp.Web ? Gleich(a.Modell.Url, f.Url) : f.Typ == AppTyp.Lokal && Gleich(a.Modell.StartBefehl, f.Start));
            bool ersterKontakt = false;

            if (vm == null)
            {
                vm = new AppVm(new AppEintrag
                {
                    Name = f.Name, Beschreibung = f.Beschreibung, Kategorie = f.Kategorie, Typ = f.Typ,
                    StartBefehl = f.Start, Arbeitsordner = f.Arbeitsordner, Url = f.Url,
                    IstEulenwerk = true, AutoSchluessel = f.Schluessel, Version = f.Version, RechnerId = f.RechnerId,
                });
                Apps.Add(vm);
                ersterKontakt = true;
                neu++;
            }
            else if (!vm.Modell.IstEulenwerk || vm.Modell.AutoSchluessel.Length == 0)
            {
                // Von Hand angelegte App, die sich als Eulenwerk-App entpuppt: hervorheben und richtige Kategorie setzen.
                var m = vm.Modell;
                m.IstEulenwerk = true;
                m.AutoSchluessel = f.Schluessel;
                m.Kategorie = f.Kategorie;
                if (m.Version.Length == 0) m.Version = f.Version;
                if (m.RechnerId.Length == 0) m.RechnerId = f.RechnerId;
                if (m.Beschreibung.Length == 0) m.Beschreibung = f.Beschreibung;
                ersterKontakt = true;
            }
            else if (f.Version.Length > 0 && vm.Modell.Version != f.Version)
            {
                vm.Modell.Version = f.Version;
                geaendert = true;
            }

            if (ersterKontakt)
            {
                RessourcenVerknuepfen(vm.Modell, f);
                geaendert = true;
            }

            SymbolSpeichern(vm, f.Symbol);
        }

        if (neu > 0 || geaendert)
        {
            AlleNeuBeschriften();
            foreach (var a in Apps) a.ModellGeaendert();
            Speichern();
        }
        return neu;
    }

    private static void SymbolSpeichern(AppVm vm, byte[]? png)
    {
        if (png == null) return;
        try
        {
            var datei = SymbolDatei(vm.Modell.Id);
            if (File.Exists(datei) && new FileInfo(datei).Length == png.Length && File.ReadAllBytes(datei).AsSpan().SequenceEqual(png)) return;
            Directory.CreateDirectory(SymbolOrdner);
            File.WriteAllBytes(datei, png);
            vm.SymbolNeuLaden();
        }
        catch { /* Symbol ist nur Zierde */ }
    }

    /// <summary>Legt die in der App beschriebenen Ressourcen an (bzw. findet vorhandene) und trägt sie bei der App ein.</summary>
    private void RessourcenVerknuepfen(AppEintrag app, EulenwerkFund f)
    {
        if (f.Manifest?.Ressourcen is not { Count: > 0 } liste) return;
        var fundRechner = Rechner.FirstOrDefault(v => v.Modell.Id == f.RechnerId) ?? Rechner.FirstOrDefault();
        foreach (var mr in liste)
        {
            if (string.IsNullOrWhiteSpace(mr.Name)) continue;
            // KI-Dienste bevorzugt auf einem GPU-Rechner, alles andere dort, wo die App liegt.
            var ziel = (mr.Ki ? Rechner.FirstOrDefault(v => v.Modell.IstGpu) : null) ?? fundRechner;
            if (ziel == null) continue;
            var host = ziel.Modell.IstLokal ? "localhost" : ziel.Modell.Adresse.Trim();
            var pruef = mr.PruefUrl.Replace("{host}", host, StringComparison.OrdinalIgnoreCase).Trim();

            var res = Ressourcen.FirstOrDefault(r => (pruef.Length > 0 && Gleich(r.Modell.PruefUrl, pruef)) || string.Equals(r.Name, mr.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (res == null)
            {
                var neu = new Ressource
                {
                    Name = mr.Name.Trim(), Beschreibung = mr.Beschreibung.Trim(), RechnerId = ziel.Modell.Id, PruefUrl = pruef,
                    IstKi = mr.Ki, Konfliktgruppe = mr.Konfliktgruppe.Trim(), AutomatischStoppen = mr.AutomatischStoppen, Automatisch = true,
                };
                if (mr.Container.Trim().Length > 0 && ziel.Modell.AgentArt == AgentArt.LinuxAgent)
                {
                    neu.Steuerung = Steuerung.Agent;
                    neu.AgentContainer = mr.Container.Trim();
                }
                else if (mr.StartBefehl.Trim().Length > 0)
                {
                    neu.Steuerung = Steuerung.Befehl;
                    neu.StartBefehl = mr.StartBefehl.Trim();
                    neu.StopBefehl = mr.StopBefehl.Trim();
                }
                res = new RessourceVm(neu);
                Ressourcen.Add(res);
                _ = ZustandAktualisierenAsync();
            }
            if (!app.RessourcenIds.Contains(res.Modell.Id)) app.RessourcenIds.Add(res.Modell.Id);
        }
    }

    /// <summary>Entfernte automatisch gefundene Apps nicht wieder anlegen.</summary>
    private void AutoAppVergessen(AppVm a)
    {
        var schluessel = a.Modell.AutoSchluessel;
        if (schluessel.Length > 0 && !Konfig.AbgelehnteAutoApps.Contains(schluessel, StringComparer.OrdinalIgnoreCase))
            Konfig.AbgelehnteAutoApps.Add(schluessel);
        try { File.Delete(SymbolDatei(a.Modell.Id)); } catch { }
    }

    public int AbgelehnteAnzahl => Konfig.AbgelehnteAutoApps.Count;

    public void AbgelehnteZuruecksetzen()
    {
        Konfig.AbgelehnteAutoApps.Clear();
        Speichern();
    }
}
