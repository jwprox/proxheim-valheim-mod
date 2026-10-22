[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'dist'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $root 'src/Proxheim/Proxheim.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Invalid project version: $version"
}
$source = Get-Content (Join-Path $root 'src/Proxheim/Plugin.cs') -Raw
if ($source -notmatch ('public const string Version = "' + [regex]::Escape($version) + '";')) {
    throw 'Plugin.cs and Proxheim.csproj versions must match.'
}
if ($env:GITHUB_REF -like 'refs/tags/*' -and $env:GITHUB_REF -ne "refs/tags/v$version") {
    throw "Release tag must match the project version: v$version"
}
$dll = Join-Path $root "src/Proxheim/bin/$Configuration/net472/Proxheim.dll"
if (!(Test-Path -LiteralPath $dll)) { throw 'Build Proxheim before packaging.' }
$binaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion.Split('+')[0]
if ($binaryVersion -ne $version) { throw "DLL version $binaryVersion does not match $version. Rebuild first." }
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$stage = Join-Path $output 'staging'
if (Test-Path -LiteralPath $stage) { throw "Staging directory exists: $stage. Choose a fresh output directory." }
$pluginFolder = Join-Path $stage 'BepInEx/plugins/Proxheim'
New-Item -ItemType Directory -Force $pluginFolder | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $pluginFolder 'Proxheim.dll')
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $stage
$zip = Join-Path $output "Proxheim-v$version-Install.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Copy-Item -LiteralPath $dll -Destination (Join-Path $output 'Proxheim.dll') -Force
$checksums = foreach ($file in @($zip, (Join-Path $output 'Proxheim.dll'))) {
    $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($file))"
}
$checksums | Set-Content (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
$tags = @(& git -C $root tag --merged HEAD --sort=-version:refname)
if ($LASTEXITCODE -ne 0) { throw 'Could not read Git tags for release notes.' }
$previous = $tags | Where-Object { $_ -match '^v\d+\.\d+\.\d+' -and $_ -ne "v$version" } | Select-Object -First 1
$range = if ($previous) { "$previous..HEAD" } else { 'HEAD' }
$changes = @(& git -C $root log $range --max-count=50 '--format=- %s (%h)')
if ($LASTEXITCODE -ne 0) { throw 'Could not generate commit release notes.' }
$changeText = $changes -join "`n"
$notes = @"
## Changes

$changeText

## Installation

Extract Proxheim-v$version-Install.zip into the Valheim installation folder, or install Proxheim.dll in BepInEx/plugins/Proxheim/.

When upgrading from Troll Butt Follower, remove TrollButtFollower.dll before starting the game/server. The old configuration is imported if the Proxheim configuration does not exist.

Growth bonus Tar is server-only. Optional client installation enables direct troll commands when playing alone on a dedicated server. Other players do not need the plugin.

Only the Proxheim plugin and documentation are distributed. Valheim and BepInEx assemblies are fetched only as build references. See README.md for configuration, known limitations, and verification status.

Source revision: $env:GITHUB_SHA

"@
$notes | Set-Content (Join-Path $output 'RELEASE_NOTES.md') -Encoding utf8
if ($env:GITHUB_OUTPUT) { "version=$version" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 }
Write-Output "Packaged Proxheim v$version in $output"
