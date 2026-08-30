[CmdletBinding()]
param(
    [string]$PackagePath = "$PSScriptRoot\bin\Release\Publish\win-x64\Standalone",
    [string]$InstallPath = "$env:LOCALAPPDATA\LGSTrayBattery"
)

$ErrorActionPreference = 'Stop'

$package = [System.IO.Path]::GetFullPath($PackagePath)
$install = [System.IO.Path]::GetFullPath($InstallPath)
$defaultInstall = [System.IO.Path]::GetFullPath("$env:LOCALAPPDATA\LGSTrayBattery")
$staging = [System.IO.Path]::GetFullPath("$env:LOCALAPPDATA\LGSTrayBattery.installing")
$backup = [System.IO.Path]::GetFullPath("$env:LOCALAPPDATA\LGSTrayBattery.previous")

if (-not (Test-Path -LiteralPath "$package\LGSTray.exe" -PathType Leaf)) {
    throw "LGSTray.exe was not found in package: $package"
}

if ($install -ne $defaultInstall) {
    throw "For safety, InstallPath must resolve to $defaultInstall"
}

$settingsPath = Join-Path $install 'appsettings.toml'
$savedSettings = if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
    Get-Content -LiteralPath $settingsPath -Raw
} else {
    $null
}

function Remove-DirectoryWithRetry([string]$Path) {
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            if (Test-Path -LiteralPath $Path) {
                Remove-Item -LiteralPath $Path -Recurse -Force
            }
            return
        } catch {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Milliseconds 300
        }
    }
}

function Move-DirectoryWithRetry([string]$Source, [string]$Destination) {
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            Move-Item -LiteralPath $Source -Destination $Destination
            return
        } catch {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Milliseconds 300
        }
    }
}

Remove-DirectoryWithRetry $staging
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -Path "$package\*" -Destination $staging -Recurse -Force

if ($null -ne $savedSettings) {
    Set-Content -LiteralPath (Join-Path $staging 'appsettings.toml') -Value $savedSettings -NoNewline
}

$processes = Get-Process -Name 'LGSTray', 'LGSTrayHID' -ErrorAction SilentlyContinue
if ($processes) {
    $processes | Stop-Process -Force
    $processes | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

Remove-DirectoryWithRetry $backup

try {
    if (Test-Path -LiteralPath $install) {
        Move-DirectoryWithRetry $install $backup
    }
    Move-DirectoryWithRetry $staging $install
} catch {
    if (-not (Test-Path -LiteralPath $install) -and (Test-Path -LiteralPath $backup)) {
        Move-DirectoryWithRetry $backup $install
    }
    throw
}

Remove-DirectoryWithRetry $backup

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$executable = Join-Path $install 'LGSTray.exe'
New-ItemProperty -Path $runKey -Name 'LGSTrayGUI' -Value "`"$executable`"" -PropertyType String -Force | Out-Null

Start-Process -FilePath $executable -WorkingDirectory $install -WindowStyle Hidden
Write-Host "Installed and started LGSTrayBattery from $install"
