using System.Text.Json.Serialization;

namespace Appmaster.Models;

/// <summary>Eulenwerk-Kategorien (Akzentfarbe je App). Die Farben stehen im Design-System.</summary>
public enum Kategorie { Organisation, Kreativ, Medien, Technik, Spielerei, Alltag, Administration }

public enum AppTyp { Lokal, Web }

/// <summary>Wie Appmaster eine Ressource steuert.</summary>
public enum Steuerung
{
    /// <summary>Nur überwachen (Erreichbarkeit prüfen), nie starten/stoppen.</summary>
    NurUeberwachen,
    /// <summary>Start/Stopp über je einen Befehl (z. B. "docker start ollama" oder "ssh athene docker start ollama").</summary>
    Befehl,
    /// <summary>Start/Stopp eines Docker-Containers über den Appmaster-Agent auf dem Rechner.</summary>
    Agent,
}

/// <summary>Was auf dem Rechner für Überwachung/Steuerung läuft.</summary>
public enum AgentArt
{
    Keiner,
    /// <summary>appmaster-agent.py (Linux): Leistungsdaten, Docker-Steuerung, optional Herunterfahren.</summary>
    LinuxAgent,
    /// <summary>status.php (Webhosting): nur lesen – Speicherplatz, Last, PHP-Version.</summary>
    PhpStatus,
}

public enum SchliessenAktion { Fragen, LaufenLassen, Beenden }

/// <summary>Ein Rechner, auf dem Ressourcen laufen (Athene, dieser PC, GPU-PC …).</summary>
public sealed class Rechner
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Hostname oder IP; leer = dieser PC.</summary>
    public string Adresse { get; set; } = "";
    /// <summary>MAC-Adresse für Wake-on-LAN (aa:bb:cc:dd:ee:ff); leer = kein Aufwecken möglich.</summary>
    public string MacAdresse { get; set; } = "";
    /// <summary>Ziel des Magic Packets; im Heimnetz meist 255.255.255.255 oder z. B. 192.168.1.255.</summary>
    public string Broadcast { get; set; } = "255.255.255.255";
    public AgentArt AgentArt { get; set; } = AgentArt.Keiner;
    /// <summary>z. B. http://athene:8765 (Linux-Agent) oder https://meine-seite.de/appmaster/status.php (PHP).</summary>
    public string AgentUrl { get; set; } = "";
    /// <summary>Wird nur lokal auf diesem PC gespeichert (nie in der Dropbox-Konfiguration), siehe KonfigStore.</summary>
    [JsonIgnore] public string AgentToken { get; set; } = "";
    /// <summary>Agent über einen SSH-Tunnel erreichen (z. B. vServer, dessen Agent nur auf 127.0.0.1 lauscht). Appmaster startet und überwacht den Tunnel selbst.</summary>
    public bool TunnelAktiv { get; set; }
    /// <summary>SSH-Ziel, z. B. root@82.165.153.177 (oder ein Name aus ~/.ssh/config).</summary>
    public string TunnelZiel { get; set; } = "";
    /// <summary>Lokaler Port am PC, zu dem Appmaster verbindet (Agent-Adresse wird dann http://127.0.0.1:port).</summary>
    public int TunnelLokalPort { get; set; } = 8766;
    /// <summary>Port des Agents auf dem Server (Standard 8765).</summary>
    public int TunnelServerPort { get; set; } = 8765;
    /// <summary>Privater Schlüssel (OpenSSH-Format, keine .ppk). Pfade sind je PC verschieden, daher nur lokal gespeichert.</summary>
    /// <summary>Nur für „dieser PC“: Docker Desktop vor Docker-Befehlen starten, falls die Engine nicht läuft.</summary>
    public bool DockerDesktopStarten { get; set; }
    /// <summary>Pfad der „Docker Desktop.exe“ (leer = automatisch suchen). Je PC verschieden, daher nur lokal gespeichert.</summary>
    [JsonIgnore] public string DockerDesktopPfad { get; set; } = "";
    [JsonIgnore] public string TunnelSchluessel { get; set; } = "";
    [JsonIgnore] public bool IstLokal => string.IsNullOrWhiteSpace(Adresse);
}

/// <summary>Etwas, das Apps brauchen: Ollama, sd-server, Datenbank, Docker-Container …</summary>
public sealed class Ressource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Beschreibung { get; set; } = "";
    public string RechnerId { get; set; } = "";
    /// <summary>Adresse zum Prüfen, ob die Ressource läuft (HTTP GET; jede Antwort unter 500 = läuft).</summary>
    public string PruefUrl { get; set; } = "";
    public Steuerung Steuerung { get; set; } = Steuerung.NurUeberwachen;
    public string StartBefehl { get; set; } = "";
    public string StopBefehl { get; set; } = "";
    /// <summary>Docker-Container-Name für Steuerung = Agent.</summary>
    public string AgentContainer { get; set; } = "";
    /// <summary>Ressourcen mit gleicher Gruppe (z. B. "GPU Athene") dürfen nicht gleichzeitig laufen; Appmaster warnt.</summary>
    public string Konfliktgruppe { get; set; } = "";
    /// <summary>Stoppen, sobald keine laufende App sie mehr braucht (nur bei Steuerung = Befehl).</summary>
    public bool AutomatischStoppen { get; set; }
    /// <summary>Sekunden, die Appmaster nach dem Start auf die Erreichbarkeit wartet.</summary>
    public int StartWartezeit { get; set; } = 60;
}

public sealed class AppEintrag
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Beschreibung { get; set; } = "";
    public Kategorie Kategorie { get; set; } = Kategorie.Organisation;
    public AppTyp Typ { get; set; } = AppTyp.Lokal;
    /// <summary>Lokal: Programm oder Befehl (z. B. "C:\Apps\BildKI.exe" oder "dotnet"). Web: optionaler Befehl, der den lokalen Server startet.</summary>
    public string StartBefehl { get; set; } = "";
    public string Argumente { get; set; } = "";
    public string Arbeitsordner { get; set; } = "";
    /// <summary>Nur Web: Adresse, die im Browser geöffnet wird.</summary>
    public string Url { get; set; } = "";
    /// <summary>Wo die App ihre Daten ablegt – Appmaster reicht den Pfad als Umgebungsvariable APPMASTER_DATA weiter.</summary>
    public string Datenpfad { get; set; } = "";
    /// <summary>Ressourcen, die vor dem Start laufen müssen.</summary>
    public List<string> RessourcenIds { get; set; } = new();
}

public sealed class Einstellungen
{
    public int PruefIntervallSekunden { get; set; } = 5;
    public SchliessenAktion BeimSchliessen { get; set; } = SchliessenAktion.Fragen;
}

public sealed class Konfiguration
{
    public int Version { get; set; } = 1;
    public Einstellungen Einstellungen { get; set; } = new();
    public List<Rechner> Rechner { get; set; } = new();
    public List<Ressource> Ressourcen { get; set; } = new();
    public List<AppEintrag> Apps { get; set; } = new();
}
