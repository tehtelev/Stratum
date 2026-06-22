using System;
using System.Runtime.InteropServices;
using System.Text;
using Vintagestory.API.Common;

namespace Vintagestory.Server;

internal static class StratumConsole
{
	private const string Prompt = "> ";
	private static readonly object SyncRoot = new object();
	private static readonly StringBuilder CurrentInput = new StringBuilder();
	private static bool interactive;
	private static bool inputLineActive;
	private static int lastPromptLength;

	public static bool IsInteractive
	{
		get
		{
			lock (SyncRoot)
			{
				return interactive;
			}
		}
	}

	public static void Initialize()
	{
		lock (SyncRoot)
		{
			interactive = ShouldUseInteractivePrompt();
			inputLineActive = interactive;
			CurrentInput.Clear();
			RenderPromptLocked();
		}
	}

	public static void Shutdown()
	{
		lock (SyncRoot)
		{
			if (interactive && inputLineActive)
			{
				ClearInputLineLocked();
				Console.WriteLine();
			}
			inputLineActive = false;
			CurrentInput.Clear();
		}
	}

	public static void RecordInput(ReadOnlySpan<char> input)
	{
		if (!interactive)
		{
			return;
		}

		lock (SyncRoot)
		{
			for (int i = 0; i < input.Length; i++)
			{
				char c = input[i];
				if (c == '\r' || c == '\n')
				{
					CurrentInput.Clear();
					continue;
				}
				if (c == '\b')
				{
					if (CurrentInput.Length > 0)
					{
						CurrentInput.Length--;
					}
					continue;
				}
				if (!char.IsControl(c))
				{
					CurrentInput.Append(c);
				}
			}
		}
	}

	public static bool TryHandleKey(ConsoleKeyInfo key, out string submittedLine)
	{
		submittedLine = null;
		if (!interactive)
		{
			return false;
		}

		lock (SyncRoot)
		{
			switch (key.Key)
			{
				case ConsoleKey.Enter:
					submittedLine = CurrentInput.ToString();
					CurrentInput.Clear();
					ClearInputLineLocked();
					inputLineActive = false;
					Console.WriteLine();
					RenderPromptLocked();
					return true;
				case ConsoleKey.Backspace:
					if (CurrentInput.Length > 0)
					{
						CurrentInput.Length--;
						RenderPromptLocked();
					}
					return false;
				case ConsoleKey.Escape:
					CurrentInput.Clear();
					RenderPromptLocked();
					return false;
				default:
					if (!char.IsControl(key.KeyChar))
					{
						CurrentInput.Append(key.KeyChar);
						RenderPromptLocked();
					}
					return false;
			}
		}
	}

	public static void ClearSubmittedInput()
	{
		if (!interactive)
		{
			return;
		}

		lock (SyncRoot)
		{
			CurrentInput.Clear();
			if (inputLineActive)
			{
				RenderPromptLocked();
			}
		}
	}

	public static void WriteLogLine(EnumLogType logType, string line)
	{
		lock (SyncRoot)
		{
			if (!interactive)
			{
				WriteColoredLine(logType, line);
				return;
			}

			ClearInputLineLocked();
			WriteColoredLine(logType, line);
			RenderPromptLocked();
		}
	}

	public static void WriteLine(string line)
	{
		lock (SyncRoot)
		{
			if (interactive)
			{
				ClearInputLineLocked();
				Console.WriteLine(line);
				RenderPromptLocked();
			}
			else
			{
				Console.WriteLine(line);
			}
		}
	}

	private static void WriteColoredLine(EnumLogType logType, string line)
	{
		ConsoleColor previous = Console.ForegroundColor;
		try
		{
			Console.ForegroundColor = GetColor(logType, previous);
			Console.WriteLine(line);
		}
		catch
		{
			Console.WriteLine(line);
		}
		finally
		{
			try
			{
				Console.ForegroundColor = previous;
			}
			catch
			{
			}
		}
	}

	private static ConsoleColor GetColor(EnumLogType logType, ConsoleColor fallback)
	{
		switch (logType)
		{
			case EnumLogType.Fatal:
				return ConsoleColor.White;
			case EnumLogType.Error:
				return ConsoleColor.Red;
			case EnumLogType.Warning:
				return ConsoleColor.Yellow;
			case EnumLogType.Event:
				return ConsoleColor.Cyan;
			case EnumLogType.Chat:
				return ConsoleColor.Green;
			case EnumLogType.Debug:
			case EnumLogType.VerboseDebug:
				return ConsoleColor.DarkGray;
			case EnumLogType.Worldgen:
				return ConsoleColor.DarkCyan;
			default:
				return fallback;
		}
	}

	private static bool ShouldUseInteractivePrompt()
	{
		if (Console.IsInputRedirected || Console.IsOutputRedirected)
		{
			return false;
		}

		string overrideValue = Environment.GetEnvironmentVariable("STRATUM_INTERACTIVE_CONSOLE");
		if (string.Equals(overrideValue, "1", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(overrideValue, "true", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		// Stratum: keep the ReadKey based prompt on Windows. Linux terminals, tmux,
		// docker attach, and systemd consoles can break when we switch to key mode.
		return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
	}

	private static void RenderPromptLocked()
	{
		if (!interactive)
		{
			return;
		}

		ClearInputLineLocked();
		string promptText = Prompt + CurrentInput;
		Console.ForegroundColor = ConsoleColor.DarkGray;
		Console.Write(Prompt);
		Console.ResetColor();
		Console.Write(CurrentInput.ToString());
		lastPromptLength = promptText.Length;
		inputLineActive = true;
	}

	private static void ClearInputLineLocked()
	{
		if (!interactive || !inputLineActive)
		{
			return;
		}

		try
		{
			int width = Math.Max(Console.BufferWidth - 1, Math.Max(lastPromptLength, Prompt.Length + CurrentInput.Length));
			Console.Write('\r');
			Console.Write(new string(' ', width));
			Console.Write('\r');
		}
		catch
		{
			Console.WriteLine();
		}
	}
}
