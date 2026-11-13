using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Appmaster.Services;

/// <summary>Ein auf einem GPU-Rechner gefundener KI-Dienst.</summary>
public sealed record KiFund(string Name, string Beschreibung, string PruefUrl, int Port, string? Container, bool Laeuft);

/// <summary>
/// Sucht auf einem GPU-Rechner nach KI-Diensten: <b>Ollama</b> (<c>/api/tags</c>) und <b>sd-server</b> / stable-diffusion.cpp
/// (<c>/sdcpp/v1/capabilities</c>) auf den üblichen Ports (wie BildKI), dazu Docker-Container mit KI-Namen samt Ports (auch gestoppte,
/// wenn der Agent sie meldet).
/// </summary>
public static class KiSuche
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private static readonly int[] Standardports = Enumerable.Range(7860, 20).Concat(Enumerable.Range(8000, 11)).Concat(Enumerable.Range(8080, 11)).Append(11434).ToArray();
    private static readonly Regex KiName = new(@"(^|[-_./])(sd|sdcpp|stable|flux|pony|ollama|comfy|comfyui|llama|whisper|diffusion|vllm|kokoro|tts)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<List<KiFund>> SuchenAsync(SuchRechner r, IReadOnlyList<ContainerInfo> container, CancellationToken ct = default)
    {
        var host = r.Host;
        if (host.Length == 0) return new();
        var containerPorts = container.Where(c => c.Ports is { Count: > 0 }).SelectMany(c => c.Ports!.Select(p => (Port: p, Container: c))).ToList();
        var kandidaten = Standardports.Concat(containerPorts.Where(x => KiName.IsMatch(x.Container.Name)).Select(x => x.Port)).Distinct().ToList();

        // Erst ein kurzer TCP-Connect je Port, nur offene Ports werden per HTTP geprüft.
        using var begrenzer = new SemaphoreSlim(24);
        var offen = (await Task.WhenAll(kandidaten.Select(async p =>
        {
            await begrenzer.WaitAsync(ct);
            try { return (Port: p, Offen: await PortOffenAsync(host, p, ct)); }
            finally { begrenzer.Release(); }
        }))).Where(x => x.Offen).Select(x => x.Port).ToList();

        var funde = new List<KiFund>();
        foreach (var port in offen)
        {
            var url = $"http://{host}:{port}";
            var c = containerPorts.FirstOrDefault(x => x.Port == port).Container;
            using var sd = await Json($"{url}/sdcpp/v1/capabilities", ct);
            if (sd != null && sd.RootElement.TryGetProperty("model", out var m))
            {
                var modell = Text(m, "stem") ?? Text(m, "name") ?? "";
                funde.Add(new KiFund(c?.Name ?? $"sd-server :{port}", "Bild-KI (stable-diffusion.cpp)" + (modell.Length > 0 ? " · Modell " + modell : ""),
                    url + "/", port, c?.Name, true));
            }
            else
            {
                using var tags = await Json($"{url}/api/tags", ct);
                if (tags == null || !tags.RootElement.TryGetProperty("models", out var ml) || ml.ValueKind != JsonValueKind.Array) continue;
                var namen = ml.EnumerateArray().Select(e => Text(e, "name")).OfType<string>().ToList();
                funde.Add(new KiFund(c?.Name ?? "Ollama",
                    namen.Count == 0 ? "Sprachmodelle (Ollama)" : "Ollama · " + string.Join(", ", namen.Take(5)) + (namen.Count > 5 ? $" … (+{namen.Count - 5})" : ""),
                    url + "/api/version", port, c?.Name, true));
            }
        }

        // KI-Container, die gerade nicht laufen (kein offener Port): trotzdem als Ressource, damit Appmaster sie starten kann.
        foreach (var x in containerPorts.Where(x => KiName.IsMatch(x.Container.Name) && !funde.Any(f => f.Port == x.Port) && !offen.Contains(x.Port)))
            if (!funde.Any(f => f.Container == x.Container.Name))
                funde.Add(new KiFund(x.Container.Name, "KI-Dienst im Docker-Container " + x.Container.Name, $"http://{host}:{x.Port}/", x.Port, x.Container.Name, false));
        return funde;
    }

    private static async Task<bool> PortOffenAsync(string host, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var zeit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        zeit.CancelAfter(TimeSpan.FromMilliseconds(600));
        try { await tcp.ConnectAsync(host, port, zeit.Token); return true; }
        catch { return false; }
    }

    private static async Task<JsonDocument?> Json(string url, CancellationToken ct)
    {
        try
        {
            using var antwort = await Http.GetAsync(url, ct);
            return antwort.IsSuccessStatusCode ? JsonDocument.Parse(await antwort.Content.ReadAsStringAsync(ct)) : null;
        }
        catch { return null; }
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
