using System.Diagnostics;
using System.IO;
using System.Windows;
using Appmaster.Models;
using Appmaster.Services;

namespace Appmaster.Views;

/// <summary>Dialoge zu Export und Import der verschlüsselten Konfigurationssicherung.</summary>
public static class SicherungsDialoge
{
    public const string Filter = "Appmaster-Sicherung (*.enc)|*.enc|Alle Dateien|*.*";

    /// <summary>Kurzer Statustext für den Einstellungsdialog.</summary>
    public static string Status()
    {
        string s;
        if (Sicherung.Aktiv && Sicherung.DateiVorhanden)
            s = $"Automatische Sicherung aktiv. Letzter Stand: {File.GetLastWriteTime(Sicherung.Datei):dd.MM.yyyy HH:mm}\n{Sicherung.Datei}";
        else if (Sicherung.DateiVorhanden)
            s = $"Eine Sicherungsdatei liegt vor ({Sicherung.Datei}), aber auf diesem PC ist kein Passwort hinterlegt – sie wird hier nicht automatisch aktualisiert. " +
                "Mit „Sicherung exportieren“ (gleiches oder neues Passwort) wird die Automatik aktiviert.";
        else
            s = "Noch keine Sicherung eingerichtet. Mit „Sicherung exportieren“ wird die gesamte Konfiguration verschlüsselt im Konfigurationsordner abgelegt und danach automatisch aktuell gehalten.";
        if (Sicherung.LetzterFehler.Length > 0) s += "\nLetzter Fehler beim automatischen Sichern: " + Sicherung.LetzterFehler;
        return s;
    }

    public static void Exportieren(Window? besitzer, Konfiguration k)
    {
        var f = new Formular(besitzer, "Sicherung exportieren");
        f.Info("Ziel", Sicherung.Datei);
        f.Info("Inhalt", "Rechner, Ressourcen, Apps, Einstellungen, Agent-Token, Pfade (SSH-Schlüssel, Docker Desktop) und Design – alles verschlüsselt (AES-256). " +
                         "Die privaten SSH-Schlüsseldateien selbst sind nicht enthalten, nur ihre Pfade.");
        var pw = f.Passwort("Passwort", "Mindestens 8 Zeichen. Ohne das Passwort lässt sich die Sicherung nicht wiederherstellen, es gibt keine Möglichkeit, es zurückzusetzen.");
        var pw2 = f.Passwort("Passwort wiederholen");
        if (!f.Zeigen(() => pw.Password.Length < 8 ? "Das Passwort braucht mindestens 8 Zeichen."
                          : pw.Password != pw2.Password ? "Die beiden Passwörter stimmen nicht überein." : null, "Exportieren")) return;
        try
        {
            var datei = Sicherung.Exportieren(k, pw.Password);
            Dlg.Hinweis(besitzer, "Sicherung gespeichert",
                $"{datei}\n\nAb jetzt hält Appmaster die Datei bei jeder Änderung automatisch aktuell. Das Passwort ist dafür geschützt (Windows-Benutzerkonto) auf diesem PC hinterlegt.");
        }
        catch (Exception ex) { Dlg.Hinweis(besitzer, "Sicherung fehlgeschlagen", ex.Message); }
    }

    public static void Importieren(Window? besitzer)
    {
        var f = new Formular(besitzer, "Konfiguration importieren");
        var datei = f.Pfad("Sicherungsdatei", Sicherung.DateiVorhanden ? Sicherung.Datei : "", ordner: false, filter: Filter,
            hinweis: "Die Datei appmaster-sicherung.enc aus dem Konfigurationsordner (z. B. Dropbox) oder eine Kopie davon.");
        var pw = f.Passwort("Passwort der Sicherung");
        SicherungsInhalt? inhalt = null;
        if (!f.Zeigen(() =>
        {
            if (string.IsNullOrWhiteSpace(datei.Text)) return "Bitte die Sicherungsdatei wählen.";
            if (!File.Exists(datei.Text.Trim())) return "Die Datei existiert nicht.";
            try { inhalt = Sicherung.Lesen(datei.Text.Trim(), pw.Password); return null; }
            catch (Exception ex) { return ex.Message; }
        }, "Importieren") || inhalt == null) return;

        var k = inhalt.Konfiguration;
        var wann = inhalt.Erstellt is { } t ? $" vom {t:dd.MM.yyyy HH:mm}" : "";
        var antwort = Dlg.Frage(besitzer, "Konfiguration wiederherstellen?",
            $"Die Sicherung{wann} enthält {k.Rechner.Count} Rechner, {k.Ressourcen.Count} Ressourcen und {k.Apps.Count} Apps.\n\n" +
            "Die aktuelle Konfiguration wird ersetzt (eine Kopie bleibt als config.json.bak-… im Konfigurationsordner), danach startet Appmaster neu. " +
            "Rechner, Ressourcen und Apps, die nicht erreichbar sind, werden trotzdem angelegt. Prüfe sie danach und passe sie an oder lösche sie, wenn es sie nicht mehr gibt.",
            "Wiederherstellen", "Abbrechen");
        if (antwort != 0) return;
        try { Sicherung.Wiederherstellen(inhalt, pw.Password); }
        catch (Exception ex) { Dlg.Hinweis(besitzer, "Wiederherstellen fehlgeschlagen", ex.Message); return; }
        Neustart();
    }

    /// <summary>Startet Appmaster neu, ohne den wiederhergestellten Stand durch ein Speichern beim Schließen zu überschreiben.</summary>
    private static void Neustart()
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true }); }
        catch { /* Benutzer startet selbst */ }
        SshTunnel.AlleBeenden();
        Sicherung.Abschliessen();
        Environment.Exit(0);
    }
}
