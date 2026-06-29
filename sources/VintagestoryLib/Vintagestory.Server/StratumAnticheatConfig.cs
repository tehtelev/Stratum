using System;

namespace Vintagestory.Server;

internal class StratumAnticheatConfig
{
	public bool Enabled { get; set; } = true;

	public bool StaffAlerts { get; set; } = true;

	public int MaxStoredViolationsPerPlayer { get; set; } = 128;

	public int KeepPlayerViolationsMinutes { get; set; } = 60;

	public StratumBlockEntityOutOfRangeAnticheatConfig BlockEntityOutOfRange { get; set; } = new StratumBlockEntityOutOfRangeAnticheatConfig();

	public StratumBlockInteractionOutOfRangeAnticheatConfig BlockInteractionOutOfRange { get; set; } = new StratumBlockInteractionOutOfRangeAnticheatConfig();

	public StratumBlockBreakProgressAnticheatConfig BlockBreakProgress { get; set; } = new StratumBlockBreakProgressAnticheatConfig();

	public StratumMovementAnticheatConfig Movement { get; set; } = new StratumMovementAnticheatConfig();

	public void EnsureSane()
	{
		MaxStoredViolationsPerPlayer = Math.Clamp(MaxStoredViolationsPerPlayer, 16, 2048);
		KeepPlayerViolationsMinutes = Math.Clamp(KeepPlayerViolationsMinutes, 5, 1440);
		BlockEntityOutOfRange ??= new StratumBlockEntityOutOfRangeAnticheatConfig();
		BlockInteractionOutOfRange ??= new StratumBlockInteractionOutOfRangeAnticheatConfig();
		BlockBreakProgress ??= new StratumBlockBreakProgressAnticheatConfig();
		Movement ??= new StratumMovementAnticheatConfig();
		BlockEntityOutOfRange.EnsureSane();
		BlockInteractionOutOfRange.EnsureSane();
		BlockBreakProgress.EnsureSane();
		Movement.EnsureSane();
	}
}

internal class StratumAnticheatRuleConfig
{
	public bool Enabled { get; set; } = true;

	public int AlertAfterViolations { get; set; } = 8;

	public int AlertWindowSeconds { get; set; } = 10;

	public int RepeatAlertSeconds { get; set; } = 20;

	public virtual void EnsureSane()
	{
		AlertAfterViolations = Math.Clamp(AlertAfterViolations, 2, 200);
		AlertWindowSeconds = Math.Clamp(AlertWindowSeconds, 2, 300);
		RepeatAlertSeconds = Math.Clamp(RepeatAlertSeconds, 5, 600);
	}
}

internal class StratumBlockEntityOutOfRangeAnticheatConfig : StratumAnticheatRuleConfig
{
}

internal class StratumBlockInteractionOutOfRangeAnticheatConfig : StratumAnticheatRuleConfig
{
	public double RangeSlack { get; set; } = 0.75;

	public bool KickConfirmedCheats { get; set; } = true;

	public int KickAfterViolations { get; set; } = 3;

	public string KickMessage { get; set; } = "Disconnected by Stratum block reach protection";

	public override void EnsureSane()
	{
		base.EnsureSane();
		RangeSlack = Math.Clamp(RangeSlack, 0, 4);
		KickAfterViolations = Math.Clamp(KickAfterViolations, 2, 100);
		KickMessage ??= "Disconnected by Stratum block reach protection";
	}
}

internal class StratumBlockBreakProgressAnticheatConfig : StratumAnticheatRuleConfig
{
	public bool KickConfirmedCheats { get; set; } = true;

	public int KickAfterViolations { get; set; } = 3;

	public string KickMessage { get; set; } = "Disconnected by Stratum block break protection";

	public override void EnsureSane()
	{
		base.EnsureSane();
		KickAfterViolations = Math.Clamp(KickAfterViolations, 2, 100);
		KickMessage ??= "Disconnected by Stratum block break protection";
	}
}

// Server-authoritative movement validation !!
// The server can never trust client WorldData flags for this: cheat clients keep FreeMove/NoClip/MoveSpeed at the server's values and only ever lie in the position stream, so we validate the position deltas themselves. 
// Violations are rubberbanded and, only when a player sustains many of them will it be escalated to a kick.
// Defaults are deliberately generous will be tested with time.
internal class StratumMovementAnticheatConfig : StratumAnticheatRuleConfig
{
	public StratumMovementAnticheatConfig()
	{
		// Movement violations cluster quickly, so use a shorter window and a high kick bar so only sustained cheating (not a lag spike) ever reaches a disconnect.
		AlertAfterViolations = 6;
		AlertWindowSeconds = 6;
		RepeatAlertSeconds = 15;
	}

	// Max horizontal speed before a position is rejected, in blocks/second.
	// vanilla sprint is ~6-7 b/s roughly, so this leaves wide headroom for speed potions, downhill, and minor desync. hopefully...
	public double MaxHorizontalBlocksPerSecond { get; set; } = 36;

	// Max upward speed in blocks/second. Jumps and knockback are brief and stay under this.
	public double MaxVerticalUpBlocksPerSecond { get; set; } = 18;

	// Max total distance travelled within a rolling 1s window, in blocks. Catches teleport hacks that stay under the per-packet cap by spreading the jump across packets.
	public double MaxCumulativeBlocksPerSecond { get; set; } = 64;

	// Flat per-packet distance allowance added on top of the speed budgets to absorb jitter.
	public double SlackBlocks { get; set; } = 2.5;

	public bool KickConfirmedCheats { get; set; } = true;

	public int KickAfterViolations { get; set; } = 25;

	public string KickMessage { get; set; } = "Disconnected by Stratum movement protection";

	// Flight / air-walk detection. Catches the slow hover/ascent that never trips the speed caps.
	// A survival player is "flying" when they stay unsupported (no solid within the scan depth
	// below, not in/over liquid, not on a climbable block) yet fail to lose altitude for longer
	// than any real jump arc. The descent rule keeps legitimate falls and paraglider descents
	// safe - only hovering or rising is flagged.
	public bool DetectFlight { get; set; } = true;

	// Cells scanned below the feet for something to stand on. 2 means flight only triggers when
	// the player is more than ~2 blocks above ground, so a jump apex never counts as flight.
	public int FlightGroundScanDepth { get; set; } = 2;

	// How long the player must be continuously unsupported before flight can be flagged.
	public double FlightMinAirborneSeconds { get; set; } = 1.5;

	// If, after that time, the player has descended less than this many blocks from where they
	// left support, they are hovering/ascending = flight. Gravity drops a real faller far more.
	public double FlightMaxNonDescentBlocks { get; set; } = 0.5;

	// Noclip detection. Flags when the player's chest point sits inside a block's collision
	// geometry for several consecutive position packets. Using the chest (not the feet) keeps
	// slabs, snow layers, paths, and fences from ever counting.
	public bool DetectNoclip { get; set; } = true;

	public int NoclipConsecutiveTicks { get; set; } = 3;

	public override void EnsureSane()
	{
		base.EnsureSane();
		MaxHorizontalBlocksPerSecond = Math.Clamp(MaxHorizontalBlocksPerSecond, 8, 1000);
		MaxVerticalUpBlocksPerSecond = Math.Clamp(MaxVerticalUpBlocksPerSecond, 4, 1000);
		MaxCumulativeBlocksPerSecond = Math.Clamp(MaxCumulativeBlocksPerSecond, MaxHorizontalBlocksPerSecond, 4000);
		SlackBlocks = Math.Clamp(SlackBlocks, 0, 32);
		KickAfterViolations = Math.Clamp(KickAfterViolations, 3, 1000);
		FlightGroundScanDepth = Math.Clamp(FlightGroundScanDepth, 1, 16);
		FlightMinAirborneSeconds = Math.Clamp(FlightMinAirborneSeconds, 0.5, 10);
		FlightMaxNonDescentBlocks = Math.Clamp(FlightMaxNonDescentBlocks, 0.1, 8);
		NoclipConsecutiveTicks = Math.Clamp(NoclipConsecutiveTicks, 2, 40);
		KickMessage ??= "Disconnected by Stratum movement protection";
	}
}
