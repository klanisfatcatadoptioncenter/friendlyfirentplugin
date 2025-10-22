using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace FriendlyFire.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly string GoatImagePath;
    private readonly Plugin Plugin;

    public MainWindow(Plugin plugin, string goatImagePath)
        : base("FriendlyFire##Main")
    {
        Plugin = plugin;
        GoatImagePath = goatImagePath;

        Size = new Vector2(600, 300);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    // The template calls Toggle(), so provide it:
    public void Toggle() => IsOpen = !IsOpen;

    // Renders a clickable "pill" button that toggles a boolean value and returns true if it flipped.
    private static bool DrawTogglePill(string label, bool current, string tooltip = null)
    {
        uint col = current ? 0xFF3CB371u /* mediumseagreen */ : 0xFF808080u /* gray */;
        using (ImRaii.PushColor(ImGuiCol.Button, col))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, col))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, col))
        {
            var clicked = ImGui.Button($"{label}: {(current ? "ON" : "OFF")}");
            if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
            return clicked;
        }
    }

    public override void Draw()
    {
        var cfg = Plugin.Configuration;
        var inPvp = Plugin.ClientState.IsPvP;

        ImGui.TextUnformatted("Toggle Plugin Functions:");
        ImGui.Separator();

        // --- Clickable status pills ---
        // Pill 1: PvP real names
        var realPvp = cfg.ShowRealNamesOnlyInPvP;
        if (DrawTogglePill("Only in PvP (real names)", realPvp,
            "When ON, show REAL names in PvP (unless 'Scramble ALL in PvP' is ON)."))
        {
            cfg.ShowRealNamesOnlyInPvP = !realPvp;
            cfg.Save();
        }

        ImGui.SameLine();

        // Pill 2: scramble outside PvP (friends only)
        var scrambleOutside = cfg.TestScrambleOutsidePvP;
        if (DrawTogglePill("Scramble Outside PvP (friends only)", scrambleOutside,
            "When ON, scramble ONLY FRIEND nameplates outside PvP for field testing."))
        {
            cfg.TestScrambleOutsidePvP = !scrambleOutside;
            cfg.Save();
        }

        ImGui.SameLine();

        // Pill 3: scramble all in PvP (highest priority)
        var scrambleAllPvp = cfg.ScrambleAllInPvP;
        if (DrawTogglePill("Scramble ALL in PvP", scrambleAllPvp,
            "When ON, scramble ALL player nameplates in PvP (overrides PvP real-name setting)."))
        {
            cfg.ScrambleAllInPvP = !scrambleAllPvp;
            cfg.Save();
        }

        ImGui.Spacing();
        ImGui.TextDisabled(inPvp ? "Current zone: PvP" : "Current zone: non-PvP");
        ImGui.Spacing();

        

        ImGui.Spacing();

        
    }
}
