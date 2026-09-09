# StratumScenarios

Fourteen end-to-end scenarios that boot a real server in-process (through the
[Atlas](https://github.com/Pixnop/Atlas) test harness) and assert documented fork
behavior: chunk persistence across save/unload/reload, the simulation distance throttles
for entities, random ticks and block tick listeners, and the fact that each
`stratum-performance.json` toggle really restores vanilla behavior. `BootScenarios` also
guards against the worst false green available here, a build made without
`-p:EmbedPatchedFiles=true` that boots the downloaded vanilla lib instead of this repo's.

Run it with `make scenarios`. That builds, materializes the install with one
`--stratum-prepare-only` launch, and runs `dotnet test` with `VINTAGE_STORY` pointing at
it. Around one minute of scenario time plus one server boot per test class, so a few
minutes in total. It pulls one NuGet package, `Pixnop.Atlas.XUnit`.

The project is outside `VintageStory.slnx` on purpose: it needs a prepared install, and
a normal build must not depend on one. Nothing runs it automatically.

The vanilla side of these measurements lives in
[StratumParity](https://github.com/Pixnop/StratumParity), which runs the same probes
against both flavors and diffs them. This copy answers a narrower question: did this
change break behavior the fork documents.
