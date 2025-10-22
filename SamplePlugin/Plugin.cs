using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;    // StatusFlags
using Dalamud.Game.ClientState.Objects.SubKinds; // IPlayerCharacter
using Dalamud.Game.Command;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FriendlyFire.Features;
using FriendlyFire.Windows;
using Lumina.Excel.Sheets;

namespace FriendlyFire;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "FriendlyFire";

    // ---- Dalamud services ----
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
    [PluginService] internal static INamePlateGui NamePlateGui { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    // ---- Commands ----
    private const string CommandMain = "/ffmain";        // toggle window
    private const string CommandScr = "/scrambletest";  // toggle: scramble FRIEND names outside PvP
    private const string CommandCfg = "/friendlyfire";  // alias: toggle window

    // ---- UI ----
    internal Configuration Configuration { get; }
    private readonly WindowSystem WindowSystem = new("FriendlyFire");
    internal ConfigWindow ConfigWindow { get; }
    private ContextMenuFeature? ctxFeature;

    // ---- Friend cache housekeeping ----
    private DateTime lastFriendCacheScanUtc = DateTime.MinValue;
    private DateTime lastFriendCacheTrimUtc = DateTime.MinValue;

    // Fallback watcher state (for GameGui polling)
    private bool friendListWasVisible = false;

    // Exposed for Debug UI
    public DateTime LastBuddySeedUtc { get; private set; } = DateTime.MinValue;
    public int LastBuddySeedAdded { get; private set; } = 0;

    // UI seeder
    private FriendListSeeder? friendSeeder;

    public Plugin()
    {
        // Load/save config
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration { Version = 4 };
        Configuration.FriendCache ??= new List<CachedFriendEntry>();
        Configuration.ExtraFriends ??= new List<ShowEntry>();

        // Hygiene on load
        CleanExtraFriends(save: true, validateWithSheet: true);

        // Seeder for FriendList UI
        friendSeeder = new FriendListSeeder(DataManager, GameGui, PluginLog);

        // Window
        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        // Slash commands
        CommandManager.AddHandler(CommandMain, new CommandInfo(OnToggleWindow) { HelpMessage = "Toggle FriendlyFire window" });
        CommandManager.AddHandler(CommandScr, new CommandInfo(OnScrambleCmd) { HelpMessage = "Toggle: scramble FRIEND names outside PvP (field test)" });
        CommandManager.AddHandler(CommandCfg, new CommandInfo(OnToggleWindow) { HelpMessage = "Toggle FriendlyFire window" });

        // UI hooks
        PluginInterface.UiBuilder.Draw += () => WindowSystem.Draw();
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;

        // Nameplate updates
        NamePlateGui.OnNamePlateUpdate += OnNamePlateUpdate;

        // Background: observe & trim friend cache
        Framework.Update += OnFrameworkUpdate;

        // Addon lifecycle hooks: seed when Friends window opens/refreshed
        TryRegisterAddonListener(AddonEvent.PostSetup, "FriendList");
        TryRegisterAddonListener(AddonEvent.PostRefresh, "FriendList");

        // Context menu feature (silent; no toasts)
        ctxFeature = new ContextMenuFeature(this, ContextMenu);
    }

    public void Dispose()
    {
        NamePlateGui.OnNamePlateUpdate -= OnNamePlateUpdate;
        Framework.Update -= OnFrameworkUpdate;

        try { AddonLifecycle.UnregisterListener(OnFriendListEvent); } catch { /* ignore */ }

        ctxFeature?.Dispose();
        ctxFeature = null;

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();

        CommandManager.RemoveHandler(CommandMain);
        CommandManager.RemoveHandler(CommandScr);
        CommandManager.RemoveHandler(CommandCfg);
    }

    // ---- Command handlers ----
    private void OnToggleWindow(string command, string args) => ToggleWindow();
    public void ToggleWindow() => ConfigWindow.Toggle();

    private void OnScrambleCmd(string _, string __)
    {
        Configuration.TestScrambleOutsidePvP = !Configuration.TestScrambleOutsidePvP;
        Configuration.Save();
        PluginLog.Info($"TestScrambleOutsidePvP (friends only) = {Configuration.TestScrambleOutsidePvP}");
    }

    // ---- Addon lifecycle helpers ----
    private void TryRegisterAddonListener(AddonEvent ev, string addon)
    {
        try { AddonLifecycle.RegisterListener(ev, addon, OnFriendListEvent); }
        catch (Exception ex) { PluginLog.Debug($"AddonLifecycle.RegisterListener {ev} failed: {ex.Message}"); }
    }

    private void OnFriendListEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            int added = friendSeeder?.TrySeedFromUi(AddOrTouchFriendCache) ?? 0;
            LastBuddySeedUtc = DateTime.UtcNow;
            LastBuddySeedAdded = added;
            if (added > 0) Configuration.Save();
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"FriendList seed via UI failed: {ex}");
        }
    }

    // Public trigger for Debug UI
    public (int added, int total) ForceSeedFriendCache()
    {
        int added = friendSeeder?.TrySeedFromUi(AddOrTouchFriendCache) ?? 0;
        return (added, Configuration.FriendCache.Count);
    }

    // ---- Background friend-cache maintenance + FriendList visibility fallback ----
    private void OnFrameworkUpdate(IFramework frame)
    {
        var now = DateTime.UtcNow;

        // Scan outside PvP at most every 5s
        if ((now - lastFriendCacheScanUtc).TotalSeconds >= 5)
        {
            lastFriendCacheScanUtc = now;
            ObserveFriendsOutsidePvP();
        }

        // Trim cache at most every 60s
        if ((now - lastFriendCacheTrimUtc).TotalSeconds >= 60)
        {
            lastFriendCacheTrimUtc = now;
            TrimFriendCache();
        }

        // Fallback: detect FriendList open by addon name (if lifecycle hook didn't fire)
        try
        {
            var ptr = GameGui.GetAddonByName("FriendList", 1);
            bool visibleNow = ptr != IntPtr.Zero;
            if (visibleNow && !friendListWasVisible)
            {
                friendListWasVisible = true;
                int added = friendSeeder?.TrySeedFromUi(AddOrTouchFriendCache) ?? 0;
                LastBuddySeedUtc = DateTime.UtcNow;
                LastBuddySeedAdded = added;
                if (added > 0) Configuration.Save();
            }
            else if (!visibleNow && friendListWasVisible)
            {
                friendListWasVisible = false;
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Add to cache (or "touch" LastSeen) and return true iff this call created a NEW entry.
    /// </summary>
    private bool AddOrTouchFriendCache(string name, ushort worldId)
    {
        if (string.IsNullOrWhiteSpace(name) || worldId == 0) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var existing = Configuration.FriendCache.Find(e =>
            e.WorldId == worldId &&
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            Configuration.FriendCache.Add(new CachedFriendEntry
            {
                Name = name,
                WorldId = worldId,
                LastSeenUnixSeconds = now
            });
            return true;
        }

        if (now - existing.LastSeenUnixSeconds > 60)
            existing.LastSeenUnixSeconds = now;

        return false;
    }

    /// <summary>
    /// Outside PvP, record players that have the Friend flag into the local cache.
    /// </summary>
    private void ObserveFriendsOutsidePvP()
    {
        if (ClientState.IsPvP) return;

        var cfg = Configuration;
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool changed = false;

        for (int i = 0; i < ObjectTable.Length; i++)
        {
            var obj = ObjectTable[i];
            if (obj is not IPlayerCharacter pc) continue;

            if ((pc.StatusFlags & StatusFlags.Friend) == 0) continue;

            var name = pc.Name?.TextValue;
            var world = (ushort)pc.HomeWorld.RowId;
            if (string.IsNullOrWhiteSpace(name) || world == 0) continue;

            var existing = cfg.FriendCache.Find(e =>
                e.WorldId == world &&
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                cfg.FriendCache.Add(new CachedFriendEntry
                {
                    Name = name!,
                    WorldId = world,
                    LastSeenUnixSeconds = nowUnix
                });
                changed = true;
            }
            else
            {
                if (nowUnix - existing.LastSeenUnixSeconds > 60)
                {
                    existing.LastSeenUnixSeconds = nowUnix;
                    changed = true;
                }
            }
        }

        if (changed)
            cfg.Save();
    }

    /// <summary>
    /// Periodically remove very old or invalid cache entries.
    /// </summary>
    private void TrimFriendCache()
    {
        var cfg = Configuration;
        if (cfg.FriendCache.Count == 0) return;

        int days = Math.Max(7, cfg.FriendCacheDaysToLive <= 0 ? 90 : cfg.FriendCacheDaysToLive);
        long cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();

        int before = cfg.FriendCache.Count;
        cfg.FriendCache.RemoveAll(e =>
            e == null ||
            e.WorldId == 0 ||
            string.IsNullOrWhiteSpace(e.Name) ||
            e.LastSeenUnixSeconds <= 0 ||
            e.LastSeenUnixSeconds < cutoff);

        if (cfg.FriendCache.Count != before)
            cfg.Save();
    }

    // ---- Nameplate logic (friends = flag OR local cache in PvP OR ExtraFriends) ----
    private void OnNamePlateUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        bool inPvp = ClientState.IsPvP;
        bool showFriends = Configuration.ShowRealNamesForFriends;

        for (int i = 0; i < handlers.Count; i++)
        {
            var plate = handlers[i];
            if (plate.NamePlateKind != NamePlateKind.PlayerCharacter || plate.PlayerCharacter is null)
                continue;

            var pc = plate.PlayerCharacter;
            var realName = pc.Name?.TextValue;
            if (string.IsNullOrEmpty(realName))
                continue;

            bool isFriend = IsFriendOrExtra(pc);

            if (inPvp)
            {
                if (Configuration.ScrambleAllInPvP)
                {
                    if (isFriend && showFriends)
                        plate.SetField(NamePlateStringField.Name, realName);
                    else
                        plate.SetField(NamePlateStringField.Name, ScrambleNameReadable(realName));
                    continue;
                }

                if (Configuration.ShowRealNamesOnlyInPvP)
                {
                    plate.SetField(NamePlateStringField.Name, realName);
                    continue;
                }

                if (showFriends && isFriend)
                {
                    plate.SetField(NamePlateStringField.Name, realName);
                    continue;
                }

                continue; // default PvP (job-only)
            }

            if (Configuration.TestScrambleOutsidePvP && isFriend)
            {
                if (showFriends)
                    plate.SetField(NamePlateStringField.Name, realName);
                else
                    plate.SetField(NamePlateStringField.Name, ScrambleNameReadable(realName));
                continue;
            }
        }
    }

    private static bool HasFriendFlag(IPlayerCharacter pc)
        => (pc.StatusFlags & StatusFlags.Friend) != 0;

    /// <summary>
    /// Friend if:
    /// 1) Friend status flag, OR
    /// 2) (In PvP AND UseFriendCacheInPvP) cache match (name+world), OR
    /// 3) ExtraFriends entry.
    /// </summary>
    internal bool IsFriendOrExtra(IPlayerCharacter pc)
    {
        if (HasFriendFlag(pc)) return true;

        var name = pc.Name?.TextValue ?? string.Empty;
        var world = (ushort)pc.HomeWorld.RowId;

        if (ClientState.IsPvP && Configuration.UseFriendCacheInPvP && world != 0 && name.Length > 0)
        {
            var cache = Configuration.FriendCache;
            for (int i = 0; i < cache.Count; i++)
            {
                var e = cache[i];
                if (e.WorldId == world && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        var list = Configuration.ExtraFriends;
        if (list is { Count: > 0 })
        {
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e.WorldId == world &&
                    name.Length == e.Name.Length &&
                    string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static string ScrambleNameReadable(string name)
    {
        var parts = name.Split(' ');
        for (int p = 0; p < parts.Length; p++)
        {
            var s = parts[p];
            if (s.Length <= 3) continue;

            var first = s[0];
            var last = s[^1];
            var mid = s.Substring(1, s.Length - 2).ToCharArray();

            var rng = new Random(GetStableSeed(s));
            for (int i = mid.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (mid[i], mid[j]) = (mid[j], mid[i]);
            }
            parts[p] = first + new string(mid) + last;
        }
        return string.Join(' ', parts);
    }

    private static int GetStableSeed(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
        return (hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | (hash[3]);
    }

    // -------- Strong cleaner --------
    /// <summary>
    /// Remove bad Extra Friends: null, empty name, worldId == 0/65535, or worldId not present in the World sheet.
    /// Returns number removed. Saves config if 'save' is true and any were removed.
    /// </summary>
    internal int CleanExtraFriends(bool save, bool validateWithSheet)
    {
        var list = Configuration.ExtraFriends;
        if (list == null || list.Count == 0) return 0;

        HashSet<ushort>? validWorlds = null;

        if (validateWithSheet)
        {
            var sheet = DataManager.GetExcelSheet<World>();
            if (sheet != null)
            {
                validWorlds = new HashSet<ushort>();
                foreach (var row in sheet)
                {
                    if (row.RowId == 0) continue;
                    var nm = row.Name.ToString();
                    if (!string.IsNullOrEmpty(nm))
                        validWorlds.Add((ushort)row.RowId);
                }
            }
        }

        bool IsInvalid(ShowEntry? e)
        {
            if (e == null) return true;
            if (string.IsNullOrWhiteSpace(e.Name)) return true;
            if (e.WorldId == 0 || e.WorldId == 65535) return true; // common sentinel
            if (validWorlds != null && !validWorlds.Contains(e.WorldId)) return true;
            return false;
        }

        int before = list.Count;
        list.RemoveAll(IsInvalid);
        int removed = before - list.Count;

        if (removed > 0 && save)
            Configuration.Save();

        return removed;
    }
}
