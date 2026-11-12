using System.Diagnostics;
using System.Net.Sockets;
using Appmaster.Models;

namespace Appmaster.Services;

/// <summary>
/// SSH-Tunnel zu Rechnern, deren Agent nur auf 127.0.0.1 lauscht (vServer im Internet).
/// Startet "ssh -N -L" unsichtbar, hält ihn am Leben (Neustart nach Abbruch, höchstens alle 30 s) und beendet ihn beim Schließen.
/// Läuft ohne Passwortabfrage (BatchMode): der Schlüssel muss auf dem Server hinterlegt und unverschlüsselt bzw. im ssh-agent geladen sein.
/// </summary>
public static class SshTunnel
{
    private sealed class Eintrag
    {
        public Process? Prozess;
        public string Argumente = "";
        public DateTime LetzterStart = DateTime.MinValue;
        public string Fehler = "";
    }

    private static readonly Dictionary<string, Eintrag> Tunnel = new();
    private static readonly object Sperre = new();

    /// <summary>Adresse, unter der Appmaster den Agent anspricht: bei Tunnel http://127.0.0.1:Port, sonst die eingetragene.</summary>
    public static string AgentUrl(Rechner r) => r.TunnelAktiv ? $"http://127.0.0.1:{r.TunnelLokalPort}" : r.AgentUrl;

    /// <summary>Letzte Fehlermeldung von ssh (z. B. „Permission denied“), oder leer.</summary>
    public static string Fehler(string rechnerId)
    {
        lock (Sperre) return Tunnel.TryGetValue(rechnerId, out var e) ? e.Fehler : "";
    }

    private static string Argumente(Rechner r)
    {
        var a = $"-N -L 127.0.0.1:{r.TunnelLokalPort}:127.0.0.1:{r.TunnelServerPort} " +
                "-o BatchMode=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -o ServerAliveCountMax=3 " +
                "-o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new ";
        if (r.TunnelSchluessel.Length > 0) a += $"-o IdentitiesOnly=yes -i \"{r.TunnelSchluessel}\" ";
        return a + r.TunnelZiel;
    }

    /// <summary>Stellt sicher, dass der Tunnel läuft (startet ihn bei Bedarf) und wartet kurz, bis der lokale Port offen ist.</summary>
    public static async Task SicherstellenAsync(Rechner r)
    {
        if (!r.TunnelAktiv || r.TunnelZiel.Length == 0) return;
        bool neuGestartet = false;
        lock (Sperre)
        {
            if (!Tunnel.TryGetValue(r.Id, out var e)) Tunnel[r.Id] = e = new Eintrag();
            var args = Argumente(r);
            bool laeuft = e.Prozess is { HasExited: false };
            if (laeuft && e.Argumente != args) { Stoppen(e); laeuft = false; }   // Einstellungen geändert
            if (!laeuft && DateTime.UtcNow - e.LetzterStart > TimeSpan.FromSeconds(30))
            {
                e.LetzterStart = DateTime.UtcNow;
                e.Argumente = args;
                e.Fehler = "";
                try
                {
                    var p = new Process
                    {
                        StartInfo = new ProcessStartInfo("ssh", args)
                        {
                            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true,
                        },
                        EnableRaisingEvents = true,
                    };
                    var eintrag = e;
                    p.ErrorDataReceived += (_, d) => { if (!string.IsNullOrWhiteSpace(d.Data)) lock (Sperre) eintrag.Fehler = d.Data.Trim(); };
                    p.Start();
                    p.BeginErrorReadLine();
                    e.Prozess = p;
                    neuGestartet = true;
                }
                catch (Exception ex) { e.Fehler = "ssh konnte nicht gestartet werden: " + ex.Message; }
            }
        }
        if (neuGestartet) await PortWartenAsync(r.TunnelLokalPort, TimeSpan.FromSeconds(8));
    }

    private static async Task PortWartenAsync(int port, TimeSpan max)
    {
        var ende = DateTime.UtcNow + max;
        while (DateTime.UtcNow < ende)
        {
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromMilliseconds(500));
                return;
            }
            catch { await Task.Delay(300); }
        }
    }

    private static void Stoppen(Eintrag e)
    {
        try { if (e.Prozess is { HasExited: false }) e.Prozess.Kill(true); } catch { /* schon beendet */ }
        e.Prozess = null;
        e.LetzterStart = DateTime.MinValue;
    }

    public static void Beenden(string rechnerId)
    {
        lock (Sperre) if (Tunnel.Remove(rechnerId, out var e)) Stoppen(e);
    }

    public static void AlleBeenden()
    {
        lock (Sperre)
        {
            foreach (var e in Tunnel.Values) Stoppen(e);
            Tunnel.Clear();
        }
    }
}
