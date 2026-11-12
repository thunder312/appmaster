using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Appmaster.Models;

namespace Appmaster.Services;

/// <summary>
/// Startet Docker Desktop bei Bedarf (nur für „dieser PC“): Vor einem Docker-Befehl prüft Appmaster, ob die Docker-Engine antwortet,
/// startet sonst Docker Desktop und wartet, bis die Engine bereit ist.
/// </summary>
public static class DockerDesktop
{
    private static readonly Regex DockerBefehl = new(@"(^|[\s&|;(""'\\/])docker(\.exe)?(\s|$)", RegexOptions.IgnoreCase);
    private static readonly SemaphoreSlim Sperre = new(1, 1);

    /// <summary>Kommt im Befehl ein docker-Aufruf vor (z. B. "docker start x", "docker compose up")?</summary>
    public static bool IstDockerBefehl(string befehl) => DockerBefehl.IsMatch(befehl);

    /// <summary>Docker Desktop.exe: eingetragener Pfad, sonst aus dem Ort von docker.exe abgeleitet, sonst Standardordner.</summary>
    public static string? ExePfad(Rechner r)
    {
        if (r.DockerDesktopPfad.Length > 0 && File.Exists(r.DockerDesktopPfad)) return r.DockerDesktopPfad;

        // docker.exe liegt in <Installation>\resources\bin – Docker Desktop.exe zwei bis drei Ordner darüber
        foreach (var ordner in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (!File.Exists(Path.Combine(ordner.Trim('"'), "docker.exe"))) continue;
                // Der oberste Treffer ist der Launcher im Installationsordner (weiter unten liegen Hilfsprogramme gleichen Namens).
                var d = new DirectoryInfo(ordner.Trim('"'));
                string? treffer = null;
                for (int i = 0; i < 4 && d != null; i++, d = d.Parent)
                {
                    var kandidat = Path.Combine(d.FullName, "Docker Desktop.exe");
                    if (File.Exists(kandidat)) treffer = kandidat;
                }
                if (treffer != null) return treffer;
            }
            catch { /* ungültiger PATH-Eintrag */ }
        }
        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");
        return File.Exists(standard) ? standard : null;
    }

    /// <summary>true, wenn "docker info" erfolgreich antwortet (Engine läuft).</summary>
    public static async Task<bool> EngineLaeuftAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            using var ende = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await p.WaitForExitAsync(ende.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }   // docker.exe nicht gefunden
    }

    /// <summary>
    /// Stellt sicher, dass die Docker-Engine läuft. Ist die Option am Rechner aus oder der Rechner nicht „dieser PC“, passiert nichts.
    /// </summary>
    public static async Task<(bool Ok, string Meldung)> SicherstellenAsync(Rechner r, Action<string>? status = null, int maxSekunden = 180)
    {
        if (!r.DockerDesktopStarten || !r.IstLokal) return (true, "");
        await Sperre.WaitAsync();   // zwei Ressourcen gleichzeitig starten Docker Desktop nicht doppelt
        try
        {
            if (await EngineLaeuftAsync()) return (true, "");
            var exe = ExePfad(r);
            if (exe == null)
                return (false, "Docker läuft nicht, und Docker Desktop wurde nicht gefunden. Pfad der „Docker Desktop.exe“ im Rechner-Dialog eintragen.");

            status?.Invoke("Docker Desktop wird gestartet …");
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! }); }
            catch (Exception ex) { return (false, "Docker Desktop konnte nicht gestartet werden: " + ex.Message); }

            var ende = DateTime.UtcNow.AddSeconds(maxSekunden);
            while (DateTime.UtcNow < ende)
            {
                await Task.Delay(3000);
                if (await EngineLaeuftAsync()) return (true, "");
            }
            return (false, $"Docker Desktop wurde gestartet, die Engine antwortet aber auch nach {maxSekunden} Sekunden nicht (evtl. Anmeldung oder Lizenzdialog offen?).");
        }
        finally { Sperre.Release(); }
    }
}
