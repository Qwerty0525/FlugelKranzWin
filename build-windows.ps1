# -TestBuild ships driverAudit=true (DriverAudit lines for flight / compass testing); a release ships false.
# It also builds the app with the test profile (FlugelKranzBuildProfile=test): new configs start with the
# gravity compass on. A release build starts with it off. Values already in config.json are kept.
param([string]$Dotnet = 'dotnet', [string]$CMake = 'cmake', [switch]$TestBuild)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & $CMake -S native/driver -B artifacts/driver-build -A x64
    if ($LASTEXITCODE -ne 0) { throw 'Driver configure failed' }
    & $CMake --build artifacts/driver-build --config Release
    if ($LASTEXITCODE -ne 0) { throw 'Driver build failed' }
    $buildProfile = if ($TestBuild) { 'test' } else { 'release' }
    & $Dotnet publish src/FlugelKranz -c Release -r win-x64 --self-contained true -o artifacts/windows-x64 "-p:FlugelKranzBuildProfile=$buildProfile"
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed' }
    $driverPath = Join-Path $PSScriptRoot 'artifacts/windows-x64/driver/flugelkranz'
    New-Item -ItemType Directory -Force (Join-Path $driverPath 'bin/win64') | Out-Null
    Copy-Item -LiteralPath 'artifacts/driver-build/Release/driver_flugelkranz.dll' -Destination (Join-Path $driverPath 'bin/win64')
    Copy-Item -LiteralPath 'native/driver/driver.vrdrivermanifest' -Destination $driverPath
    Copy-Item -LiteralPath 'native/driver/resources' -Destination $driverPath -Recurse -Force
    $settingsPath = Join-Path $driverPath 'resources/settings/default.vrsettings'
    $audit = if ($TestBuild) { 'true' } else { 'false' }
    $settingsText = [IO.File]::ReadAllText($settingsPath)
    if ($settingsText -notmatch '"driverAudit"\s*:\s*(true|false)') { throw 'driverAudit not found in default.vrsettings' }
    $settingsText = [regex]::Replace($settingsText, '("driverAudit"\s*:\s*)(true|false)', ('${1}' + $audit))
    [IO.File]::WriteAllText($settingsPath, $settingsText)
    Write-Host ("driverAudit = $audit, app profile = $buildProfile (" + $(if ($TestBuild) { 'test build' } else { 'release' }) + ')')
    Copy-Item -LiteralPath 'native/driver/vendor/openvr/LICENSE' -Destination (Join-Path $driverPath 'LICENSE-OpenVR')
    Copy-Item -LiteralPath 'native/driver/vendor/minhook/LICENSE.txt' -Destination (Join-Path $driverPath 'LICENSE-MinHook.txt')
    Copy-Item -LiteralPath 'install-driver.ps1' -Destination 'artifacts/windows-x64'
} finally { Pop-Location }
