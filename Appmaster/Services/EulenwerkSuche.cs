using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Appmaster.Models;
using Appmaster.ViewModels;
using Microsoft.Win32;

namespace Appmaster.Services;

/// <summary>
/// Optionale Beschreibung einer Eulenwerk-App: Windows-Apps legen <c>eulenwerk.json</c> neben die .exe,
/// Web-Apps liefern sie unter <c>/eulenwerk.json</c>. Alles ist optional – fehlt die Datei, nutzt Appmaster
/// die Informationen, die es sonst findet (Dateiversion, Seitentitel, Design-System-Kennzeichen).
/// </summary>
public sealed class EulenwerkManifest
{
    public string Name { get; set; } = "";
    public string Beschreibung { get; set; } = "";
    /// <summary>organisation | kreativ | medien | technik | spielerei | alltag | administration</summary>
    public string Kategorie { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Dienste, die die App braucht (Ollama, sd-server, Datenbank …).</summary>
    public List<ManifestRessource> Ressourcen { get; set; } = new();
}

public sealed class ManifestRessource
{
    public string Name { get; set; } = "";
    public string Beschreibung { get; set; } = "";
    /// <summary>Prüfadresse; <c>{host}</c> wird durch den Rechner ersetzt, auf dem die Ressource angelegt wird (z. B. http://{host}:11434/api/version).</summary>
    public string PruefUrl { get; set; } = "";
    /// <summary>KI-Dienst: wird bevorzugt auf einem GPU-Rechner angelegt.</summary>
    public bool Ki { get; set; }
    /// <summary>Docker-Container-Name (Steuerung über den Agent des Rechners).</summary>
    public string Container { get; set; } = "";
    public string StartBefehl { get; set; } = "";
    public string StopBefehl { get; set; } = "";
    public string Konfliktgruppe { get; set; } = "";
    public bool AutomatischStoppen { get; set; }
}

/// <summary>Was die Suche über einen Rechner wissen muss (Momentaufnahme, damit sie ohne Oberfläche laufen kann).</summary>
public sealed record SuchRechner(string Id, string Name, string Adresse, bool IstLokal, RechnerKategorie Kategorien, IReadOnlyList<int> Ports, IReadOnlyList<string>? Hosts = null)
{
    /// <summary>Hostname bzw. IP ohne Schema, Port und Pfad (auch wenn „http://…“ eingetragen wurde).</summary>
    public string Host => IstLokal ? "localhost" : Adresse.Contains("://") && Uri.TryCreate(Adresse, UriKind.Absolute, out var u) ? u.Host : Adresse.Trim().Trim('/');
}

/// <summary>Eine gefundene Eulenwerk-App.</summary>
public sealed record EulenwerkFund(
    string Schluessel, string Name, string Beschreibung, Kategorie Kategorie, AppTyp Typ,
    string Start, string Arbeitsordner, string Url, string Version,
    string RechnerId, byte[]? Symbol, EulenwerkManifest? Manifest);

/// <summary>
/// Sucht automatisch nach Eulenwerk-Apps:
/// <list type="bullet">
/// <item><b>Arbeitsrechner</b> (nur dieser PC): installierte Windows-Programme – erkannt an <c>eulenwerk.json</c>, am Hersteller „Daniel Ertl“
/// oder am Eulenwerk-Design-System in der Programmdatei.</item>
/// <item><b>Webserver</b>: Webanwendungen auf Port 80/443 und auf den vom Agent gemeldeten Container-Ports – erkannt am
/// Kennzeichen <c>data-eulenwerk</c> im &lt;html&gt;-Tag des Design-Systems oder an <c>/eulenwerk.json</c>.</item>
/// </list>
/// </summary>
public static class EulenwerkSuche
{
    public const string Autor = "Daniel Ertl";
    public const string ManifestName = "eulenwerk.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private static readonly JsonSerializerOptions JsonOpt = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Kategorie je bekanntem Eulenwerk-Projekt (Tabelle des Design-Systems) – letzte Rückfallebene, wenn die App selbst nichts verrät.</summary>
    private static readonly (string Teil, Kategorie Kat)[] Bekannte =
    {
        ("paperpile", Kategorie.Organisation), ("mynotes", Kategorie.Organisation), ("mybooks", Kategorie.Organisation),
        ("indexdata", Kategorie.Organisation), ("vacation", Kategorie.Organisation), ("intime", Kategorie.Organisation),
        ("geschichtenerz", Kategorie.Kreativ), ("bildki", Kategorie.Kreativ), ("kidiskussion", Kategorie.Kreativ),
        ("kumihimo", Kategorie.Kreativ), ("epubtovoice", Kategorie.Kreativ),
        ("videobuddy", Kategorie.Medien), ("spotify", Kategorie.Medien),
        ("e3dcpull", Kategorie.Technik), ("homescan", Kategorie.Technik), ("extinfoscreen", Kategorie.Technik),
        ("gameoflife", Kategorie.Spielerei), ("futuramadecoder", Kategorie.Spielerei),
        ("myrecipes", Kategorie.Alltag), ("appmaster", Kategorie.Administration),
    };

    // ------------------------------------------------------------------------------------------ Einstieg

    public static async Task<List<EulenwerkFund>> SuchenAsync(IReadOnlyList<SuchRechner> rechner, CancellationToken ct = default)
    {
        var aufgaben = new List<Task<List<EulenwerkFund>>>();
        foreach (var r in rechner)
        {
            if (r.Kategorien.HasFlag(RechnerKategorie.Arbeitsrechner) && r.IstLokal)
                aufgaben.Add(Task.Run(() => WindowsAppsSuchen(r), ct));
            if (r.Kategorien.HasFlag(RechnerKategorie.Webserver))
                aufgaben.Add(WebAppsSuchenAsync(r, ct));
        }
        var ergebnisse = await Task.WhenAll(aufgaben);
        return ergebnisse.SelectMany(x => x).GroupBy(f => f.Schluessel, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    // ------------------------------------------------------------------------------------------ Kategorie

    public static Kategorie? KategorieLesen(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.Contains("technik", StringComparison.OrdinalIgnoreCase)) return Kategorie.Technik;   // „Haus & Technik“
        return Enum.TryParse<Kategorie>(t, ignoreCase: true, out var k) && Enum.IsDefined(k) ? k : null;
    }

    private static Kategorie? BekannteKategorie(params string?[] namen)
    {
        foreach (var n in namen)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            var norm = new string(n.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            foreach (var (teil, kat) in Bekannte)
                if (norm.Contains(teil, StringComparison.Ordinal)) return kat;
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------ Windows-Apps

    private static List<EulenwerkFund> WindowsAppsSuchen(SuchRechner rechner)
    {
        var funde = new List<EulenwerkFund>();
        var eigene = Environment.ProcessPath;
        foreach (var ordner in Programmordner())
        {
            string[] exes;
            try { exes = Directory.GetFiles(ordner, "*.exe", SearchOption.TopDirectoryOnly); }
            catch { continue; }
            foreach (var exe in exes)
            {
                var datei = Path.GetFileName(exe);
                if (datei.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || string.Equals(exe, eigene, StringComparison.OrdinalIgnoreCase)) continue;
                try { if (WindowsFund(exe, rechner.Id) is { } f) funde.Add(f); }
                catch { /* unlesbare Datei überspringen */ }
            }
        }
        return funde;
    }

    /// <summary>Ordner, in denen Programme liegen können: Installationsorte aus der Registry (Hersteller „Daniel Ertl“) und die üblichen Programmordner.</summary>
    private static IEnumerable<string> Programmordner()
    {
        var gesehen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var pfad in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            string[] namen;
            RegistryKey? wurzel = null;
            try { wurzel = hive.OpenSubKey(pfad); namen = wurzel?.GetSubKeyNames() ?? Array.Empty<string>(); }
            catch { namen = Array.Empty<string>(); }
            foreach (var n in namen)
            {
                string? ort = null;
                try
                {
                    using var k = wurzel!.OpenSubKey(n);
                    if (!string.Equals((k?.GetValue("Publisher") as string)?.Trim(), Autor, StringComparison.OrdinalIgnoreCase)) continue;
                    ort = (k?.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                    if (string.IsNullOrEmpty(ort) && k?.GetValue("DisplayIcon") is string icon)
                        ort = Path.GetDirectoryName(icon.Split(',')[0].Trim().Trim('"'));
                }
                catch { continue; }
                if (!string.IsNullOrEmpty(ort) && Directory.Exists(ort) && gesehen.Add(Path.GetFullPath(ort))) yield return Path.GetFullPath(ort);
            }
            wurzel?.Dispose();
        }

        var wurzeln = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        foreach (var w in wurzeln.Where(x => x.Length > 0 && Directory.Exists(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] unter;
            try { unter = Directory.GetDirectories(w); } catch { continue; }
            foreach (var d in unter)
                if (gesehen.Add(Path.GetFullPath(d))) yield return Path.GetFullPath(d);
        }
    }

    private static EulenwerkFund? WindowsFund(string exe, string rechnerId)
    {
        var ordner = Path.GetDirectoryName(exe)!;
        var manifest = ManifestLesen(Path.ChangeExtension(exe, ".eulenwerk.json")) ?? ManifestLesen(Path.Combine(ordner, ManifestName));
        var info = FileVersionInfo.GetVersionInfo(exe);
        var vomAutor = string.Equals(info.CompanyName?.Trim(), Autor, StringComparison.OrdinalIgnoreCase);

        // Bei Apps mit eigener .dll (Standard bei .NET) steht das Design-System und die Kategorie-Farbwelt im Code.
        HashSet<string> spuren = new();
        var dll = Path.ChangeExtension(exe, ".dll");
        if (File.Exists(dll) && new FileInfo(dll).Length < 200_000_000)
        {
            var nadeln = new List<string> { "eulenwerkci" };
            nadeln.AddRange(Enum.GetNames<Kategorie>().Select(k => "eulenwerk." + k.ToLowerInvariant() + "."));
            spuren = DateiSuche(dll, nadeln);
        }

        if (manifest == null && !vomAutor && !spuren.Contains("eulenwerkci")) return null;

        var name = Erster(manifest?.Name, info.ProductName, info.FileDescription, Path.GetFileNameWithoutExtension(exe))!;
        var beschreibung = Erster(manifest?.Beschreibung, info.Comments, info.FileDescription == name ? null : info.FileDescription) ?? "";
        var kat = KategorieLesen(manifest?.Kategorie)
                  ?? Enum.GetValues<Kategorie>().Where(k => spuren.Contains("eulenwerk." + k.ToString().ToLowerInvariant() + ".")).Cast<Kategorie?>().FirstOrDefault()
                  ?? BekannteKategorie(name, Path.GetFileNameWithoutExtension(exe))
                  ?? Kategorie.Organisation;
        var version = Erster(manifest?.Version, info.ProductVersion?.Split('+')[0], info.FileVersion) ?? "";

        return new EulenwerkFund("win:" + exe.ToLowerInvariant(), name, beschreibung, kat, AppTyp.Lokal, exe, ordner, "", version,
            rechnerId, SymbolAusDatei(exe), manifest);
    }

    private static string? Erster(params string?[] werte) => werte.Select(w => w?.Trim()).FirstOrDefault(w => !string.IsNullOrEmpty(w));

    private static EulenwerkManifest? ManifestLesen(string datei)
    {
        try { return File.Exists(datei) ? JsonSerializer.Deserialize<EulenwerkManifest>(File.ReadAllText(datei), JsonOpt) : null; }
        catch { return null; }
    }

    /// <summary>Sucht Zeichenfolgen (ASCII, Groß-/Kleinschreibung egal) in einer Datei; gibt die gefundenen (klein geschrieben) zurück.</summary>
    private static HashSet<string> DateiSuche(string datei, IReadOnlyList<string> nadeln)
    {
        var gefunden = new HashSet<string>();
        var bytes = nadeln.Select(Encoding.ASCII.GetBytes).ToList();
        var maxLaenge = bytes.Max(b => b.Length);
        var puffer = new byte[1 << 20];
        int behalten = 0;
        try
        {
            using var s = new FileStream(datei, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int gelesen;
            while ((gelesen = s.Read(puffer, behalten, puffer.Length - behalten)) > 0)
            {
                int gesamt = behalten + gelesen;
                for (int i = behalten; i < gesamt; i++) if (puffer[i] is >= (byte)'A' and <= (byte)'Z') puffer[i] += 32;
                var fenster = puffer.AsSpan(0, gesamt);
                for (int n = 0; n < bytes.Count; n++)
                    if (!gefunden.Contains(nadeln[n]) && fenster.IndexOf(bytes[n]) >= 0) gefunden.Add(nadeln[n]);
                if (gefunden.Count == nadeln.Count) break;
                behalten = Math.Min(maxLaenge - 1, gesamt);
                Buffer.BlockCopy(puffer, gesamt - behalten, puffer, 0, behalten);
            }
        }
        catch { /* gesperrt oder unlesbar */ }
        return gefunden;
    }

    // ------------------------------------------------------------------------------------------ Symbole

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string datei, int start, int breite, int hoehe, IntPtr[] icons, int[]? ids, uint anzahl, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    /// <summary>Das Programmsymbol der .exe als PNG (möglichst 256 px).</summary>
    private static byte[]? SymbolAusDatei(string exe)
    {
        try
        {
            foreach (var groesse in new[] { 256, 64, 32 })
            {
                var handles = new IntPtr[1];
                if (PrivateExtractIcons(exe, 0, groesse, groesse, handles, null, 1, 0) < 1 || handles[0] == IntPtr.Zero) continue;
                try
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(handles[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    return PngBytes(bmp);
                }
                finally { DestroyIcon(handles[0]); }
            }
        }
        catch { /* ohne Symbol */ }
        return null;
    }

    private static byte[] PngBytes(BitmapSource bild)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bild));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Wandelt PNG/ICO/JPEG/GIF in ein PNG (größtes Bild); null bei Unbrauchbarem.</summary>
    private static byte[]? ZuPng(byte[] daten)
    {
        try
        {
            using var ms = new MemoryStream(daten);
            var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var bild = dec.Frames.OrderByDescending(f => f.PixelWidth).First();
            bild.Freeze();
            return PngBytes(bild);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------------------------------ Webanwendungen

    private static readonly Regex HtmlKennzeichen = new(@"<html\b[^>]*\bdata-eulenwerk\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitelRegex = new(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BeschreibungRegex = new(@"<meta\b[^>]*\bname\s*=\s*[""']description[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? Attribut(string tag, string name)
    {
        var m = Regex.Match(tag, @"\b" + name + @"\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
    }

    private static async Task<List<EulenwerkFund>> WebAppsSuchenAsync(SuchRechner r, CancellationToken ct)
    {
        if (r.Host.Length == 0) return new();
        var kandidaten = new List<string>();
        foreach (var port in r.Ports.Distinct()) kandidaten.Add($"http://{r.Host}:{port}/");
        // Hostname-basierte Webseiten (nginx/Apache-Domains, vom Agent gemeldet) zusätzlich zur Adresse des Rechners
        var hosts = new List<string> { r.Host };
        hosts.AddRange((r.Hosts ?? Array.Empty<string>()).Where(h => !string.Equals(h, r.Host, StringComparison.OrdinalIgnoreCase)));
        foreach (var h in hosts) { kandidaten.Add($"https://{h}/"); kandidaten.Add($"http://{h}/"); }

        var ergebnisse = await Task.WhenAll(kandidaten.Select(u => WebFund(u, r, ct)));
        var funde = ergebnisse.Where(f => f != null).Select(f => f!).ToList();
        // Dieselbe Seite über http und https: nur https behalten.
        foreach (var h in hosts)
            if (funde.Any(f => f.Url == $"https://{h}/")) funde.RemoveAll(f => f.Url == $"http://{h}/");
        return funde;
    }

    private static async Task<EulenwerkFund?> WebFund(string basis, SuchRechner r, CancellationToken ct)
    {
        string html;
        try
        {
            using var antwort = await Http.GetAsync(basis, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!antwort.IsSuccessStatusCode || antwort.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) != true) return null;
            await using var s = await antwort.Content.ReadAsStreamAsync(ct);
            var puffer = new byte[128 * 1024];
            int n = 0, g;
            while (n < puffer.Length && (g = await s.ReadAsync(puffer.AsMemory(n), ct)) > 0) n += g;
            html = Encoding.UTF8.GetString(puffer, 0, n);
        }
        catch { return null; }

        var kennzeichen = HtmlKennzeichen.Match(html);
        var manifest = await ManifestHolenAsync(basis, ct);
        if (!kennzeichen.Success && manifest == null) return null;

        var titel = TitelRegex.Match(html) is { Success: true } t ? System.Net.WebUtility.HtmlDecode(Regex.Replace(t.Groups[1].Value, @"\s+", " ")).Trim() : "";
        var kurzTitel = Regex.Split(titel, @"\s+[–—|·-]\s+")[0].Trim();
        var meta = BeschreibungRegex.Match(html) is { Success: true } mm ? Attribut(mm.Value, "content") : null;
        var uri = new Uri(basis);
        var name = Erster(manifest?.Name, kurzTitel, uri.Host)!;
        var kat = KategorieLesen(manifest?.Kategorie) ?? KategorieLesen(kennzeichen.Success ? kennzeichen.Groups[1].Value : null)
                  ?? BekannteKategorie(name, uri.Host) ?? Kategorie.Organisation;

        return new EulenwerkFund("web:" + basis.TrimEnd('/').ToLowerInvariant(), name, Erster(manifest?.Beschreibung, meta) ?? "", kat, AppTyp.Web,
            "", "", basis, manifest?.Version ?? "", r.Id, await WebSymbolAsync(basis, html, KategorieFarben.Pinsel(kat, false) is SolidColorBrush sb ? sb.Color : Colors.White, ct), manifest);
    }

    private static async Task<EulenwerkManifest?> ManifestHolenAsync(string basis, CancellationToken ct)
    {
        try
        {
            using var antwort = await Http.GetAsync(new Uri(new Uri(basis), "/" + ManifestName), ct);
            if (!antwort.IsSuccessStatusCode) return null;
            var text = await antwort.Content.ReadAsStringAsync(ct);
            return text.TrimStart().StartsWith('{') ? JsonSerializer.Deserialize<EulenwerkManifest>(text, JsonOpt) : null;
        }
        catch { return null; }
    }

    private static async Task<byte[]?> WebSymbolAsync(string basis, string html, Color akzent, CancellationToken ct)
    {
        var liste = new List<(int Rang, string Url)>();
        foreach (Match m in LinkRegex.Matches(html))
        {
            var rel = Attribut(m.Value, "rel")?.ToLowerInvariant() ?? "";
            var href = Attribut(m.Value, "href");
            if (string.IsNullOrWhiteSpace(href) || !rel.Contains("icon")) continue;
            bool svg = href.Split('?')[0].EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || (Attribut(m.Value, "type") ?? "").Contains("svg");
            int rang = rel.Contains("apple-touch") ? 0 : svg ? 1 : href.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
            try { liste.Add((rang, new Uri(new Uri(basis), href).AbsoluteUri)); } catch { }
        }
        var ordnung = liste.OrderBy(x => x.Rang).Select(x => x.Url).ToList();
        foreach (var pfad in new[] { "/icon.png", "/apple-touch-icon.png", "/favicon.svg", "/icon.svg", "/favicon.ico" })
            ordnung.Add(new Uri(new Uri(basis), pfad).AbsoluteUri);

        foreach (var url in ordnung.Distinct())
        {
            try
            {
                using var antwort = await Http.GetAsync(url, ct);
                if (!antwort.IsSuccessStatusCode) continue;
                var daten = await antwort.Content.ReadAsByteArrayAsync(ct);
                if (daten.Length is 0 or > 2_000_000) continue;
                var istSvg = url.Split('?')[0].EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || antwort.Content.Headers.ContentType?.MediaType?.Contains("svg") == true;
                var png = istSvg ? SvgRenderer.ZuPng(Encoding.UTF8.GetString(daten), akzent) : ZuPng(daten);
                if (png != null) return png;
            }
            catch { /* nächster Kandidat */ }
        }
        return null;
    }
}
