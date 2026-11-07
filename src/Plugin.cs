using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
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
    public const string Guid = "com.fabuleo8796.fihsync", Version = "0.1.0";
    internal static ManualLogSource Logger;

    internal enum Who { Everyone, FriendsOnly, Nobody }

    internal static ConfigEntry<Who> ShowFrom;
    internal static ConfigEntry<bool> ShareMine, ShowSign;
    internal static ConfigEntry<int> MaxMB;
    internal static ConfigEntry<double> CacheDays;
    internal static ConfigEntry<string> Blocked;
    internal static ConfigEntry<Key> SkipKey;

    public override void Load()
    {
        Logger = Log;
        ShowFrom = Config.Bind("Sharing", "ShowFrom", Who.Everyone, "Whose customizations you see: Everyone, FriendsOnly (your Steam friends) or Nobody.");
        ShareMine = Config.Bind("Sharing", "ShareMine", true, "Let other modded players see your customizations.");
        MaxMB = Config.Bind("Sharing", "MaxMB", 30, "Biggest look (in MB) you download from another player. Accessories with videos can be big.");
        Blocked = Config.Bind("Sharing", "Blocked", "", "Steam IDs (numbers, separated by commas) whose customizations you never see. Their IDs are in LogOutput.log.");
        CacheDays = Config.Bind("Cache", "Days", 3.0, "Other players' files delete themselves after this many days without being used.");
        ShowSign = Config.Bind("Sign", "Show", true, "Show the \"Loading mod textures\" sign while other players' files come in.");
        SkipKey = Config.Bind("Sign", "SkipKey", Key.F7, "Hides the loading sign (the default look shows until the files arrive).");
        ShowFrom.SettingChanged += Recheck;
        Blocked.SettingChanged += Recheck;

        ClassInjector.RegisterTypeInIl2Cpp<SyncDriver>();
        var host = new GameObject("FIHSync");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<SyncDriver>();
        Cache.Sweep();
        Log.LogInfo($"FIH Sync {Version} loaded: showing customizations from {ShowFrom.Value}, files kept {CacheDays.Value:0.#} days");
    }

    static void Recheck(object sender, EventArgs e) => Share.Recheck();

    internal static string ConfigFolder => Path.Combine(Paths.ConfigPath, "FIHSync");

    internal static HashSet<string> BlockedSet() => new(Blocked.Value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries));
}

public class SyncDriver : MonoBehaviour
{
    public SyncDriver(IntPtr ptr) : base(ptr) { }

    int errors;

    void Update()
    {
        try
        {
            Share.Tick();
            Sign.Tick();
            Ui.Tick();
        }
        catch (DllNotFoundException e)
        {
            if (errors++ == 0) Plugin.Logger.LogError("Steam isn't reachable, FIH Sync is off: " + e.Message);
            enabled = false;
        }
        catch (Exception e)
        {
            if (errors++ < 20) Plugin.Logger.LogError("update: " + e);
        }
    }
}
