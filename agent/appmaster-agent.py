#!/usr/bin/env python3
"""Appmaster-Agent – kleiner HTTP-Dienst für Linux-Rechner (Athene, vServer).

Nur Python-Standardbibliothek. Liefert Leistungsdaten (CPU, RAM, GPU, Temperatur, Platten, Docker)
und startet/stoppt Docker-Container. Jede Anfrage braucht den Header  X-Appmaster-Token.

Einstellungen über Umgebungsvariablen (siehe install.sh / README.md):
  APPMASTER_TOKEN        Pflicht. Geheimer Zugangsschlüssel (ohne ihn startet der Agent nicht).
  APPMASTER_PORT         Standard 8765
  APPMASTER_BIND         Standard 0.0.0.0 (für vServer: 127.0.0.1 + HTTPS-Proxy / SSH-Tunnel)
  APPMASTER_CONTAINERS   Komma-Liste erlaubter Container; leer = alle
  APPMASTER_DISKS        Komma-Liste der Pfade für den Speicherplatz, Standard "/"
  APPMASTER_ALLOW_POWER  "1" erlaubt POST /power/shutdown (Standard aus)
"""
import glob
import hmac
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

VERSION = "0.2.0"
TOKEN = os.environ.get("APPMASTER_TOKEN", "")
PORT = int(os.environ.get("APPMASTER_PORT", "8765"))
BIND = os.environ.get("APPMASTER_BIND", "0.0.0.0")
CONTAINERS = [c.strip() for c in os.environ.get("APPMASTER_CONTAINERS", "").split(",") if c.strip()]
DISKS = [d.strip() for d in os.environ.get("APPMASTER_DISKS", "/").split(",") if d.strip()]
ALLOW_POWER = os.environ.get("APPMASTER_ALLOW_POWER", "0") == "1"
NAME_OK = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")

_cpu = {"percent": None}


def _lies(pfad, standard=None):
    try:
        with open(pfad, encoding="utf-8", errors="replace") as f:
            return f.read().strip()
    except OSError:
        return standard


def _cpu_messen():
    """Misst die CPU-Auslastung im Hintergrund alle 2 s aus /proc/stat."""
    def werte():
        teile = _lies("/proc/stat", "cpu 0 0 0 0").splitlines()[0].split()[1:]
        zahlen = [int(x) for x in teile]
        leerlauf = zahlen[3] + (zahlen[4] if len(zahlen) > 4 else 0)
        return sum(zahlen), leerlauf

    gesamt0, leer0 = werte()
    while True:
        time.sleep(2)
        gesamt1, leer1 = werte()
        d = gesamt1 - gesamt0
        _cpu["percent"] = round(100.0 * (d - (leer1 - leer0)) / d, 1) if d > 0 else 0.0
        gesamt0, leer0 = gesamt1, leer1


def _ram():
    info = {}
    for zeile in (_lies("/proc/meminfo", "") or "").splitlines():
        k, _, v = zeile.partition(":")
        try:
            info[k] = int(v.split()[0]) / 1024.0  # MB
        except (ValueError, IndexError):
            pass
    gesamt = info.get("MemTotal", 0)
    frei = info.get("MemAvailable", info.get("MemFree", 0))
    return round(gesamt, 0), round(gesamt - frei, 0)


def _temps():
    ergebnis = {}
    for hw in glob.glob("/sys/class/hwmon/hwmon*"):
        name = _lies(os.path.join(hw, "name"), os.path.basename(hw))
        werte = []
        for t in glob.glob(os.path.join(hw, "temp*_input")):
            roh = _lies(t)
            if roh and roh.lstrip("-").isdigit():
                werte.append(int(roh) / 1000.0)
        if werte:
            ergebnis[name] = round(max(werte), 1)
    return ergebnis


def _gpus():
    liste = []
    # AMD (amdgpu): Auslastung und Speicher stehen in sysfs
    for dev in sorted(glob.glob("/sys/class/drm/card[0-9]/device")):
        busy = _lies(os.path.join(dev, "gpu_busy_percent"))
        if busy is None:
            continue
        def mb(datei):
            v = _lies(os.path.join(dev, datei))
            return round(int(v) / 1048576.0, 0) if v and v.isdigit() else None
        temp = None
        for t in glob.glob(os.path.join(dev, "hwmon", "hwmon*", "temp1_input")):
            roh = _lies(t)
            if roh and roh.isdigit():
                temp = int(roh) / 1000.0
        liste.append({
            "name": _lies(os.path.join(dev, "product_name")) or "AMD GPU (" + os.path.basename(os.path.dirname(dev)) + ")",
            "busy_percent": float(busy) if busy.replace(".", "", 1).isdigit() else None,
            "vram_used_mb": mb("mem_info_vram_used"), "vram_total_mb": mb("mem_info_vram_total"),
            "gtt_used_mb": mb("mem_info_gtt_used"), "gtt_total_mb": mb("mem_info_gtt_total"),
            "temp": temp,
        })
    # NVIDIA: nvidia-smi, falls vorhanden
    if shutil.which("nvidia-smi"):
        try:
            aus = subprocess.run(
                ["nvidia-smi", "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu",
                 "--format=csv,noheader,nounits"], capture_output=True, text=True, timeout=5).stdout
            for zeile in aus.strip().splitlines():
                n, u, mu, mt, t = [x.strip() for x in zeile.split(",")]
                liste.append({"name": n, "busy_percent": float(u), "vram_used_mb": float(mu), "vram_total_mb": float(mt), "temp": float(t)})
        except Exception:
            pass
    return liste


def _platten():
    ergebnis = []
    for pfad in DISKS:
        try:
            u = shutil.disk_usage(pfad)
            ergebnis.append({"pfad": pfad, "total_gb": round(u.total / 1e9, 1), "used_gb": round(u.used / 1e9, 1)})
        except OSError:
            pass
    return ergebnis


def _docker():
    if not shutil.which("docker"):
        return []
    try:
        aus = subprocess.run(["docker", "ps", "-a", "--format", "{{.Names}}\t{{.State}}\t{{.Status}}\t{{.Ports}}"],
                             capture_output=True, text=True, timeout=10).stdout
    except Exception:
        return []
    liste = []
    for zeile in aus.strip().splitlines():
        teile = zeile.split("\t")
        if len(teile) >= 3 and (not CONTAINERS or teile[0] in CONTAINERS):
            # Veröffentlichte TCP-Ports (z. B. "0.0.0.0:8080->80/tcp"); Appmaster sucht dort nach Eulenwerk-Webanwendungen.
            ports = sorted({int(p) for p in re.findall(r":(\d+)->\d+/tcp", teile[3] if len(teile) > 3 else "")})
            liste.append({"name": teile[0], "state": teile[1], "status": teile[2], "ports": ports})
    return liste


def _web_hosts():
    """Domainnamen aus nginx-/Apache-Konfigurationen des Servers (Appmaster sucht dort nach Eulenwerk-Webanwendungen)."""
    hosts = set()
    muster = ("/etc/nginx/sites-enabled/*", "/etc/nginx/conf.d/*.conf", "/etc/apache2/sites-enabled/*")
    dom = re.compile(r"^[A-Za-z0-9][A-Za-z0-9.-]*\.[A-Za-z]{2,}$")
    for m in muster:
        for pfad in glob.glob(m):
            try:
                with open(pfad, encoding="utf-8", errors="ignore") as f:
                    text = f.read()
            except OSError:
                continue
            namen = [n for z in re.findall(r"^\s*server_name\s+([^;]+);", text, re.M) for n in z.split()]
            namen += re.findall(r"^\s*ServerName\s+(\S+)", text, re.M | re.I)
            hosts.update(n.lower() for n in namen if dom.match(n))
    return sorted(hosts)


def status():
    gesamt, belegt = _ram()
    try:
        last = list(os.getloadavg())
    except (OSError, AttributeError):
        last = None
    up = _lies("/proc/uptime", "0").split()[0]
    return {
        "dienst": "appmaster-agent", "version": VERSION, "host": socket.gethostname(),
        "cpu_percent": _cpu["percent"], "load": last,
        "mem_total_mb": gesamt, "mem_used_mb": belegt, "uptime_s": float(up),
        "temps": _temps(), "gpus": _gpus(), "disks": _platten(), "docker": _docker(), "web_hosts": _web_hosts(),
        "power_allowed": ALLOW_POWER, "read_only": False,
    }


class Handler(BaseHTTPRequestHandler):
    server_version = "appmaster-agent/" + VERSION

    def _antwort(self, code, daten):
        roh = json.dumps(daten).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(roh)))
        self.end_headers()
        self.wfile.write(roh)

    def _erlaubt(self):
        gesendet = self.headers.get("X-Appmaster-Token", "")
        return hmac.compare_digest(gesendet.encode("utf-8"), TOKEN.encode("utf-8"))

    def do_GET(self):
        if self.path == "/health":   # ohne Token: nur "ich bin da"
            return self._antwort(200, {"dienst": "appmaster-agent", "version": VERSION})
        if not self._erlaubt():
            return self._antwort(401, {"fehler": "Token fehlt oder ist falsch"})
        if self.path == "/status":
            return self._antwort(200, status())
        self._antwort(404, {"fehler": "unbekannter Pfad"})

    def do_POST(self):
        if not self._erlaubt():
            return self._antwort(401, {"fehler": "Token fehlt oder ist falsch"})
        teile = self.path.strip("/").split("/")
        if len(teile) == 3 and teile[0] == "docker" and teile[2] in ("start", "stop", "restart"):
            name = teile[1]
            if not NAME_OK.match(name) or (CONTAINERS and name not in CONTAINERS):
                return self._antwort(403, {"fehler": "Container nicht erlaubt"})
            try:
                r = subprocess.run(["docker", teile[2], name], capture_output=True, text=True, timeout=120)
            except Exception as ex:
                return self._antwort(500, {"fehler": str(ex)})
            if r.returncode != 0:
                return self._antwort(500, {"fehler": (r.stderr or r.stdout).strip()})
            return self._antwort(200, {"ok": True, "container": name, "aktion": teile[2]})
        if teile == ["power", "shutdown"]:
            if not ALLOW_POWER:
                return self._antwort(403, {"fehler": "Herunterfahren ist am Agent nicht erlaubt (APPMASTER_ALLOW_POWER=1)"})
            subprocess.Popen(["systemctl", "poweroff"])
            return self._antwort(200, {"ok": True})
        self._antwort(404, {"fehler": "unbekannter Pfad"})

    def log_message(self, fmt, *args):
        sys.stderr.write("%s %s\n" % (self.address_string(), fmt % args))


def main():
    if len(TOKEN) < 16:
        sys.exit("APPMASTER_TOKEN fehlt oder ist zu kurz (mindestens 16 Zeichen). Siehe README.md.")
    threading.Thread(target=_cpu_messen, daemon=True).start()
    ThreadingHTTPServer((BIND, PORT), Handler).serve_forever()


if __name__ == "__main__":
    main()
