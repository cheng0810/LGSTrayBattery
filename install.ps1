[CmdletBinding()]
param(
    [string]$PackagePath = "$PSScriptRoot\bin\Release\Publish\win-x64\Standalone",
    [string]$InstallPath = "$env:LOCALAPPDATA\LGSTrayBattery"
)

$ErrorActionPreference = 'Stop'

$package = [System.IO.Path]::GetFullPath($PackagePath)
$install = [System.IO.Path]::GetFullPath($InstallPath)
$defaultInstall = [System.IO.Path]::GetFullPath("$env:LOCALAPPDATA\LGSTrayBattery")

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

Get-Process -Name 'LGSTray', 'LGSTrayHID' -ErrorAction SilentlyContinue |
    Stop-Process -Force

New-Item -ItemType Directory -Path $install -Force | Out-Null
Get-ChildItem -LiteralPath $install -Force | Remove-Item -Recurse -Force
Copy-Item -Path "$package\*" -Destination $install -Recurse -Force

if ($null -ne $savedSettings) {
    Set-Content -LiteralPath $settingsPath -Value $savedSettings -NoNewline
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$executable = Join-Path $install 'LGSTray.exe'
New-ItemProperty -Path $runKey -Name 'LGSTrayGUI' -Value "`"$executable`"" -PropertyType String -Force | Out-Null

Start-Process -FilePath $executable -WorkingDirectory $install -WindowStyle Hidden
Write-Host "Installed and started LGSTrayBattery from $install"
