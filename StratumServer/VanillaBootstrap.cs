using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace StratumServer;

// Downloads the matching vanilla server zip on first run (or version bump) and copies
// across any files the install doesn't already have. We never redistribute vanilla bytes.
internal static class VanillaBootstrap
{
	private const string CdnBase = "https://cdn.vintagestory.at/gamefiles/stable";

	internal static void EnsureVanillaAssets(string baseGameVersion, bool refresh)
	{
		string installDir = AppContext.BaseDirectory;
		string markerPath = Path.Combine(installDir, ".stratum-base");
		string expectedMarker = baseGameVersion;

		if (!refresh && File.Exists(markerPath) && File.ReadAllText(markerPath).Trim() == expectedMarker)
		{
			return;
		}

		(string archiveName, bool isZip) = GetArchiveForPlatform(baseGameVersion);
		string cacheDir = Path.Combine(installDir, ".vanilla-cache");
		Directory.CreateDirectory(cacheDir);
		string archivePath = Path.Combine(cacheDir, archiveName);

		if (!File.Exists(archivePath))
		{
			string url = $"{CdnBase}/{archiveName}";
			Console.WriteLine($"Stratum: downloading vanilla base game from {url}");
			DownloadFile(url, archivePath);
		}
		else
		{
			Console.WriteLine($"Stratum: using cached {archivePath}");
		}

		string extractDir = Path.Combine(cacheDir, "extract");
		if (Directory.Exists(extractDir))
		{
			Directory.Delete(extractDir, recursive: true);
		}
		Directory.CreateDirectory(extractDir);

		Console.WriteLine($"Stratum: unpacking {archiveName}");
		if (isZip)
		{
			ZipFile.ExtractToDirectory(archivePath, extractDir);
		}
		else
		{
			ExtractTarGz(archivePath, extractDir);
		}

		string sourceRoot = FindContentRoot(extractDir);
		int copied = OverlayMissingFiles(sourceRoot, installDir);
		Console.WriteLine($"Stratum: installed {copied} vanilla file(s) (existing files were preserved)");

		File.WriteAllText(markerPath, expectedMarker);
	}

	private static (string ArchiveName, bool IsZip) GetArchiveForPlatform(string version)
	{
		string arch = RuntimeInformation.ProcessArchitecture switch
		{
			Architecture.X64 => "x64",
			Architecture.Arm64 => "arm64",
			_ => "x64"
		};

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return ($"vs_server_win-{arch}_{version}.zip", true);
		}

		return ($"vs_server_linux-{arch}_{version}.tar.gz", false);
	}

	private static void DownloadFile(string url, string destination)
	{
		using HttpClient client = new();
		client.Timeout = TimeSpan.FromMinutes(10);
		using HttpResponseMessage response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
		response.EnsureSuccessStatusCode();
		using FileStream output = File.Create(destination);
		response.Content.CopyToAsync(output).GetAwaiter().GetResult();
	}

	private static void ExtractTarGz(string archivePath, string destination)
	{
		using FileStream input = File.OpenRead(archivePath);
		using GZipStream gz = new(input, CompressionMode.Decompress);
		System.Formats.Tar.TarFile.ExtractToDirectory(gz, destination, overwriteFiles: true);
	}

	private static string FindContentRoot(string extractDir)
	{
		// The Linux tarball wraps content in a top-level "server/" folder; the Windows zip doesn't.
		string nested = Path.Combine(extractDir, "server");
		if (Directory.Exists(nested))
		{
			return nested;
		}

		string[] entries = Directory.GetFileSystemEntries(extractDir);
		if (entries.Length == 1 && Directory.Exists(entries[0]))
		{
			return entries[0];
		}

		return extractDir;
	}

	private static int OverlayMissingFiles(string sourceRoot, string installDir)
	{
		int copied = 0;
		foreach (string sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
		{
			string rel = Path.GetRelativePath(sourceRoot, sourcePath);
			string destPath = Path.Combine(installDir, rel);
			if (File.Exists(destPath))
			{
				continue;
			}

			string destDir = Path.GetDirectoryName(destPath);
			if (destDir != null)
			{
				Directory.CreateDirectory(destDir);
			}
			File.Copy(sourcePath, destPath);
			copied++;
		}
		return copied;
	}
}
