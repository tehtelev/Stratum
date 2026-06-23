# Building Stratum

## Prerequisites

**All platforms:**
* .NET 10 SDK.
* git.
* About 2 GB of free disk space for the vanilla source tree and build output.

**Windows:**
* PowerShell 5.1+ (ships with Windows 10+).

**Linux:**
* bash, curl, perl, python3, tar.

**macOS:**
* bash 4+ and GNU coreutils (`brew install bash coreutils`).
* curl, perl, python3.

## First build

**Windows:**

```powershell
git clone https://github.com/trevorftp/Stratum.git
cd Stratum
.\scripts\bootstrap.ps1
dotnet build VintageStory.slnx -c Release
```

**Linux / macOS:**

```bash
git clone https://github.com/trevorftp/Stratum.git
cd Stratum
./scripts/bootstrap.sh
dotnet build VintageStory.slnx -c Release
```

`bootstrap` does the following:

1. Downloads the vanilla server archive (`vs_server_win-x64_<version>.zip` on Windows,
   `vs_server_linux-x64_<version>.tar.gz` on Linux/macOS) from `cdn.vintagestory.at`
   into `.vanilla-zips/`. Cached, so re-runs are quick.
2. Extracts the archive into `.vanilla/`.
3. Installs `ilspycmd` as a dotnet global tool if it is not already on PATH.
4. Decompiles `VintagestoryLib.dll` and `VintagestoryServer.dll`
   into `.baseline/<project>/`.
5. Clones the open-source Anego forks (vsapi, vsessentialsmod, vssurvivalmod, Cairo,
   vscreativemod) at the commits pinned in `forks.json` into `.baseline/<project>/`.
6. Copies every baseline into the matching working folder at the repo root.
7. Applies every `.patch` file under `patches/` with `git apply`.

After that the solution has every project it needs and `dotnet build` works.

### Targeting a different version

```powershell
.\scripts\bootstrap.ps1 -Version 1.22.3
```

```bash
./scripts/bootstrap.sh --version 1.22.3
```

### Using an archive you already have

```powershell
.\scripts\bootstrap.ps1 -ServerZip C:\downloads\vs_server_win-x64_1.22.3.zip
```

```bash
./scripts/bootstrap.sh --server-archive ~/downloads/vs_server_linux-x64_1.22.3.tar.gz
```

### Re-bootstrapping after an upstream version bump

```powershell
.\scripts\bootstrap.ps1 -Version 1.22.4 -Refresh
```

```bash
./scripts/bootstrap.sh --version 1.22.4 --refresh
```

`-Refresh` / `--refresh` wipes `.vanilla/` and `.baseline/` so the new release gets decompiled clean.
For the open-source forks, bump the `ref` values in `forks.json` to the matching
upstream commits before running with refresh. Expect some patches to fail. Fix them
in the working tree and run `scripts\extract-patches.ps1` (Windows) or `scripts/extract-patches.sh` (Linux/macOS) to regenerate the patch files.

## Producing patches

After editing anything inside `VintagestoryLib/`, `VintagestoryServer/`, or `Cairo/`:

```powershell
.\scripts\extract-patches.ps1
```

```bash
./scripts/extract-patches.sh
```

That diffs each file against the corresponding baseline and writes or updates files
under `patches/<project>/<relative-path>.patch`. Commit those.

## Running the server

The solution outputs to `<project>/bin/Release/net10.0/`. For a real install you still
need the vanilla assets, native libs, and base mods. The `BuildStratumOutputs` and
`DeployStratumServer` MSBuild targets in `StratumServer.csproj` handle that locally if
you set `VSInstall` and `StratumInstall` in a `*.local.props` file at the repo root.

## Troubleshooting

* `ilspycmd` install fails: install it manually with `dotnet tool install -g ilspycmd`
  and make sure `~/.dotnet/tools` (Linux/macOS) or `%USERPROFILE%\.dotnet\tools` (Windows) is on PATH.
* A patch fails to apply: usually means upstream changed the surrounding code. Open
  the working file, fix the conflict, then re-run `extract-patches`.
* `dotnet build` reports missing types from `VintagestoryLib`: the bootstrap step did
  not complete. Re-run bootstrap and watch for errors.
