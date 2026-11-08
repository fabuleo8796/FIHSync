using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace FIHSync;

/// What other mods use. A mod registers one "key" (its name) with:
/// - version: a cheap string that changes whenever its look changes (FIH Sync asks once a second),
/// - data: the look itself as bytes, or null for "nothing to show",
/// - received: another modded player's look arrived (their FishNet client id, the bytes, and a scratch folder of
///   FIH Sync's own for that exact look, for files that must sit on disk, like videos),
/// - gone: that player left, switched it off, or is blocked.
/// Everything is called on the main thread.
public static class Share
{
    public const int Protocol = 1;

    sealed class Mod
    {
        public string Key = "";
        public Func<string> Version;
        public Func<byte[]> Data;
        public Action<int, byte[], string> Received;
        public Action<int> Gone;
        public string LastVersion;
        public byte[] Bytes;
        public string Hash;
    }

    static readonly Dictionary<string, Mod> mods = new();

    public static void Register(string key, Func<string> version, Func<byte[]> data, Action<int, byte[], string> received, Action<int> gone)
    {
        mods[key] = new Mod { Key = key, Version = version, Data = data, Received = received, Gone = gone };
        Plugin.Logger.LogInfo($"sharing '{key}'");
    }

    /// A remote player by the FishNet client id `received` gave, or null while they're not spawned (loading, dead).
    public static EHS.PlayerRef FindPlayer(int ownerId)
    {
        try
        {
            EHS.PlayerRef p = null;
            if (EHS.PlayerRef.TryGetConnectedPlayerByClientId(ownerId, out p) && p != null) return p;
        }
        catch { /* fall through to the list */ }
        var list = EHS.PlayerRef.GetConnectedPlayersClientSnapshot();
        if (list != null)
            foreach (var p in list)
                if (p != null && p.OwnerId == ownerId) return p;
        return null;
    }

    /// Whether this player's videos may play here (the "Videos from" setting: friends only by default, because a video is
    /// the one thing Windows itself has to decode).
    public static bool VideosAllowed(int ownerId)
    {
        var p = peers.Values.FirstOrDefault(x => x.OwnerId == ownerId);
        if (p == null) return false;
        return Plugin.VideosFrom.Value switch
        {
            Plugin.Who.Nobody => false,
            Plugin.Who.FriendsOnly => Steam.IsFriend(p.Id),
            _ => true,
        };
    }

    /// Mods report what drawing this player's stuff cost this frame (milliseconds). With the lag shield on, a player
    /// whose stuff keeps costing too much gets hidden ("too heavy"), until "Show anyway" in the Mod sharing panel.
    public static void ReportCost(int ownerId, double ms)
    {
        var p = peers.Values.FirstOrDefault(x => x.OwnerId == ownerId);
        if (p != null) p.CostMs += Math.Min(ms, 4.0); // a one-off build spike mustn't trip the lag shield
    }

    // ------------------------------------------------------------------ the network side

    sealed class Peer
    {
        public ulong Id;
        public string Name = "";
        public int OwnerId = -1;
        public float LastHeard, LastHelloSent = -99f;
        public readonly Dictionary<string, (string hash, int size)> Offers = new();
        public readonly Dictionary<string, string> Shown = new(); // key -> hash handed to the mod
        public double CostMs;
        public int CostFrames;
        public bool TooHeavy, ShowAnyway;
    }

    /// For the Mod sharing panel.
    internal sealed class PeerInfo
    {
        public ulong Id;
        public string Name = "", What = "";
        public bool Hidden, Blocked, TooHeavy, Loading;
    }

    internal static List<PeerInfo> Peers()
    {
        var blocked = Plugin.BlockedSet();
        return peers.Values.Where(p => p.Name != "").OrderBy(p => p.Name).Select(p => new PeerInfo
        {
            Id = p.Id,
            Name = p.Name,
            What = p.Offers.Count == 0 ? "nothing shared" : string.Join(", ", p.Offers.Keys),
            Hidden = hidden.Contains(p.Id),
            Blocked = blocked.Contains(p.Id.ToString()),
            TooHeavy = p.TooHeavy && !p.ShowAnyway,
            Loading = downloads.Values.Any(d => d.From == p.Id),
        }).ToList();
    }

    static readonly HashSet<ulong> hidden = new(); // "Hide" in the panel: this session only

    internal static void SetHidden(ulong id, bool on)
    {
        if (on) hidden.Add(id); else hidden.Remove(id);
        Reload();
    }

    internal static void ShowAnyway(ulong id)
    {
        if (!peers.TryGetValue(id, out var p)) return;
        p.ShowAnyway = true;
        p.TooHeavy = false;
        Reload();
    }

    sealed class Download
    {
        public ulong From;
        public string Key = "", Hash = "";
        public byte[][] Chunks;
        public int Got, Size;
        public float LastProgress;
        public bool Sign; // counts on the loading sign
    }

    const byte Hello = 1, Want = 2, Chunk = 3;
    const int ChunkSize = 96 * 1024, SendBudget = 192 * 1024; // bytes sent per frame at most

    static readonly Dictionary<ulong, Peer> peers = new();
    static readonly Dictionary<string, Download> downloads = new(); // by hash
    static readonly Queue<(ulong to, string key, string hash, int index)> outbox = new();
    static ulong lobby;
    static float nextLobby, nextVersions, nextSweep, nextCost;
    static bool changed;

    /// For the loading sign: looks still coming in, and how many came in since the sign showed up.
    internal static int Pending => downloads.Values.Count(d => d.Sign);
    static float joinedAt;
    internal static int DownloadedSinceJoin;

    internal static void Tick()
    {
        try { if (!Steam.Ready()) return; }
        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
        {
            Steam.Off = true;
            Plugin.Logger.LogError("Steam isn't reachable, sharing is off: " + e.Message);
            return;
        }
        float now = Time.unscaledTime;
        if (now >= nextVersions) { nextVersions = now + 1f; CheckVersions(); }
        if (now >= nextLobby || changed) { nextLobby = now + 2f; Lobby(now); changed = false; }
        foreach (var (from, data) in Steam.Receive())
        {
            try { Handle(from, data, now); }
            catch (Exception e) { Plugin.Logger.LogWarning($"bad message from {from}: {e.Message}"); }
        }
        SendChunks();
        foreach (var p in peers.Values) if (p.Shown.Count > 0) p.CostFrames++;
        if (now >= nextCost) { nextCost = now + 2f; LagShield(); }
        foreach (var d in downloads.Values.Where(d => now - d.LastProgress > 20f).ToList())
        {
            Plugin.Logger.LogWarning($"'{d.Key}' from {Name(d.From)} stalled, dropped");
            downloads.Remove(d.Hash);
        }
        if (now >= nextSweep) { nextSweep = now + 1800f; Cache.Sweep(); }
    }

    static string Name(ulong id) => peers.TryGetValue(id, out var p) && p.Name != "" ? p.Name : id.ToString();

    /// Every 2 s: a player whose stuff averaged more than LagShieldMs a frame gets hidden.
    static void LagShield()
    {
        foreach (var p in peers.Values)
        {
            double avg = p.CostFrames > 0 ? p.CostMs / p.CostFrames : 0;
            p.CostMs = 0;
            p.CostFrames = 0;
            if (!Plugin.LagShield.Value || p.ShowAnyway || p.TooHeavy || avg <= Plugin.MaxCostMs.Value) continue;
            p.TooHeavy = true;
            Plugin.Logger.LogWarning($"{p.Name}'s stuff costs {avg:0.00} ms a frame: hidden by the lag shield");
            foreach (var key in p.Shown.Keys.ToList()) HideFor(p, key);
        }
    }

    /// Hides and re-shows everyone's looks (a setting changed that mods read when a look arrives, like videos).
    internal static void Reload()
    {
        foreach (var p in peers.Values)
        {
            foreach (var key in p.Shown.Keys.ToList()) HideFor(p, key);
            Update(p);
        }
    }

    /// Asks each mod whether its look changed, and if so takes the new bytes.
    static void CheckVersions()
    {
        foreach (var m in mods.Values)
        {
            string v;
            try { v = Plugin.ShareMine.Value ? m.Version() : null; }
            catch (Exception e) { Plugin.Logger.LogWarning($"'{m.Key}' version: {e.Message}"); continue; }
            if (v == m.LastVersion) continue;
            m.LastVersion = v;
            try { m.Bytes = v == null ? null : m.Data(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"'{m.Key}' data: {e.Message}"); m.Bytes = null; }
            if (m.Bytes != null && m.Bytes.Length == 0) m.Bytes = null;
            m.Hash = m.Bytes == null ? null : Convert.ToHexString(SHA256.HashData(m.Bytes)).Substring(0, 32);
            Plugin.Logger.LogInfo(m.Bytes == null ? $"'{m.Key}': nothing to share" : $"'{m.Key}': sharing {m.Bytes.Length / 1024.0:0.#} KB ({m.Hash.Substring(0, 8)})");
            changed = true;
            foreach (var p in peers.Values) p.LastHelloSent = -99f; // tell everyone now
        }
    }

    static ulong CurrentLobby()
    {
        try
        {
            EHS.Steam.SteamManager s = null;
            if (EHS.Steam.SteamLobbyService.TryGetSteam(out s) && s != null && s.IsInLobby) return s.CurrentLobbySteamId.m_SteamID;
        }
        catch { /* not in a lobby yet */ }
        return 0;
    }

    /// Who's in the lobby now: says hello to the members (the unmodded ones just never answer), forgets leavers.
    static void Lobby(float now)
    {
        ulong l = CurrentLobby();
        if (l != lobby)
        {
            Plugin.Logger.LogInfo(l == 0 ? "left the lobby" : $"in lobby {l}");
            lobby = l;
            foreach (var id in peers.Keys.ToList()) Forget(id);
            downloads.Clear();
            outbox.Clear();
            DownloadedSinceJoin = 0;
            joinedAt = now;
            Sign.Reset();
        }
        if (lobby == 0) return;
        var members = Steam.LobbyMembers(lobby);
        foreach (var id in peers.Keys.Where(k => !members.Contains(k) || now - peers[k].LastHeard > 30f).ToList()) Forget(id);
        foreach (var id in members)
        {
            if (!peers.TryGetValue(id, out var p)) peers[id] = p = new Peer { Id = id, LastHeard = now };
            if (now - p.LastHelloSent >= 6f)
            {
                p.LastHelloSent = now;
                SendHello(id);
            }
        }
    }

    static int LocalOwnerId()
    {
        try
        {
            var p = EHS.PlayerRef.LocalPlayer;
            return p != null ? p.OwnerId : -1;
        }
        catch { return -1; }
    }

    static void SendHello(ulong to)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Hello);
        w.Write(Protocol);
        w.Write(LocalOwnerId());
        var shared = mods.Values.Where(m => m.Bytes != null).ToList();
        w.Write(shared.Count);
        foreach (var m in shared) { w.Write(m.Key); w.Write(m.Hash); w.Write(m.Bytes.Length); }
        w.Flush();
        Steam.Send(to, ms.GetBuffer(), (int)ms.Length);
    }

    static void Handle(ulong from, byte[] data, float now)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        byte kind = r.ReadByte();
        if (!peers.TryGetValue(from, out var p))
        {
            if (lobby == 0 || !Steam.LobbyMembers(lobby).Contains(from)) return; // only people in our lobby
            peers[from] = p = new Peer { Id = from };
        }
        p.LastHeard = now;
        if (kind == Hello)
        {
            if (r.ReadInt32() != Protocol) return;
            int owner = r.ReadInt32();
            int n = r.ReadInt32();
            if (p.Name == "") { p.Name = Steam.Name(from); Plugin.Logger.LogInfo($"{p.Name} has FIH Sync"); }
            if (owner != p.OwnerId && p.OwnerId >= 0) foreach (var key in p.Shown.Keys.ToList()) HideFor(p, key);
            p.OwnerId = owner;
            p.Offers.Clear();
            for (int i = 0; i < Math.Min(n, 32); i++)
            {
                string key = r.ReadString(), hash = r.ReadString();
                int size = r.ReadInt32();
                if (size > 0 && hash.Length == 32 && key.Length <= 40) p.Offers[key] = (hash, size);
            }
            if (now - p.LastHelloSent > 1f && p.LastHelloSent < 0f) { p.LastHelloSent = now; SendHello(from); } // a newcomer: answer right away
            Update(p);
        }
        else if (kind == Want)
        {
            string key = r.ReadString(), hash = r.ReadString();
            if (!mods.TryGetValue(key, out var m) || m.Hash != hash || m.Bytes == null) return;
            int count = (m.Bytes.Length + ChunkSize - 1) / ChunkSize;
            for (int i = 0; i < count; i++) outbox.Enqueue((from, key, hash, i));
            Plugin.Logger.LogInfo($"sending '{key}' to {p.Name} ({m.Bytes.Length / 1024.0:0.#} KB)");
        }
        else if (kind == Chunk)
        {
            string hash = r.ReadString();
            int index = r.ReadInt32(), count = r.ReadInt32();
            var bytes = r.ReadBytes(r.ReadInt32());
            if (!downloads.TryGetValue(hash, out var d) || d.From != from || count != d.Chunks.Length || index < 0 || index >= count || d.Chunks[index] != null) return;
            int expected = index < count - 1 ? ChunkSize : d.Size - (count - 1) * ChunkSize;
            if (bytes.Length != expected) { Plugin.Logger.LogWarning($"'{d.Key}' from {p.Name}: wrong piece size, dropped"); downloads.Remove(hash); return; }
            d.Chunks[index] = bytes;
            d.Got++;
            d.LastProgress = now;
            if (d.Got < count) return;
            downloads.Remove(hash);
            var all = new byte[d.Chunks.Sum(c => c.Length)];
            int at = 0;
            foreach (var c in d.Chunks) { Buffer.BlockCopy(c, 0, all, at, c.Length); at += c.Length; }
            if (Convert.ToHexString(SHA256.HashData(all)).Substring(0, 32) != hash) { Plugin.Logger.LogWarning($"'{d.Key}' from {p.Name}: damaged, dropped"); return; }
            Cache.Put(hash, all);
            if (d.Sign) DownloadedSinceJoin++;
            Plugin.Logger.LogInfo($"got '{d.Key}' from {p.Name} ({all.Length / 1024.0:0.#} KB)");
            Update(p);
        }
    }

    /// Shows, swaps or hides each of this player's looks to match what they offer and what the settings allow.
    static void Update(Peer p)
    {
        bool allowed = Allowed(p.Id);
        foreach (var key in p.Shown.Keys.ToList())
            if (!allowed || !p.Offers.TryGetValue(key, out var o) || o.hash != p.Shown[key]) HideFor(p, key);
        if (!allowed || p.OwnerId < 0) return;
        foreach (var offer in p.Offers)
        {
            string key = offer.Key, hash = offer.Value.hash;
            int size = offer.Value.size;
            if (!mods.TryGetValue(key, out var m) || p.Shown.ContainsKey(key)) continue;
            var cached = Cache.Get(hash);
            if (cached != null)
            {
                p.Shown[key] = hash;
                try { m.Received(p.OwnerId, cached, Cache.Folder(hash)); }
                catch (Exception e) { Plugin.Logger.LogWarning($"'{key}' of {p.Name}: {e.Message}"); }
                continue;
            }
            if (size > Plugin.MaxMB.Value * 1024 * 1024) { Plugin.Logger.LogInfo($"'{key}' of {p.Name} skipped: {size / 1048576.0:0.#} MB is over the {Plugin.MaxMB.Value} MB limit"); continue; }
            if (downloads.ContainsKey(hash)) continue;
            int count = Math.Max(1, (size + ChunkSize - 1) / ChunkSize);
            downloads[hash] = new Download
            {
                From = p.Id, Key = key, Hash = hash, Size = size, Chunks = new byte[count][], LastProgress = Time.unscaledTime,
                Sign = Time.unscaledTime - joinedAt < 60f || size > 512 * 1024, // joining, or something big: not a slider tweak
            };
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(Want); w.Write(key); w.Write(hash);
            w.Flush();
            Steam.Send(p.Id, ms.GetBuffer(), (int)ms.Length);
        }
    }

    static bool Allowed(ulong id)
    {
        if (Plugin.BlockedSet().Contains(id.ToString()) || hidden.Contains(id)) return false;
        if (peers.TryGetValue(id, out var p) && p.TooHeavy && !p.ShowAnyway) return false;
        return Plugin.ShowFrom.Value switch
        {
            Plugin.Who.Nobody => false,
            Plugin.Who.FriendsOnly => Steam.IsFriend(id),
            _ => true,
        };
    }

    static void HideFor(Peer p, string key)
    {
        p.Shown.Remove(key);
        if (mods.TryGetValue(key, out var m) && p.OwnerId >= 0)
            try { m.Gone(p.OwnerId); } catch (Exception e) { Plugin.Logger.LogWarning($"'{key}' gone: {e.Message}"); }
    }

    static void Forget(ulong id)
    {
        if (!peers.TryGetValue(id, out var p)) return;
        foreach (var key in p.Shown.Keys.ToList()) HideFor(p, key);
        peers.Remove(id);
        foreach (var d in downloads.Values.Where(d => d.From == id).ToList()) downloads.Remove(d.Hash);
    }

    /// Settings changed (or a block): re-check every player.
    internal static void Recheck()
    {
        foreach (var p in peers.Values) Update(p);
    }

    static void SendChunks()
    {
        int budget = SendBudget;
        while (budget > 0 && outbox.Count > 0)
        {
            var (to, key, hash, index) = outbox.Peek();
            if (!mods.TryGetValue(key, out var m) || m.Hash != hash || m.Bytes == null || !peers.ContainsKey(to)) { outbox.Dequeue(); continue; }
            int count = (m.Bytes.Length + ChunkSize - 1) / ChunkSize;
            int start = index * ChunkSize, len = Math.Min(ChunkSize, m.Bytes.Length - start);
            using var ms = new MemoryStream(len + 64);
            using var w = new BinaryWriter(ms);
            w.Write(Chunk); w.Write(hash); w.Write(index); w.Write(count); w.Write(len);
            w.Write(m.Bytes, start, len);
            w.Flush();
            // Steam refuses when its send buffer is full: keep the piece and try again next frame
            if (!Steam.Send(to, ms.GetBuffer(), (int)ms.Length)) break;
            outbox.Dequeue();
            budget -= len;
        }
    }
}

/// Other players' looks on disk, named by their hash, so meeting someone again needs no download. Files not used for
/// a few days (config) delete themselves, and so do the scratch folders mods unpack into.
internal static class Cache
{
    static string Root => Path.Combine(BepInEx.Paths.BepInExRootPath, "cache", "FIHSync");

    internal static byte[] Get(string hash)
    {
        var path = Path.Combine(Root, hash + ".bin");
        if (!File.Exists(path)) return null;
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // still in use: the expiry counts from the last use
            var dir = Folder(hash);
            if (Directory.Exists(dir)) Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
            return File.ReadAllBytes(path);
        }
        catch { return null; }
    }

    internal static void Put(string hash, byte[] data)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(Path.Combine(Root, hash + ".bin"), data);
    }

    internal static string Folder(string hash) => Path.Combine(Root, "unpacked", hash);

    internal static void Sweep()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            var old = DateTime.UtcNow.AddDays(-Math.Max(0.1, Plugin.CacheDays.Value));
            int n = 0;
            foreach (var f in Directory.GetFiles(Root, "*.bin"))
                if (File.GetLastWriteTimeUtc(f) < old) { try { File.Delete(f); n++; } catch { /* in use */ } }
            var unpacked = Path.Combine(Root, "unpacked");
            if (Directory.Exists(unpacked))
                foreach (var d in Directory.GetDirectories(unpacked))
                    if (Directory.GetLastWriteTimeUtc(d) < old) { try { Directory.Delete(d, true); n++; } catch { /* a video still playing */ } }
            if (n > 0) Plugin.Logger.LogInfo($"cache: deleted {n} old look(s) from other players");
        }
        catch (Exception e) { Plugin.Logger.LogWarning("cache sweep: " + e.Message); }
    }
}
