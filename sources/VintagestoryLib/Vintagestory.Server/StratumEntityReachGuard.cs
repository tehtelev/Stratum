using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Vintagestory.Server;

// Survival reach check for entity interactions (right-click: feed, trade, mount, and melee).
// Mirrors StratumBlockReachGuard and shares StratumReach, so an entity is no more reachable than a
// block: nothing interactable beyond PickingRange + slack. Vanilla only enforces entity reach
// loosely (a coarse horizontal range, and a stricter check gated behind server AntiAbuse), so this
// closes the reach-hack gap uniformly.
internal static class StratumEntityReachGuard
{
	// Returns true to accept. On false the caller must not process the interaction; when
	// disconnectReason is non-null it should also disconnect the player.
	public static bool TryAcceptEntityInteract(ServerMain server, ConnectedClient client, Entity target, out string disconnectReason)
	{
		disconnectReason = null;
		ServerPlayer player = client?.Player;
		if (server == null || player?.Entity == null || target?.Pos == null)
		{
			return true;
		}

		StratumRuntime.Config.EnsurePopulated();
		StratumAnticheatConfig anticheat = StratumRuntime.Config.Anticheat;
		StratumEntityInteractionOutOfRangeAnticheatConfig config = anticheat.EntityInteractionOutOfRange;
		if (!anticheat.Enabled || !config.Enabled || player.WorldData.CurrentGameMode != EnumGameMode.Survival)
		{
			return true;
		}

		double maxRange = StratumReach.MaxReach(player, config.RangeSlack);
		double distance = StratumReach.EntityDistance(player, target);
		if (distance <= maxRange)
		{
			return true;
		}

		ServerMain.Logger.Audit(
			"{0} tried to interact with entity {1} out of survival reach {2:0.##}/{3:0.##}",
			player.PlayerName,
			target.Code?.ToString() ?? target.EntityId.ToString(),
			distance,
			maxRange);

		bool shouldKick = StratumAnticheatReporter.RecordEntityInteractionOutOfRange(server, player, target, distance, maxRange, out disconnectReason);
		if (shouldKick)
		{
			return false;
		}

		disconnectReason = null;
		return false;
	}
}
