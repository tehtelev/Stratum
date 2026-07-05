using System;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Vintagestory.Server;

// Shared eye-to-hitbox reach math. One place so block reach, entity-interaction reach, and (later)
// combat reach all measure the same way: shortest distance from the player's eye to the target's
// axis-aligned box, compared against PickingRange + a slack for lag. Nothing should be interactable
// beyond that.
internal static class StratumReach
{
	public static FastVec3d EyePos(ServerPlayer player)
	{
		return player.Entity.Pos.XYZFast.Add(player.Entity.LocalEyePos);
	}

	public static double MaxReach(ServerPlayer player, double slack)
	{
		return Math.Max(0.0, player.WorldData.PickingRange) + Math.Max(0.0, slack);
	}

	// Shortest distance from the eye to the block's unit cell AABB.
	public static double BlockDistance(ServerPlayer player, BlockPos pos)
	{
		FastVec3d eyes = EyePos(player);
		double nearestX = Clamp(eyes.X, pos.X, pos.X + 1);
		double nearestY = Clamp(eyes.Y, pos.InternalY, pos.InternalY + 1);
		double nearestZ = Clamp(eyes.Z, pos.Z, pos.Z + 1);
		double dx = eyes.X - nearestX;
		double dy = eyes.Y - nearestY;
		double dz = eyes.Z - nearestZ;
		return Math.Sqrt(dx * dx + dy * dy + dz * dz);
	}

	// Shortest distance from the eye to the entity's selection hitbox (falls back to the entity
	// centre if it has no selection box).
	public static double EntityDistance(ServerPlayer player, Entity target)
	{
		FastVec3d eyes = EyePos(player);
		if (target.SelectionBox == null)
		{
			double dx = eyes.X - target.Pos.X;
			double dy = eyes.Y - target.Pos.Y;
			double dz = eyes.Z - target.Pos.Z;
			return Math.Sqrt(dx * dx + dy * dy + dz * dz);
		}

		Cuboidd box = target.SelectionBox.ToDouble().Translate(target.Pos.X, target.Pos.Y, target.Pos.Z);
		return box.ShortestDistanceFrom(eyes.X, eyes.Y, eyes.Z);
	}

	private static double Clamp(double value, double min, double max)
	{
		if (value < min) return min;
		if (value > max) return max;
		return value;
	}
}
