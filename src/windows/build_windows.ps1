$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root

if (-not (Test-Path ".venv")) {
    py -3 -m venv .venv
}

& ".\.venv\Scripts\python.exe" -m pip install --upgrade pip
& ".\.venv\Scripts\python.exe" -m pip install -r requirements.txt
& ".\.venv\Scripts\pyinstaller.exe" --clean --noconfirm LanMessenger.spec

$IsccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)

$Iscc = $IsccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $Iscc) {
    throw "Inno Setup 6 was not found. Install it, then rerun this script."
}

& $Iscc ".\LanMessenger.iss"

Write-Host ""
Write-Host "Build complete."
Write-Host "App folder: dist\LanMessenger"
Write-Host "Installer: dist-installer\LanMessengerSetup.exe"
