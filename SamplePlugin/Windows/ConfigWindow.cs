using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;

namespace FriendlyFire.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration Configuration;
    private readonly Plugin Plugin;

    private string newPlayerName = string.Empty;
    private string newWorldName = string.Empty;
    private string lastAddError = string.Empty;

    // debug helpers
    private string cacheFilter = string.Empty;
    private bool showDebug = false;

    public ConfigWindow(Plugin plugin)
        : base("FriendlyFire###FriendlyFireWindow")
    {
        Plugin = plugin;
        Configuration = plugin.Configuration;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        // defensive
        Configuration.ExtraFriends ??= new();
        Configuration.FriendCache ??= new();
    }

    public void Dispose() { }
    public void Toggle() => IsOpen = !IsOpen;

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

    private void DrawScalablePills()
    {
        var cfg = Plugin.Configuration;
        var availX = ImGui.GetContentRegionAvail().X;

        int cols = availX >= 820f ? 4 : (availX >= 520f ? 2 : 1);
        var flags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.PadOuterX;

        if (ImGui.BeginTable("##ff_pills", cols, flags))
        {
            for (int c = 0; c < cols; c++)
                ImGui.TableSetupColumn($"##col{c}", ImGuiTableColumnFlags.WidthStretch, 1f);

            void CellPill(string label, ref bool value, string tip)
            {
                ImGui.TableNextColumn();
                float cellW = ImGui.GetContentRegionAvail().X;
                if (DrawTogglePill(label, value, cellW, tip))
                {
                    value = !value;
                    cfg.Save();
                }
            }

            ImGui.TableNextRow();
            var p1 = cfg.ShowRealNamesForFriends;
            CellPill("Friends → Real Names", ref p1, "Friends (including Extra Friends) always show real names.");
            cfg.ShowRealNamesForFriends = p1;

            if (cols == 1) ImGui.TableNextRow();

            if (cols <= 2) ImGui.TableNextRow();
            var p4 = cfg.ShowRealNamesOnlyInPvP;
            CellPill("All → Real Names", ref p4, "Show real names for everyone in PvP.");
            cfg.ShowRealNamesOnlyInPvP = p4;

            var p2 = cfg.TestScrambleOutsidePvP;
            CellPill("DEBUG: Scramble Friends Outside PvP", ref p2, "Field test: scramble ONLY friend nameplates outside PvP.");
            cfg.TestScrambleOutsidePvP = p2;

            ImGui.EndTable();
        }
    }

    public override void Draw()
    {
        // light hygiene; avoid sheet check here
        Plugin.CleanExtraFriends(save: true, validateWithSheet: false);

        var inPvp = Plugin.ClientState.IsPvP;

        ImGui.TextUnformatted("Toggle FriendlyFire");
        ImGui.SameLine();
        ImGui.Spacing();
        ImGui.SameLine();
        if (ImGui.SmallButton(showDebug ? "Hide Debug##ff_dbg_toggle" : "Show Debug##ff_dbg_toggle"))
            showDebug = !showDebug;

        ImGui.Separator();
        DrawScalablePills();

        ImGui.Spacing();
        ImGui.TextDisabled(inPvp ? "Current zone: PvP" : "Current zone: Non-PvP");
        ImGui.Spacing();
        ImGui.Separator();

        if (showDebug)
        {
            DrawDebugFriendCache();
            ImGui.Separator();
        }

        DrawExtraFriends();
    }

    // ===========================
    // Debug: Friend Cache section
    // ===========================
    private void DrawDebugFriendCache()
    {
        var cfg = Configuration;
        cfg.FriendCache ??= new();

        ImGui.TextUnformatted("Debug — Friend Cache (names learned outside PvP or from opening the Friends list)");
        ImGui.Spacing();

        // Top controls (left group)
        using (ImRaii.Group())
        {
            ImGui.TextDisabled($"Cached entries: {cfg.FriendCache.Count}");
            ImGui.TextDisabled($"TTL (days):");
            ImGui.SameLine();
            int ttl = cfg.FriendCacheDaysToLive <= 0 ? 90 : cfg.FriendCacheDaysToLive;
            if (ImGui.InputInt("##ff_ttl", ref ttl))
            {
                cfg.FriendCacheDaysToLive = Math.Max(7, ttl);
                cfg.Save();
            }

            bool useCache = cfg.UseFriendCacheInPvP;
            if (ImGui.Checkbox("Use cache in PvP", ref useCache))
            {
                cfg.UseFriendCacheInPvP = useCache;
                cfg.Save();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear cache"))
            {
                cfg.FriendCache.Clear();
                cfg.Save();
            }
        }

        // Right-side controls (filter)
        ImGui.SameLine();
        ImGui.Spacing();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240f);
        ImGui.InputTextWithHint("##ff_cache_filter", "Filter (name/world)", ref cacheFilter, 64);

        ImGui.Spacing();

        // Table
        var flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.Sortable;

        var avail = ImGui.GetContentRegionAvail();
        float tableHeight = MathF.Max(200f, avail.Y * 0.45f);
        using (ImRaii.Child("##ff_cache_wrap", new Vector2(0, tableHeight), true))
        {
            if (ImGui.BeginTable("##ff_cache_table", 5, flags))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.38f);
                ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 0.20f);
                ImGui.TableSetupColumn("WorldId", ImGuiTableColumnFlags.WidthFixed, 80f);
                ImGui.TableSetupColumn("Last Seen (UTC)", ImGuiTableColumnFlags.WidthStretch, 0.27f);
                ImGui.TableSetupColumn("Age", ImGuiTableColumnFlags.WidthStretch, 0.15f);
                ImGui.TableHeadersRow();

                var entries = cfg.FriendCache;

                // Filtering (case-insensitive)
                var filter = (cacheFilter ?? string.Empty).Trim();
                bool hasFilter = filter.Length > 0;
                if (hasFilter) filter = filter.ToLowerInvariant();

                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e == null) continue;

                    string name = e.Name ?? string.Empty;
                    string? worldName = WorldName(e.WorldId);
                    string worldCell = worldName ?? $"#{e.WorldId}";

                    if (hasFilter)
                    {
                        var hay = $"{name} {worldCell}".ToLowerInvariant();
                        if (!hay.Contains(filter))
                            continue;
                    }

                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(name);

                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(worldCell);

                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted(e.WorldId.ToString());

                    ImGui.TableSetColumnIndex(3);
                    var dt = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, e.LastSeenUnixSeconds)).UtcDateTime;
                    ImGui.TextUnformatted(dt == DateTime.MinValue ? "-" : dt.ToString("yyyy-MM-dd HH:mm:ss"));

                    ImGui.TableSetColumnIndex(4);
                    ImGui.TextUnformatted(AgeString(e.LastSeenUnixSeconds));
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Delete##ff_cache_del_{i}"))
                    {
                        entries.RemoveAt(i);
                        cfg.Save();
                        i--;
                    }
                }

                ImGui.EndTable();
            }
        }
    }

    private static string AgeString(long unixSeconds)
    {
        if (unixSeconds <= 0) return "-";
        var then = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var span = DateTimeOffset.UtcNow - then;

        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s ago";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 48) return $"{(int)span.TotalHours}h ago";
        return $"{(int)Math.Round(span.TotalDays)}d ago";
    }

    // ===========================
    // Extra Friends section
    // ===========================
    private void DrawExtraFriends()
    {
        ImGui.TextUnformatted("Extra Friends (treated exactly like FRIENDS by this plugin)");
        ImGui.TextDisabled("Add exact character name and world. Case insensitive. You can also add via right-click context menu.");

        DrawAddRowResponsive();

        ImGui.Spacing();

        var entries = Configuration.ExtraFriends;

        // One more inline pass so bad rows never render
        if (InlinePurgeInvalidEntries(entries))
            Configuration.Save();

        var flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.NoHostExtendX |
            ImGuiTableFlags.ScrollY;

        var avail = ImGui.GetContentRegionAvail();
        float tableHeight = MathF.Max(180f, avail.Y - 60f);
        using (ImRaii.Child("##ff_table_wrap", new Vector2(0, tableHeight), true))
        {
            if (ImGui.BeginTable("##ff_extrafriends", 3, flags))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.60f);
                ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 0.25f);
                ImGui.TableSetupColumn("Remove", ImGuiTableColumnFlags.WidthStretch, 0.15f);
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

        ImGui.Spacing();
        if (entries.Count > 0 && ImGui.Button("Clear All"))
        {
            entries.Clear();
            Configuration.Save();
        }
    }

    // Purge entries with worldId == 0/65535 or unknown to the sheet. Returns true if any were removed.
    private bool InlinePurgeInvalidEntries(System.Collections.Generic.List<ShowEntry> list)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<World>();
        bool removedAny = false;

        for (int i = list.Count - 1; i >= 0; --i)
        {
            var e = list[i];
            if (e == null || string.IsNullOrWhiteSpace(e.Name) || e.WorldId == 0 || e.WorldId == 65535)
            {
                list.RemoveAt(i);
                removedAny = true;
                continue;
            }

            if (sheet != null && !sheet.TryGetRow(e.WorldId, out _))
            {
                list.RemoveAt(i);
                removedAny = true;
            }
        }

        return removedAny;
    }

    private void DrawAddRowResponsive()
    {
        float availX = ImGui.GetContentRegionAvail().X;
        bool narrow = availX < 520f;

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
                var nameEnter = ImGui.InputTextWithHint("##ff_name", "Character Name (e.g., Apple Soda)", ref newPlayerName, 64, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.PopItemWidth();

                ImGui.TableNextColumn();
                ImGui.PushItemWidth(-1);
                var worldEnter = ImGui.InputTextWithHint("##ff_world", "World (e.g., Gilgamesh)", ref newWorldName, 48, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.PopItemWidth();

                ImGui.TableNextColumn();
                float btnW = ImGui.GetContentRegionAvail().X;
                if (ImGui.Button("Add##ff_add_btn", new Vector2(btnW, 0)) || nameEnter || worldEnter)
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
            var nameEnter = ImGui.InputTextWithHint("##ff_name", "Character Name (e.g., Apple Soda)", ref newPlayerName, 64, ImGuiInputTextFlags.EnterReturnsTrue);
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
                var worldEnter = ImGui.InputTextWithHint("##ff_world", "World (e.g., Gilgamesh)", ref newWorldName, 48, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.PopItemWidth();

                ImGui.TableNextColumn();
                float btnW = ImGui.GetContentRegionAvail().X;
                if (ImGui.Button("Add##ff_add_btn_narrow", new Vector2(btnW, 0)) || nameEnter || worldEnter)
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
        lastAddError = string.Empty;
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
}
