using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FriendlyFire.Windows;

public sealed class FirstRunWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration cfg;

    // countdown management
    private bool countdownActive = false;
    private DateTime countdownStartUtc;
    private const double RequiredSeconds = 10.0;

    // session checkbox state mirrors cfg.ShowFirstRunNotice
    private bool dontShowAgainChecked;

    public FirstRunWindow(Plugin plugin)
        : base("FriendlyFire — Quick Setup###FriendlyFireFirstRunWindow")
    {
        this.plugin = plugin;
        this.cfg = plugin.Configuration;
        this.dontShowAgainChecked = !cfg.ShowFirstRunNotice;

        // A friendly default size
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 240),
            MaximumSize = new Vector2(900, 600),
        };

        // Open only if config says so
        IsOpen = cfg.ShowFirstRunNotice;
    }

    public void Dispose() { }

    /// <summary>
    /// Called by Plugin when the Friend List addon opens or refreshes.
    /// If the window is open, start (or restart) the 5-second countdown.
    /// </summary>
    public void NotifyFriendListOpened()
    {
        if (!IsOpen) return;

        // Start a fresh countdown each time Friend List opens while the window is visible.
        countdownActive = true;
        countdownStartUtc = DateTime.UtcNow;
    }

    public override void Draw()
    {
        // Title
        ImGui.TextWrapped("Thanks for installing FriendlyFire!");
        ImGui.Spacing();
        ImGui.TextWrapped("To enable fast and reliable friend detection in PvP, FriendlyFire uses your in-game Friend List as a seed.");
        ImGui.TextWrapped("Please open your Friend List (Social → Friend List) and leave it open for ~10 seconds.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Countdown / progress
        float progress = 0f;
        if (countdownActive)
        {
            var elapsed = (float)(DateTime.UtcNow - countdownStartUtc).TotalSeconds;
            progress = Math.Clamp(elapsed / (float)RequiredSeconds, 0f, 1f);

            ImGui.Text("Seeding from Friend List…");
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{MathF.Round(progress * 100)}%");
        }
        else
        {
            ImGui.TextDisabled("Waiting for Friend List to open…");
            ImGui.ProgressBar(0f, new Vector2(-1, 0), "0%");
        }

        // Auto-complete when progress hits 100%
        if (countdownActive && progress >= 1f)
        {
            countdownActive = false;
            // mark as completed and close
            cfg.ShowFirstRunNotice = false;
            cfg.Save();
            IsOpen = false;
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Checkbox & controls
        var checkedNow = dontShowAgainChecked;
        if (ImGui.Checkbox(" Don’t show this again", ref checkedNow))
        {
            dontShowAgainChecked = checkedNow;
            cfg.ShowFirstRunNotice = !dontShowAgainChecked;
            cfg.Save();
        }

        // bottom row: Close button and a small help
        if (ImGui.Button("Close"))
        {
            IsOpen = false;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("You can re-enable this notice from Debug in settings.");
    }

    /// <summary>
    /// Allow Plugin to reopen window if user resets the flag from Debug.
    /// </summary>
    public void Reopen()
    {
        dontShowAgainChecked = false;
        countdownActive = false;
        IsOpen = true;
    }
}
