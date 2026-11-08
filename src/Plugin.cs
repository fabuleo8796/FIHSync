using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace FIHSync;

/// FIH Sync for Flipping is Hard: modded players see each other's customizations (Flippy Auras, Body Accessories),
/// automatically. In a lobby it says hello to the other members over Steam peer-to-peer; the ones running FIH Sync
/// answer with what they wear, and each look is downloaded once, cached on disk, and deleted after a few unused days.
/// Unmodded players never notice anything. It stays out of the game's own netcode.
[BepInPlugin(Guid, "FIH Sync", Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.fabuleo8796.fihsync", Version = "0.1.1";
    internal static ManualLogSource Logger;

    internal enum Who { Everyone, FriendsOnly, Nobody }

    internal static ConfigEntry<Who> ShowFrom, VideosFrom;
    internal static ConfigEntry<bool> ShareMine, ShowSign, LagShield;
    internal static ConfigEntry<int> MaxMB;
    internal static ConfigEntry<double> CacheDays, MaxCostMs;
    internal static ConfigEntry<string> Blocked;
    internal static ConfigEntry<Key> SkipKey, PanelKey;

    public override void Load()
    {
        Logger = Log;
        ShowFrom = Config.Bind("Sharing", "ShowFrom", Who.Everyone, "Whose customizations you see: Everyone, FriendsOnly (your Steam friends) or Nobody.");
        ShareMine = Config.Bind("Sharing", "ShareMine", true, "Let other modded players see your customizations.");
        MaxMB = Config.Bind("Sharing", "MaxMB", 30, "Biggest look (in MB) you download from another player. Accessories with videos can be big.");
        VideosFrom = Config.Bind("Sharing", "VideosFrom", Who.FriendsOnly, "Whose video textures play here: Everyone, FriendsOnly or Nobody (videos are the one thing Windows itself decodes).");
        LagShield = Config.Bind("Sharing", "LagShield", true, "Hide a player's stuff when it keeps costing too much frame time.");
        MaxCostMs = Config.Bind("Sharing", "LagShieldMs", 1.5, "The lag shield's limit: milliseconds per frame for one player's stuff.");
        PanelKey = Config.Bind("Keys", "Panel", Key.F8, "Opens the Mod sharing panel (it's also a button in the pause menu).");
        Blocked = Config.Bind("Sharing", "Blocked", "", "Steam IDs (numbers, separated by commas) whose customizations you never see. Their IDs are in LogOutput.log.");
        CacheDays = Config.Bind("Cache", "Days", 3.0, "Other players' files delete themselves after this many days without being used.");
        ShowSign = Config.Bind("Sign", "Show", true, "Show the \"Loading mod textures\" sign while other players' files come in.");
        SkipKey = Config.Bind("Sign", "SkipKey", Key.F7, "Hides the loading sign (the default look shows until the files arrive).");
        ShowFrom.SettingChanged += Recheck;
        Blocked.SettingChanged += Recheck;
        VideosFrom.SettingChanged += Reload;

        Steam.Resolve();
        ClassInjector.RegisterTypeInIl2Cpp<SyncDriver>();
        var host = new GameObject("FIHSync");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<SyncDriver>();
        Cache.Sweep();
        Log.LogInfo($"FIH Sync {Version} loaded: showing customizations from {ShowFrom.Value}, files kept {CacheDays.Value:0.#} days");
    }

    static void Recheck(object sender, EventArgs e) => Share.Recheck();
    static void Reload(object sender, EventArgs e) => Share.Reload();

    internal static void Block(ulong id, bool on)
    {
        var set = BlockedSet();
        if (on) set.Add(id.ToString()); else set.Remove(id.ToString());
        Blocked.Value = string.Join(",", set); // SettingChanged re-checks everyone
    }

    internal static string ConfigFolder => Path.Combine(Paths.ConfigPath, "FIHSync");

    internal static HashSet<string> BlockedSet() => new(Blocked.Value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries));
}

public class SyncDriver : MonoBehaviour
{
    public SyncDriver(IntPtr ptr) : base(ptr) { }

    int errors;

    [HideFromIl2Cpp]
    void Run(Action tick, string what)
    {
        try { tick(); }
        catch (Exception e) { if (errors++ < 20) Plugin.Logger.LogError($"{what}: {e}"); }
    }

    void Update()
    {
        try
        {
            Run(Share.Tick, "sharing");
            Run(Sign.Tick, "sign");
            Run(PauseButton.Tick, "menu button");
            Run(SharePanel.Tick, "panel");
            Ui.Tick();
        }
        catch (Exception e)
        {
            if (errors++ < 20) Plugin.Logger.LogError("update: " + e);
        }
    }
}
