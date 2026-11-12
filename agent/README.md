# Appmaster-Agent

Kleine Helfer, die Appmaster auf anderen Rechnern braucht, um sie zu überwachen und zu steuern.

| | Linux-Agent (`appmaster-agent.py`) | PHP-Status (`webhosting/status.php`) |
|---|---|---|
| Geeignet für | Athene, Strato vServer, jeder Linux-Rechner mit Root-Zugriff | Strato Webhosting (und anderes Shared-Hosting) |
| Zeigt | CPU, RAM, GPU (AMD/NVIDIA), Temperatur, Platten, Docker-Container, Laufzeit | Speicherplatz, Last, PHP-Version |
| Steuert | Docker-Container starten/stoppen/neustarten, optional Herunterfahren | nichts (nur lesen) |
| Braucht | Python 3 (Standardbibliothek), systemd | PHP, HTTPS |

Auf Shared-Webhosting sind dauerhaft laufende Programme und Prozesssteuerung in der Regel nicht erlaubt –
deshalb gibt es dort nur die lesende Variante. Auf einem vServer mit Root-Zugriff läuft der volle Agent.

## Linux-Agent installieren (Athene, vServer)

```bash
sudo ./install.sh                 # Standard: Port 8765, im ganzen Netz erreichbar, kein Herunterfahren
sudo ./install.sh --power         # erlaubt zusätzlich „Herunterfahren“ aus Appmaster (Agent läuft dann als root)
sudo ./install.sh --bind 127.0.0.1   # für vServer: nur lokal lauschen, davor HTTPS-Proxy oder SSH-Tunnel
```

Am Ende gibt das Skript den **Token** aus. In Appmaster beim Rechner eintragen:

- Überwachung/Steuerung: *Appmaster-Agent (Linux)*
- Agent-Adresse: `http://athene:8765`
- Token: der ausgegebene Wert

Einstellungen (erlaubte Container, Platten, Port) stehen in `/etc/appmaster-agent.env`; danach
`sudo systemctl restart appmaster-agent`. Der Benutzer, der `sudo` aufruft, muss in der Gruppe `docker` sein.

Test von Windows aus: `curl -H "X-Appmaster-Token: <token>" http://athene:8765/status`

### Sicherheit

- Der Agent spricht **HTTP ohne Verschlüsselung**. Im Heimnetz (Athene) ist das vertretbar; für einen vServer im Internet
  nur auf `127.0.0.1` lauschen lassen und per SSH-Tunnel (`ssh -L 8765:127.0.0.1:8765 …`) oder HTTPS-Reverse-Proxy erreichen.
  Port nicht ungeschützt ins Internet freigeben.
- Ohne gültigen Token antwortet der Agent nur auf `/health`.
- `APPMASTER_CONTAINERS` beschränkt, welche Container Appmaster steuern darf.

## Athene per Wake-on-LAN aufwecken

Das funktioniert nur, wenn Athene per **Kabel** im selben Netzwerk hängt und Wake-on-LAN eingerichtet ist:

1. Im BIOS/UEFI „Wake on LAN“ bzw. „Power on by PCI-E“ aktivieren.
2. Im System: `sudo ethtool -s <netzwerkkarte> wol g` (mit `ip link` den Namen finden). Dauerhaft z. B. mit NetworkManager:
   `nmcli connection modify <verbindung> 802-3-ethernet.wake-on-lan magic`.
3. In Appmaster beim Rechner die **MAC-Adresse** (`ip link`) und die Broadcast-Adresse (z. B. `192.168.1.255`) eintragen.

Appmaster fragt vor dem Aufwecken nach, wenn du eine App startest, deren Ressource auf dem schlafenden Rechner liegt.

## PHP-Status für Webhosting

1. `webhosting/status.php` öffnen und `TOKEN` durch einen langen Zufallswert ersetzen.
2. Datei hochladen, z. B. nach `https://deine-seite.de/appmaster/status.php`.
3. In Appmaster beim Rechner: *status.php (Webhosting)*, die volle Adresse und denselben Token eintragen.
