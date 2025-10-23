// Rowless text-group seeder for FriendList that tolerates both
// - IGameGui.GetAddonByName returning AtkUnitBasePtr (NativeWrapper)  **and**
// - older signatures returning IntPtr.
//
// It gathers all Text nodes, groups by sliding windows, picks the first
// plausible "Firstname Lastname" as the friend name, optional world via
// Lumina World sheet, else names-only (worldId = 0).
//
// Drop-in for FriendlyFire. Requires:
//   <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
//   using FFXIVClientStructs for Atk types.

using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI; // AtkUnitBase, AtkResNode, AtkTextNode, NodeType
using Lumina.Excel.Sheets;

namespace FriendlyFire.Features;

internal unsafe sealed class FriendListSeeder
{
    private readonly IDataManager data;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    private Dictionary<string, ushort>? worldMap;

    public string DebugLastResult { get; private set; } = string.Empty;

    public FriendListSeeder(IDataManager data, IGameGui gameGui, IPluginLog log)
    {
        this.data = data;
        this.gameGui = gameGui;
        this.log = log;
    }

    public int TrySeedFromUi(Func<string, ushort, bool> addOrTouchCache)
    {
        DebugLastResult = string.Empty;
        worldMap ??= BuildWorldMap();

        int totalAdded = 0;
        int totalTexts = 0;

        // Try both instances; some clients populate 0, others 1.
        for (int instance = 0; instance <= 1; instance++)
        {
            var unit = GetAddonUnitPtr("FriendList", instance);
            if (unit == null)
                continue;

            var texts = new List<string>(512);

            // Collect all Text in (rough) visual order
            if (unit->UldManager.RootNode != null)
            {
                CollectTextInOrder(unit->UldManager.RootNode, texts);
            }
            else if (unit->UldManager.NodeList != null && unit->UldManager.NodeListCount > 0)
            {
                for (int i = 0; i < unit->UldManager.NodeListCount; i++)
                {
                    var n = unit->UldManager.NodeList[i];
                    if (n != null) CollectTextInOrder(n, texts);
                }
            }

            if (texts.Count == 0)
                continue;

            totalTexts += texts.Count;

            // Sliding windows to form row candidates
            int addedHere = 0;
            addedHere += ConsumeWindows(texts, 6, addOrTouchCache);
            addedHere += ConsumeWindows(texts, 5, addOrTouchCache);
            addedHere += ConsumeWindows(texts, 4, addOrTouchCache);
            addedHere += ConsumeWindows(texts, 3, addOrTouchCache);

            totalAdded += addedHere;
        }

        DebugLastResult = $"Texts={totalTexts}, Added={totalAdded}";
        return totalAdded;
    }

    // ---- Addon pointer helper (works with AtkUnitBasePtr or IntPtr) ----
    private AtkUnitBase* GetAddonUnitPtr(string name, int instance)
    {
        try
        {
            // Call whatever overload exists; we don't know the static return type here,
            // so we treat it as object and inspect.
            object any = gameGui.GetAddonByName(name, instance);

            if (any is nint addrNint)                      // older signature returns IntPtr/nint
                return addrNint != nint.Zero ? (AtkUnitBase*)addrNint : null;

            // Newer NativeWrapper: type has an Address property we can read via reflection
            var t = any.GetType();
            var prop = t.GetProperty("Address", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                var addrObj = prop.GetValue(any);
                if (addrObj is nint addr && addr != nint.Zero)
                    return (AtkUnitBase*)addr;
                if (addrObj is IntPtr ip && ip != IntPtr.Zero)
                    return (AtkUnitBase*)(nint)ip;
            }

            // Some wrappers expose a Pointer / Value property — try common fallbacks
            prop = t.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance) ??
                   t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                var ptrObj = prop.GetValue(any);
                if (ptrObj is nint addr2 && addr2 != nint.Zero)
                    return (AtkUnitBase*)addr2;
                if (ptrObj is IntPtr ip2 && ip2 != IntPtr.Zero)
                    return (AtkUnitBase*)(nint)ip2;
            }
        }
        catch
        {
            // swallow and return null
        }

        return null;
    }

    // ---- Text collection & windowing ----

    // Collect all text under this subtree in (rough) draw order (prev + next sibling walks).
    private static void CollectTextInOrder(AtkResNode* root, List<string> outTexts)
    {
        if (root == null) return;
        var visited = new HashSet<nint>();

        void Walk(AtkResNode* n)
        {
            if (n == null) return;
            var key = (nint)n;
            if (!visited.Add(key)) return;

            if (n->Type == NodeType.Text)
            {
                var txt = (AtkTextNode*)n;
                var s = txt->NodeText.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    outTexts.Add(s.Trim());
            }

            var c = n->ChildNode;
            if (c != null)
            {
                for (var p = c; p != null; p = p->PrevSiblingNode) Walk(p);
                for (var q = c; q != null; q = q->NextSiblingNode) Walk(q);
            }
        }

        Walk(root);
    }

    private int ConsumeWindows(List<string> texts, int window, Func<string, ushort, bool> addOrTouchCache)
    {
        int added = 0;
        for (int i = 0; i + window <= texts.Count; i++)
        {
            string? name = null;
            ushort wid = 0;

            for (int j = i; j < i + window; j++)
            {
                var t = texts[j].Trim();
                if (name == null && LooksLikeCharacterName(t))
                    name = t;
                else if (wid == 0 && IsValidWorld(t, out var w))
                    wid = w;

                if (name != null && wid != 0) break;
            }

            if (!string.IsNullOrEmpty(name))
            {
                if (addOrTouchCache(name, wid))
                    added++;
            }
        }
        return added;
    }

    // ---- World lookup & heuristics ----

    private Dictionary<string, ushort> BuildWorldMap()
    {
        var map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        var sheet = data.GetExcelSheet<World>();
        if (sheet != null)
        {
            foreach (var row in sheet)
            {
                var nm = row.Name.ToString();
                if (!string.IsNullOrWhiteSpace(nm))
                    map[nm.Trim()] = (ushort)row.RowId;
            }
        }
        return map;
    }

    private bool IsValidWorld(string s, out ushort wid)
    {
        wid = 0;
        if (worldMap == null) return false;
        return worldMap.TryGetValue(s.Trim(), out wid) && wid != 0;
    }

    // Very light name heuristic: "Firstname Lastname"
    private static bool LooksLikeCharacterName(string s)
    {
        s = s.Trim();
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        if (parts[0].Length < 2 || parts[1].Length < 2) return false;

        static bool CapOk(ReadOnlySpan<char> p)
            => char.IsLetter(p[0]) && char.IsUpper(p[0]);

        return CapOk(parts[0]) && CapOk(parts[1]);
    }
}
