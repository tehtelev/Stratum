using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

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
		string manifestPath = Path.Combine(installDir, ".stratum-overlay-manifest");
		Assembly self = typeof(EmbeddedOverlay).Assembly;
		string[] names = GetOverlayResourceNames(self);
		string expectedManifest = BuildManifest(self, overlayStamp, names);
		bool overlayMatches = File.Exists(markerPath)
			&& File.ReadAllText(markerPath).Trim() == overlayStamp
			&& File.Exists(manifestPath)
			&& string.Equals(File.ReadAllText(manifestPath), expectedManifest, StringComparison.Ordinal);
		if (overlayMatches)
		{
			return 0;
		}

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
		File.WriteAllText(manifestPath, expectedManifest);
		return written;
	}

	internal static bool HasEmbeddedOverlay()
	{
		return typeof(EmbeddedOverlay).Assembly
			.GetManifestResourceNames()
			.Any(n => n.StartsWith(RootPrefix, StringComparison.Ordinal) || n.StartsWith(ModsPrefix, StringComparison.Ordinal));
	}

	private static string[] GetOverlayResourceNames(Assembly assembly)
	{
		return assembly.GetManifestResourceNames()
			.Where(n => n.StartsWith(RootPrefix, StringComparison.Ordinal) || n.StartsWith(ModsPrefix, StringComparison.Ordinal))
			.OrderBy(n => n, StringComparer.Ordinal)
			.ToArray();
	}

	private static string BuildManifest(Assembly assembly, string overlayStamp, string[] names)
	{
		StringBuilder manifest = new StringBuilder();
		manifest.Append("stamp=").Append(overlayStamp).Append('\n');
		foreach (string name in names)
		{
			using Stream resource = assembly.GetManifestResourceStream(name)
				?? throw new InvalidOperationException($"Embedded resource missing: {name}");
			manifest.Append(name).Append('|').Append(resource.Length).Append('|').Append(HashResource(resource)).Append('\n');
		}
		return manifest.ToString();
	}

	private static string HashResource(Stream stream)
	{
		using SHA256 sha = SHA256.Create();
		byte[] hash = sha.ComputeHash(stream);
		return Convert.ToHexString(hash);
	}
}
