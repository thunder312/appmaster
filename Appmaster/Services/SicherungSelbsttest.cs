using System.IO;
using System.Text;
using Appmaster.Models;

namespace Appmaster.Services;

/// <summary>
/// Entwicklerhilfe: "Appmaster.exe --sicherung-test &lt;ziel.txt&gt;" spielt Export, Automatik und Wiederherstellung in einem
/// temporären Ordner durch (über APPMASTER_LOKAL, die echte Konfiguration bleibt unberührt) und schreibt das Ergebnis in die Datei.
/// </summary>
public static class SicherungSelbsttest
{
    public static string Ausfuehren()
    {
        var log = new StringBuilder();
        bool alleOk = true;
        void Pruefe(string name, bool ok, string zusatz = "") { alleOk &= ok; log.AppendLine($"{(ok ? "OK    " : "FEHLER")} {name} {zusatz}"); }

        var alt = Environment.GetEnvironmentVariable("APPMASTER_LOKAL");
        var wurzel = Path.Combine(Path.GetTempPath(), "appmaster-sicherungstest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // ---- PC 1: Konfiguration anlegen
            var pc1 = Path.Combine(wurzel, "pc1"); Directory.CreateDirectory(pc1);
            Environment.SetEnvironmentVariable("APPMASTER_LOKAL", pc1);
            Ort.Setzen(Path.Combine(wurzel, "konfig"));   // eigener Konfigurationsordner wie die Dropbox

            var k = new Konfiguration();
            var r = new Rechner { Name = "Test-vServer", Adresse = "10.255.255.1", AgentArt = AgentArt.LinuxAgent, AgentToken = "GEHEIMTOKEN-123456789",
                                  TunnelAktiv = true, TunnelZiel = "root@10.255.255.1", TunnelSchluessel = @"C:\gibt-es-nicht\schluessel", DockerDesktopStarten = true,
                                  DockerDesktopPfad = @"X:\nirgends\Docker Desktop.exe" };
            k.Rechner.Add(r);
            k.Ressourcen.Add(new Ressource { Name = "Gibt-es-nicht", RechnerId = r.Id, PruefUrl = "http://10.255.255.1:1", Steuerung = Steuerung.Befehl, StartBefehl = "docker start x" });
            k.Apps.Add(new AppEintrag { Name = "Fehlende App", StartBefehl = @"C:\gibt-es-nicht\app.exe" });
            KonfigStore.Speichern(k);

            // ---- Export
            var datei = Sicherung.Exportieren(k, "TestPasswort-1");
            var roh = File.ReadAllText(datei);
            Pruefe("Datei im Konfigurationsordner angelegt", datei.StartsWith(Path.Combine(wurzel, "konfig")), datei);
            Pruefe("Datei enthält keinen Klartext (Token/Name/Pfad)", !roh.Contains("GEHEIMTOKEN") && !roh.Contains("Test-vServer") && !roh.Contains("gibt-es-nicht"));
            Pruefe("Automatik nach Export aktiv", Sicherung.Aktiv);

            // ---- Lesen: richtig, falsch, manipuliert
            var inhalt = Sicherung.Lesen(datei, "TestPasswort-1");
            Pruefe("Lesen mit richtigem Passwort", inhalt.Konfiguration.Rechner.Count == 1 && inhalt.Tokens[r.Id] == "GEHEIMTOKEN-123456789");
            try { Sicherung.Lesen(datei, "falsch"); Pruefe("Falsches Passwort wird abgelehnt", false); }
            catch (InvalidDataException ex) { Pruefe("Falsches Passwort wird abgelehnt", true, "→ " + ex.Message); }
            var kaputt = Path.Combine(wurzel, "kaputt.enc");
            var text = roh; var pos = text.IndexOf("\"daten\": \"", StringComparison.Ordinal) + 12;
            File.WriteAllText(kaputt, text[..pos] + (text[pos] == 'A' ? "B" : "A") + text[(pos + 1)..]);
            try { Sicherung.Lesen(kaputt, "TestPasswort-1"); Pruefe("Manipulierte Datei wird erkannt", false); }
            catch (InvalidDataException ex) { Pruefe("Manipulierte Datei wird erkannt", true, "→ " + ex.Message); }
            File.WriteAllText(Path.Combine(wurzel, "unsinn.enc"), "kein json");
            try { Sicherung.Lesen(Path.Combine(wurzel, "unsinn.enc"), "x"); Pruefe("Fremde Datei wird abgelehnt", false); }
            catch (InvalidDataException ex) { Pruefe("Fremde Datei wird abgelehnt", true, "→ " + ex.Message); }

            // ---- Automatik: Änderung speichern → Sicherung enthält sie
            k.Ressourcen.Add(new Ressource { Name = "Nachträglich hinzugefügt", RechnerId = r.Id });
            KonfigStore.Speichern(k);
            Sicherung.Abschliessen();
            var aktuell = Sicherung.Lesen(datei, "TestPasswort-1");
            Pruefe("Automatik: neue Ressource steht in der Sicherung", aktuell.Konfiguration.Ressourcen.Count == 2, $"(Ressourcen: {aktuell.Konfiguration.Ressourcen.Count})");
            var zeit1 = File.GetLastWriteTimeUtc(datei);
            KonfigStore.Speichern(k); Sicherung.Abschliessen();
            Pruefe("Automatik: unveränderter Stand schreibt nicht neu", File.GetLastWriteTimeUtc(datei) == zeit1);
            Pruefe("Automatik: kein Fehler gemeldet", Sicherung.LetzterFehler.Length == 0, Sicherung.LetzterFehler);

            // ---- PC 2: frische Installation, nur die Sicherungsdatei ist da (wie Dropbox)
            var pc2 = Path.Combine(wurzel, "pc2"); Directory.CreateDirectory(pc2);
            Environment.SetEnvironmentVariable("APPMASTER_LOKAL", pc2);
            Pruefe("PC 2: erkennt sich als Erststart", Ort.Erststart);
            Pruefe("PC 2: keine Automatik ohne Passwort", !Sicherung.Aktiv);
            var kopie = Path.Combine(wurzel, "mitgebracht.enc"); File.Copy(datei, kopie);
            var zuLesen = Sicherung.Lesen(kopie, "TestPasswort-1");
            Ort.Setzen(Path.Combine(wurzel, "konfig2"));
            Sicherung.Wiederherstellen(zuLesen, "TestPasswort-1");
            var neu = KonfigStore.Laden();
            Pruefe("Wiederhergestellt: Rechner, Ressourcen, Apps angelegt (auch nicht erreichbare)",
                neu.Rechner.Count == 1 && neu.Ressourcen.Count == 2 && neu.Apps.Count == 1, $"({neu.Rechner.Count}/{neu.Ressourcen.Count}/{neu.Apps.Count})");
            Pruefe("Wiederhergestellt: Token lokal zurück", neu.Rechner[0].AgentToken == "GEHEIMTOKEN-123456789");
            Pruefe("Wiederhergestellt: Schlüssel- und Docker-Pfad zurück (auch wenn Datei fehlt)",
                neu.Rechner[0].TunnelSchluessel == @"C:\gibt-es-nicht\schluessel" && neu.Rechner[0].DockerDesktopPfad == @"X:\nirgends\Docker Desktop.exe");
            Pruefe("Wiederhergestellt: Tunnel/Docker-Optionen zurück", neu.Rechner[0].TunnelAktiv && neu.Rechner[0].DockerDesktopStarten && neu.Rechner[0].TunnelZiel == "root@10.255.255.1");
            Pruefe("Wiederhergestellt: Automatik auf PC 2 aktiv", Sicherung.Aktiv);
            Pruefe("PC 2: Sicherungsdatei im neuen Konfigurationsordner", File.Exists(Path.Combine(wurzel, "konfig2", Sicherung.DateiName)));

            // ---- Bestehende config.json wird gesichert
            neu.Apps.Clear(); KonfigStore.Speichern(neu); Sicherung.Abschliessen();
            Sicherung.Wiederherstellen(Sicherung.Lesen(kopie, "TestPasswort-1"), "TestPasswort-1");
            var baks = Directory.GetFiles(Path.Combine(wurzel, "konfig2"), "config.json.bak-*");
            Pruefe("Erneutes Wiederherstellen hebt alte config.json als .bak auf", baks.Length >= 1, $"({baks.Length} Kopie(n))");
        }
        catch (Exception ex) { Pruefe("Unerwartete Ausnahme", false, ex.ToString()); }
        finally
        {
            Environment.SetEnvironmentVariable("APPMASTER_LOKAL", alt);
            try { Directory.Delete(wurzel, true); } catch { /* egal */ }
        }
        log.AppendLine(alleOk ? "\nALLE TESTS BESTANDEN" : "\nTESTS FEHLGESCHLAGEN");
        return log.ToString();
    }
}
