using System;
using System.Collections.Generic;
using System.Linq;

// Dalamud
using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

// Role-colored line support
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

// ClientStructs (for ContentId)
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

// Lumina
using Lumina.Excel.Sheets;

namespace FriendlyFire;

public sealed unsafe class Plugin : IDalamudPlugin
{
    public string Name => "FriendlyFire";

    // ===== Dalamud Services =====
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static INamePlateGui NamePlateGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;

    // ===== Config & UI =====
    internal Configuration Configuration { get; }
    private readonly WindowSystem windowSystem = new("FriendlyFire");
    internal Windows.ConfigWindow? ConfigWindow { get; }
    private Windows.FirstRunWindow? FirstRunWindow { get; set; }

    // ===== Commands =====
    private const string CmdMain = "/friendlyfire";

    // ===== Housekeeping =====
    private DateTime lastTrimUtc = DateTime.MinValue;

    // Seed scheduler (multiple one-shots)
    private readonly List<DateTime> pendingSeedsUtc = new();
    private DateTime lastFriendListEventUtc = DateTime.MinValue;

    // Debug exposure (read-only)
    public DateTime LastBuddySeedUtc { get; private set; } = DateTime.MinValue;
    public int LastBuddySeedAdded { get; private set; } = 0;

    // Context menu feature (legacy, known-working)
    private FriendlyFire.Features.ContextMenuFeature? ctxFeatureLegacy;

    public Plugin()
    {
        // Load / initialize configuration
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration { Version = 6 };
        Configuration.Initialize(PluginInterface);

        // Defensive init
        Configuration.ExtraFriends ??= new();
        Configuration.ExtraFriendCids ??= new();
        Configuration.FriendCache ??= new();

        // Hygiene at load
        CleanExtraFriends(save: true, validateWithSheet: true);

        // Windows
        ConfigWindow = new Windows.ConfigWindow(this);
        windowSystem.AddWindow(ConfigWindow);

        FirstRunWindow = new Windows.FirstRunWindow(this);
        windowSystem.AddWindow(FirstRunWindow);

        PluginInterface.UiBuilder.Draw += () => windowSystem.Draw();
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;

        // Commands
        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCmdToggleWindow)
        {
            HelpMessage = "Open/Close FriendlyFire settings"
        });

        // Nameplates
        NamePlateGui.OnNamePlateUpdate += OnNamePlateUpdate;

        // For delayed seeding & periodic trimming only
        Framework.Update += OnFrameworkUpdate;

        // FriendList lifecycle: schedule two seeds (quick + deep) & notify first-run window
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "FriendList", OnFriendListOpenedOrRefreshed);
        AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "FriendList", OnFriendListOpenedOrRefreshed);

        // ===== Context menu =====
        // Use ONLY the legacy feature that worked for you.
        ctxFeatureLegacy = new FriendlyFire.Features.ContextMenuFeature(this, ContextMenu);

        PluginLog.Info($"{Name} loaded.");
    }

    public void Dispose()
    {
        ctxFeatureLegacy?.Dispose();

        AddonLifecycle.UnregisterListener(OnFriendListOpenedOrRefreshed);
        Framework.Update -= OnFrameworkUpdate;
        NamePlateGui.OnNamePlateUpdate -= OnNamePlateUpdate;

        CommandManager.RemoveHandler(CmdMain);

        windowSystem.RemoveAllWindows();
        ConfigWindow?.Dispose();
        FirstRunWindow?.Dispose();

        PluginLog.Info($"{Name} disposed.");
    }

    // ========= Commands =========
    private void OnCmdToggleWindow(string cmd, string args) => ToggleWindow();
    public void ToggleWindow() { if (ConfigWindow is not null) ConfigWindow.Toggle(); }

    // ========= Addon lifecycle (FriendList) =========
    private void OnFriendListOpenedOrRefreshed(AddonEvent ev, AddonArgs args)
    {
        // first-run helper: start its countdown if visible
        FirstRunWindow?.NotifyFriendListOpened();

        var now = DateTime.UtcNow;

        // Avoid spamming: if events fire within 1s, treat them as the same open/refresh burst.
        if ((now - lastFriendListEventUtc).TotalSeconds > 1.0)
        {
            lastFriendListEventUtc = now;

            // Schedule two reads:
            //  - quick pass soon after UI appears (350ms)
            //  - deep pass 8s later to catch the fully paged friend list
            ScheduleFriendSeed(TimeSpan.FromMilliseconds(350));
            ScheduleFriendSeed(TimeSpan.FromSeconds(8));
        }

        PluginLog.Debug($"[FF SeedAgent] FriendList {ev}: scheduled seeds (+0.35s, +8s).");
    }

    private void ScheduleFriendSeed(TimeSpan delay)
    {
        var when = DateTime.UtcNow + delay;

        // Deduplicate any existing scheduled times within +/- 300ms
        for (int i = 0; i < pendingSeedsUtc.Count; i++)
        {
            if (Math.Abs((pendingSeedsUtc[i] - when).TotalMilliseconds) <= 300)
                return; // already scheduled near this time
        }

        pendingSeedsUtc.Add(when);
        pendingSeedsUtc.Sort();
    }

    // ========= Framework.Update (run due seeds + cache trimming) =========
    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            var now = DateTime.UtcNow;

            // Run any due seeds (in order); if multiple are due, run them one after another
            int ran = 0;
            while (pendingSeedsUtc.Count > 0 && pendingSeedsUtc[0] <= now && ran < 3)
            {
                pendingSeedsUtc.RemoveAt(0);

                int added = 0;
                try
                {
                    added = SeedFriendCacheFromAgentFriendlist();
                }
                catch (Exception ex)
                {
                    PluginLog.Debug($"[FF SeedAgent] Seed crash guard: {ex.GetType().Name}: {ex.Message}");
                }

                LastBuddySeedUtc = now;
                LastBuddySeedAdded = added;
                if (added > 0) Configuration.Save();

                PluginLog.Debug($"[FF SeedAgent] Seed ran; added {added} entr{(added == 1 ? "y" : "ies")}.");
                ran++;
            }

            // Periodic TTL trim (once a minute)
            if ((now - lastTrimUtc).TotalSeconds >= 60)
            {
                lastTrimUtc = now;
                TrimFriendCache();
            }
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"FrameworkUpdate guard: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Read AgentFriendlist->InfoProxy->GetEntry(i) and add names/CIDs to local cache.
    /// This does NOT send RequestData() and does NOT cause refresh loops.
    /// Returns number of new/updated entries.
    /// </summary>
    public unsafe int SeedFriendCacheFromAgentFriendlist()
    {
        if (ClientState.LocalPlayer is null)
        {
            PluginLog.Debug("[FF SeedAgent] Not logged in / LocalPlayer null.");
            return 0;
        }

        AgentFriendlist* agent;
        try
        {
            agent = AgentFriendlist.Instance();
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"[FF SeedAgent] AgentFriendlist.Instance() ex: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }

        if (agent == null)
        {
            PluginLog.Debug("[FF SeedAgent] AgentFriendlist null.");
            return 0;
        }

        if (agent->InfoProxy == null)
        {
            PluginLog.Debug("[FF SeedAgent] agent->InfoProxy null (addon not ready).");
            return 0;
        }

        uint count;
        try { count = agent->InfoProxy->EntryCount; }
        catch (Exception ex)
        {
            PluginLog.Debug($"[FF SeedAgent] Read EntryCount ex: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }

        if (count == 0)
        {
            PluginLog.Debug("[FF SeedAgent] EntryCount=0 (Friend List empty or still populating).");
            return 0;
        }

        int added = 0;

        for (uint i = 0; i < count; i++)
        {
            try
            {
                var f = agent->InfoProxy->GetEntry(i);
                if (f == null) continue;

                var name = f->NameString;
                if (string.IsNullOrWhiteSpace(name)) continue;

                // Prefer current world, fall back to home world; 0 if unknown
                ushort worldId = (ushort)(f->CurrentWorld != 0 ? f->CurrentWorld :
                                          f->HomeWorld != 0 ? f->HomeWorld : 0);

                ulong cid = f->ContentId;

                if (AddOrTouchFriendCache(name.Trim(), worldId, cid))
                    added++;
            }
            catch (Exception ex)
            {
                PluginLog.Debug($"[FF SeedAgent] GetEntry({i}) ex: {ex.GetType().Name}: {ex.Message}");
            }
        }

        PluginLog.Debug($"[FF SeedAgent] Added {added} from AgentFriendlist (count={count}).");
        return added;
    }

    // ========= Nameplate logic (single-line: [ROLE-COLORED JOB] Real Name) =========
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

            void ShowOneLineJobAndName()
            {
                // === Role tag toggle respected here ===
                if (!Configuration.ShowRoleTag)
                {
                    // Role tag OFF: just show real name and clear top line
                    plate.SetField(NamePlateStringField.Name, new SeStringBuilder().AddText(realName!).Build());
                    plate.SetField(NamePlateStringField.Title, string.Empty);
                    return;
                }

                // Build single-line [JOB] Name; color role only if not in PvP (alliance color overrides in PvP).
                var se = inPvp
                    ? BuildNameWithJob_Plain(pc, realName!)
                    : BuildNameWithJob_Colored(pc, realName!);

                plate.SetField(NamePlateStringField.Name, se);

                // Always clear any top line to avoid above/below variance
                plate.SetField(NamePlateStringField.Title, string.Empty);
            }

            void ScrambleAndClearTop()
            {
                plate.SetField(NamePlateStringField.Name, ScrambleNameReadable(realName!));
                plate.SetField(NamePlateStringField.Title, string.Empty);
            }

            void LeaveDefaultButClearTop()
            {
                plate.SetField(NamePlateStringField.Title, string.Empty);
            }

            if (inPvp)
            {
                if (Configuration.ScrambleAllInPvP)
                {
                    if (isFriend && showFriends)
                        ShowOneLineJobAndName();
                    else
                        ScrambleAndClearTop();
                    continue;
                }

                if (Configuration.ShowRealNamesOnlyInPvP)
                {
                    ShowOneLineJobAndName();
                    continue;
                }

                if (showFriends && isFriend)
                {
                    ShowOneLineJobAndName();
                    continue;
                }

                LeaveDefaultButClearTop();
                continue;
            }

            if (Configuration.TestScrambleOutsidePvP && isFriend)
            {
                if (showFriends)
                    ShowOneLineJobAndName();
                else
                    ScrambleAndClearTop();
                continue;
            }

            LeaveDefaultButClearTop();
        }
    }

    // ===== Role + Job helpers (whitelist) =====
    private enum FfRole { Tank, Healer, Dps }

    private static readonly HashSet<string> TankAbbr = new(StringComparer.OrdinalIgnoreCase)
    { "PLD", "WAR", "DRK", "GNB" };

    private static readonly HashSet<string> HealerAbbr = new(StringComparer.OrdinalIgnoreCase)
    { "WHM", "SCH", "AST", "SGE" };

    private static string GetJobAbbrev(IPlayerCharacter pc)
    {
        try
        {
            var sheet = DataManager.GetExcelSheet<ClassJob>();
            if (sheet != null && sheet.TryGetRow((uint)pc.ClassJob.RowId, out var job))
                return job.Abbreviation.ToString();
        }
        catch { /* ignore */ }
        return string.Empty;
    }

    private static FfRole GetRole(IPlayerCharacter pc)
    {
        try
        {
            var sheet = DataManager.GetExcelSheet<ClassJob>();
            if (sheet != null && sheet.TryGetRow((uint)pc.ClassJob.RowId, out var job))
            {
                var abbr = job.Abbreviation.ToString();
                if (TankAbbr.Contains(abbr)) return FfRole.Tank;
                if (HealerAbbr.Contains(abbr)) return FfRole.Healer;

                // Optional fallback to sheet Role (common mapping: 1=Tank, 4=Healer)
                var rv = (int)job.Role;
                if (rv == 1) return FfRole.Tank;
                if (rv == 4) return FfRole.Healer;
            }
        }
        catch { /* ignore */ }
        return FfRole.Dps;
    }

    private static ushort GetRoleColorId(IPlayerCharacter pc)
    {
        return GetRole(pc) switch
        {
            FfRole.Tank => (ushort)517, // blue
            FfRole.Healer => (ushort)45,  // green
            _ => (ushort)506, // red for DPS
        };
    }

    // Build one-line "[JOB] Real Name" (colored role)
    private static SeString BuildNameWithJob_Colored(IPlayerCharacter pc, string realName)
    {
        var job = GetJobAbbrev(pc);
        if (string.IsNullOrEmpty(job))
            return new SeStringBuilder().AddText(realName).Build();

        return new SeStringBuilder()
            .AddUiForeground(GetRoleColorId(pc))
            .AddText(job)
            .AddUiForegroundOff()
            .AddText(" ")
            .AddText(realName)
            .Build();
    }

    // Build one-line "[JOB] Real Name" (plain, for PvP so alliance color can apply)
    private static SeString BuildNameWithJob_Plain(IPlayerCharacter pc, string realName)
    {
        var job = GetJobAbbrev(pc);
        if (string.IsNullOrEmpty(job))
            return new SeStringBuilder().AddText(realName).Build();

        return new SeStringBuilder()
            .AddText(job)
            .AddText(" ")
            .AddText(realName)
            .Build();
    }

    // ========= Friend checks =========
    private static bool HasFriendFlag(IPlayerCharacter pc)
        => (pc.StatusFlags & Dalamud.Game.ClientState.Objects.Enums.StatusFlags.Friend) != 0;

    internal unsafe bool IsFriendOrExtra(IPlayerCharacter pc)
    {
        // Fast path: official friend flag
        if (HasFriendFlag(pc))
            return true;

        // Prefer CID match (extra-friends via context menu OR cache)
        try
        {
            var chara = (Character*)pc.Address;
            if (chara != null && chara->ContentId != 0)
            {
                if (Configuration.ExtraFriendCids.Contains(chara->ContentId))
                    return true;

                if (ClientState.IsPvP && Configuration.UseFriendCacheInPvP)
                {
                    if (Configuration.FriendCache.Any(e => e.ContentId == chara->ContentId))
                        return true;
                }
            }
        }
        catch { /* ignore */ }

        // Fallbacks: cache by name/world & manual entries
        var name = pc.Name?.TextValue ?? string.Empty;
        var world = (ushort)pc.HomeWorld.RowId;

        if (ClientState.IsPvP && Configuration.UseFriendCacheInPvP && name.Length > 0)
        {
            var cache = Configuration.FriendCache;
            for (int i = 0; i < cache.Count; i++)
            {
                var e = cache[i];
                if ((e.WorldId == 0 || e.WorldId == world) &&
                    string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
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

    // ========= Scramble helper (used in PvP when scrambling non-friends) =========
    private static string ScrambleNameReadable(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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

    // ========= Cache utilities =========
    private bool AddOrTouchFriendCache(string name, ushort worldId, ulong contentId)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cache = Configuration.FriendCache;

        // CID-exact match exists?
        var existingCid = (contentId != 0)
            ? cache.Find(e => e.ContentId == contentId)
            : null;

        if (existingCid is not null)
        {
            // Update name/world if newer/better
            if (!string.Equals(existingCid.Name, name, StringComparison.Ordinal))
                existingCid.Name = name;
            if (existingCid.WorldId == 0 && worldId != 0)
                existingCid.WorldId = worldId;
            if (now - existingCid.LastSeenUnixSeconds > 60)
                existingCid.LastSeenUnixSeconds = now;
            return false;
        }

        // If no CID, try name+world merge/upgrade
        if (contentId == 0)
        {
            if (worldId == 0 && cache.Any(e =>
                    e.WorldId != 0 && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                return false;

            var idx = cache.FindIndex(e =>
                e.WorldId == 0 &&
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

            if (idx >= 0 && worldId != 0)
            {
                cache[idx].WorldId = worldId;
                cache[idx].LastSeenUnixSeconds = now;
                return true;
            }

            cache.Add(new CachedFriendEntry
            {
                ContentId = 0,
                Name = name,
                WorldId = worldId,
                LastSeenUnixSeconds = now
            });
            return true;
        }

        // New CID entry
        cache.Add(new CachedFriendEntry
        {
            ContentId = contentId,
            Name = name,
            WorldId = worldId,
            LastSeenUnixSeconds = now
        });
        return true;
    }

    private void TrimFriendCache()
    {
        var cache = Configuration.FriendCache;
        if (cache.Count == 0) return;

        int days = Math.Max(7, Configuration.FriendCacheDaysToLive <= 0 ? 90 : Configuration.FriendCacheDaysToLive);
        long cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();

        int before = cache.Count;
        cache.RemoveAll(e =>
            e == null ||
            string.IsNullOrWhiteSpace(e.Name) ||
            e.LastSeenUnixSeconds <= 0 ||
            e.LastSeenUnixSeconds < cutoff);

        if (cache.Count != before)
            Configuration.Save();
    }

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

        bool IsInvalid(ShowEntry e)
        {
            if (string.IsNullOrWhiteSpace(e.Name)) return true;
            if (e.WorldId == 0 || e.WorldId == 65535) return true;
            if (validWorlds != null && !validWorlds.Contains(e.WorldId)) return true;
            return false;
        }

        int before = list.Count;
        list.RemoveAll(e => IsInvalid(e));
        int removed = before - list.Count;

        if (removed > 0 && save)
            Configuration.Save();

        return removed;
    }

    // Exposed helper for ConfigWindow (debug reset)
    internal void ResetFirstRunNotice()
    {
        Configuration.ShowFirstRunNotice = true;
        Configuration.Save();
        FirstRunWindow?.Reopen();
    }
}
