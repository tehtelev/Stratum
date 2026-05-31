using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StratumServer;

// Writes our patched assemblies (and the seed stratum.json) on top of whatever
// the vanilla bootstrap just unpacked. Resources are embedded with a logical
// name prefix of "Stratum.Overlay." for root files and "Stratum.OverlayMods."
// for files that go in Mods\. Anything ending in .json is treated as a one-shot
// seed and only written when the destination doesn't already exist.
internal static class EmbeddedOverlay
{
	private const string RootPrefix = "Stratum.Overlay.";
	private const string ModsPrefix = "Stratum.OverlayMods.";

	internal static int Apply(string installDir, string overlayStamp)
	{
		string markerPath = Path.Combine(installDir, ".stratum-overlay");
		bool stampMatches = File.Exists(markerPath) && File.ReadAllText(markerPath).Trim() == overlayStamp;
		if (stampMatches)
		{
			return 0;
		}

		Assembly self = typeof(EmbeddedOverlay).Assembly;
		string[] names = self.GetManifestResourceNames()
			.Where(n => n.StartsWith(RootPrefix, StringComparison.Ordinal) || n.StartsWith(ModsPrefix, StringComparison.Ordinal))
			.ToArray();

		int written = 0;
		foreach (string name in names)
		{
			string targetDir = installDir;
			string fileName;
			if (name.StartsWith(ModsPrefix, StringComparison.Ordinal))
			{
				targetDir = Path.Combine(installDir, "Mods");
				fileName = name.Substring(ModsPrefix.Length);
			}
			else
			{
				fileName = name.Substring(RootPrefix.Length);
			}

			// Treat .json resources as one-shot seeds; never overwrite existing config.
			if (fileName.Equals("stratum.default.json", StringComparison.OrdinalIgnoreCase))
			{
				targetDir = Path.Combine(installDir, "Data");
				fileName = "stratum.json";
			}

			Directory.CreateDirectory(targetDir);
			string destPath = Path.Combine(targetDir, fileName);
			bool isSeed = string.Equals(Path.GetExtension(fileName), ".json", StringComparison.OrdinalIgnoreCase);
			if (isSeed && File.Exists(destPath))
			{
				continue;
			}

			using Stream resource = self.GetManifestResourceStream(name)
				?? throw new InvalidOperationException($"Embedded resource missing: {name}");
			using FileStream dest = File.Create(destPath);
			resource.CopyTo(dest);
			written++;
		}

		File.WriteAllText(markerPath, overlayStamp);
		return written;
	}

	internal static bool HasEmbeddedOverlay()
	{
		return typeof(EmbeddedOverlay).Assembly
			.GetManifestResourceNames()
			.Any(n => n.StartsWith(RootPrefix, StringComparison.Ordinal) || n.StartsWith(ModsPrefix, StringComparison.Ordinal));
	}
}
