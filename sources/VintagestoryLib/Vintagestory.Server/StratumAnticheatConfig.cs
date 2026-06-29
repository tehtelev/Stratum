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

	public void EnsureSane()
	{
		MaxStoredViolationsPerPlayer = Math.Clamp(MaxStoredViolationsPerPlayer, 16, 2048);
		KeepPlayerViolationsMinutes = Math.Clamp(KeepPlayerViolationsMinutes, 5, 1440);
		BlockEntityOutOfRange ??= new StratumBlockEntityOutOfRangeAnticheatConfig();
		BlockInteractionOutOfRange ??= new StratumBlockInteractionOutOfRangeAnticheatConfig();
		BlockEntityOutOfRange.EnsureSane();
		BlockInteractionOutOfRange.EnsureSane();
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
