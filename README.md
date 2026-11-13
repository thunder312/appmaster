# Appmaster

Zentrale Windows-App („Schaltzentrale“) zum Starten und Steuern der Eulenwerk-Apps und der Dienste, die sie brauchen:
KI-Server (Ollama, Stable Diffusion), Docker-Container, Datenbanken. Appmaster startet eine App erst, wenn ihre
Ressourcen laufen, warnt vor Konflikten auf der GPU, weckt schlafende Rechner auf und fährt Ressourcen herunter, die
niemand mehr braucht.

- **Plattform:** Windows 10/11, .NET 8, WPF
- **Design:** Eulenwerk-Design-System (hell/dunkel, Kategorie *Administration*)
- **Sprache der Oberfläche:** Deutsch

## Die drei Bausteine

| Seite | Was dort steht |
|---|---|
| **Apps** | Programme und Web-Apps, die du starten willst, samt den Ressourcen, die sie brauchen |
| **Ressourcen** | Dienste, auf die Apps angewiesen sind (Ollama, `sd-server`, Datenbank, Container …) |
| **Rechner** | Maschinen, auf denen Ressourcen laufen (dieser PC, Athene, vServer, Webhosting …) |

## Funktionsübersicht

### Eulenwerk-Apps automatisch finden

Appmaster ist vor allem für die Eulenwerk-Apps gedacht. Sie erscheinen im Apps-Tab **oben in einer eigenen Gruppe**, mit ihrem App-Logo
(dunkle Kachel, Design-System), Rahmen und Kennzeichen in der Kategoriefarbe, Version und Rechner. **Kategorie, Name, Beschreibung, Version und Symbol
werden automatisch gesetzt**, die App wird ohne Zutun angelegt. Gesucht wird beim Start, danach alle 15 Minuten, nach jeder Änderung an einem
Rechner und auf Knopfdruck („Nach Eulenwerk-Apps suchen“). Entfernst du eine gefundene App, legt Appmaster sie nicht erneut an
(*Einstellungen → Entfernte Eulenwerk-Apps wieder zulassen*).

| Wo gesucht wird | Woran eine Eulenwerk-App erkannt wird |
|---|---|
| **Arbeitsrechner** (derzeit nur dieser PC): installierte Windows-Programme in der Registry (Hersteller *Daniel Ertl*), `%LOCALAPPDATA%\Programs`, `Program Files` | `eulenwerk.json` neben der .exe, **oder** Hersteller *Daniel Ertl* in den Dateieigenschaften, **oder** das Eulenwerk-Design-System in der Programmdatei |
| **Webserver**: Port 80/443 und – mit Agent ab 0.2.0 – die veröffentlichten Ports laufender Docker-Container | `data-eulenwerk="…"` im `<html>`-Tag (Design-System) **oder** `/eulenwerk.json` |

Die **Kategorie** kommt aus dem Manifest, sonst aus `data-eulenwerk` bzw. dem Design-System der App, sonst aus der Projekttabelle des Design-Systems.
Das **Symbol** ist das Programmsymbol der .exe bzw. `apple-touch-icon` / `icon.png` / `favicon.ico` der Webseite (SVG-Icons wandelt Appmaster selbst in ein PNG um, siehe `Services/SvgRenderer.cs`; unterstützt werden einfache Linien-/Flächenzeichnungen ohne Verläufe, Text und CSS-Klassen).

**Benötigte Ressourcen** liest Appmaster aus dem optionalen Manifest `eulenwerk.json` (alle Felder optional):

```json
{
  "name": "BildKI",
  "beschreibung": "Bilder aus deutschen Prompts",
  "kategorie": "kreativ",
  "version": "1.18.6",
  "ressourcen": [
    { "name": "Ollama", "ki": true, "pruefUrl": "http://{host}:11434/api/version", "container": "ollama" },
    { "name": "sd-server FLUX", "ki": true, "pruefUrl": "http://{host}:7860/", "container": "sd-flux", "konfliktgruppe": "GPU" }
  ]
}
```

`{host}` wird durch den Rechner ersetzt, auf dem die Ressource angelegt wird. Mit `"ki": true` landet sie auf dem **GPU-Rechner**, sonst auf dem Rechner der App.
Eine gleichnamige oder gleich geprüfte Ressource wird wiederverwendet, nichts doppelt angelegt. `container` steuert über den Agent des Rechners,
alternativ `startBefehl` / `stopBefehl`. Windows-Apps legen die Datei neben die .exe (oder als `<name>.eulenwerk.json`), Web-Apps liefern sie unter `/eulenwerk.json`.

### KI-Dienste auf GPU-Rechnern automatisch finden

Rechner der Kategorie **GPU-Rechner** durchsucht Appmaster nach KI-Diensten und legt sie als **Ressourcen** an (Konfliktgruppe „GPU <Rechner>“):
**Ollama** (`/api/tags`, Port 11434), **sd-server** / stable-diffusion.cpp (`/sdcpp/v1/capabilities`, Ports 7860–7879, 8000–8010, 8080–8090, mit Modellname)
und Docker-Container mit KI-Namen (sd, flux, pony, ollama, comfy, whisper …), auch gestoppte – dafür muss der Agent (ab 0.2.0) auf dem Rechner laufen.
Container-Dienste lassen sich dann über den Agent starten und stoppen. Vorhandene Ressourcen (gleicher Port, Name oder Container auf demselben Rechner)
werden nicht doppelt angelegt oder geändert. Auf diesem PC werden nur laufende Dienste erkannt und nur überwacht.

### Apps starten und beenden

- **Lokale Apps** (Programm, Skript, `.bat`, Verknüpfung) und **Web-Apps** (Adresse im Browser, optional mit lokalem Startbefehl).
- Vor dem Start prüft Appmaster alle benötigten Ressourcen und startet fehlende. Läuft eine Ressource nicht an, fragt Appmaster nach,
  ob die App trotzdem starten soll.
- Bei Web-Apps mit lokalem Server wartet Appmaster, bis der Server antwortet, bevor der Browser aufgeht.
- Der **Datenspeicherpfad** einer App wird ihr als Umgebungsvariable `APPMASTER_DATA` mitgegeben.
- Jede App hat eine **Kategorie** (Akzentfarbe nach Eulenwerk-Design).
- Beim Schließen von Appmaster, wenn Apps laufen, wählbar: nachfragen, laufen lassen oder alles beenden.

### Ressourcen überwachen und steuern

- **Überwachung:** Appmaster fragt periodisch die Prüf-Adresse ab (HTTP GET, jede Antwort unter 500 zählt als „läuft“).
  Das Intervall ist einstellbar (Standard 5 Sekunden).
- **Steuerung** je Ressource:
  - *Nur überwachen*
  - *Befehl:* Start- und Stoppbefehl, z. B. `docker start ollama` oder `ssh athene docker start ollama`
  - *Agent:* Docker-Container über den Appmaster-Agent auf einem Linux-Rechner starten und stoppen
- Die Anzeige zeigt, **welche laufenden Apps** eine Ressource gerade nutzen. Genutzte Ressourcen werden nicht ungefragt gestoppt.
- **Automatisch stoppen:** Ressourcen mit Befehlssteuerung werden beendet, sobald keine laufende App sie mehr braucht (optional).
- **Konfliktgruppen:** Ressourcen mit gleicher Gruppe (z. B. alle Bild-KI-Server auf einer GPU) laufen vermutlich nicht gleichzeitig.
  Appmaster warnt beim Start und bietet an, die andere zu stoppen, beide laufen zu lassen oder abzubrechen.

### Rechner

- **Dieser PC** (Adresse leer) und **entfernte Rechner** (Adresse oder IP).
- **Kategorien** (mehrere möglich): *GPU-Rechner* (bevorzugter Ausführort von KI-Diensten – neue KI-Ressourcen landen hier), *Arbeitsrechner*
  (Installationsort für Windows-Apps, hier sucht Appmaster automatisch nach Eulenwerk-Apps) und *Webserver* (Ort für Web-Anwendungen, hier sucht
  Appmaster automatisch nach Eulenwerk-Webanwendungen). Bei von Hand angelegten Apps wird der Rechner nach Kategorie vorgeschlagen.
- **Erreichbarkeit** per Ping und über den Agent.
- **Wake-on-LAN:** Rechner per Magic Packet aufwecken (MAC- und Broadcast-Adresse). Startest du eine App, deren Ressource auf einem
  schlafenden Rechner liegt, fragt Appmaster vorher nach.
- **Herunterfahren** über den Agent (nur wenn der Agent mit `--power` installiert ist), mit Warnung vor genutzten Ressourcen.
- **Leistungsanzeige** mit dem Agent: CPU, RAM, GPU-Auslastung und VRAM, Temperatur, Platten, Laufzeit, Anzahl laufender Container.
- **Fehlerhinweis in der Karte**, wenn der Agent keine Daten liefert (Token falsch, Tunnel-Fehler, Agent nicht erreichbar).
- **Docker Desktop bei Bedarf starten** (nur dieser PC): Läuft die Docker-Engine nicht, startet Appmaster Docker Desktop vor einem
  Docker-Befehl und wartet auf die Engine. Den Pfad der `Docker Desktop.exe` findet Appmaster selbst oder du trägst ihn ein.

### Agent und SSH-Tunnel

- **Linux-Agent** (`agent/appmaster-agent.py`): Leistungsdaten, Docker-Steuerung, optional Herunterfahren. Läuft per systemd,
  Authentifizierung per Token. Siehe [agent/README.md](agent/README.md).
- **PHP-Status** (`agent/webhosting/status.php`): rein lesende Variante für Webhosting (Speicherplatz, Last, PHP-Version).
- **SSH-Tunnel:** Für Server im Internet, deren Agent nur auf `127.0.0.1` lauscht, baut Appmaster den Tunnel (`ssh -L`) selbst auf,
  hält ihn am Leben und beendet ihn beim Schließen. Dafür sind SSH-Ziel, privater Schlüssel (OpenSSH-Format) und Ports einstellbar.

### Verschlüsselte Sicherung der Konfiguration

- Unter *Einstellungen* exportiert Appmaster die **gesamte Konfiguration** verschlüsselt (AES-256-GCM, Schlüssel per PBKDF2 aus deinem Passwort)
  als `appmaster-sicherung.enc` in den Konfigurationsordner.
- Enthalten sind Rechner, Ressourcen, Apps, Einstellungen, Design sowie die lokalen Daten (Agent-Token, Pfade von SSH-Schlüsseln und
  Docker Desktop). **Nicht enthalten** sind die privaten SSH-Schlüsseldateien selbst, nur ihre Pfade.
- Nach dem ersten Export hält Appmaster die Datei **automatisch aktuell**. Das Passwort liegt dafür mit Windows-DPAPI geschützt auf dem PC.
- **Wiederherstellen:** beim ersten Start einer Neuinstallation (Feld „Sicherungsdatei zum Wiederherstellen“) oder später unter
  *Einstellungen → Konfiguration aus Sicherung importieren*. Nicht erreichbare Server, Ressourcen und Apps werden trotzdem angelegt;
  du passt sie danach an oder löschst sie. Die bisherige `config.json` bleibt als `config.json.bak-…` erhalten.
- Das Passwort lässt sich nicht zurücksetzen. Ohne Passwort ist die Sicherung nicht lesbar.

## Installation

Ein fertiger **MSI-Installer** entsteht mit `installer\build.ps1` (siehe unten). Er installiert pro Benutzer nach
`%LOCALAPPDATA%\Programs\Appmaster` (ohne Administratorrechte), legt einen Startmenü-Eintrag an und bringt die Agent-Dateien mit
(*„Agent für Linux-Server / Webhosting installieren …“* öffnet den Ordner).

Beim ersten Start fragt Appmaster, **wo die Konfiguration liegen soll**: nur auf diesem PC, in der Dropbox (gemeinsam mit anderen PCs)
oder in einem eigenen Ordner. Hier lässt sich auch gleich eine Sicherung wiederherstellen.

## Wo liegen die Daten?

| Was | Wo | Hinweis |
|---|---|---|
| Rechner, Ressourcen, Apps, Einstellungen | `config.json` im gewählten Konfigurationsordner | wird zwischen PCs geteilt, wenn der Ordner in der Dropbox liegt |
| Verschlüsselte Sicherung | `appmaster-sicherung.enc` im Konfigurationsordner | siehe oben |
| Agent-Token | `%APPDATA%\Eulenwerk\Appmaster\tokens.json` | nur auf diesem PC, nie in der Dropbox |
| Pfade von SSH-Schlüssel und Docker Desktop | `tunnelkeys.json`, `dockerpfade.json` im selben lokalen Ordner | je PC verschieden |
| Passwort der Sicherung (DPAPI) | `sicherung.pw` im selben lokalen Ordner | nur dieser Windows-Benutzer auf diesem PC |
| Gewählter Speicherort, Design | `ort.txt`, `design.txt` im selben lokalen Ordner | |

## Sicherheit

- Der Linux-Agent spricht **unverschlüsseltes HTTP**. Im Heimnetz ist das vertretbar. Für Server im Internet den Agent nur auf `127.0.0.1`
  lauschen lassen und den **SSH-Tunnel** verwenden. Den Agent-Port nie ungeschützt ins Internet freigeben.
- Ohne gültigen Token antwortet der Agent nur auf `/health`. Mit `APPMASTER_CONTAINERS` lässt sich beschränken, welche Container
  Appmaster steuern darf.
- Der SSH-Tunnel läuft ohne Passwortabfrage (`BatchMode`): Der Schlüssel muss auf dem Server hinterlegt und ohne Passphrase bzw. im
  `ssh-agent` geladen sein. `.ppk`-Schlüssel (PuTTY) kann OpenSSH nicht lesen.

## Entwicklung

Voraussetzungen: .NET 8 SDK (Windows), für den Installer zusätzlich WiX 5 (`dotnet tool install --global wix --version 5.0.2`).

```powershell
dotnet build Appmaster                    # App bauen
dotnet run --project Appmaster            # App starten
.\installer\build.ps1                     # MSI bauen: installer\Appmaster-<Version>.msi
.\installer\build.ps1 -Installieren       # bauen und gleich installieren
```

Projektstruktur:

```
Appmaster/            WPF-App (Models, Services, ViewModels, Views, Themes)
  Services/           Steuerzentrale, Agent-Client, SshTunnel, DockerDesktop, KonfigStore, Sicherung
agent/                Linux-Agent, install.sh und PHP-Status für Webhosting
installer/            WiX-Quelle und build.ps1 für den MSI-Installer
```

Entwicklerhilfen (Aufrufe der `Appmaster.exe`):

| Aufruf | Wirkung |
|---|---|
| `--shot <ziel.png> [apps\|ressourcen\|rechner] [hell]` | rendert das Hauptfenster als Bild und beendet sich |
| `--icon <ziel.ico>` | schreibt das Logo als `.ico` |
| `--docker-test <ziel.txt>` | prüft Pfadsuche und Engine-Zustand von Docker Desktop |
| `--suche-test <ziel.txt> [port …]` | sucht auf diesem PC und auf localhost (angegebene Ports) nach Eulenwerk-Apps und schreibt die Funde |
| `--sicherung-test <ziel.txt>` | spielt Export, Automatik und Wiederherstellung in einem Temp-Ordner durch |

Die Umgebungsvariable `APPMASTER_LOKAL` lenkt den lokalen Datenordner um (für Tests), `APPMASTER_FOTO=<ordner>` speichert von
jedem Formular ein PNG.

## Lizenz

Siehe [LICENSE](LICENSE).
