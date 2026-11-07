using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FIHSync;

/// Steam peer-to-peer through the game's own steam_api64.dll (already loaded and initialized by the game), with the
/// flat C API. This skips Steamworks.NET's interop wrappers, so nothing here can be a stripped-and-rebuilt method
/// (kit trap 19). Messages go through ISteamNetworkingMessages, relayed by Steam: no ports, no IPs, and it stays
/// out of the game's FishNet connection completely.
internal static unsafe class Steam
{
    const string Lib = "steam_api64";
    internal const int Channel = 4649; // "FI": our own channel, the game doesn't use ISteamNetworkingMessages
    const int Reliable = 8, AutoRestartBrokenSession = 32;

    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
    [DllImport(Lib)] static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self, Identity* to, void* data, uint size, int flags, int channel);
    [DllImport(Lib)] static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self, int channel, IntPtr* messages, int max);
    [DllImport(Lib)] static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);
    [DllImport(Lib)] static extern void SteamAPI_SteamNetworkingIdentity_SetSteamID64(Identity* self, ulong id);
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamMatchmaking_v009();
    [DllImport(Lib)] static extern int SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(IntPtr self, ulong lobby);
    [DllImport(Lib)] static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(IntPtr self, ulong lobby, int index);
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamUser_v023();
    [DllImport(Lib)] static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamFriends_v017();
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SteamAPI_ISteamFriends_HasFriend(IntPtr self, ulong id, int flags);
    [DllImport(Lib)] static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong id);

    /// SteamNetworkingIdentity: type, size, then a 128-byte union (the SteamID64 sits at its start).
    [StructLayout(LayoutKind.Sequential, Size = 136)]
    struct Identity { public int Type, Size; public ulong Id; }

    static IntPtr messages, matchmaking, friends;
    internal static ulong Me;
    internal static bool Off; // steam_api64 couldn't be reached: sharing stays off, the menus still work

    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

    /// .NET only looks for "steam_api64" next to the game and in the plugin's folder, but the game keeps it in
    /// Flipping is Hard_Data\Plugins\x86_64. Point our imports at the copy the game already loaded (or that file).
    internal static void Resolve()
    {
        NativeLibrary.SetDllImportResolver(typeof(Steam).Assembly, Find);
    }

    static IntPtr Find(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        var loaded = GetModuleHandleW("steam_api64.dll");
        if (loaded != IntPtr.Zero) return loaded;
        var file = Path.Combine(BepInEx.Paths.GameRootPath, "Flipping is Hard_Data", "Plugins", "x86_64", "steam_api64.dll");
        return File.Exists(file) && NativeLibrary.TryLoad(file, out var h) ? h : IntPtr.Zero;
    }

    /// False until the game's Steam is up (main menu onwards).
    internal static bool Ready()
    {
        if (Me != 0) return true;
        if (Off) return false;
        messages = SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
        matchmaking = SteamAPI_SteamMatchmaking_v009();
        friends = SteamAPI_SteamFriends_v017();
        var user = SteamAPI_SteamUser_v023();
        if (messages == IntPtr.Zero || matchmaking == IntPtr.Zero || friends == IntPtr.Zero || user == IntPtr.Zero) return false;
        Me = SteamAPI_ISteamUser_GetSteamID(user);
        if (Me != 0) Plugin.Logger.LogInfo($"Steam ready: {Name(Me)}");
        return Me != 0;
    }

    internal static List<ulong> LobbyMembers(ulong lobby)
    {
        var list = new List<ulong>();
        int n = SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(matchmaking, lobby);
        for (int i = 0; i < n; i++)
        {
            ulong id = SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(matchmaking, lobby, i);
            if (id != 0 && id != Me) list.Add(id);
        }
        return list;
    }

    internal static bool IsFriend(ulong id) => SteamAPI_ISteamFriends_HasFriend(friends, id, 4); // k_EFriendFlagImmediate

    internal static string Name(ulong id)
    {
        var p = SteamAPI_ISteamFriends_GetFriendPersonaName(friends, id);
        return p != IntPtr.Zero ? Marshal.PtrToStringUTF8(p) ?? id.ToString() : id.ToString();
    }

    /// Reliable and in order. Also accepts that player's session, so two modded players who both say hello are
    /// connected without a session-request callback.
    internal static bool Send(ulong to, byte[] data, int length)
    {
        var who = new Identity();
        SteamAPI_SteamNetworkingIdentity_SetSteamID64(&who, to);
        fixed (byte* p = data)
            return SteamAPI_ISteamNetworkingMessages_SendMessageToUser(messages, &who, p, (uint)length, Reliable | AutoRestartBrokenSession, Channel) == 1; // k_EResultOK
    }

    /// Everything that arrived since last frame: (sender, bytes).
    internal static List<(ulong from, byte[] data)> Receive()
    {
        var got = new List<(ulong, byte[])>();
        var buffer = stackalloc IntPtr[32];
        while (true)
        {
            int n = SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(messages, Channel, buffer, 32);
            for (int i = 0; i < n; i++)
            {
                // SteamNetworkingMessage_t: m_pData at 0, m_cbSize at 8, m_identityPeer at 16 (type 16 = SteamID, id at 24)
                byte* m = (byte*)buffer[i];
                var data = *(IntPtr*)m;
                int size = *(int*)(m + 8);
                int type = *(int*)(m + 16);
                ulong from = *(ulong*)(m + 24);
                if (type == 16 && size > 0 && data != IntPtr.Zero)
                {
                    var bytes = new byte[size];
                    Marshal.Copy(data, bytes, 0, size);
                    got.Add((from, bytes));
                }
                SteamAPI_SteamNetworkingMessage_t_Release(buffer[i]);
            }
            if (n < 32) break;
        }
        return got;
    }
}
