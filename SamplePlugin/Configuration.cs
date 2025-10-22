using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace FriendlyFire
{
    public sealed class ShowEntry
    {
        public string Name { get; set; } = string.Empty; // Exact character name (case-insensitive match)
        public ushort WorldId { get; set; }              // Lumina World RowId
    }

    public sealed class CachedFriendEntry
    {
        public string Name { get; set; } = string.Empty;     // Exact character name (case-insensitive)
        public ushort WorldId { get; set; }                  // Lumina World RowId
        public long LastSeenUnixSeconds { get; set; } = 0; // UTC seconds
    }

    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 4;

        // ---------------- Core behaviour toggles ----------------
        /// <summary>
        /// If true, friends (real friends + ExtraFriends) show real names.
        /// Applies everywhere; in PvP it is honored by our nameplate handler.
        /// </summary>
        public bool ShowRealNamesForFriends { get; set; } = true;

        /// <summary>
        /// PvP: when true, show real names for everyone in PvP (bypasses job-only).
        /// </summary>
        public bool ShowRealNamesOnlyInPvP { get; set; } = false;

        /// <summary>
        /// PvP: when true, scramble ALL names in PvP. If ShowRealNamesForFriends is also true,
        /// friends are exempt and keep real names.
        /// </summary>
        public bool ScrambleAllInPvP { get; set; } = false;

        /// <summary>
        /// Non-PvP debug toggle: when true, scramble ONLY friend names outside PvP (for field testing).
        /// If ShowRealNamesForFriends is true, friends keep real names and are not scrambled.
        /// </summary>
        public bool TestScrambleOutsidePvP { get; set; } = false;

        // ---------------- Local friend cache (for PvP friend recognition) ----------------
        /// <summary>
        /// Persisted local cache built outside PvP from players with Friend status flag.
        /// Used in PvP to recognize friends when the flag is hidden.
        /// </summary>
        public List<CachedFriendEntry> FriendCache { get; set; } = new();

        /// <summary>
        /// If true, consult FriendCache in PvP to treat cached players as friends.
        /// </summary>
        public bool UseFriendCacheInPvP { get; set; } = true;

        /// <summary>
        /// Days-to-live for FriendCache entries (auto-trim).
        /// </summary>
        public int FriendCacheDaysToLive { get; set; } = 90;

        // ---------------- User-maintained “extra friends” list ----------------
        /// <summary>
        /// Extra friends behave like real friends for nameplate logic.
        /// </summary>
        public List<ShowEntry> ExtraFriends { get; set; } = new();

        // ---------------- UI prefs ----------------
        public bool IsConfigWindowMovable { get; set; } = true;

        // ---------------- Persistence ----------------
        [NonSerialized] private IDalamudPluginInterface? pluginInterface;

        public void Initialize(IDalamudPluginInterface pi)
        {
            pluginInterface = pi;
            // Defensive: ensure lists are not null after load/migration
            ExtraFriends ??= new();
            FriendCache ??= new();
        }

        public void Save()
        {
            try
            {
                // Ensure lists exist before saving
                ExtraFriends ??= new();
                FriendCache ??= new();
                Plugin.PluginInterface.SavePluginConfig(this);
            }
            catch (Exception)
            {
                // Swallow to avoid crashing caller; logging can be added if desired.
            }
        }
    }
}
