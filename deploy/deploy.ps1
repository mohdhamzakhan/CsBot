
# ============================================================
# CsBot - Oracle Cloud Deployment
# ============================================================
#
# Run from PowerShell:
#
# Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
#
# .\deploy.ps1 -Server 130.210.56.120 `
#   -Key "$env:USERPROFILE\.ssh\oracle_rsa"
#
# Optional:
#   -User ubuntu
#   -Project "C:\Users\mohd-\source\repos\CsBot\CsBot"
#
# Prerequisites:
# - .NET SDK installed on Windows
# - SSH/SCP client installed on Windows
# - Oracle VM SSH public-key authentication working
# - Server setup completed (setup-server.sh)
# ============================================================

param(
    [Parameter(Mandatory = $true)]
    [string]$Server,

    [Parameter(Mandatory = $true)]
    [string]$Key,

    [string]$User = "ubuntu",

    [string]$Project = (Join-Path $PSScriptRoot "..\CsBot")
)

$ErrorActionPreference = "Stop"

$target = "$User@$Server"
$publishDir = Join-Path $env:TEMP "csbot-publish"
$remoteTemp = "/tmp/csbot-publish"
$remoteApp = "/opt/csbot"
$sshOptions = @(
    "-i", $Key,
    "-o", "IdentitiesOnly=yes",
    "-o", "ConnectTimeout=15",
    "-o", "ServerAliveInterval=15"
)

function Assert-ExitCode {
    param([string]$Message)

    if ($LASTEXITCODE -ne 0) {
        throw "$Message (exit code: $LASTEXITCODE)"
    }
}

try {
    # --------------------------------------------------------
    # 0. Validate local prerequisites
    # --------------------------------------------------------
    Write-Host "`n[0/5] Checking prerequisites..." -ForegroundColor Cyan

    if (-not (Test-Path -LiteralPath $Key -PathType Leaf)) {
        throw "Private key not found: $Key"
    }

    if (-not (Test-Path -LiteralPath $Project -PathType Container)) {
        throw "Project directory not found: $Project"
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET SDK is not installed or dotnet is not on PATH."
    }

    if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) {
        throw "OpenSSH client (ssh.exe) is not available."
    }

    if (-not (Get-Command scp -ErrorAction SilentlyContinue)) {
        throw "OpenSSH client (scp.exe) is not available."
    }

    Write-Host "Project: $Project" -ForegroundColor Gray
    Write-Host "Server:  $target" -ForegroundColor Gray
    Write-Host "Key:     $Key" -ForegroundColor Gray

    # --------------------------------------------------------
    # 1. Check the private key
    # --------------------------------------------------------
    Write-Host "`n[1/5] Checking SSH private key..." -ForegroundColor Cyan

    & ssh-keygen -lf $Key
    Assert-ExitCode "Could not read the SSH key. Verify its path and format."

    # --------------------------------------------------------
    # 2. Test SSH authentication
    # --------------------------------------------------------
    Write-Host "`n[2/5] Testing SSH authentication..." -ForegroundColor Cyan

    & ssh @sshOptions $target "echo SSH_AUTHENTICATION_OK"
    if ($LASTEXITCODE -ne 0) {
        throw @"
SSH authentication failed for $target.

Check that:
1. The key is the same key configured when creating the Oracle VM.
2. The username is correct (usually ubuntu for Ubuntu images).
3. The VM public IP is correct.
4. The VM's authorized public key matches this private key.

Try this diagnostic command:
ssh -v -o IdentitiesOnly=yes -i "$Key" $target

Do not disable SSH authentication or expose your private key.
"@
    }

    # --------------------------------------------------------
    # 3. Publish the .NET application
    # --------------------------------------------------------
    Write-Host "`n[3/5] Publishing .NET application..." -ForegroundColor Cyan

    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    & dotnet publish $Project -c Release -o $publishDir
    Assert-ExitCode "dotnet publish failed."

    if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
        throw "Publish output directory was not created."
    }

    $dll = Get-ChildItem -LiteralPath $publishDir -Filter "*.dll" |
        Where-Object { $_.Name -eq "CsBot.dll" } |
        Select-Object -First 1

    if (-not $dll) {
        throw "CsBot.dll not found in publish output. Verify the project's assembly name."
    }

    Write-Host "Publish completed: $publishDir" -ForegroundColor Green

    # --------------------------------------------------------
    # 4. Upload published files
    # --------------------------------------------------------
    Write-Host "`n[4/5] Uploading published files..." -ForegroundColor Cyan

    & ssh @sshOptions $target "rm -rf $remoteTemp && mkdir -p $remoteTemp"
    Assert-ExitCode "Could not prepare the remote temporary directory."

    & scp @sshOptions -r "$publishDir\." "${target}:${remoteTemp}/"
    Assert-ExitCode "SCP upload failed."

    Write-Host "Upload completed." -ForegroundColor Green

    # --------------------------------------------------------
    # 5. Install and restart the application
    # --------------------------------------------------------
    Write-Host "`n[5/5] Installing and restarting CsBot..." -ForegroundColor Cyan

    # Assumes setup-server.sh already created:
    # /opt/csbot, the csbot service account, and systemd service.
    #
    # Published files are overlaid rather than deleting the
    # whole app directory, to avoid removing unrelated data.
    $remoteScript = @'
set -eu

APP="/opt/csbot"
STAGE="/tmp/csbot-publish"

test -d "$APP"
test -f "$STAGE/CsBot.dll"
id csbot >/dev/null 2>&1

# Copy application files without deleting the whole app directory.
sudo cp -r "$STAGE"/. "$APP"/
sudo chown -R csbot:csbot "$APP"

# Restart and verify the systemd service.
sudo systemctl restart csbot
sleep 3

if ! sudo systemctl is-active --quiet csbot; then
    sudo journalctl -u csbot -n 60 --no-pager
    exit 1
fi

echo "CsBot service is active."
'@

    & ssh @sshOptions $target $remoteScript
    Assert-ExitCode "Installation or service verification failed."

    Write-Host "`n========================================" -ForegroundColor Green
    Write-Host " CsBot deployment completed successfully" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green

    Write-Host "`nTo check the service later:" -ForegroundColor Cyan
    Write-Host "ssh -i `"$Key`" $target `"sudo systemctl status csbot --no-pager`""

    Write-Host "`nTo view recent logs:" -ForegroundColor Cyan
    Write-Host "ssh -i `"$Key`" $target `"sudo journalctl -u csbot -n 80 --no-pager`""

    Write-Host "`nUse an SSH tunnel to access the application securely." -ForegroundColor Yellow
}
catch {
    Write-Host "`nDEPLOYMENT FAILED:" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
