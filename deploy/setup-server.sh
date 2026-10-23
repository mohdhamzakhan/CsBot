#!/usr/bin/env bash
# One-time server preparation for Ubuntu (Oracle Cloud). Run:  sudo bash setup-server.sh
set -euo pipefail
[ "$EUID" -eq 0 ] || { echo "Run with sudo:  sudo bash setup-server.sh"; exit 1; }
cd "$(dirname "$0")"

apt-get update -y
apt-get install -y aspnetcore-runtime-8.0 unzip

id csbot &>/dev/null || useradd --system --create-home --shell /usr/sbin/nologin csbot
mkdir -p /opt/csbot
chown csbot:csbot /opt/csbot

if [ ! -f /etc/csbot.env ]; then cp csbot.env.example /etc/csbot.env; echo "Created /etc/csbot.env - edit it next."; fi
chown root:root /etc/csbot.env
chmod 600 /etc/csbot.env

cp csbot.service /etc/systemd/system/csbot.service
systemctl daemon-reload
systemctl enable csbot

echo
echo "Done. Next:  sudo nano /etc/csbot.env   (add your keys and a password)"
echo "Then deploy the app from your PC with deploy.ps1, which also starts the service."
