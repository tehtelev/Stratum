<#
.SYNOPSIS
Packages Stratum release archives from `dotnet publish` output.

Only Stratum-built artifacts go in the zip:
  StratumServer.exe / .dll / .runtimeconfig.json
  VintagestoryServer.dll, VintagestoryLib.dll, VintagestoryAPI.dll, cairo-sharp.dll
  Mods/VSEssentials.dll, Mods/VSSurvivalMod.dll

Vanilla assets (assets/, Lib/, base mods, native runtimes) are NOT included.
They are downloaded from cdn.vintagestory.at on first launch by StratumServer.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishRoot,
    [Parameter(Mandatory)][string]$OutDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$infoFile = Join-Path $repoRoot 'sources/VintagestoryLib/Vintagestory/Server/StratumInfo.cs'
$infoText = Get-Content $infoFile -Raw
$baseVer = [regex]::Match($infoText, 'BaseGameVersion\s*=\s*"([^"]+)"').Groups[1].Value
$rev     = [regex]::Match($infoText, 'StratumRevision\s*=\s*"([^"]+)"').Groups[1].Value
$pre     = [regex]::Match($infoText, 'PreRelease\s*=\s*"([^"]*)"').Groups[1].Value
if (-not $baseVer -or -not $rev) { throw "Could not parse version pieces from $infoFile" }
$version = if ($pre) { "$baseVer-stratum.$rev-$pre" } else { "$baseVer-stratum.$rev" }

$keepFiles = @(
    'StratumServer.exe',
    'StratumServer.dll',
    'StratumServer.runtimeconfig.json',
    'StratumServer.deps.json',
    'VintagestoryServer.exe',
    'VintagestoryServer.dll',
    'VintagestoryServer.runtimeconfig.json',
    'VintagestoryLib.dll',
    'VintagestoryAPI.dll',
    'cairo-sharp.dll',
    'Nimbus.Shared.dll'
)
$keepMods = @(
    'VSEssentials.dll',
    'VSSurvivalMod.dll'
)

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Get-ChildItem -Directory $PublishRoot | ForEach-Object {
    $rid = $_.Name
    $stage = Join-Path $OutDir "stage-$rid"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    # Ship matching .pdb for every .dll we ship. The vanilla bootstrap drops vanilla
    # PDBs next to our patched DLLs, and LoggerBase's static cctor NREs on the PDB
    # signature mismatch (frame.GetFileName() returns null). Our PDB wins because
    # the bootstrap only overlays missing files.
    foreach ($name in $keepFiles) {
        $src = Join-Path $_.FullName $name
        if (Test-Path $src) { Copy-Item $src $stage }
        if ([IO.Path]::GetExtension($name) -eq '.dll') {
            $pdb = [IO.Path]::ChangeExtension((Join-Path $_.FullName $name), '.pdb')
            if (Test-Path $pdb) { Copy-Item $pdb $stage }
        }
    }

    $modsDst = Join-Path $stage 'Mods'
    New-Item -ItemType Directory -Force -Path $modsDst | Out-Null
    foreach ($name in $keepMods) {
        $candidates = @(
            (Join-Path $_.FullName "Mods/$name"),
            (Join-Path $_.FullName $name)
        )
        $src = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($src) {
            Copy-Item $src $modsDst
            $pdb = [IO.Path]::ChangeExtension($src, '.pdb')
            if (Test-Path $pdb) { Copy-Item $pdb $modsDst }
        }
    }

    # Seed stratum.json so the server has config to read on first launch.
    $dataDst = Join-Path $stage 'Data'
    New-Item -ItemType Directory -Force -Path $dataDst | Out-Null
    $seed = Join-Path $repoRoot 'StratumServer/stratum.default.json'
    if (Test-Path $seed) {
        Copy-Item $seed (Join-Path $dataDst 'stratum.json')
    }

    Copy-Item (Join-Path $repoRoot 'LICENSE')  $stage
    Copy-Item (Join-Path $repoRoot 'README.md') $stage

    $zipName = "stratum-$version-$rid.zip"
    $zipPath = Join-Path $OutDir $zipName
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath
    Write-Host "Wrote $zipPath"
}
