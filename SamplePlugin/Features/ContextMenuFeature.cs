using System;
using System.Linq;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using Dalamud.Game.Text.SeStringHandling;

namespace FriendlyFire.Features;

internal sealed class ContextMenuFeature : IDisposable
{
    private readonly Plugin plugin;
    private readonly IContextMenu contextMenu;

    private readonly MenuItem addExtraFriendItem;

    public ContextMenuFeature(Plugin plugin, IContextMenu contextMenu)
    {
        this.plugin = plugin;
        this.contextMenu = contextMenu;

        addExtraFriendItem = new MenuItem
        {
            Name = new SeStringBuilder().AddText("FriendlyFire: Add to Extra Friends").Build(),
            Priority = 0,
            IsEnabled = true,
            OnClicked = OnAddClicked,
        };

        this.contextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose()
    {
        try { this.contextMenu.OnMenuOpened -= OnMenuOpened; } catch { /* ignore */ }
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Default)
            return;

        args.AddMenuItem(addExtraFriendItem);
    }

    private void OnAddClicked(IMenuItemClickedArgs clickArgs)
    {
        // Pre-clean (also ensures list allocation)
        plugin.CleanExtraFriends(save: true, validateWithSheet: true);

        if (clickArgs.Target is not MenuTargetDefault target)
        {
            plugin.CleanExtraFriends(save: true, validateWithSheet: true);
            return;
        }

        var name = target.TargetName ?? string.Empty;
        var worldId = (ushort)target.TargetHomeWorld.RowId;

        if (string.IsNullOrWhiteSpace(name) || worldId == 0 || worldId == 65535)
        {
            plugin.CleanExtraFriends(save: true, validateWithSheet: true);
            return;
        }

        var cfg = plugin.Configuration;

        if (!cfg.ExtraFriends.Any(e =>
                e.WorldId == worldId &&
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            cfg.ExtraFriends.Add(new ShowEntry { Name = name, WorldId = worldId });
            cfg.Save();
        }

        // Final strong cleanup
        plugin.CleanExtraFriends(save: true, validateWithSheet: true);
    }
}
