using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

internal static class StratumAnticheatReporter
{
	private const string BlockEntityOutOfRangeType = "block-entity-range";
	private const string BlockInteractionOutOfRangeType = "block-interaction-range";

	private static readonly object Lock = new object();
	private static readonly Dictionary<string, PlayerViolationState> PlayerViolations = new Dictionary<string, PlayerViolationState>(StringComparer.OrdinalIgnoreCase);

	public static void RecordBlockEntityOutOfRange(ServerMain server, ServerPlayer player, BlockPos pos)
	{
		if (server == null || player == null || pos == null)
		{
			return;
		}

		StratumRuntime.Config.EnsurePopulated();
		StratumAnticheatConfig config = StratumRuntime.Config.Anticheat;
		if (!config.Enabled || !config.BlockEntityOutOfRange.Enabled)
		{
			return;
		}

		DateTime now = DateTime.UtcNow;
		ViolationRecordResult result = RecordViolation(player, now, BlockEntityOutOfRangeType, FormatBlockPos(pos), config, config.BlockEntityOutOfRange);

		if (result.ShouldAlert)
		{
			SendStaffAlert(server, player, "block entity range", pos, result.RollingCount, result.TotalCount, config.BlockEntityOutOfRange.AlertWindowSeconds);
		}
	}

	public static bool RecordBlockInteractionOutOfRange(ServerMain server, ServerPlayer player, string action, BlockPos pos, double distance, double maxRange, out string disconnectReason)
	{
		disconnectReason = null;
		if (server == null || player == null || pos == null)
		{
			return false;
		}

		StratumRuntime.Config.EnsurePopulated();
		StratumAnticheatConfig config = StratumRuntime.Config.Anticheat;
		if (!config.Enabled || !config.BlockInteractionOutOfRange.Enabled)
		{
			return false;
		}

		string detail = action + " at " + FormatBlockPos(pos) + " distance=" + distance.ToString("0.##", CultureInfo.InvariantCulture) + "/" + maxRange.ToString("0.##", CultureInfo.InvariantCulture);
		DateTime now = DateTime.UtcNow;
		ViolationRecordResult result = RecordViolation(player, now, BlockInteractionOutOfRangeType, detail, config, config.BlockInteractionOutOfRange);
		if (result.ShouldAlert)
		{
			SendStaffAlert(server, player, "survival block reach", pos, result.RollingCount, result.TotalCount, config.BlockInteractionOutOfRange.AlertWindowSeconds);
		}

		if (config.BlockInteractionOutOfRange.KickConfirmedCheats && result.RollingCount >= config.BlockInteractionOutOfRange.KickAfterViolations)
		{
			disconnectReason = string.IsNullOrWhiteSpace(config.BlockInteractionOutOfRange.KickMessage)
				? "Disconnected by Stratum block reach protection"
				: config.BlockInteractionOutOfRange.KickMessage;
			return true;
		}

		return false;
	}

	private static ViolationRecordResult RecordViolation(ServerPlayer player, DateTime now, string type, string detail, StratumAnticheatConfig config, StratumAnticheatRuleConfig rule)
	{
		int rollingCount;
		int totalCount;
		bool shouldAlert;

		lock (Lock)
		{
			PruneStalePlayers(now, config);

			string playerKey = GetPlayerKey(player);
			if (!PlayerViolations.TryGetValue(playerKey, out PlayerViolationState state))
			{
				state = new PlayerViolationState(playerKey, player.PlayerName);
				PlayerViolations[playerKey] = state;
			}

			state.PlayerName = player.PlayerName;
			state.LastSeenUtc = now;
			state.TotalCount++;
			state.Events.AddLast(new PlayerViolationEvent(type, now, detail));

			while (state.Events.Count > config.MaxStoredViolationsPerPlayer)
			{
				state.Events.RemoveFirst();
			}

			TimeSpan window = TimeSpan.FromSeconds(rule.AlertWindowSeconds);
			DateTime cutoff = now - window;
			rollingCount = state.Events.Count(entry => entry.Type == type && entry.Utc >= cutoff);
			totalCount = state.TotalCount;

			DateTime lastAlertUtc = state.GetLastStaffAlertUtc(type);
			shouldAlert = config.StaffAlerts
				&& rollingCount >= rule.AlertAfterViolations
				&& now - lastAlertUtc >= TimeSpan.FromSeconds(rule.RepeatAlertSeconds);

			if (shouldAlert)
			{
				state.SetLastStaffAlertUtc(type, now);
			}
		}

		return new ViolationRecordResult(rollingCount, totalCount, shouldAlert);
	}

	private static void SendStaffAlert(ServerMain server, ServerPlayer player, string label, BlockPos pos, int rollingCount, int totalCount, int windowSeconds)
	{
		string message = StratumCommandText.Pill("Stratum AC", StratumCommandText.Bad)
			+ " "
			+ StratumCommandText.Warning(player.PlayerName)
			+ " has "
			+ rollingCount.ToString(CultureInfo.InvariantCulture)
			+ " "
			+ StratumCommandText.Escape(label)
			+ " violations in "
			+ windowSeconds.ToString(CultureInfo.InvariantCulture)
			+ "s. Total recorded: "
			+ totalCount.ToString(CultureInfo.InvariantCulture)
			+ ". Last: "
			+ StratumCommandText.Escape(FormatBlockPos(pos));

		foreach (ConnectedClient client in server.Clients.Values)
		{
			if (ShouldReceiveStaffAlert(client))
			{
				client.Player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
			}
		}
	}

	private static bool ShouldReceiveStaffAlert(ConnectedClient client)
	{
		if (client == null || !client.State.IsAdmitted() || client.Player == null)
		{
			return false;
		}

		return client.Player.HasPrivilege(Privilege.controlserver) || StratumCommandAccessCatalog.PlayerHasAccess(client.Player, StratumRuntime.Config.Commands.StaffChat);
	}

	private static void PruneStalePlayers(DateTime now, StratumAnticheatConfig config)
	{
		TimeSpan keepFor = TimeSpan.FromMinutes(config.KeepPlayerViolationsMinutes);
		List<string> staleKeys = null;
		foreach (KeyValuePair<string, PlayerViolationState> entry in PlayerViolations)
		{
			if (now - entry.Value.LastSeenUtc <= keepFor)
			{
				continue;
			}

			staleKeys ??= new List<string>();
			staleKeys.Add(entry.Key);
		}

		if (staleKeys == null)
		{
			return;
		}

		foreach (string key in staleKeys)
		{
			PlayerViolations.Remove(key);
		}
	}

	private static string GetPlayerKey(ServerPlayer player)
	{
		return !string.IsNullOrWhiteSpace(player.PlayerUID) ? player.PlayerUID : player.PlayerName;
	}

	private static string FormatBlockPos(BlockPos pos)
	{
		return pos.X.ToString(CultureInfo.InvariantCulture) + ", " + pos.InternalY.ToString(CultureInfo.InvariantCulture) + ", " + pos.Z.ToString(CultureInfo.InvariantCulture);
	}

	private sealed class PlayerViolationState
	{
		public PlayerViolationState(string playerKey, string playerName)
		{
			PlayerKey = playerKey;
			PlayerName = playerName;
		}

		public string PlayerKey { get; }

		public string PlayerName { get; set; }

		public int TotalCount { get; set; }

		public DateTime LastSeenUtc { get; set; }

		public DateTime LastStaffAlertUtc { get; set; } = DateTime.MinValue;

		private Dictionary<string, DateTime> LastStaffAlertsByType { get; } = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

		public LinkedList<PlayerViolationEvent> Events { get; } = new LinkedList<PlayerViolationEvent>();

		public DateTime GetLastStaffAlertUtc(string type)
		{
			return LastStaffAlertsByType.TryGetValue(type, out DateTime utc) ? utc : DateTime.MinValue;
		}

		public void SetLastStaffAlertUtc(string type, DateTime utc)
		{
			LastStaffAlertUtc = utc;
			LastStaffAlertsByType[type] = utc;
		}
	}

	private readonly struct PlayerViolationEvent
	{
		public PlayerViolationEvent(string type, DateTime utc, string detail)
		{
			Type = type;
			Utc = utc;
			Detail = detail;
		}

		public string Type { get; }

		public DateTime Utc { get; }

		public string Detail { get; }
	}

	private readonly struct ViolationRecordResult
	{
		public ViolationRecordResult(int rollingCount, int totalCount, bool shouldAlert)
		{
			RollingCount = rollingCount;
			TotalCount = totalCount;
			ShouldAlert = shouldAlert;
		}

		public int RollingCount { get; }

		public int TotalCount { get; }

		public bool ShouldAlert { get; }
	}
}
