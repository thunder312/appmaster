using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Appmaster.Models;

namespace Appmaster.Services;

/// <summary>
/// Wo die Konfiguration liegt: lokal (%APPDATA%) oder in einem frei gewählten Ordner, z. B. der Dropbox.
/// Der gewählte Ordner steht in %APPDATA%\Eulenwerk\Appmaster\ort.txt (pro PC).
/// </summary>
public static class Ort
{
    /// <summary>%APPDATA%\Eulenwerk\Appmaster (für Selbsttests per Umgebungsvariable APPMASTER_LOKAL umlenkbar).</summary>
    public static string LokalerOrdner => Environment.GetEnvironmentVariable("APPMASTER_LOKAL") is { Length: > 0 } umgelenkt
        ? umgelenkt
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Eulenwerk", "Appmaster");

    private static string Zeiger => Path.Combine(LokalerOrdner, "ort.txt");

    /// <summary>true, solange der Benutzer den Speicherort noch nie gewählt hat.</summary>
    public static bool Erststart => !File.Exists(Zeiger);

    public static string Ordner
    {
        get
        {
            try
            {
                var gewaehlt = File.Exists(Zeiger) ? File.ReadAllText(Zeiger).Trim() : "";
                if (gewaehlt.Length > 0) return gewaehlt;
            }
            catch { /* Standard */ }
            return LokalerOrdner;
        }
    }

    public static void Setzen(string ordner)
    {
        Directory.CreateDirectory(LokalerOrdner);
        File.WriteAllText(Zeiger, ordner == LokalerOrdner ? "" : ordner);
    }

    /// <summary>Dropbox-Ordner dieses PCs (aus %LOCALAPPDATA%\Dropbox\info.json), oder null.</summary>
    public static string? DropboxOrdner()
    {
        try
        {
            var info = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "info.json");
            if (!File.Exists(info)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(info));
            foreach (var konto in new[] { "personal", "business" })
                if (doc.RootElement.TryGetProperty(konto, out var k) && k.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 } pfad)
                    return pfad;
        }
        catch { /* keine Dropbox */ }
        return null;
    }
}

/// <summary>Lädt/speichert die Konfiguration als config.json im gewählten Ordner. Agent-Tokens bleiben lokal.</summary>
public static class KonfigStore
{
    internal static readonly JsonSerializerOptions JsonOptionen = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Datei => Path.Combine(Ort.Ordner, "config.json");

    /// <summary>Zugangsschlüssel der Agenten: nur auf diesem PC, damit sie nicht in die Dropbox wandern.</summary>
    private static string TokenDatei => Path.Combine(Ort.LokalerOrdner, "tokens.json");

    private static string DockerPfadDatei => Path.Combine(Ort.LokalerOrdner, "dockerpfade.json");

    private static string SchluesselDatei => Path.Combine(Ort.LokalerOrdner, "tunnelkeys.json");

    public static Konfiguration Laden()
    {
        var k = new Konfiguration();
        try
        {
            if (File.Exists(Datei))
                k = JsonSerializer.Deserialize<Konfiguration>(File.ReadAllText(Datei), JsonOptionen) ?? k;
        }
        catch { /* defekte Datei: leer starten, die alte bleibt bis zum nächsten Speichern liegen */ }

        try
        {
            if (File.Exists(TokenDatei))
            {
                var tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TokenDatei)) ?? new();
                foreach (var r in k.Rechner)
                    if (tokens.TryGetValue(r.Id, out var t)) r.AgentToken = t;
            }
        }
        catch { /* Tokens neu eintragen */ }

        try
        {
            if (File.Exists(SchluesselDatei))
            {
                var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SchluesselDatei)) ?? new();
                foreach (var r in k.Rechner)
                    if (keys.TryGetValue(r.Id, out var s)) r.TunnelSchluessel = s;
            }
        }
        catch { /* Schlüsselpfad neu eintragen */ }

        try
        {
            if (File.Exists(DockerPfadDatei))
            {
                var pfade = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(DockerPfadDatei)) ?? new();
                foreach (var r in k.Rechner)
                    if (pfade.TryGetValue(r.Id, out var d)) r.DockerDesktopPfad = d;
            }
        }
        catch { /* Pfad wird automatisch gesucht */ }
        return k;
    }

    public static void Speichern(Konfiguration k)
    {
        Directory.CreateDirectory(Ort.Ordner);
        var tmp = Datei + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(k, JsonOptionen));
        File.Move(tmp, Datei, overwrite: true);

        Directory.CreateDirectory(Ort.LokalerOrdner);
        var tokens = k.Rechner.Where(r => r.AgentToken.Length > 0).ToDictionary(r => r.Id, r => r.AgentToken);
        File.WriteAllText(TokenDatei, JsonSerializer.Serialize(tokens, JsonOptionen));
        var keys = k.Rechner.Where(r => r.TunnelSchluessel.Length > 0).ToDictionary(r => r.Id, r => r.TunnelSchluessel);
        File.WriteAllText(SchluesselDatei, JsonSerializer.Serialize(keys, JsonOptionen));
        var pfade = k.Rechner.Where(r => r.DockerDesktopPfad.Length > 0).ToDictionary(r => r.Id, r => r.DockerDesktopPfad);
        File.WriteAllText(DockerPfadDatei, JsonSerializer.Serialize(pfade, JsonOptionen));

        // Verschlüsselte Gesamtsicherung nachziehen (nur wenn der Benutzer sie eingerichtet hat)
        try { Sicherung.Aktualisieren(k); } catch { /* Sicherung darf das Speichern nie verhindern */ }
    }
}
