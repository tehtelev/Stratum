<#
.SYNOPSIS
Builds a clean working tree by laying down:
  1. Decompiled closed-source vanilla VS libraries (VintagestoryLib, VintagestoryServer, Cairo),
     reconstructed from the official server zip.
  2. Open-source Anego forks (VintagestoryApi, VSEssentials, VSSurvivalMod), cloned from
     their upstream repos at the commits pinned in forks.json.

Then applies every patch in patches/ on top.

.PARAMETER Version
Vintage Story server version to download when -ServerZip is not provided. Defaults to 1.22.3.

.PARAMETER ServerZip
Path to an already-downloaded vs_server_*.zip. If omitted, the script downloads the
zip for -Version from cdn.vintagestory.at into .vanilla-zips/.

.PARAMETER Refresh
Force re-extract, re-decompile, and re-clone even if cached output already exists.

.EXAMPLE
.\scripts\bootstrap.ps1
.\scripts\bootstrap.ps1 -Version 1.22.3
.\scripts\bootstrap.ps1 -Refresh
#>

[CmdletBinding()]
param(
    [string]$Version = '1.22.3',
    [string]$ServerZip,
    [switch]$Refresh
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    # Closed-source DLLs decompiled from the official server zip. Cairo used to
    # live here too, but its source is now mirrored on GitHub (anegostudios/Cairo)
    # and cloned via forks.json instead.
    $libMap = [ordered]@{
        'VintagestoryLib.dll'    = @{ Project = 'VintagestoryLib';    Work = 'baseline/VintagestoryLib' }
        'VintagestoryServer.dll' = @{ Project = 'VintagestoryServer'; Work = 'baseline/VintagestoryServer' }
    }

    $vanillaDir  = Join-Path $repoRoot '.vanilla'
    $baselineDir = Join-Path $repoRoot '.baseline'
    $zipCacheDir = Join-Path $repoRoot '.vanilla-zips'

    if ($Refresh -and (Test-Path $vanillaDir))  { Remove-Item -Recurse -Force $vanillaDir }
    if ($Refresh -and (Test-Path $baselineDir)) { Remove-Item -Recurse -Force $baselineDir }

    #
    # 1. Vanilla server zip -> decompile -> baseline -> working folders
    #
    if (-not $ServerZip) {
        New-Item -ItemType Directory -Force -Path $zipCacheDir | Out-Null
        $zipName = "vs_server_win-x64_$Version.zip"
        $ServerZip = Join-Path $zipCacheDir $zipName
        if (-not (Test-Path $ServerZip)) {
            $url = "https://cdn.vintagestory.at/gamefiles/stable/$zipName"
            Write-Host "Downloading $url"
            $ProgressPreference = 'Continue'
            Invoke-WebRequest -Uri $url -OutFile $ServerZip
        } else {
            Write-Host "Using cached $ServerZip"
        }
    }

    if (-not (Test-Path $vanillaDir)) {
        if (-not (Test-Path $ServerZip)) { throw "Server zip not found: $ServerZip" }
        Write-Host "Extracting $ServerZip"
        Expand-Archive -Path $ServerZip -DestinationPath $vanillaDir -Force
    }

    # Build-time compatibility: some server zips omit a small set of managed
    # client-facing references that VintagestoryLib still compiles against.
    # Pull only those DLLs from the public Linux client archive when missing.
    $vanillaLibDir = Join-Path $vanillaDir 'Lib'
    New-Item -ItemType Directory -Force -Path $vanillaLibDir | Out-Null
    $requiredClientRefs = @('OpenTK.Graphics.dll', 'csogg.dll', 'csvorbis.dll')
    $missingClientRefs = @($requiredClientRefs | Where-Object { -not (Test-Path (Join-Path $vanillaLibDir $_)) })
    if ($missingClientRefs.Count -gt 0) {
        Write-Host "Missing managed refs in server zip: $($missingClientRefs -join ', ')"
        New-Item -ItemType Directory -Force -Path $zipCacheDir | Out-Null
        $clientTarName = "vs_client_linux-x64_$Version.tar.gz"
        $clientTarPath = Join-Path $zipCacheDir $clientTarName
        if (-not (Test-Path $clientTarPath)) {
            $clientUrl = "https://cdn.vintagestory.at/gamefiles/stable/$clientTarName"
            Write-Host "Downloading $clientUrl"
            Invoke-WebRequest -Uri $clientUrl -OutFile $clientTarPath
        } else {
            Write-Host "Using cached $clientTarPath"
        }

        $tmpClientExtract = Join-Path $repoRoot ".tmp-client-$Version"
        if (Test-Path $tmpClientExtract) { Remove-Item -Recurse -Force $tmpClientExtract }
        New-Item -ItemType Directory -Force -Path $tmpClientExtract | Out-Null
        tar -xzf $clientTarPath -C $tmpClientExtract

        foreach ($refName in $missingClientRefs) {
            $sourceRef = Get-ChildItem -Path $tmpClientExtract -Recurse -File -Filter $refName | Select-Object -First 1
            if ($sourceRef) {
                Copy-Item -Force $sourceRef.FullName (Join-Path $vanillaLibDir $refName)
                Write-Host "Restored build ref $refName from Linux client archive"
            } else {
                Write-Warning "Could not find $refName in $clientTarName"
            }
        }

        Remove-Item -Recurse -Force $tmpClientExtract
    }

    if (-not (Get-Command ilspycmd -ErrorAction SilentlyContinue)) {
        $manifest = Join-Path $PSScriptRoot "../.config/dotnet-tools.json"
        if (Test-Path $manifest) {
            $json = Get-Content $manifest -Raw | ConvertFrom-Json
            $pinnedVersion = $json.tools.ilspycmd.version
            Write-Host "Installing ilspycmd $pinnedVersion (from tool manifest)"
            dotnet tool install -g ilspycmd --version $pinnedVersion | Out-Null
        } else {
            Write-Host "Installing ilspycmd (no manifest found, using latest)"
            dotnet tool install -g ilspycmd | Out-Null
        }
        $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
    }

    foreach ($dll in $libMap.Keys) {
        $proj = $libMap[$dll].Project
        $workRel = $libMap[$dll].Work
        $dllPath = Get-ChildItem -Path $vanillaDir -Recurse -Filter $dll | Select-Object -First 1
        if (-not $dllPath) { Write-Warning "Skipping $dll, not found in zip"; continue }

        $out = Join-Path $baselineDir $proj
        if (-not (Test-Path $out) -or $Refresh) {
            Write-Host "Decompiling $dll into $out"
            New-Item -ItemType Directory -Force -Path $out | Out-Null
            ilspycmd $dllPath.FullName --project -o $out | Out-Null
        }

        # ilspycmd writes <LangVersion>15.0</LangVersion>, which the .NET 10 SDK
        # rejects with CS1617. Normalize to a value the current Roslyn accepts.
        Get-ChildItem -Path $out -Filter '*.csproj' -File | ForEach-Object {
            $csprojText = [IO.File]::ReadAllText($_.FullName)
            $patched = $csprojText -replace '<LangVersion>15\.0</LangVersion>', '<LangVersion>latest</LangVersion>'
            if ($patched -ne $csprojText) {
                [IO.File]::WriteAllText($_.FullName, $patched)
            }
        }

        $work = Join-Path $repoRoot $workRel
        if (Test-Path $work) { Remove-Item -Recurse -Force $work }
        Copy-Item -Recurse -Force $out $work
    }

    #
    # 2. Upstream Anego forks -> baseline -> working folders
    #
    $forksFile = Join-Path $repoRoot 'forks.json'
    if (Test-Path $forksFile) {
        $cfg = Get-Content $forksFile -Raw | ConvertFrom-Json
        foreach ($fork in $cfg.forks) {
            $name = $fork.name
            $url  = $fork.url
            $ref  = $fork.ref
            $base = Join-Path $baselineDir $name

            if (-not (Test-Path $base) -or $Refresh) {
                if (Test-Path $base) { Remove-Item -Recurse -Force $base }
                Write-Host "Cloning $url at $ref into $base"
                git clone --quiet $url $base | Out-Null
                git -C $base checkout --quiet $ref
                # Drop the upstream .git so this is just a baseline snapshot.
                Remove-Item -Recurse -Force (Join-Path $base '.git')

                # Normalize text files to LF. extract-patches.ps1 diffs against
                # LF-normalized content, so the patches in patches/ assume LF; some
                # upstream repos ship .gitattributes that force CRLF on checkout
                # (or Windows autocrlf does), which makes `git apply` reject hunks.
                Get-ChildItem -Path $base -Recurse -File -Include '*.cs','*.csproj','*.json','*.xml','*.props','*.targets' -ErrorAction SilentlyContinue | ForEach-Object {
                    $bytes = [IO.File]::ReadAllBytes($_.FullName)
                    $hasCR = $false
                    foreach ($byte in $bytes) { if ($byte -eq 13) { $hasCR = $true; break } }
                    if ($hasCR) {
                        $text = [Text.Encoding]::UTF8.GetString($bytes) -replace "`r`n", "`n"
                        [IO.File]::WriteAllBytes($_.FullName, [Text.Encoding]::UTF8.GetBytes($text))
                    }
                }
            }

            $work = Join-Path $repoRoot $name
            if (Test-Path $work) { Remove-Item -Recurse -Force $work }
            Copy-Item -Recurse -Force $base $work
        }
    }

    #
    # 3. Apply patches/ on top of everything.
    #
    $patchesDir = Join-Path $repoRoot 'patches'
    # Vanilla (decompiled) projects live under baseline/; their patches are applied
    # with --directory=baseline so paths like a/VintagestoryLib/... resolve into
    # baseline/VintagestoryLib/...
    $vanillaPatchProjects = @('VintagestoryLib','VintagestoryServer')
    if (Test-Path $patchesDir) {
        $failed = @()
        Get-ChildItem -Path $patchesDir -Recurse -Filter '*.patch' | ForEach-Object {
            $rel = $_.FullName.Substring($repoRoot.Length + 1)
            $topProj = ($rel -split '[\\/]')[1]
            $applyArgs = @('apply','--whitespace=nowarn')
            if ($vanillaPatchProjects -contains $topProj) { $applyArgs += '--directory=baseline' }
            $applyArgs += $_.FullName
            Write-Host "Applying $rel"
            $prevEAP = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            $out = & git @applyArgs 2>&1
            $code = $LASTEXITCODE
            $ErrorActionPreference = $prevEAP
            if ($code -ne 0) {
                $failed += $rel
                $out | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
            }
        }
        if ($failed.Count -gt 0) {
            Write-Host ""
            Write-Host "$($failed.Count) patch(es) failed to apply:" -ForegroundColor Red
            $failed | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
            Write-Host "Fix the conflicts in the working tree, then run scripts\extract-patches.ps1." -ForegroundColor Red
        }
    } else {
        Write-Host "No patches/ directory, skipping patch step."
    }

    #
    # 4. Drop Stratum-original sources on top.
    #    sources/<project>/<rel>.cs is committed as real source (Paper does the same
    #    with paper-server/src/main/java for its own new files); the script just
    #    copies them into the working folder so the csproj globs them.
    #
    $sourcesDir = Join-Path $repoRoot 'sources'
    if (Test-Path $sourcesDir) {
        Get-ChildItem -Path $sourcesDir -Directory | ForEach-Object {
            $proj = $_.Name
            $dst  = if ($vanillaPatchProjects -contains $proj) {
                Join-Path $repoRoot "baseline/$proj"
            } else {
                Join-Path $repoRoot $proj
            }
            if (-not (Test-Path $dst)) {
                Write-Warning "sources/$proj has no matching working folder; skipping."
                return
            }
            Get-ChildItem -Path $_.FullName -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring((Join-Path $sourcesDir $proj).Length + 1)
                $target = Join-Path $dst $rel
                $targetDir = Split-Path $target
                if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Force -Path $targetDir | Out-Null }
                Copy-Item -Force $_.FullName $target
            }
            Write-Host "Synced sources/$proj into $proj/"
        }
    }

    Write-Host ""
    Write-Host "Bootstrap complete. Run: dotnet build VintageStory.slnx -c Release" -ForegroundColor Green
} finally {
    Pop-Location
}
