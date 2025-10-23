using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using static FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyCommonList.CharacterData;

namespace FriendlyFire.Windows;

public unsafe class FriendListDebugWindow : Window
{
    private readonly Plugin Plugin;

    // Track manual seeds locally to avoid touching Plugin's private setters.
    private DateTime _lastManualSeedUtc = DateTime.MinValue;
    private int _lastManualSeedAdded = 0;

    public FriendListDebugWindow(Plugin plugin)
        : base("Friend List Debug##FF")
    {
        Plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 330),
            MaximumSize = new Vector2(860, float.MaxValue)
        };
        IsOpen = false;
    }

    public override void Draw()
    {
        var agent = AgentFriendlist.Instance();
        if (agent == null || agent->InfoProxy == null)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Friend list is not loaded.");
            ImGui.TextDisabled(
                $"Cache: {Plugin.Configuration.FriendCache.Count} CID entries | " +
                $"Auto LastSeed: {(Plugin.LastBuddySeedUtc == default ? "-" : Plugin.LastBuddySeedUtc.ToLocalTime().ToString("T"))} (+{Plugin.LastBuddySeedAdded}) | " +
                $"Manual LastSeed: {(_lastManualSeedUtc == default ? "-" : _lastManualSeedUtc.ToLocalTime().ToString("T"))} (+{_lastManualSeedAdded})"
            );
            DrawManualSeedButtons();
            return;
        }

        if (ImGui.BeginTable("friends", 7, ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("ContentID");
            ImGui.TableSetupColumn("Job");
            ImGui.TableSetupColumn("Location");
            ImGui.TableSetupColumn("Company");
            ImGui.TableSetupColumn("Languages");
            ImGui.TableSetupColumn("State");
            ImGui.TableHeadersRow();

            for (var i = 0U; i < agent->InfoProxy->EntryCount; i++)
            {
                var friend = agent->InfoProxy->GetEntry(i);
                if (friend == null) continue;

                ImGui.TableNextRow();

                var name = friend->NameString ?? string.Empty;
                ImGui.TableNextColumn(); ImGui.Text(name);
                ImGui.TableNextColumn(); ImGui.Text($"{friend->ContentId:X}");

                ImGui.TableNextColumn();
                if (!Plugin.DataManager.GetExcelSheet<ClassJob>().TryGetRow(friend->Job, out var job))
                    ImGui.TextDisabled("Unknown");
                else
                    ImGui.Text($"{job.Abbreviation}");

                ImGui.TableNextColumn();
                if (!Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(friend->Location, out var location))
                    ImGui.TextDisabled($"Unknown");
                else
                    ImGui.Text($"{location.PlaceName.Value.Name}");

                ImGui.TableNextColumn();
                ImGui.Text($"{friend->GrandCompany} {friend->FCTagString}");

                ImGui.TableNextColumn();
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(1));
                var dl = ImGui.GetWindowDrawList();
                ImGui.TextColored(friend->Languages.HasFlag(LanguageMask.Jp) ? ImGuiColors.DalamudWhite : ImGuiColors.DalamudGrey3, "J");
                ImGui.SameLine();
                ImGui.TextColored(friend->Languages.HasFlag(LanguageMask.En) ? ImGuiColors.DalamudWhite : ImGuiColors.DalamudGrey3, "E");
                ImGui.SameLine();
                ImGui.TextColored(friend->Languages.HasFlag(LanguageMask.De) ? ImGuiColors.DalamudWhite : ImGuiColors.DalamudGrey3, "D");
                ImGui.SameLine();
                ImGui.TextColored(friend->Languages.HasFlag(LanguageMask.Fr) ? ImGuiColors.DalamudWhite : ImGuiColors.DalamudGrey3, "F");
                ImGui.PopStyleVar();

                ImGui.TableNextColumn();
                ImGui.Text($"{friend->State}");
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        DrawManualSeedButtons();

        ImGui.SameLine();
        ImGui.TextDisabled(
            $"Cache: {Plugin.Configuration.FriendCache.Count} CID entries | " +
            $"Auto LastSeed: {(Plugin.LastBuddySeedUtc == default ? "-" : Plugin.LastBuddySeedUtc.ToLocalTime().ToString("T"))} (+{Plugin.LastBuddySeedAdded}) | " +
            $"Manual LastSeed: {(_lastManualSeedUtc == default ? "-" : _lastManualSeedUtc.ToLocalTime().ToString("T"))} (+{_lastManualSeedAdded})"
        );
    }

    private void DrawManualSeedButtons()
    {
        if (ImGui.Button("Force Seed Now"))
        {
            var add = Plugin.SeedFriendCacheFromAgentFriendlist();
            _lastManualSeedUtc = DateTime.UtcNow;
            _lastManualSeedAdded = add;
            if (add > 0) Plugin.Configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear Cache"))
        {
            Plugin.Configuration.FriendCache.Clear();
            Plugin.Configuration.Save();
        }
    }
}
