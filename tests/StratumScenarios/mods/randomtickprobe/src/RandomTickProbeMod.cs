using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace StratumParityProbe
{
	public class RandomTickProbeModSystem : ModSystem
	{
		public override void Start(ICoreAPI api)
		{
			base.Start(api);
			api.RegisterBlockClass("RandomTickProbeBlock", typeof(RandomTickProbeBlock));
		}
	}

	/// <summary>
	/// Opts into every server random tick and converts itself to granite when one lands.
	/// The world itself is the counter: scenarios place a platform of these and count how
	/// many turned to granite, no cross-assembly state needed (the ModLoader compiles
	/// this source mod into its own assembly).
	/// </summary>
	public class RandomTickProbeBlock : Block
	{
		public override bool ShouldReceiveServerGameTicks(IWorldAccessor world, BlockPos pos, Random offThreadRandom, out object extra)
		{
			extra = null;
			return true;
		}

		public override void OnServerGameTick(IWorldAccessor world, BlockPos pos, object extra = null)
		{
			Block granite = world.GetBlock(new AssetLocation("game:rock-granite"));
			world.BlockAccessor.SetBlock(granite.BlockId, pos);
		}
	}
}
