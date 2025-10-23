using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace FriendlyFire.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration Configuration;
    private readonly Plugin Plugin;

    private string newPlayerName = string.Empty;
    private string newWorldName = string.Empty;
    private string lastAddError = string.Empty;

    // Session-only master switch for debug UI
    private bool showDebug = false;

    public ConfigWindow(Plugin plugin)
        : base("FriendlyFire Frontlines Settings###FriendlyFireWindow")
    {
        Plugin = plugin;
        Configuration = plugin.Configuration;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        IsOpen = false;
    }

    public void Dispose() { }
    public void Toggle() => IsOpen = !IsOpen;

    public override void Draw()
    {
        // ======= Top row: Pills (left) + Debug toggle (right) =======
        if (ImGui.BeginTable("##ff_toprow", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("##left", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("##right", ImGuiTableColumnFlags.WidthFixed, 130f);

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            // nudged a bit left for alignment
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() - 2f);
            DrawPills();

            ImGui.TableSetColumnIndex(1);
            float btnW = 120f;
            float avail = ImGui.GetContentRegionAvail().X;
            if (avail > btnW)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - btnW));
            using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(6, 4)))
            {
                var label = showDebug ? "Debug: ON" : "Debug: OFF";
                if (ImGui.SmallButton(label))
                    showDebug = !showDebug;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Show/hide experimental options and cache tools.");
            }

            ImGui.EndTable();
        }

        var inPvp = Plugin.ClientState.IsPvP;
        ImGui.Spacing();
        ImGui.TextDisabled(inPvp ? "Current zone: PvP" : "Current zone: Non-PvP");
        ImGui.Spacing();

        ImGui.Separator();
        DrawExtraFriends();

        if (showDebug)
        {
            ImGui.Separator();
            DrawCacheBox();
        }
    }

    private static bool DrawTogglePill(string label, bool current, float width, string? tooltip = null)
    {
        uint col = current ? 0xFF3CB371u : 0xFF808080u;
        using (ImRaii.PushColor(ImGuiCol.Button, col))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, col))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, col))
        {
            var clicked = ImGui.Button($"{label}: {(current ? "ON" : "OFF")}", new Vector2(width, 0));
            if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip!);
            return clicked;
        }
    }

    private void DrawPills()
    {
        var cfg = Configuration;
        var availX = ImGui.GetContentRegionAvail().X;

        int cols = availX >= 820f ? 4 : (availX >= 560f ? 2 : 1);
        var flags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.PadOuterX;

        if (ImGui.BeginTable("##ff_pills", cols, flags))
        {
            for (int c = 0; c < cols; c++)
                ImGui.TableSetupColumn($"##col{c}", ImGuiTableColumnFlags.WidthStretch, 1f);

            void Cell(System.Action drawCell)
            {
                ImGui.TableNextColumn();
                drawCell();
            }

            ImGui.TableNextRow();

            // Always-visible
            Cell(() =>
            {
                float w = ImGui.GetContentRegionAvail().X;
                if (DrawTogglePill("Friends → Real Names", cfg.ShowRealNamesForFriends, w,
                        "Show real names for friends in PvP."))
                {
                    cfg.ShowRealNamesForFriends = !cfg.ShowRealNamesForFriends;
                    cfg.Save();
                }
            });

            if (cols <= 2) ImGui.TableNextRow();
            Cell(() =>
            {
                float w = ImGui.GetContentRegionAvail().X;
                if (DrawTogglePill("All → Real Names ", cfg.ShowRealNamesOnlyInPvP, w,
                        "Show real names for everyone in PvP."))
                {
                    cfg.ShowRealNamesOnlyInPvP = !cfg.ShowRealNamesOnlyInPvP;
                    cfg.Save();
                }
            });

            // NEW: Role tag toggle (always visible)
            if (cols == 1) ImGui.TableNextRow();
            Cell(() =>
            {
                float w = ImGui.GetContentRegionAvail().X;
                if (DrawTogglePill("Role Tag on Nameplate", cfg.ShowRoleTag, w,
                        "Show player job tags next to the nameplate."))
                {
                    cfg.ShowRoleTag = !cfg.ShowRoleTag;
                    cfg.Save();
                }
            });

            // Debug-only pills
            if (showDebug)
            {
                if (cols <= 2) ImGui.TableNextRow();
                Cell(() =>
                {
                    float w = ImGui.GetContentRegionAvail().X;
                    if (DrawTogglePill("DEBUG: Scramble Friends (non-PvP)", cfg.TestScrambleOutsidePvP, w,
                            "Scramble ONLY friend nameplates outside PvP."))
                    {
                        cfg.TestScrambleOutsidePvP = !cfg.TestScrambleOutsidePvP;
                        cfg.Save();
                    }
                });

                if (cols == 1) ImGui.TableNextRow();
                Cell(() =>
                {
                    float w = ImGui.GetContentRegionAvail().X;
                    if (DrawTogglePill("DEBUG: Scramble ALL (PvP)", cfg.ScrambleAllInPvP, w,
                            "Scramble ALL PvP nameplates; friends show real names if enabled."))
                    {
                        cfg.ScrambleAllInPvP = !cfg.ScrambleAllInPvP;
                        cfg.Save();
                    }
                });

                if (cols == 1) ImGui.TableNextRow();
                Cell(() =>
                {
                    float w = ImGui.GetContentRegionAvail().X;
                    if (DrawTogglePill("Use CID Cache in PvP", cfg.UseFriendCacheInPvP, w,
                            "Use ContentId cache to detect friends in PvP."))
                    {
                        cfg.UseFriendCacheInPvP = !cfg.UseFriendCacheInPvP;
                        cfg.Save();
                    }
                });
            }

            ImGui.EndTable();
        }
    }

    private void DrawExtraFriends()
    {
        ImGui.TextUnformatted("Extra Friends (Treats listed players as friends by this plugin only)");
        ImGui.TextDisabled("Add exact character name and world. Case sensitive.");
        ImGui.TextDisabled("Players can also be added through the context menu.");
        DrawAddRowResponsive();

        ImGui.Spacing();

        var entries = Configuration.ExtraFriends;
        var flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.NoHostExtendX |
            ImGuiTableFlags.ScrollY;

        var avail = ImGui.GetContentRegionAvail();
        float tableHeight = MathF.Max(200f, avail.Y - (showDebug ? 180f : 6f));

        // Single-box presentation (no double frame)
        if (ImGui.BeginTable("##ff_extrafriends", 4, flags, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.50f);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 0.20f);
            ImGui.TableSetupColumn("PlayerId", ImGuiTableColumnFlags.WidthStretch, 0.20f);
            ImGui.TableSetupColumn("Remove", ImGuiTableColumnFlags.WidthStretch, 0.10f);
            ImGui.TableHeadersRow();

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(e.Name);

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(WorldName(e.WorldId) ?? $"#{e.WorldId}");

                ImGui.TableSetColumnIndex(2);
                var cid = ResolveContentIdForExtraFriend(e.Name, e.WorldId);
                ImGui.TextUnformatted(cid != 0 ? $"{cid:X}" : "-");

                ImGui.TableSetColumnIndex(3);
                using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(6, 3)))
                {
                    if (ImGui.SmallButton($"Delete##ff_rm_{i}"))
                    {
                        entries.RemoveAt(i);
                        Configuration.Save();
                        i--;
                    }
                }
            }

            ImGui.EndTable();
        }
    }

    private ulong ResolveContentIdForExtraFriend(string name, ushort worldId)
    {
        // Prefer cache (name + world match if known)
        var match = Configuration.FriendCache.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(x.Name) &&
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) &&
            (worldId == 0 || x.WorldId == worldId) &&
            x.ContentId != 0);

        if (match != null)
            return match.ContentId;

        // Try live objects
        try
        {
            var livePc = Plugin.ObjectTable
                .OfType<IPlayerCharacter>()
                .FirstOrDefault(pc =>
                    pc != null &&
                    string.Equals(pc.Name?.TextValue ?? string.Empty, name, StringComparison.OrdinalIgnoreCase) &&
                    (worldId == 0 || pc.HomeWorld.RowId == worldId || pc.CurrentWorld.RowId == worldId));

            if (livePc != null)
            {
                unsafe
                {
                    var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)livePc.Address;
                    if (chara != null && chara->ContentId != 0)
                        return chara->ContentId;
                }
            }
        }
        catch { /* ignore */ }

        return 0;
    }

    private void DrawAddRowResponsive()
    {
        float availX = ImGui.GetContentRegionAvail().X;
        bool narrow = availX < 560f;

        if (!narrow)
        {
            if (ImGui.BeginTable("##ff_addrow", 3,
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupColumn("##name", ImGuiTableColumnFlags.WidthStretch, 0.60f);
                ImGui.TableSetupColumn("##world", ImGuiTableColumnFlags.WidthStretch, 0.25f);
                ImGui.TableSetupColumn("##add", ImGuiTableColumnFlags.WidthStretch, 0.15f);

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.PushItemWidth(-1);
                ImGui.InputTextWithHint("##ff_name", "Character Name (e.g., Apple Soda)", ref newPlayerName, 64);
                ImGui.PopItemWidth();

                ImGui.TableNextColumn();
                ImGui.PushItemWidth(-1);
                ImGui.InputTextWithHint("##ff_world", "World (e.g., Gilgamesh)", ref newWorldName, 48);
                ImGui.PopItemWidth();

                ImGui.TableNextColumn();
                float btnW = ImGui.GetContentRegionAvail().X;
                if (ImGui.Button("Add", new Vector2(btnW, 0)))
                    TryAddExtraFriend();

                ImGui.EndTable();
            }

            if (!string.IsNullOrEmpty(lastAddError))
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), lastAddError);
            }
        }
        else
        {
            ImGui.PushItemWidth(-1);
            ImGui.InputTextWithHint("##ff_name", "Character Name (e.g., Apple Soda)", ref newPlayerName, 64);
            ImGui.PopItemWidth();

            if (!string.IsNullOrEmpty(lastAddError))
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), lastAddError);

            if (ImGui.BeginTable("##ff_addrow_narrow", 2,
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupColumn("##world", ImGuiTableColumnFlags.WidthStretch, 0.65f);
                ImGui.TableSetupColumn("##add", ImGuiTableColumnFlags.WidthStretch, 0.35f);

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.PushItemWidth(-1);
                ImGui.InputTextWithHint("##ff_world", "World (e.g., Gilgamesh)", ref newWorldName, 48);
                ImGui.PopItemWidth();

                ImGui.TableNextColumn();
                float btnW = ImGui.GetContentRegionAvail().X;
                if (ImGui.Button("Add", new Vector2(btnW, 0)))
                    TryAddExtraFriend();

                ImGui.EndTable();
            }
        }
    }

    private void TryAddExtraFriend()
    {
        lastAddError = string.Empty;

        var name = (newPlayerName ?? string.Empty).Trim();
        var worldStr = (newWorldName ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(name)) { lastAddError = "Enter a character name."; return; }
        if (string.IsNullOrEmpty(worldStr)) { lastAddError = "Enter a world name."; return; }

        var worldId = ResolveWorldId(worldStr);
        if (worldId == 0)
        {
            lastAddError = $"Unknown world: {worldStr}";
            return;
        }

        if (Configuration.ExtraFriends.Any(e =>
                e.WorldId == worldId &&
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            lastAddError = "Entry already exists.";
            return;
        }

        Configuration.ExtraFriends.Add(new ShowEntry { Name = name, WorldId = worldId });
        Configuration.Save();

        // Auto-clear inputs after successful add
        newPlayerName = string.Empty;
        newWorldName = string.Empty;
    }

    private ushort ResolveWorldId(string worldName)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<World>();
        if (sheet == null) return 0;

        foreach (var row in sheet)
        {
            var nm = row.Name.ToString();
            if (!string.IsNullOrEmpty(nm) && string.Equals(nm, worldName, StringComparison.OrdinalIgnoreCase))
                return (ushort)row.RowId;
        }
        return 0;
    }

    private string? WorldName(ushort id)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<World>();
        if (sheet == null) return null;

        if (sheet.TryGetRow(id, out var row))
            return row.Name.ToString();

        return null;
    }

    private void DrawCacheBox()
    {
        ImGui.TextUnformatted("ContentId Friend Cache");
        ImGui.TextDisabled("Seeded from Friend List and context menu adds by ContentId.");
        ImGui.Spacing();

        var cache = Configuration.FriendCache;
        if (ImGui.BeginTable("##ff_cache", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("PlayerId", ImGuiTableColumnFlags.WidthStretch, 0.35f);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.30f);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 0.20f);
            ImGui.TableSetupColumn("Last Seen", ImGuiTableColumnFlags.WidthStretch, 0.15f);
            ImGui.TableHeadersRow();

            for (int i = 0; i < cache.Count; i++)
            {
                var e = cache[i];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.Text($"{e.ContentId:X}");
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(e.Name ?? string.Empty);
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(WorldName(e.WorldId) ?? (e.WorldId == 0 ? "-" : $"#{e.WorldId}"));
                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(e.LastSeenUnixSeconds > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(e.LastSeenUnixSeconds).LocalDateTime.ToString("g")
                    : "-");
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        if (ImGui.Button("Trim (TTL)"))
        {
            int before = cache.Count;
            var days = Math.Max(7, Configuration.FriendCacheDaysToLive <= 0 ? 90 : Configuration.FriendCacheDaysToLive);
            long cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
            cache.RemoveAll(e => e.ContentId == 0 || e.LastSeenUnixSeconds <= 0 || e.LastSeenUnixSeconds < cutoff);
            if (cache.Count != before) Configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear Cache"))
        {
            cache.Clear();
            Configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Seed Now"))
        {
            int add = Plugin.SeedFriendCacheFromAgentFriendlist();
            if (add > 0) Configuration.Save();
        }

        // ⬇️ Restored: First-Run Window reset button (debug area)
        ImGui.SameLine();
        if (ImGui.Button("Reset First-Run Notice"))
        {
            Plugin.ResetFirstRunNotice();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"Entries: {cache.Count} | Last seed +{Plugin.LastBuddySeedAdded} @ {(Plugin.LastBuddySeedUtc == default ? "-" : Plugin.LastBuddySeedUtc.ToLocalTime().ToString("T"))}");
    }
}
