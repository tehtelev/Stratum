using Xunit;

// Required by Atlas: one in-process server per test class, classes must run sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// The random tick probe mod is staged per class ([AtlasWorld(Mods = ...)] on the two
// random tick classes) rather than assembly-wide: the extra mod lengthens the
// off-thread server assets packet build at every boot, which proved crash-prone on
// slow CI runners for classes that never use the block.
