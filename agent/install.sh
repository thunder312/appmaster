#!/usr/bin/env bash
# Installiert den Appmaster-Agent als systemd-Dienst.
# Aufruf (als root):   sudo ./install.sh [--power] [--bind 127.0.0.1] [--port 8765]
#   --power   erlaubt dem Agent, den Rechner herunterzufahren (Appmaster: „Herunterfahren“)
#   --bind    Adresse, auf der der Agent lauscht (vServer: 127.0.0.1 + HTTPS-Proxy/SSH-Tunnel)
set -euo pipefail

[ "$(id -u)" -eq 0 ] || { echo "Bitte mit sudo ausführen."; exit 1; }

POWER=0; BIND=0.0.0.0; PORT=8765
while [ $# -gt 0 ]; do
  case "$1" in
    --power) POWER=1 ;;
    --bind) BIND="$2"; shift ;;
    --port) PORT="$2"; shift ;;
    *) echo "Unbekannte Option: $1"; exit 1 ;;
  esac
  shift
done

HIER="$(cd "$(dirname "$0")" && pwd)"
BENUTZER="${SUDO_USER:-root}"          # braucht Zugriff auf Docker (Gruppe „docker“)
ZIEL=/opt/appmaster-agent
ENV=/etc/appmaster-agent.env

install -d "$ZIEL"
install -m 755 "$HIER/appmaster-agent.py" "$ZIEL/appmaster-agent.py"

if [ ! -f "$ENV" ]; then
  TOKEN="$(head -c 24 /dev/urandom | base64 | tr -d '/+=' | head -c 32)"
  umask 077
  cat > "$ENV" <<EOF
APPMASTER_TOKEN=$TOKEN
APPMASTER_PORT=$PORT
APPMASTER_BIND=$BIND
APPMASTER_ALLOW_POWER=$POWER
# APPMASTER_CONTAINERS=ollama,sd-flux
# APPMASTER_DISKS=/,/home
EOF
  chmod 600 "$ENV"
  NEU=1
else
  NEU=0
fi

# Herunterfahren aus einem Dienst braucht root; sonst läuft der Agent als normaler Benutzer.
if [ "$POWER" -eq 1 ]; then DIENSTBENUTZER=root; else DIENSTBENUTZER="$BENUTZER"; fi

cat > /etc/systemd/system/appmaster-agent.service <<EOF
[Unit]
Description=Appmaster-Agent
After=network-online.target docker.service
Wants=network-online.target

[Service]
User=$DIENSTBENUTZER
EnvironmentFile=$ENV
ExecStart=/usr/bin/python3 $ZIEL/appmaster-agent.py
Restart=on-failure

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now appmaster-agent
echo
echo "Appmaster-Agent läuft auf Port $PORT."
if [ "$NEU" -eq 1 ]; then
  echo "Token (in Appmaster beim Rechner eintragen):"
  grep ^APPMASTER_TOKEN= "$ENV" | cut -d= -f2
else
  echo "Vorhandene Einstellungen in $ENV wurden beibehalten."
fi
