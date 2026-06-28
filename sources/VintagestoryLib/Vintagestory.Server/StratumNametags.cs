using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

/// <summary>
/// Nametag and chat-tag system. Each player can have up to two prefix tags:
///   1. An entitlement tag derived from their real VS entitlement (e.g. "[VS Team]"),
///      coloured with a slightly adjusted version of their entitlement colour.
///   2. A role tag from the server's Chat.RolePrefixes config (e.g. "[Admin]").
///
/// By default both are shown together. Players with multiple tags can cycle through
/// display modes with /prefix.
///
/// Nametags above the head are single-colour (vanilla limit): we inject a fake entitlement
/// code so the vanilla renderer uses the right colour. Chat messages use full VTML so both
/// the prefix tag and the player name can be coloured independently.
/// </summary>
internal sealed class StratumNametags
{
	internal enum TagDisplayMode { All, EntitlementOnly, RoleOnly, None }

	internal readonly record struct TagOption(
		string Key,
		string TagText,
		string TagColorHex,
		bool TagBold,
		string NameColorHex,
		string EntitlementCodeForColor);

	private static StratumNametags instance;

	private readonly ServerMain server;

	private readonly Dictionary<string, string> injectedByUid = new(StringComparer.Ordinal);
	private readonly Dictionary<string, List<TagOption>> tagsByUid = new(StringComparer.Ordinal);
	private readonly Dictionary<string, TagDisplayMode> modeByUid = new(StringComparer.Ordinal);

	public StratumNametags(ServerMain server)
	{
		this.server = server;
		instance = this;
		server.EventManager.OnPlayerJoin += OnPlayerJoin;
		server.EventManager.OnPlayerDisconnect += OnPlayerDisconnect;

		server.api.commandapi.Create("prefix")
			.WithDesc("Show or cycle your active nametag/chat prefix")
			.WithArgs(server.api.commandapi.Parsers.OptionalWord(""))
			.HandleWith(HandlePrefixCommand);
	}

	private StratumNametagsConfig Cfg => StratumRuntime.Config?.Nametags;

	/// <summary>Called externally (e.g. on role change) to refresh a player's tag.</summary>
	public static bool RefreshFor(IServerPlayer player)
	{
		return instance?.Refresh(player) == true;
	}

	/// <summary>
	/// Returns the prefix VTML and the hex colour to apply to the player's name in chat.
	/// Returns false when the system is disabled or the player has no active tags.
	/// </summary>
	internal static bool TryGetChatFormat(IServerPlayer player, out string prefixVtml, out string nameColorHex)
	{
		prefixVtml = null;
		nameColorHex = null;
		if (instance == null) return false;
		return instance.BuildChatFormat(player, out prefixVtml, out nameColorHex);
	}

	private void OnPlayerJoin(IServerPlayer player)
	{
		Refresh(player);
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		if (player == null) return;
		injectedByUid.Remove(player.PlayerUID);
		tagsByUid.Remove(player.PlayerUID);
		modeByUid.Remove(player.PlayerUID);
	}

	private bool Refresh(IServerPlayer player)
	{
		StratumNametagsConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled) return false;
		if (player?.Role == null) return false;

		List<TagOption> tags = BuildTagOptions(player, cfg);
		tagsByUid[player.PlayerUID] = tags;

		TagDisplayMode mode = GetMode(player.PlayerUID, tags);

		if (cfg.ApplyChatPrefix)
		{
			ApplyNametagString(player, tags, mode, cfg);
		}

		RemoveInjectedEntitlement(player);
		InjectColorEntitlement(player, tags, mode, cfg);
		return true;
	}

	private List<TagOption> BuildTagOptions(IServerPlayer player, StratumNametagsConfig cfg)
	{
		var options = new List<TagOption>();

		// 1. Entitlement-based tags from the player's real VS entitlements
		if (cfg.ShowEntitlementPrefix && player is ServerPlayer sp && sp.Entitlements?.Count > 0)
		{
			foreach (Entitlement ent in sp.Entitlements)
			{
				if (string.IsNullOrWhiteSpace(ent?.Code)) continue;
				if (!GlobalConstants.playerColorByEntitlement.TryGetValue(ent.Code, out double[] rgba)) continue;

				string nameColorHex = DoubleArrayToHex(rgba);
				string tagColorHex = AdjustBrightness(rgba, cfg.EntitlementPrefixColorAdjust);
				string displayName = GetEntitlementDisplayName(ent.Code, cfg);

				options.Add(new TagOption(
					Key: "ent:" + ent.Code,
					TagText: displayName,
					TagColorHex: tagColorHex,
					TagBold: false,
					NameColorHex: nameColorHex,
					EntitlementCodeForColor: ent.Code));
			}
		}

		// 2. Role-based tag from Chat.RolePrefixes
		StratumChatRolePrefixConfig rolePrefix = FindRolePrefix(StratumRuntime.Config?.Chat?.RolePrefixes, player.Role.Code);
		if (rolePrefix != null && rolePrefix.Enabled && !string.IsNullOrWhiteSpace(rolePrefix.Tag))
		{
			string roleEntCode = null;
			cfg.EntitlementColorByRole?.TryGetValue(player.Role.Code, out roleEntCode);

			options.Add(new TagOption(
				Key: "role:" + player.Role.Code,
				TagText: rolePrefix.Tag,
				TagColorHex: rolePrefix.Color ?? "#ffffff",
				TagBold: rolePrefix.Bold,
				NameColorHex: rolePrefix.Color,
				EntitlementCodeForColor: roleEntCode));
		}

		return options;
	}

	private static void ApplyNametagString(IServerPlayer player, List<TagOption> tags, TagDisplayMode mode, StratumNametagsConfig cfg)
	{
		EntityPlayer entity = player.Entity;
		if (entity == null) return;

		string baseName = player.PlayerName;
		if (string.IsNullOrEmpty(baseName)) return;

		ITreeAttribute nametagTree = entity.WatchedAttributes.GetTreeAttribute("nametag");
		if (nametagTree == null) return;

		List<TagOption> active = SelectTags(tags, mode);
		string pfxFormat = cfg.PrefixFormat ?? "[{tag}] ";
		string prefix = active.Count > 0
			? string.Concat(active.Select(t => pfxFormat.Replace("{tag}", t.TagText)))
			: string.Empty;

		string desired = prefix + baseName;
		string current = nametagTree.GetString("name");
		if (string.Equals(current, desired, StringComparison.Ordinal)) return;

		entity.SetName(desired);
		entity.WatchedAttributes.MarkPathDirty("nametag");
	}

	private void InjectColorEntitlement(IServerPlayer player, List<TagOption> tags, TagDisplayMode mode, StratumNametagsConfig cfg)
	{
		if (player is not ServerPlayer sp) return;

		List<Entitlement> list = sp.Entitlements;
		if (list == null) return;

		List<TagOption> active = SelectTags(tags, mode);

		// If any active tag's real entitlement code is already in the player's list, vanilla
		// already colours the nametag correctly — no injection needed.
		foreach (TagOption t in active)
		{
			if (t.EntitlementCodeForColor == null) continue;
			if (list.Any(e => string.Equals(e?.Code, t.EntitlementCodeForColor, StringComparison.OrdinalIgnoreCase)))
				return;
		}

		// Find the first active tag that has a colour entitlement code to inject
		string codeToInject = null;
		foreach (TagOption t in active)
		{
			if (!string.IsNullOrWhiteSpace(t.EntitlementCodeForColor))
			{
				codeToInject = t.EntitlementCodeForColor;
				break;
			}
		}

		if (codeToInject == null) return;
		if (cfg.OnlyInjectIfNoExistingEntitlement && list.Count > 0) return;
		if (list.Any(e => string.Equals(e?.Code, codeToInject, StringComparison.OrdinalIgnoreCase))) return;

		list.Add(new Entitlement { Code = codeToInject, Name = codeToInject });
		injectedByUid[player.PlayerUID] = codeToInject;
	}

	private void RemoveInjectedEntitlement(IServerPlayer player)
	{
		if (player is not ServerPlayer sp) return;
		if (!injectedByUid.TryGetValue(player.PlayerUID, out string prevCode)) return;

		sp.Entitlements?.RemoveAll(e => string.Equals(e?.Code, prevCode, StringComparison.OrdinalIgnoreCase));
		injectedByUid.Remove(player.PlayerUID);
	}

	private bool BuildChatFormat(IServerPlayer player, out string prefixVtml, out string nameColorHex)
	{
		prefixVtml = null;
		nameColorHex = null;

		StratumNametagsConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled) return false;

		StratumConfig root = StratumRuntime.Config;
		if (root?.Chat == null || !root.Chat.RolePrefixesEnabled) return false;

		if (!tagsByUid.TryGetValue(player.PlayerUID, out List<TagOption> tags) || tags.Count == 0)
			return false;

		TagDisplayMode mode = GetMode(player.PlayerUID, tags);
		List<TagOption> activeTags = SelectTags(tags, mode);
		if (activeTags.Count == 0) return false;

		// Each tag gets its own colour via VTML
		string chatPfxFormat = root.Chat.PrefixFormat ?? "[{tag}]";
		var sb = new StringBuilder();
		foreach (TagOption t in activeTags)
		{
			string tagStr = EnsureTrailingSpace(chatPfxFormat.Replace("{tag}", EscapeVtml(t.TagText)));
			sb.Append(ApplyColorAndWeight(tagStr, t.TagColorHex, t.TagBold));
		}
		prefixVtml = sb.ToString();

		// Entitlement tag colour goes on the player name; role colour is fallback
		int entIdx = activeTags.FindIndex(t => t.Key.StartsWith("ent:", StringComparison.Ordinal));
		nameColorHex = entIdx >= 0 ? activeTags[entIdx].NameColorHex : null;

		return prefixVtml.Length > 0;
	}

	private TagDisplayMode GetMode(string uid, List<TagOption> tags)
	{
		if (!modeByUid.TryGetValue(uid, out TagDisplayMode mode))
			return TagDisplayMode.All;

		bool hasEnt = tags.Any(t => t.Key.StartsWith("ent:", StringComparison.Ordinal));
		bool hasRole = tags.Any(t => t.Key.StartsWith("role:", StringComparison.Ordinal));
		if (mode == TagDisplayMode.EntitlementOnly && !hasEnt) return hasRole ? TagDisplayMode.RoleOnly : TagDisplayMode.None;
		if (mode == TagDisplayMode.RoleOnly && !hasRole) return hasEnt ? TagDisplayMode.EntitlementOnly : TagDisplayMode.None;
		return mode;
	}

	private static List<TagOption> SelectTags(List<TagOption> tags, TagDisplayMode mode)
	{
		return mode switch
		{
			TagDisplayMode.All => tags,
			TagDisplayMode.EntitlementOnly => tags.Where(t => t.Key.StartsWith("ent:", StringComparison.Ordinal)).ToList(),
			TagDisplayMode.RoleOnly => tags.Where(t => t.Key.StartsWith("role:", StringComparison.Ordinal)).ToList(),
			TagDisplayMode.None => new List<TagOption>(),
			_ => tags
		};
	}

	private TextCommandResult HandlePrefixCommand(TextCommandCallingArgs args)
	{
		IServerPlayer player = args.Caller?.Player as IServerPlayer;
		if (player == null) return TextCommandResult.Error("Must be called by a player.");

		StratumNametagsConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled)
			return TextCommandResult.Error("Nametag system is disabled.");

		if (!tagsByUid.TryGetValue(player.PlayerUID, out List<TagOption> tags) || tags.Count == 0)
			return TextCommandResult.Success("You have no prefix tags available.");

		bool hasEnt = tags.Any(t => t.Key.StartsWith("ent:", StringComparison.Ordinal));
		bool hasRole = tags.Any(t => t.Key.StartsWith("role:", StringComparison.Ordinal));

		string sub = (args[0] as string ?? "").Trim().ToLowerInvariant();

		if (sub == "" || sub == "list")
		{
			TagDisplayMode current = GetMode(player.PlayerUID, tags);
			var sb = new StringBuilder("Your prefix tags:\n");
			foreach (TagOption t in tags)
				sb.Append("  [" + t.TagText + "] (" + t.Key + ")\n");
			sb.Append("Active mode: " + ModeLabel(current));
			if (tags.Count > 1) sb.Append("\nUse /prefix cycle (or all|entitlement|role|none) to switch.");
			return TextCommandResult.Success(sb.ToString());
		}

		TagDisplayMode next;
		if (sub == "cycle")
		{
			TagDisplayMode current = GetMode(player.PlayerUID, tags);
			next = current switch
			{
				TagDisplayMode.All => hasEnt ? TagDisplayMode.EntitlementOnly : (hasRole ? TagDisplayMode.RoleOnly : TagDisplayMode.None),
				TagDisplayMode.EntitlementOnly => hasRole ? TagDisplayMode.RoleOnly : TagDisplayMode.None,
				TagDisplayMode.RoleOnly => TagDisplayMode.None,
				TagDisplayMode.None => TagDisplayMode.All,
				_ => TagDisplayMode.All
			};
		}
		else if (sub == "all") next = TagDisplayMode.All;
		else if (sub == "entitlement") next = TagDisplayMode.EntitlementOnly;
		else if (sub == "role") next = TagDisplayMode.RoleOnly;
		else if (sub == "none") next = TagDisplayMode.None;
		else return TextCommandResult.Error("Usage: /prefix [list|cycle|all|entitlement|role|none]");

		modeByUid[player.PlayerUID] = next;
		Refresh(player);

		return TextCommandResult.Success("Prefix mode set to: " + ModeLabel(next));
	}

	private static string ModeLabel(TagDisplayMode mode) => mode switch
	{
		TagDisplayMode.All => "all tags",
		TagDisplayMode.EntitlementOnly => "entitlement tag only",
		TagDisplayMode.RoleOnly => "role tag only",
		TagDisplayMode.None => "no prefix",
		_ => mode.ToString()
	};

	private static string GetEntitlementDisplayName(string code, StratumNametagsConfig cfg)
	{
		if (cfg.EntitlementPrefixNames != null && cfg.EntitlementPrefixNames.TryGetValue(code, out string name))
			return name;
		return code;
	}

	private static string DoubleArrayToHex(double[] rgba)
	{
		int r = (int)Math.Round(rgba[0] * 255);
		int g = (int)Math.Round(rgba[1] * 255);
		int b = (int)Math.Round(rgba[2] * 255);
		return "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
	}

	private static string AdjustBrightness(double[] rgba, float adjust)
	{
		double r = Math.Clamp(rgba[0] + adjust, 0, 1);
		double g = Math.Clamp(rgba[1] + adjust, 0, 1);
		double b = Math.Clamp(rgba[2] + adjust, 0, 1);
		return "#"
			+ ((int)Math.Round(r * 255)).ToString("X2")
			+ ((int)Math.Round(g * 255)).ToString("X2")
			+ ((int)Math.Round(b * 255)).ToString("X2");
	}

	private static string ApplyColorAndWeight(string text, string color, bool bold)
	{
		string output = bold ? "<strong>" + text + "</strong>" : text;
		if (string.IsNullOrWhiteSpace(color)) return output;
		return "<font color=\"" + color + "\">" + output + "</font>";
	}

	private static string EnsureTrailingSpace(string s)
		=> string.IsNullOrEmpty(s) || s[s.Length - 1] == ' ' ? s : s + " ";

	private static string EscapeVtml(string value)
	{
		if (string.IsNullOrEmpty(value)) return string.Empty;
		var sb = new StringBuilder(value.Length);
		foreach (char c in value)
		{
			if (c == '&') sb.Append("&amp;");
			else if (c == '<') sb.Append("&lt;");
			else if (c == '>') sb.Append("&gt;");
			else sb.Append(c);
		}
		return sb.ToString();
	}

	private static StratumChatRolePrefixConfig FindRolePrefix(Dictionary<string, StratumChatRolePrefixConfig> prefixes, string roleCode)
	{
		if (prefixes == null || string.IsNullOrWhiteSpace(roleCode)) return null;
		return prefixes
			.Where(kv => kv.Value != null && kv.Value.Enabled && string.Equals(kv.Key, roleCode, StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(kv => kv.Value.Priority)
			.Select(kv => kv.Value)
			.FirstOrDefault();
	}
}
