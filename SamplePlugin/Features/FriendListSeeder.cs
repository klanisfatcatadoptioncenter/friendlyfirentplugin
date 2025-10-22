// FriendlyFire/Features/FriendListSeeder.cs
// Reads the "FriendList" addon (UI) and extracts Name + World from its rows.
// Uses IntPtr from GameGui.GetAddonByName and FFXIVClientStructs node/component types.
// Enable <AllowUnsafeBlocks>true</AllowUnsafeBlocks> in your .csproj and reference FFXIVClientStructs.

using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI; // AtkUnitBase, AtkResNode, AtkComponentNode, AtkComponentList, AtkTextNode, NodeType, ComponentType
using Lumina.Excel.Sheets;

namespace FriendlyFire.Features;

internal unsafe sealed class FriendListSeeder
{
    private readonly IDataManager data;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    public FriendListSeeder(IDataManager data, IGameGui gameGui, IPluginLog log)
    {
        this.data = data;
        this.gameGui = gameGui;
        this.log = log;
    }

    /// <summary>
    /// Walk the FriendList addon tree and for each row call addOrTouchCache(name, worldId).
    /// Returns the number of NEW entries added (touches don't count).
    /// </summary>
    public int TrySeedFromUi(Func<string, ushort, bool> addOrTouchCache)
    {
        int added = 0;

        // Your SDK returns IntPtr here.
        IntPtr addonPtr = gameGui.GetAddonByName("FriendList", 1);
        if (addonPtr == IntPtr.Zero)
            return 0;

        var unit = (AtkUnitBase*)addonPtr;
        if (unit == null)
            return 0;

        if (unit->UldManager.NodeListCount <= 0 || unit->UldManager.NodeList == null)
            return 0;

        // Find the main component list by scanning component nodes for ComponentType.List
        AtkComponentList* bestList = null;
        int bestRowCount = 0;

        for (int i = 0; i < unit->UldManager.NodeListCount; i++)
        {
            var node = unit->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Component)
                continue;

            var compNode = (AtkComponentNode*)node;
            if (compNode->ComponentType != ComponentType.List || compNode->Component == null)
                continue;

            var list = (AtkComponentList*)compNode->Component;
            if (list == null)
                continue;

            int rows = list->ListLength;
            if (rows > bestRowCount)
            {
                bestRowCount = rows;
                bestList = list;
            }
        }

        if (bestList == null || bestRowCount == 0)
            return 0;

        // Walk rows; ItemRendererList[i] returns a VALUE (not pointer) in this SDK.
        for (int i = 0; i < bestList->ListLength; i++)
        {
            var renderer = bestList->ItemRendererList[i]; // value
            var rowRoot = renderer.AtkComponentBase.OwnerNode;
            if (rowRoot == null)
                continue;

            string? charName = null;
            string? worldStr = null;

            // Heuristic: first text node is Name, second is World.
            for (var child = rowRoot->ChildNode; child != null; child = child->PrevSiblingNode)
            {
                if (child->Type != NodeType.Text)
                    continue;

                var txt = (AtkTextNode*)child;
                var text = txt->NodeText.ToString();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (charName == null) { charName = text; continue; }
                if (worldStr == null) { worldStr = text; break; }
            }

            if (string.IsNullOrWhiteSpace(charName) || string.IsNullOrWhiteSpace(worldStr))
                continue;

            var wid = ResolveWorldId(worldStr);
            if (wid == 0)
                continue;

            if (addOrTouchCache(charName, wid))
                added++;
        }

        return added;
    }

    private ushort ResolveWorldId(string worldName)
    {
        var sheet = data.GetExcelSheet<World>();
        if (sheet == null) return 0;

        foreach (var row in sheet)
        {
            var nm = row.Name.ToString();
            if (!string.IsNullOrEmpty(nm) &&
                string.Equals(nm, worldName, StringComparison.OrdinalIgnoreCase))
                return (ushort)row.RowId;
        }

        return 0;
    }
}
