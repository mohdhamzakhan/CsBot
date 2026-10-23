#!/usr/bin/env bash
# Puts CsBot on the internet over HTTPS (free certificate) at your own domain name.
# Run:  sudo bash install-caddy.sh yourname.duckdns.org
set -euo pipefail
[ "$EUID" -eq 0 ] || { echo "Run with sudo"; exit 1; }
DOMAIN="${1:-}"
[ -n "$DOMAIN" ] || { echo "Usage: sudo bash install-caddy.sh yourname.duckdns.org"; exit 1; }
grep -Eq '^CS_APP_PASSWORD=.{12,}' /etc/csbot.env || { echo "Set CS_APP_PASSWORD (12+ characters, ideally 20+) in /etc/csbot.env first. The site can use your API keys."; exit 1; }

apt-get install -y debian-keyring debian-archive-keyring apt-transport-https curl gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor --yes -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' > /etc/apt/sources.list.d/caddy-stable.list
apt-get update -y
apt-get install -y caddy

cat > /etc/caddy/Caddyfile <<CADDY
$DOMAIN {
    reverse_proxy 127.0.0.1:5080
}
CADDY

# Oracle's Ubuntu images block everything except SSH at the OS firewall; open web ports.
for p in 80 443; do
  iptables -C INPUT -m state --state NEW -p tcp --dport "$p" -j ACCEPT 2>/dev/null || iptables -I INPUT 1 -m state --state NEW -p tcp --dport "$p" -j ACCEPT
done
netfilter-persistent save 2>/dev/null || true

systemctl enable caddy
systemctl restart caddy
echo "Done. Also make sure ports 80 and 443 are allowed in the Oracle console (VCN security list). Then open https://$DOMAIN"
