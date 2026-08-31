# Builds the Release DLL and assembles the Thunderstore package zip in dist/.
# Pass -Deploy to also drop the DLL straight into the development profile.
param([switch]$Deploy)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

dotnet build "$root\WeatherGordion.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$manifest = Get-Content "$root\manifest.json" -Raw | ConvertFrom-Json
$version = $manifest.version_number

$stage = Join-Path $root "dist\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item "$root\manifest.json" $stage
Copy-Item "$root\README.md" $stage
Copy-Item "$root\CHANGELOG.md" $stage
Copy-Item "$root\icon.png" $stage
Copy-Item "$root\bin\Release\netstandard2.1\WeatherGordion.dll" $stage

$zip = Join-Path $root "dist\WeatherGordion-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip

Remove-Item $stage -Recurse -Force
Write-Host "Thunderstore package ready: $zip"

if ($Deploy) {
    # Not $profile: that is a PowerShell automatic variable.
    $devProfile = Join-Path $env:APPDATA "Thunderstore Mod Manager\DataFolder\LethalCompany\profiles\HARDMODEv81_INVDEV"
    $target = Join-Path $devProfile "BepInEx\plugins\Solon-WeatherGordion"
    if (-not (Test-Path $devProfile)) { throw "Development profile not found: $devProfile" }

    # A loose copy in the plugins root makes BepInEx see two installs of the same version and skip
    # one of them ("because a newer version exists"), so clear it before deploying.
    $stray = Join-Path $devProfile "BepInEx\plugins\WeatherGordion.dll"
    if (Test-Path $stray) {
        Remove-Item $stray -Force
        Write-Host "Removed a duplicate copy from the plugins root: $stray"
    }

    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item "$root\bin\Release\netstandard2.1\WeatherGordion.dll" $target -Force
    Copy-Item "$root\manifest.json" $target -Force
    Write-Host "Deployed to: $target"
}
