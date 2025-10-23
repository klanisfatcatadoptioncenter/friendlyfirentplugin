using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;

namespace FriendlyFire;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 6;

    // === Your existing options ===
    public bool ShowRealNamesForFriends = true;
    public bool ShowRealNamesOnlyInPvP = false;
    public bool TestScrambleOutsidePvP = false;
    public bool ScrambleAllInPvP = false;
    public bool UseFriendCacheInPvP = true;

    public int FriendCacheDaysToLive = 90;

    // First run helper
    public bool ShowFirstRunNotice = true;

    // === NEW: Role tag (job) toggle ===
    public bool ShowRoleTag = true;

    // Data
    public List<ShowEntry> ExtraFriends = new();
    public List<ulong> ExtraFriendCids = new();
    public List<CachedFriendEntry> FriendCache = new();

    [NonSerialized]
    private IDalamudPluginInterface? pi;

    public void Initialize(IDalamudPluginInterface pluginInterface)
        => this.pi = pluginInterface;

    public void Save()
        => this.pi?.SavePluginConfig(this);
}

// Manual-name entry (Name + World)
[Serializable]
public struct ShowEntry
{
    public string Name;
    public ushort WorldId;
}

// CID cache entry
[Serializable]
public class CachedFriendEntry
{
    public ulong ContentId;
    public string? Name;
    public ushort WorldId;
    public long LastSeenUnixSeconds;
}
