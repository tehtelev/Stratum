<#
.SYNOPSIS
Builds and packages Stratum launcher zips.

Produces a tiny zip per RID containing only:
  StratumServer.exe (single-file, framework-dependent, with all patched
  Stratum DLLs embedded as resources)
  StratumServer.runtimeconfig.json
  LICENSE, README.md

The launcher downloads the matching vanilla server zip from cdn.vintagestory.at
on first run, then unpacks the embedded Stratum overlay on top.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Rids,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $infoFile = @(
        (Join-Path $repoRoot 'sources/VintagestoryLib/Vintagestory.Server/StratumInfo.cs'),
        (Join-Path $repoRoot 'sources/VintagestoryLib/Vintagestory/Server/StratumInfo.cs'),
        (Join-Path $repoRoot 'baseline/VintagestoryLib/Vintagestory.Server/StratumInfo.cs'),
        (Join-Path $repoRoot 'baseline/VintagestoryLib/Vintagestory/Server/StratumInfo.cs')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $infoFile) { throw 'Could not locate StratumInfo.cs for version parsing.' }
    $infoText = Get-Content $infoFile -Raw
    $baseVer = [regex]::Match($infoText, 'BaseGameVersion\s*=\s*"([^"]+)"').Groups[1].Value
    $rev     = [regex]::Match($infoText, 'StratumRevision\s*=\s*"([^"]+)"').Groups[1].Value
    $pre     = [regex]::Match($infoText, 'PreRelease\s*=\s*"([^"]*)"').Groups[1].Value
    if (-not $baseVer -or -not $rev) { throw "Could not parse version pieces from $infoFile" }
    $version = if ($pre) { "$baseVer-stratum.$rev-$pre" } else { "$baseVer-stratum.$rev" }

    $libProject = @(
        (Join-Path $repoRoot 'baseline/VintagestoryLib/VintagestoryLib.csproj'),
        (Join-Path $repoRoot 'VintagestoryLib/VintagestoryLib.csproj')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $libProject) { throw 'Could not find VintagestoryLib.csproj in baseline/VintagestoryLib/ or VintagestoryLib/.' }

    # Build the patched server library first. The solution intentionally skips the
    # baseline lib project, but StratumServer embeds its output during publish.
    Write-Host "Building patched server library (Configuration=$Configuration, Version=$version)"
    & dotnet build $libProject -c $Configuration -p:Version=$version -p:InformationalVersion=$version -p:SkipDeployToVSInstall=true -nologo
    if ($LASTEXITCODE -ne 0) { throw "Patched server library build failed" }

    # Build the rest of the solution so API/mod outputs exist before publish.
    Write-Host "Building solution (Configuration=$Configuration, Version=$version)"
    & dotnet build VintageStory.slnx -c $Configuration -p:Version=$version -p:InformationalVersion=$version -nologo
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed" }

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

    foreach ($rid in $Rids) {
        Write-Host "Publishing StratumServer for $rid"
        $publishDir = Join-Path $repoRoot "StratumServer/bin/$Configuration/publish-$rid"
        if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

        & dotnet publish StratumServer/StratumServer.csproj `
            -c $Configuration `
            -r $rid `
            -p:SelfContained=false `
            -p:PublishSingleFile=true `
            -p:EmbedPatchedDlls=true `
            -p:Version=$version `
            -p:InformationalVersion=$version `
            -o $publishDir `
            -nologo
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }

        $stage = Join-Path $OutDir "stage-$rid"
        if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
        New-Item -ItemType Directory -Force -Path $stage | Out-Null

        $exeName = if ($rid -like 'win-*') { 'StratumServer.exe' } else { 'StratumServer' }
        Copy-Item (Join-Path $publishDir $exeName) $stage
        $runtimeCfg = Join-Path $publishDir 'StratumServer.runtimeconfig.json'
        if (Test-Path $runtimeCfg) { Copy-Item $runtimeCfg $stage }
        Copy-Item (Join-Path $repoRoot 'LICENSE')  $stage
        Copy-Item (Join-Path $repoRoot 'README.md') $stage

        $zipName = "stratum-$version-$rid.zip"
        $zipPath = Join-Path $OutDir $zipName
        if (Test-Path $zipPath) { Remove-Item $zipPath }
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath
        Write-Host "Wrote $zipPath"
    }
}
finally {
    Pop-Location
}
