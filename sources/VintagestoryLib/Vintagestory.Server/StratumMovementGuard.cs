using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Vintagestory.Server;

// Server-authoritative flight and noclip detection. Companion to the speed/teleport guard in ServerUdpNetwork which catches fast movement,
// but hovering / air-walk is slow and noclip moves at normal walking speed, so neither trips a speed budget so we validate against the authoritative world instead
// is the player unsupported yet not falling (flight), or embedded in solid blocks (noclip) - rubberband them to the last known-good position
// and escalate to a kick through the shared anticheat reporter only after sustained violations.
//
// some cheat clients never send a ModeChange packet so the server's WorldData flags stay clean; the only evidence is the position stream, which is what this reads.
internal static class StratumMovementGuard
{
	private static readonly object gate = new object();
	private static readonly Dictionary<string, MoveState> states = new Dictionary<string, MoveState>(StringComparer.Ordinal);

	public static void ForgetPlayer(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return;
		}

		lock (gate)
		{
			states.Remove(key);
		}
	}

	// Called after the packet has already been applied to entity.Pos. Returns true to accept. On false the caller must rubberband the client (entity.Pos has been reset to a safe position)
	// and also when disconnectReason is non-null, disconnect the player.
	public static bool TryAccept(ServerMain server, ServerPlayer player, EntityPlayer entity, string key, out string disconnectReason)
	{
		disconnectReason = null;
		if (server == null || player == null || entity?.Pos == null || string.IsNullOrEmpty(key))
		{
			return true;
		}

		StratumConfig stratumConfig = StratumRuntime.Config;
		stratumConfig.EnsurePopulated();
		StratumMovementAnticheatConfig config = stratumConfig.Anticheat.Movement;
		if (!stratumConfig.Anticheat.Enabled || !config.Enabled || (!config.DetectFlight && !config.DetectNoclip))
		{
			return true;
		}

		// Same legitimate-bypass exemptions as the speed guard: creative, spectator, and any server-granted free-move / noclip privilege.
		if (player.WorldData.CurrentGameMode == EnumGameMode.Creative
			|| player.WorldData.CurrentGameMode == EnumGameMode.Spectator
			|| player.WorldData.FreeMove
			|| player.WorldData.NoClip)
		{
			ResetToCurrent(key, entity);
			return true;
		}

		BlockPos feetCell = entity.Pos.AsBlockPos;
		if (feetCell == null)
		{
			return true;
		}

		long now = server.ElapsedMilliseconds;
		bool supported = IsSupported(server, entity, feetCell, config);
		bool embedded = config.DetectNoclip && IsEmbedded(server, entity, feetCell);

		MoveState state;
		lock (gate)
		{
			if (!states.TryGetValue(key, out state))
			{
				state = new MoveState();
				states[key] = state;
			}
		}

		// Remember the last position the player was provably standing somewhere legitimate; this is where we rubberband them back to.
		if (supported && !embedded)
		{
			state.HasSafePos = true;
			state.SafeX = entity.Pos.X;
			state.SafeY = entity.Pos.Y;
			state.SafeZ = entity.Pos.Z;
		}

		if (config.DetectNoclip)
		{
			state.EmbeddedTicks = embedded ? state.EmbeddedTicks + 1 : 0;
			if (state.EmbeddedTicks >= Math.Max(2, config.NoclipConsecutiveTicks))
			{
				state.EmbeddedTicks = 0;
				state.AirborneSinceMs = 0;
				Rubberband(entity, state);
				StratumAnticheatReporter.RecordMovementViolation(server, player, entity.Pos.AsBlockPos, "noclip: chest inside solid blocks", out disconnectReason);
				return false;
			}
		}

		if (config.DetectFlight)
		{
			if (supported)
			{
				state.AirborneSinceMs = 0;
			}
			else if (state.AirborneSinceMs == 0)
			{
				state.AirborneSinceMs = now;
				state.AirborneStartY = entity.Pos.Y;
			}
			else
			{
				double airborneSeconds = (now - state.AirborneSinceMs) / 1000.0;
				double descended = state.AirborneStartY - entity.Pos.Y;
				if (airborneSeconds >= config.FlightMinAirborneSeconds && descended < config.FlightMaxNonDescentBlocks)
				{
					state.AirborneSinceMs = 0;
					Rubberband(entity, state);
					StratumAnticheatReporter.RecordMovementViolation(server, player, entity.Pos.AsBlockPos, "flight: airborne without descent", out disconnectReason);
					return false;
				}
			}
		}

		return true;
	}

	private static void Rubberband(EntityPlayer entity, MoveState state)
	{
		if (state.HasSafePos)
		{
			entity.Pos.X = state.SafeX;
			entity.Pos.Y = state.SafeY;
			entity.Pos.Z = state.SafeZ;
		}

		entity.Pos.Motion.Set(0.0, 0.0, 0.0);
	}

	private static void ResetToCurrent(string key, EntityPlayer entity)
	{
		lock (gate)
		{
			if (!states.TryGetValue(key, out MoveState state))
			{
				state = new MoveState();
				states[key] = state;
			}

			state.AirborneSinceMs = 0;
			state.EmbeddedTicks = 0;
			state.HasSafePos = true;
			state.SafeX = entity.Pos.X;
			state.SafeY = entity.Pos.Y;
			state.SafeZ = entity.Pos.Z;
		}
	}

	// Supported = standing on / within step range of a collidable block, or in/over liquid, or on a climbable block. Any of these is a legitimate reason to not be falling
	// so flight is only considered when none hold. Unloaded chunks count as supported so we never flag on missing data.
	private static bool IsSupported(ServerMain server, EntityPlayer entity, BlockPos feetCell, StratumMovementAnticheatConfig config)
	{
		var blocks = server.WorldMap.RelaxedBlockAccess;
		int feetY = feetCell.Y;
		BlockPos probe = feetCell.Copy();

		// Body column (just below feet up to head): liquid or climbable surfaces.
		for (int dy = -1; dy <= 1; dy++)
		{
			probe.Y = feetY + dy;
			Block block = blocks.GetBlock(probe);
			if (block == null)
			{
				return true;
			}

			if (block.Climbable)
			{
				return true;
			}

			Block fluid = blocks.GetBlock(probe, 2);
			if (fluid != null && fluid.IsLiquid())
			{
				return true;
			}
		}

		// Ground within the scan depth below the feet.
		int depth = Math.Max(1, config.FlightGroundScanDepth);
		for (int d = 0; d <= depth; d++)
		{
			probe.Y = feetY - d;
			Block block = blocks.GetBlock(probe);
			if (block == null)
			{
				return true;
			}

			Cuboidf[] boxes = block.GetCollisionBoxes(blocks, probe);
			if (boxes != null && boxes.Length > 0)
			{
				return true;
			}
		}

		return false;
	}

	// Embedded = the player's chest point lies inside the collision geometry of the block occupying that cell. 
	// Testing the chest (not the feet) means partial boxes a player legitimately stands in/under - slabs, snow layers, paths, fences - never count.
	private static bool IsEmbedded(ServerMain server, EntityPlayer entity, BlockPos feetCell)
	{
		double px = entity.Pos.X;
		double py = entity.Pos.Y;
		double pz = entity.Pos.Z;
		double chestWorldY = py + 0.9;

		int chestCellWorldY = (int)Math.Floor(chestWorldY);
		int feetCellWorldY = (int)Math.Floor(py);

		BlockPos chestCell = feetCell.Copy();
		chestCell.Y = feetCell.Y + (chestCellWorldY - feetCellWorldY);

		var blocks = server.WorldMap.RelaxedBlockAccess;
		Block block = blocks.GetBlock(chestCell);
		if (block == null)
		{
			return false;
		}

		Cuboidf[] boxes = block.GetCollisionBoxes(blocks, chestCell);
		if (boxes == null || boxes.Length == 0)
		{
			return false;
		}

		double lx = px - Math.Floor(px);
		double ly = chestWorldY - chestCellWorldY;
		double lz = pz - Math.Floor(pz);
		foreach (Cuboidf box in boxes)
		{
			if (box == null)
			{
				continue;
			}

			if (lx >= box.X1 && lx <= box.X2 && ly >= box.Y1 && ly <= box.Y2 && lz >= box.Z1 && lz <= box.Z2)
			{
				return true;
			}
		}

		return false;
	}

	private sealed class MoveState
	{
		public long AirborneSinceMs;
		public double AirborneStartY;
		public int EmbeddedTicks;
		public bool HasSafePos;
		public double SafeX;
		public double SafeY;
		public double SafeZ;
	}
}
