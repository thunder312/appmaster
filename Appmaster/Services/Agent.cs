using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Appmaster.Models;

namespace Appmaster.Services;

/// <summary>Wake-on-LAN: schickt das „Magic Packet“ (6 × 0xFF, dann 16 × MAC) per UDP-Broadcast.</summary>
public static class WakeOnLan
{
    public static bool MacGueltig(string mac) => MacLesen(mac) != null;

    private static byte[]? MacLesen(string mac)
    {
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12 || mac.Count(c => !Uri.IsHexDigit(c) && c != ':' && c != '-' && c != ' ') > 0) return null;
        return Convert.FromHexString(hex);
    }

    public static async Task SendenAsync(string mac, string broadcast)
    {
        var bytes = MacLesen(mac) ?? throw new FormatException("Ungültige MAC-Adresse.");
        var paket = new byte[6 + 16 * 6];
        Array.Fill(paket, (byte)0xFF, 0, 6);
        for (int i = 0; i < 16; i++) Buffer.BlockCopy(bytes, 0, paket, 6 + i * 6, 6);

        var ziel = IPAddress.TryParse(broadcast, out var ip) ? ip : IPAddress.Broadcast;
        using var udp = new UdpClient { EnableBroadcast = true };
        // Port 9 (Standard) und 7 (manche Geräte); zwei Mal zur Sicherheit
        for (int runde = 0; runde < 2; runde++)
        {
            await udp.SendAsync(paket, paket.Length, new IPEndPoint(ziel, 9));
            await udp.SendAsync(paket, paket.Length, new IPEndPoint(ziel, 7));
            await Task.Delay(300);
        }
    }
}

public sealed class GpuInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("busy_percent")] public double? Auslastung { get; set; }
    [JsonPropertyName("vram_used_mb")] public double? VramBelegt { get; set; }
    [JsonPropertyName("vram_total_mb")] public double? VramGesamt { get; set; }
    [JsonPropertyName("gtt_used_mb")] public double? GttBelegt { get; set; }
    [JsonPropertyName("gtt_total_mb")] public double? GttGesamt { get; set; }
    [JsonPropertyName("temp")] public double? Temperatur { get; set; }
}

public sealed class DiskInfo
{
    [JsonPropertyName("pfad")] public string Pfad { get; set; } = "";
    [JsonPropertyName("total_gb")] public double Gesamt { get; set; }
    [JsonPropertyName("used_gb")] public double Belegt { get; set; }
}

public sealed class ContainerInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("state")] public string Zustand { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    /// <summary>Veröffentlichte TCP-Ports (ab Agent 0.2.0); dort sucht Appmaster nach Eulenwerk-Webanwendungen.</summary>
    [JsonPropertyName("ports")] public List<int>? Ports { get; set; }
}

/// <summary>Messwerte, die der Agent (oder status.php) liefert. Fehlende Felder bleiben null.</summary>
public sealed class AgentStatus
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("cpu_percent")] public double? Cpu { get; set; }
    [JsonPropertyName("load")] public double[]? Last { get; set; }
    [JsonPropertyName("mem_total_mb")] public double? RamGesamt { get; set; }
    [JsonPropertyName("mem_used_mb")] public double? RamBelegt { get; set; }
    [JsonPropertyName("uptime_s")] public double? Laufzeit { get; set; }
    [JsonPropertyName("temps")] public Dictionary<string, double>? Temperaturen { get; set; }
    [JsonPropertyName("gpus")] public List<GpuInfo>? Gpus { get; set; }
    [JsonPropertyName("disks")] public List<DiskInfo>? Platten { get; set; }
    [JsonPropertyName("docker")] public List<ContainerInfo>? Container { get; set; }
    [JsonPropertyName("power_allowed")] public bool KannHerunterfahren { get; set; }
    [JsonPropertyName("read_only")] public bool NurLesen { get; set; }
}

/// <summary>Spricht den Appmaster-Agent (agent/appmaster-agent.py) bzw. status.php an. Authentifizierung per Header X-Appmaster-Token.</summary>
public static class AgentClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static HttpRequestMessage Anfrage(Rechner r, HttpMethod m, string pfad)
    {
        var basis = SshTunnel.AgentUrl(r).TrimEnd('/');
        // PHP-Status ist eine einzelne Datei: Adresse unverändert verwenden.
        var url = r.AgentArt == AgentArt.PhpStatus ? basis : basis + pfad;
        var req = new HttpRequestMessage(m, url);
        req.Headers.Add("X-Appmaster-Token", r.AgentToken);
        return req;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Fehlergruende = new();

    /// <summary>Warum der Agent keine Daten liefert (Tunnel-Fehler, falscher Token, nicht erreichbar), oder leer.</summary>
    public static string Hinweis(Rechner r)
    {
        if (!Eingerichtet(r)) return "";
        var teile = new List<string>();
        var tunnel = SshTunnel.Fehler(r.Id);
        if (tunnel.Length > 0) teile.Add("Tunnel: " + tunnel);
        if (Fehlergruende.TryGetValue(r.Id, out var f) && f.Length > 0) teile.Add(f);
        return string.Join(" · ", teile);
    }

    public static bool Eingerichtet(Rechner r) => r.AgentArt != AgentArt.Keiner && SshTunnel.AgentUrl(r).Length > 0;

    public static async Task<AgentStatus?> StatusAsync(Rechner r, CancellationToken ct = default)
    {
        if (!Eingerichtet(r)) return null;
        try
        {
            await SshTunnel.SicherstellenAsync(r);
            using var resp = await Http.SendAsync(Anfrage(r, HttpMethod.Get, "/status"), ct);
            if (!resp.IsSuccessStatusCode)
            {
                Fehlergruende[r.Id] = (int)resp.StatusCode == 401 || (int)resp.StatusCode == 403
                    ? "Agent lehnt den Token ab (HTTP " + (int)resp.StatusCode + ") – Token prüfen"
                    : "Agent antwortet mit HTTP " + (int)resp.StatusCode;
                return null;
            }
            var daten = JsonSerializer.Deserialize<AgentStatus>(await resp.Content.ReadAsStringAsync(ct));
            Fehlergruende[r.Id] = "";
            return daten;
        }
        catch (Exception ex)
        {
            Fehlergruende[r.Id] = ex is TaskCanceledException ? "Agent antwortet nicht (Zeitüberschreitung)"
                                : ex.InnerException?.Message is { Length: > 0 } m ? "Agent nicht erreichbar: " + m
                                : "Agent nicht erreichbar: " + ex.Message;
            return null;
        }
    }

    private static async Task<(bool Ok, string Meldung)> PostAsync(Rechner r, string pfad)
    {
        if (r.AgentArt != AgentArt.LinuxAgent) return (false, "Auf diesem Rechner ist kein Linux-Agent eingerichtet.");
        try
        {
            await SshTunnel.SicherstellenAsync(r);
            using var resp = await Http.SendAsync(Anfrage(r, HttpMethod.Post, pfad));
            var text = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, text);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>aktion: start | stop | restart</summary>
    public static Task<(bool Ok, string Meldung)> DockerAsync(Rechner r, string container, string aktion) =>
        PostAsync(r, $"/docker/{Uri.EscapeDataString(container)}/{aktion}");

    public static Task<(bool Ok, string Meldung)> HerunterfahrenAsync(Rechner r) => PostAsync(r, "/power/shutdown");
}

public static class Erreichbar
{
    /// <summary>Ping an den Rechner; für diesen PC immer true.</summary>
    public static async Task<bool> PingAsync(Rechner r)
    {
        if (r.IstLokal) return true;
        try
        {
            using var p = new Ping();
            var antwort = await p.SendPingAsync(r.Adresse, 1500);
            return antwort.Status == IPStatus.Success;
        }
        catch { return false; }
    }
}
