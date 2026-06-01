<#
.SYNOPSIS
Builds Stratum and stages a full runnable server folder at repo root.

.DESCRIPTION
Creates release-full/stratum-<version>-full by:
  1) Building the workspace solution.
  2) Copying the vanilla server runtime from .vanilla/.
  3) Overlaying locally-built Stratum outputs (launcher, API, mods, shared libs).

This is intended to produce a contributor/server-owner friendly "everything together"
folder that can be launched directly.
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutDir,
    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) {
    $OutDir = Join-Path $repoRoot 'release-full'
}

function Get-StratumVersion {
    param([string]$RepoRoot)

    $candidates = @(
        (Join-Path $RepoRoot 'sources/VintagestoryLib/Vintagestory.Server/StratumInfo.cs'),
        (Join-Path $RepoRoot 'sources/VintagestoryLib/Vintagestory/Server/StratumInfo.cs'),
        (Join-Path $RepoRoot 'baseline/VintagestoryLib/Vintagestory.Server/StratumInfo.cs'),
        (Join-Path $RepoRoot 'baseline/VintagestoryLib/Vintagestory/Server/StratumInfo.cs')
    )

    $infoFile = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $infoFile) {
        throw 'Could not locate StratumInfo.cs in sources/ or baseline/.'
    }

    $infoText = Get-Content $infoFile -Raw
    $baseVer = [regex]::Match($infoText, 'BaseGameVersion\s*=\s*"([^"]+)"').Groups[1].Value
    $rev = [regex]::Match($infoText, 'StratumRevision\s*=\s*"([^"]+)"').Groups[1].Value
    $pre = [regex]::Match($infoText, 'PreRelease\s*=\s*"([^"]*)"').Groups[1].Value

    if (-not $baseVer -or -not $rev) {
        throw "Could not parse version fields from $infoFile"
    }

    if ($pre) {
        return "$baseVer-stratum.$rev-$pre"
    }

    return "$baseVer-stratum.$rev"
}

function Copy-IfExists {
    param(
        [string]$Path,
        [string]$Destination
    )

    if (Test-Path $Path) {
        Copy-Item $Path $Destination -Force
    }
}

function Get-FirstExistingPath {
    param([string[]]$Candidates)

    foreach ($candidate in $Candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    return $null
}

function Require-File {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        throw "Required build artifact missing: $Path"
    }
}

Push-Location $repoRoot
try {
    $version = Get-StratumVersion -RepoRoot $repoRoot
    $stageDir = Join-Path $OutDir "stratum-$version-full"

    $libProject = Get-FirstExistingPath @(
        (Join-Path $repoRoot 'VintagestoryLib\VintagestoryLib.csproj'),
        (Join-Path $repoRoot 'baseline\VintagestoryLib\VintagestoryLib.csproj')
    )
    if (-not $libProject) {
        throw 'Could not find VintagestoryLib.csproj in VintagestoryLib/ or baseline/VintagestoryLib/.'
    }

    $solutionFile = Join-Path $repoRoot 'VintageStory.slnx'
    if (-not (Test-Path $solutionFile)) {
        throw "Solution file missing: $solutionFile"
    }

    Write-Host "Building $solutionFile (Configuration=$Configuration, Version=$version)"
    & dotnet build $solutionFile -c $Configuration -p:SkipDeployToVSInstall=true -nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Solution build failed."
    }

    if (-not (Test-Path '.vanilla')) {
        throw '.vanilla is missing. Run scripts/bootstrap.ps1 first.'
    }

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    if ((Test-Path $stageDir) -and -not $NoClean) {
        Remove-Item -Recurse -Force $stageDir
    }

    Write-Host "Staging full runtime into $stageDir"
    Copy-Item '.vanilla' $stageDir -Recurse -Force

    # Write the bootstrap marker so StratumServer does not re-download vanilla files
    # when launched from this folder. The marker content must match BaseGameVersion exactly.
    $baseGameVersion = ($version -split '-stratum')[0]
    Set-Content -Path (Join-Path $stageDir '.stratum-base') -Value $baseGameVersion -NoNewline

    $tfm = 'net10.0'
    $binRoot = Join-Path $repoRoot "bin/$Configuration/$tfm"
    $stratumOut = Join-Path $repoRoot "StratumServer/bin/$Configuration/$tfm"
    $nimbusOut = Join-Path $repoRoot "Nimbus/Nimbus.Shared/bin/$Configuration/$tfm"
    $libOutRoot = Split-Path -Parent $libProject
    $libBinRoot = Join-Path $libOutRoot "bin/$Configuration/$tfm"
    $modsDir = Join-Path $stageDir 'Mods'

    New-Item -ItemType Directory -Force -Path $modsDir | Out-Null

    # Overlay launcher output
    Copy-IfExists (Join-Path $stratumOut 'StratumServer.exe') $stageDir
    Copy-IfExists (Join-Path $stratumOut 'StratumServer.dll') $stageDir
    Copy-IfExists (Join-Path $stratumOut 'StratumServer.pdb') $stageDir
    Copy-IfExists (Join-Path $stratumOut 'StratumServer.deps.json') $stageDir
    Copy-IfExists (Join-Path $stratumOut 'StratumServer.runtimeconfig.json') $stageDir

    # Visible copy of the shipped default config so operators can see/edit it.
    # The launcher still seeds a fresh stratum.json from the embedded copy on first run.
    Copy-IfExists (Join-Path $repoRoot 'StratumServer\stratum.default.json') $stageDir

    # Fail fast if patched artifacts are missing: release-full must not silently ship vanilla binaries.
    Require-File (Join-Path $stratumOut 'StratumServer.exe')
    Require-File (Join-Path $stratumOut 'StratumServer.dll')
    Require-File (Join-Path $binRoot 'VintagestoryAPI.dll')
    Require-File (Join-Path $libBinRoot 'VintagestoryLib.dll')

    # Overlay patched API/native wrapper from shared bin root
    Get-ChildItem -Path $binRoot -File -Filter 'VintagestoryAPI.*' -ErrorAction SilentlyContinue | ForEach-Object {
        Copy-Item $_.FullName $stageDir -Force
    }
    Get-ChildItem -Path $libBinRoot -File -Filter 'VintagestoryLib.*' -ErrorAction SilentlyContinue | ForEach-Object {
        Copy-Item $_.FullName $stageDir -Force
    }
    Get-ChildItem -Path $binRoot -File -Filter 'cairo-sharp.*' -ErrorAction SilentlyContinue | ForEach-Object {
        Copy-Item $_.FullName $stageDir -Force
    }

    # Overlay mod outputs (fork mods only; optional private mods are staged
    # opportunistically if their build outputs happen to be present)
    foreach ($modName in @('VSCreativeMod', 'VSEssentials', 'VSSurvivalMod')) {
        Get-ChildItem -Path $binRoot -File -Filter "$modName.*" -ErrorAction SilentlyContinue | ForEach-Object {
            Copy-Item $_.FullName $modsDir -Force
        }
    }
    foreach ($optionalMod in @('StratumUI', 'VsNpc')) {
        Get-ChildItem -Path $binRoot -File -Filter "$optionalMod.*" -ErrorAction SilentlyContinue | ForEach-Object {
            Copy-Item $_.FullName $modsDir -Force
        }
    }

    # Nimbus is an optional integration; only overlay if its build output exists
    if (Test-Path $nimbusOut) {
        Get-ChildItem -Path $nimbusOut -File -Filter 'Nimbus.Shared.*' -ErrorAction SilentlyContinue | ForEach-Object {
            Copy-Item $_.FullName $stageDir -Force
        }
    }

    # The full stage should boot through StratumServer, not vanilla server executables.
    Get-ChildItem -Path $stageDir -File -Filter 'VintagestoryServer*' -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item $_.FullName -Force
    }

    $manifest = @(
        "version=$version",
        "configuration=$Configuration",
        "built_utc=$([DateTime]::UtcNow.ToString('o'))",
        "source=.vanilla + workspace build outputs (patched API/lib required)"
    )
    Set-Content -Path (Join-Path $stageDir 'release-full-manifest.txt') -Value $manifest

    Write-Host "Release-full ready: $stageDir" -ForegroundColor Green
}
finally {
    Pop-Location
}
