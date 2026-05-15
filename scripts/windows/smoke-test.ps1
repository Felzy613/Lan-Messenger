#Requires -Version 5.1
# Startup smoke test for LAN Messenger Windows
# Usage: smoke-test.ps1 -ArtifactPath <path-to-exe-installer>
# Exit 0: app launched and remained alive; Exit 1: crash or no-show
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactPath
)

$ErrorActionPreference = 'Stop'
$StartupWait = 12   # seconds to wait for startup
$AliveWait   = 18   # additional seconds for stability
$LogFile     = "smoke.log"

function Write-Log {
    param([string]$Msg)
    $line = "[$(Get-Date -Format 'HH:mm:ss')] $Msg"
    Write-Host $line
    Add-Content -Path $LogFile -Value $line -ErrorAction SilentlyContinue
}

# ── Install silently ────────────────────────────────────────────────────────
if (-not (Test-Path $ArtifactPath)) {
    Write-Log "::error::Artifact not found: $ArtifactPath"
    exit 1
}

Write-Log "Installing $ArtifactPath silently..."
$InstallLog = "$env:TEMP\LanMessenger-install.log"
$install = Start-Process -FilePath $ArtifactPath `
    -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$InstallLog" `
    -PassThru

# Wait up to 3 minutes for the installer to finish; kill it if it stalls
$InstallTimeoutMs = 180000
Write-Log "Waiting up to 3 minutes for installer to complete..."
if (-not $install.WaitForExit($InstallTimeoutMs)) {
    $install.Kill()
    Write-Log "::error::Installer did not complete within 3 minutes — killed"
    if (Test-Path $InstallLog) {
        Write-Log "--- Inno Setup install log (last 40 lines) ---"
        Get-Content $InstallLog -Tail 40 | Tee-Object -Append -FilePath $LogFile
    }
    exit 1
}

if ($install.ExitCode -ne 0) {
    Write-Log "::error::Installer exited with code $($install.ExitCode)"
    if (Test-Path $InstallLog) {
        Write-Log "--- Inno Setup install log (last 40 lines) ---"
        Get-Content $InstallLog -Tail 40 | Tee-Object -Append -FilePath $LogFile
    }
    exit 1
}
Write-Log "Installer completed (exit code 0)"

# ── Locate the installed executable ─────────────────────────────────────────
$SearchPaths = @(
    "$env:LOCALAPPDATA\Programs\LanMessenger\LanMessenger.exe",
    "$env:ProgramFiles\LanMessenger\LanMessenger.exe",
    "${env:ProgramFiles(x86)}\LanMessenger\LanMessenger.exe"
)
$ExePath = $SearchPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $ExePath) {
    Write-Log "Searching recursively in LOCALAPPDATA\Programs..."
    $ExePath = Get-ChildItem "$env:LOCALAPPDATA\Programs" -Filter "LanMessenger.exe" `
        -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $ExePath) {
    Write-Log "::error::LanMessenger.exe not found after installation"
    exit 1
}
Write-Log "Launching $ExePath..."

# ── Launch the app ──────────────────────────────────────────────────────────
$proc = Start-Process -FilePath $ExePath -PassThru

Write-Log "Waiting ${StartupWait}s for startup..."
Start-Sleep -Seconds $StartupWait

if ($proc.HasExited) {
    Write-Log "::error::LanMessenger exited early (exit code: $($proc.ExitCode))"

    # Collect Windows Event Log errors from the last 5 minutes
    Write-Log "--- Windows Event Log (Application errors, last 5 min) ---"
    $since = (Get-Date).AddMinutes(-5)
    Get-WinEvent -FilterHashtable @{
        LogName   = 'Application'
        StartTime = $since
        Level     = 1, 2   # Critical, Error
    } -ErrorAction SilentlyContinue | Format-List | Tee-Object -Append -FilePath $LogFile

    # Collect crash dumps if present
    $DumpDir = "$env:LOCALAPPDATA\CrashDumps"
    if (Test-Path $DumpDir) {
        $dumps = Get-ChildItem $DumpDir -Filter "LanMessenger*" -ErrorAction SilentlyContinue
        if ($dumps) {
            Write-Log "Crash dumps found: $($dumps.Name -join ', ')"
        }
    }
    exit 1
}

Write-Log "✓ Process alive (PID=$($proc.Id)) — stability check for ${AliveWait}s..."
Start-Sleep -Seconds $AliveWait

$proc.Refresh()
if ($proc.HasExited) {
    Write-Log "::error::LanMessenger (PID=$($proc.Id)) died during stability window (exit code: $($proc.ExitCode))"
    exit 1
}

# ── Graceful shutdown ───────────────────────────────────────────────────────
Write-Log "✓ Smoke test passed — app stable for $($StartupWait + $AliveWait)s. Shutting down..."
$proc.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 3
if (-not $proc.HasExited) {
    $proc.Kill()
}
exit 0
