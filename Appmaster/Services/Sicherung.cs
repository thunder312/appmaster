using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Appmaster.Models;

namespace Appmaster.Services;

/// <summary>Alles, was Appmaster speichert – Inhalt der verschlüsselten Sicherungsdatei.</summary>
public sealed class SicherungsInhalt
{
    public int Format { get; set; } = 1;
    public string App { get; set; } = "Appmaster";
    public string Version { get; set; } = "";
    public DateTime? Erstellt { get; set; }
    /// <summary>Rechner, Ressourcen, Apps und Einstellungen (wie config.json).</summary>
    public Konfiguration Konfiguration { get; set; } = new();
    /// <summary>Was sonst nur lokal auf dem PC liegt, je Rechner-Id: Agent-Token, SSH-Schlüsselpfad, Pfad der Docker Desktop.exe.</summary>
    public Dictionary<string, string> Tokens { get; set; } = new();
    public Dictionary<string, string> TunnelSchluessel { get; set; } = new();
    public Dictionary<string, string> DockerPfade { get; set; } = new();
    /// <summary>Gewähltes Farbdesign (design.txt), falls vorhanden.</summary>
    public string? Design { get; set; }
}

/// <summary>
/// Verschlüsselte Sicherung der gesamten Konfiguration (appmaster-sicherung.enc im Konfigurationsordner).
/// AES-256-GCM, Schlüssel aus dem Passwort per PBKDF2-SHA256 (600 000 Runden, zufälliges Salz).
/// Nach dem ersten Export aktualisiert Appmaster die Datei bei jeder Änderung selbst; das Passwort liegt dafür
/// mit Windows-DPAPI (nur dieser Benutzer auf diesem PC) in %APPDATA%\Eulenwerk\Appmaster\sicherung.pw.
/// Private SSH-Schlüssel selbst sind NICHT enthalten, nur ihre Pfade.
/// </summary>
public static class Sicherung
{
    public const string DateiName = "appmaster-sicherung.enc";
    private const string Kennung = "appmaster-sicherung";
    private const int Iterationen = 600_000;
    private static readonly byte[] Zusatz = Encoding.UTF8.GetBytes(Kennung + "/1");
    private static readonly byte[] Entropie = Encoding.UTF8.GetBytes("Appmaster/sicherung.pw");

    private static readonly object Sperre = new();
    private static Task _laufend = Task.CompletedTask;
    private static (string Passwort, string Salz, byte[] Schluessel)? _schluesselCache;
    private static string _letzterHash = "";

    public static string Datei => Path.Combine(Ort.Ordner, DateiName);
    private static string PasswortDatei => Path.Combine(Ort.LokalerOrdner, "sicherung.pw");

    /// <summary>Letzter Fehler der automatischen Aktualisierung (leer = in Ordnung).</summary>
    public static string LetzterFehler { get; private set; } = "";

    public static bool DateiVorhanden => File.Exists(Datei);

    /// <summary>true, wenn auf diesem PC ein Passwort hinterlegt ist, also automatisch aktualisiert wird.</summary>
    public static bool Aktiv => GespeichertesPasswort() != null;

    // ------------------------------------------------------------------ Passwort lokal ablegen (DPAPI)

    private static string? GespeichertesPasswort()
    {
        try
        {
            if (!File.Exists(PasswortDatei)) return null;
            var klar = ProtectedData.Unprotect(File.ReadAllBytes(PasswortDatei), Entropie, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(klar);
        }
        catch { return null; }
    }

    private static void PasswortMerken(string passwort)
    {
        Directory.CreateDirectory(Ort.LokalerOrdner);
        File.WriteAllBytes(PasswortDatei, ProtectedData.Protect(Encoding.UTF8.GetBytes(passwort), Entropie, DataProtectionScope.CurrentUser));
    }

    /// <summary>Automatische Sicherung abschalten (Passwort auf diesem PC vergessen). Die Datei bleibt liegen.</summary>
    public static void AutomatikAus()
    {
        try { File.Delete(PasswortDatei); } catch { /* nicht vorhanden */ }
        _schluesselCache = null;
    }

    // ------------------------------------------------------------------ Inhalt zusammenstellen

    private static string DesignDatei => Path.Combine(Ort.LokalerOrdner, "design.txt");

    private static SicherungsInhalt Erzeugen(Konfiguration k) => new()
    {
        Version = App.Version,
        Konfiguration = k,
        Tokens = k.Rechner.Where(r => r.AgentToken.Length > 0).ToDictionary(r => r.Id, r => r.AgentToken),
        TunnelSchluessel = k.Rechner.Where(r => r.TunnelSchluessel.Length > 0).ToDictionary(r => r.Id, r => r.TunnelSchluessel),
        DockerPfade = k.Rechner.Where(r => r.DockerDesktopPfad.Length > 0).ToDictionary(r => r.Id, r => r.DockerDesktopPfad),
        Design = File.Exists(DesignDatei) ? File.ReadAllText(DesignDatei).Trim() : null,
    };

    /// <summary>Serialisiert den Stand ohne Zeitstempel (für den Vergleich) und mit Zeitstempel (für die Datei).</summary>
    private static (string OhneZeit, byte[] Mit) Serialisieren(Konfiguration k)
    {
        var inhalt = Erzeugen(k);
        var ohne = JsonSerializer.Serialize(inhalt, KonfigStore.JsonOptionen);
        inhalt.Erstellt = DateTime.Now;
        return (ohne, JsonSerializer.SerializeToUtf8Bytes(inhalt, KonfigStore.JsonOptionen));
    }

    // ------------------------------------------------------------------ Verschlüsselung

    private static byte[] Schluessel(string passwort, byte[] salz, int iterationen)
    {
        var salzText = Convert.ToBase64String(salz);
        if (_schluesselCache is { } c && c.Passwort == passwort && c.Salz == salzText) return c.Schluessel;
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passwort), salz, iterationen, HashAlgorithmName.SHA256, 32);
        _schluesselCache = (passwort, salzText, key);
        return key;
    }

    private static byte[] Verschluesseln(byte[] klartext, string passwort, byte[] salz)
    {
        var key = Schluessel(passwort, salz, Iterationen);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var chiffre = new byte[klartext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, klartext, chiffre, tag, Zusatz);
        var huelle = new Dictionary<string, object>
        {
            ["format"] = Kennung, ["version"] = 1, ["kdf"] = "PBKDF2-SHA256", ["iterationen"] = Iterationen,
            ["verschluesselung"] = "AES-256-GCM",
            ["salz"] = Convert.ToBase64String(salz), ["nonce"] = Convert.ToBase64String(nonce),
            ["tag"] = Convert.ToBase64String(tag), ["daten"] = Convert.ToBase64String(chiffre),
        };
        return JsonSerializer.SerializeToUtf8Bytes(huelle, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Entschlüsselt eine Sicherungsdatei. Wirft <see cref="InvalidDataException"/> mit verständlicher Meldung bei falschem Passwort oder defekter Datei.</summary>
    public static SicherungsInhalt Lesen(string datei, string passwort)
    {
        JsonElement h;
        try { h = JsonDocument.Parse(File.ReadAllBytes(datei)).RootElement; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { throw new InvalidDataException("Die Datei konnte nicht gelesen werden oder ist keine Appmaster-Sicherung."); }

        if (h.ValueKind != JsonValueKind.Object || !h.TryGetProperty("format", out var f) || f.GetString() != Kennung)
            throw new InvalidDataException("Das ist keine Appmaster-Sicherungsdatei.");
        if (h.GetProperty("version").GetInt32() > 1)
            throw new InvalidDataException("Die Sicherung stammt aus einer neueren Appmaster-Version. Bitte Appmaster aktualisieren.");

        byte[] klar;
        try
        {
            var salz = Convert.FromBase64String(h.GetProperty("salz").GetString()!);
            var key = Schluessel(passwort, salz, h.GetProperty("iterationen").GetInt32());
            var nonce = Convert.FromBase64String(h.GetProperty("nonce").GetString()!);
            var tag = Convert.FromBase64String(h.GetProperty("tag").GetString()!);
            var chiffre = Convert.FromBase64String(h.GetProperty("daten").GetString()!);
            klar = new byte[chiffre.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, chiffre, tag, klar, Zusatz);
        }
        catch (CryptographicException) { throw new InvalidDataException("Das Passwort ist falsch oder die Datei ist beschädigt."); }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("Die Sicherungsdatei ist beschädigt."); }

        try { return JsonSerializer.Deserialize<SicherungsInhalt>(klar, KonfigStore.JsonOptionen) ?? throw new InvalidDataException("Die Sicherung ist leer."); }
        catch (JsonException) { throw new InvalidDataException("Der Inhalt der Sicherung ist beschädigt."); }
    }

    // ------------------------------------------------------------------ Export und automatische Aktualisierung

    private static void DateiSchreiben(byte[] inhalt)
    {
        Directory.CreateDirectory(Ort.Ordner);
        var tmp = Datei + ".tmp";
        File.WriteAllBytes(tmp, inhalt);
        File.Move(tmp, Datei, overwrite: true);
    }

    /// <summary>Erstellt die Sicherung neu (mit neuem Salz), merkt das Passwort für die Automatik und schreibt in den Konfigurationsordner.</summary>
    public static string Exportieren(Konfiguration k, string passwort)
    {
        lock (Sperre)
        {
            var (ohne, mit) = Serialisieren(k);
            DateiSchreiben(Verschluesseln(mit, passwort, RandomNumberGenerator.GetBytes(16)));
            PasswortMerken(passwort);
            _letzterHash = Hash(ohne);
            LetzterFehler = "";
        }
        return Datei;
    }

    /// <summary>
    /// Aktualisiert die Sicherung im Hintergrund, wenn die Automatik aktiv ist und sich etwas geändert hat. Wird nach jedem Speichern der Konfiguration aufgerufen.
    /// </summary>
    public static void Aktualisieren(Konfiguration k)
    {
        var passwort = GespeichertesPasswort();
        if (passwort == null) return;
        var (ohne, mit) = Serialisieren(k);   // Momentaufnahme auf dem aufrufenden Thread
        var hash = Hash(ohne);
        lock (Sperre)
        {
            _laufend = _laufend.ContinueWith(_ =>
            {
                lock (Sperre)
                {
                    try
                    {
                        if (hash == _letzterHash && File.Exists(Datei)) return;
                        DateiSchreiben(Verschluesseln(mit, passwort, VorhandenesSalz() ?? RandomNumberGenerator.GetBytes(16)));
                        _letzterHash = hash;
                        LetzterFehler = "";
                    }
                    catch (Exception ex) { LetzterFehler = ex.Message; }
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Beim Beenden: wartet kurz, bis eine laufende Aktualisierung fertig ist.</summary>
    public static void Abschliessen()
    {
        try { _laufend.Wait(TimeSpan.FromSeconds(5)); } catch { /* ignorieren */ }
    }

    private static byte[]? VorhandenesSalz()
    {
        try
        {
            if (!File.Exists(Datei)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Datei));
            return Convert.FromBase64String(doc.RootElement.GetProperty("salz").GetString()!);
        }
        catch { return null; }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ------------------------------------------------------------------ Wiederherstellen

    /// <summary>
    /// Stellt die Konfiguration aus der Sicherung wieder her: config.json im Konfigurationsordner (die bisherige wird als .bak-… aufgehoben),
    /// Tokens, Schlüsselpfade, Docker-Pfade und Design lokal. Nichts wird auf Erreichbarkeit geprüft – nicht vorhandene Server, Ressourcen
    /// und Apps werden trotzdem angelegt und können danach von Hand angepasst oder gelöscht werden.
    /// </summary>
    public static void Wiederherstellen(SicherungsInhalt inhalt, string passwort)
    {
        Directory.CreateDirectory(Ort.Ordner);
        if (File.Exists(KonfigStore.Datei))
            File.Copy(KonfigStore.Datei, KonfigStore.Datei + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);

        var k = inhalt.Konfiguration;
        foreach (var r in k.Rechner)
        {
            if (inhalt.Tokens.TryGetValue(r.Id, out var t)) r.AgentToken = t;
            if (inhalt.TunnelSchluessel.TryGetValue(r.Id, out var s)) r.TunnelSchluessel = s;
            if (inhalt.DockerPfade.TryGetValue(r.Id, out var d)) r.DockerDesktopPfad = d;
        }
        if (!string.IsNullOrWhiteSpace(inhalt.Design))
        {
            Directory.CreateDirectory(Ort.LokalerOrdner);
            File.WriteAllText(DesignDatei, inhalt.Design);
        }
        PasswortMerken(passwort);   // damit die Automatik auf dem neuen PC gleich weiterläuft
        KonfigStore.Speichern(k);   // schreibt config.json und die lokalen Dateien
        Abschliessen();
    }
}
