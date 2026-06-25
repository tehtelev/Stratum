namespace Vintagestory.Server;

internal static class StratumCommandRegistration
{
	public static bool ShouldRegister(StratumCommandAccessConfig access, string commandLabel, string configPath)
	{
		if (access != null && access.Enabled)
		{
			return true;
		}

		StratumRuntime.LogInfo("skipped " + commandLabel + " because " + configPath + ".Enabled=false");
		return false;
	}

	public static bool ShouldRegister(bool enabled, string commandLabel, string configPath)
	{
		if (enabled)
		{
			return true;
		}

		StratumRuntime.LogInfo("skipped " + commandLabel + " because " + configPath + "=false");
		return false;
	}
}
