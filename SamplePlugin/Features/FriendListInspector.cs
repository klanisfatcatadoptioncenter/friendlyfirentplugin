// FriendlyFire/Features/FriendListInspector.cs
// Dumps the FriendList addon node tree (incl. Component->UldManager.RootNode) for debugging.

using System;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FriendlyFire.Features;

internal unsafe sealed class FriendListInspector
{
    private readonly IGameGui gameGui;

    public FriendListInspector(IGameGui gameGui)
    {
        this.gameGui = gameGui;
    }

    public string Dump(int maxLines = 400)
    {
        var sb = new StringBuilder(16 * 1024);

        IntPtr addonPtr = gameGui.GetAddonByName("FriendList", 1);
        if (addonPtr == IntPtr.Zero) return "FriendList addon not found.";

        var unit = (AtkUnitBase*)addonPtr;
        if (unit == null) return "FriendList unit null.";

        int lines = 0;

        void WriteLine(int depth, string text)
        {
            if (lines >= maxLines) return;
            sb.Append(' ', Math.Clamp(depth, 0, 32) * 2);
            sb.AppendLine(text);
            lines++;
        }

        void Walk(AtkResNode* node, int depth, bool tag = false)
        {
            if (node == null || lines >= maxLines) return;

            string core = node->Type switch
            {
                NodeType.Text => $"Text   id=0x{(ulong)node:X} '{((AtkTextNode*)node)->NodeText.ToString()}'",
                NodeType.Component => $"Comp   id=0x{(ulong)node:X}",
                NodeType.Image => $"Image  id=0x{(ulong)node:X}",
                _ => $"Node   {node->Type} id=0x{(ulong)node:X}"
            };

            WriteLine(depth, tag ? $"[{core}]" : core);

            // If this is a Component, dive into its ULD tree too
            if (node->Type == NodeType.Component)
            {
                var comp = (AtkComponentNode*)node;
                if (comp->Component != null && comp->Component->UldManager.RootNode != null)
                {
                    WriteLine(depth + 1, $"→ Component.Uld Root 0x{(ulong)comp->Component->UldManager.RootNode:X}");
                    Walk(comp->Component->UldManager.RootNode, depth + 2, true);
                }
            }

            for (var child = node->ChildNode; child != null && lines < maxLines; child = child->PrevSiblingNode)
                Walk(child, depth + 1);
        }

        if (unit->UldManager.RootNode != null)
        {
            sb.AppendLine("=== RootNode walk ===");
            Walk(unit->UldManager.RootNode, 0);
        }
        else if (unit->UldManager.NodeList != null && unit->UldManager.NodeListCount > 0)
        {
            sb.AppendLine("=== NodeList walk ===");
            for (int i = 0; i < unit->UldManager.NodeListCount && lines < maxLines; i++)
            {
                var n = unit->UldManager.NodeList[i];
                if (n != null)
                {
                    sb.AppendLine($"-- NodeList[{i}] 0x{(ulong)n:X}");
                    Walk(n, 1);
                }
            }
        }
        else
        {
            return "FriendList has neither RootNode nor NodeList.";
        }

        if (lines >= maxLines) sb.AppendLine($"(truncated at {maxLines} lines)");
        return sb.ToString();
    }
}
