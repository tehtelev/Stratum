# StratumScenarios

Seventeen end-to-end scenarios that boot a real server in-process (through the
[Atlas](https://github.com/Pixnop/Atlas) test harness) and assert documented fork
behavior: chunk persistence across save/unload/reload, the simulation distance throttles
for entities, random ticks and block tick listeners, and the fact that each
`stratum-performance.json` toggle really restores vanilla behavior. `BootScenarios` also
checks that the fork's own command groups are registered, and guards against the worst
false green available here, a build made without `-p:EmbedPatchedFiles=true` that boots
the downloaded vanilla lib instead of this repo's.

Run it with `make scenarios`, `bash scripts/scenarios.sh` or `.\scripts\scenarios.ps1`.
That builds if needed, materializes the install with one `--stratum-prepare-only` launch,
and runs `dotnet test` with `VINTAGE_STORY` pointing at it. Extra arguments go to
`dotnet test`, so `bash scripts/scenarios.sh --filter BootScenarios` runs a single class. About a minute and a half, the eight server boots included. It pulls four NuGet
packages: xunit, its Visual Studio runner, Microsoft.NET.Test.Sdk, and
`Pixnop.Atlas.XUnit`, which must be 0.13.1 or newer: older Atlas releases open the
synthetic join with the identification packet, which Stratum's first-packet gate drops,
so every scenario that joins a player times out.

The project is outside `VintageStory.slnx` on purpose: it needs a prepared install, and
a normal build must not depend on one. Nothing runs it automatically.

The vanilla side of these measurements lives in
[StratumParity](https://github.com/Pixnop/StratumParity), which runs the same probes
against both flavors and diffs them. This copy answers a narrower question: did this
change break behavior the fork documents.
