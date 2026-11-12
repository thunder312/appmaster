using System.Diagnostics;
using System.Net.Http;

namespace Appmaster.Services;

public enum Zustand { Unbekannt, Laeuft, Gestoppt, Startet }

/// <summary>Prüft per HTTP, ob eine Ressource antwortet.</summary>
public static class Gesundheit
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public static async Task<Zustand> PruefenAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return Zustand.Unbekannt;
        try
        {
            using var antwort = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            // Jede HTTP-Antwort unter 500 heißt: der Dienst läuft (manche liefern 404 auf "/").
            return (int)antwort.StatusCode < 500 ? Zustand.Laeuft : Zustand.Gestoppt;
        }
        catch { return Zustand.Gestoppt; }
    }
}

/// <summary>Führt Start-/Stopp-Befehle aus (z. B. "ssh athene docker start ollama").</summary>
public static class BefehlsLauf
{
    /// <summary>Führt die Zeile über cmd.exe aus, ohne Fenster. Gibt Exitcode und Ausgabe zurück.</summary>
    public static async Task<(int Code, string Ausgabe)> AusfuehrenAsync(string befehl, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c " + befehl)
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Prozess konnte nicht gestartet werden.");
        var aus = p.StandardOutput.ReadToEndAsync(ct);
        var fehler = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, (await aus + await fehler).Trim());
    }
}
