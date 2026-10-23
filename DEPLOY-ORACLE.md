# Running CsBot on Oracle Cloud (Always Free)

Why: the server runs 24/7, so paper trading and charts keep going when your PC is off.
Everything below was checked as far as possible without an Oracle account; the Oracle console screens may be labelled slightly differently.

## Read first (5 minutes that can save you money)
1. **Region matters.** CoinSwitch is an Indian exchange and may refuse connections from foreign IP addresses. Oracle's free resources only exist in your *home region* (chosen at sign-up, cannot be changed). Check it: Oracle console, top bar. Mumbai or Hyderabad is ideal. After creating the server (step 2) run `curl -s https://coinswitch.co/trade/api/v2/time` on it. If you get a time value back, it can reach CoinSwitch.
2. **API key IP limits.** If your CoinSwitch API key is restricted to certain IP addresses, add the server's public IP to it (or the server will get 401 errors).
3. **This site can place real orders with your keys.** The app now has a login password and refuses to start on a public address without one. Never open port 5080 to the internet; the guide uses SSH or HTTPS in front of it.
4. **Paper-trader state lives in memory.** If the server restarts or you deploy an update, a running paper/live trader stops and forgets its position. For live trading that means an open position nobody is managing. Use the server for charts, backtests and paper trading first; do not run live money unattended yet.
5. Oracle may reclaim Always Free VMs that stay almost idle for a week. Upgrading the account to "Pay as you go" (you still pay nothing inside the free limits) avoids that; check Oracle's current policy.

## 1. Create the server
Oracle console > Compute > Instances > Create instance
- Image: **Canonical Ubuntu 22.04** (or 24.04)
- Shape: **VM.Standard.A1.Flex** (Ampere ARM, "Always Free eligible", 1 OCPU / 6 GB is plenty). If you get "out of capacity", try again later or pick another availability domain; the alternative is **VM.Standard.E2.1.Micro** (AMD, 1 GB RAM, also enough).
- Networking: put it in a public subnet and **assign a public IPv4 address**.
- SSH keys: choose "Generate a key pair" and **download the private key** (keep it safe).
Note the public IP when it is running.

On Windows, make the key file private once (PowerShell), otherwise ssh refuses it:
```powershell
icacls C:\path\oracle.key /inheritance:r
icacls C:\path\oracle.key /grant:r "$($env:USERNAME):R"
ssh -i C:\path\oracle.key ubuntu@<public-ip>      # test login, then type exit
```

## 2. Prepare the server (once)
From the folder that contains the `deploy` folder, on your PC:
```powershell
scp -i C:\path\oracle.key -r deploy ubuntu@<public-ip>:~/deploy
ssh -i C:\path\oracle.key ubuntu@<public-ip> "sudo bash ~/deploy/setup-server.sh"
```
Then put your keys and a password in the settings file:
```powershell
ssh -i C:\path\oracle.key ubuntu@<public-ip>
sudo nano /etc/csbot.env
```
Fill in `CS_API_KEY`, `CS_SECRET_KEY` and `CS_APP_PASSWORD` (long, 20+ characters). Save with Ctrl+O, Enter, exit with Ctrl+X. Leave `CS_LIVE_ENABLED` commented out. Check the server can reach CoinSwitch: `curl -s https://coinswitch.co/trade/api/v2/time`.

## 3. Put the app on the server (and every time you update it)
```powershell
cd deploy
.\deploy.ps1 -Server <public-ip> -Key C:\path\oracle.key
```
It builds the app, uploads it and restarts the service. Candles and your saved scripts on the server are kept between updates.

## 4a. Easiest and safest access: an SSH tunnel (no website exposed)
```powershell
ssh -i C:\path\oracle.key -L 5080:127.0.0.1:5080 ubuntu@<public-ip>
```
Leave that window open and browse to http://localhost:5080 (log in with your password). Nothing is open to the internet except SSH.

## 4b. A real website with HTTPS (optional)
1. Get a free name, e.g. at duckdns.org, and point it at the server's public IP.
2. Oracle console > Networking > your VCN > subnet > security list > add ingress rules (source 0.0.0.0/0, TCP) for ports **80** and **443**. Do NOT open 5080.
3. On the server: `sudo bash ~/deploy/install-caddy.sh yourname.duckdns.org`
   (installs Caddy, gets a free certificate, opens the OS firewall for 80/443, and forwards to the app. It refuses to run if no app password is set.)
4. Browse to https://yourname.duckdns.org and log in.
Wrong passwords are locked out for 10 minutes after 5 tries, and only your computer's own browser (via the tunnel) can change API keys through the app; on the server you edit `/etc/csbot.env`.

## Everyday commands (on the server)
```bash
sudo systemctl status csbot          # is it running?
journalctl -u csbot -f               # live log (Ctrl+C to stop)
sudo systemctl restart csbot         # after editing /etc/csbot.env
```
Back up `/opt/csbot/data/scripts` if you write many scripts (copy with scp).

## If something does not work
- Page will not load over HTTPS: check ports 80/443 in the Oracle security list AND `sudo iptables -L INPUT -n | head`, then `journalctl -u caddy -n 50`.
- "Please log in" loop: the password in `/etc/csbot.env` has a typo or a space; restart the service after fixing it.
- 401 from CoinSwitch only on the server: API key IP restriction, or the region issue in "Read first".
- The app logs "REFUSING TO START": you set a public CS_URL without CS_APP_PASSWORD.
