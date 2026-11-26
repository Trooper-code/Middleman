using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
#if IL2CPP
using Il2CppFishNet;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Networking;
using Il2CppSteamworks;
using GameSteamManager = Il2Cpp.SteamManager;
#else
using FishNet;
using ScheduleOne.DevUtilities;
using ScheduleOne.Networking;
using Steamworks;
using GameSteamManager = SteamManager;
#endif

[assembly: MelonInfo(typeof(NetProbe.NetProbeMod), "NetProbe", "0.1.0", "Trooper-code")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace NetProbe;

/// <summary>
/// Debug-only throwaway probe mod for verifying Steam lobby chat messaging
/// between a non-host client and the host on both Mono and IL2CPP.
/// </summary>
public sealed class NetProbeMod : MelonMod
{
    private const string Prefix = "S1NET|1|netprobe|";
    private const int MaxMessageBytes = 4096;

#if !IL2CPP
    private static readonly FieldInfo? LobbyServiceField =
        AccessTools.Field(typeof(Lobby), "_lobbyService");

    private static readonly PropertyInfo? SteamLobbyIdProp =
        AccessTools.Property(typeof(SteamLobbyService), "_lobbyID");
#endif

    private Callback<LobbyChatMsg_t>? _lobbyChatCallback;
    private int _pingCounter;

    public override void OnInitializeMelon()
    {
        LoggerInstance.Msg("NetProbe v0.1.0 initialized (debug probe). Press F9 in a co-op session to test ping/pong.");
    }

    public override void OnUpdate()
    {
        EnsureCallbackRegistered();

        if (!Input.GetKeyDown(KeyCode.F9))
        {
            return;
        }

        if (!TryGetCurrentLobbyId(out CSteamID lobbyId))
        {
            LoggerInstance.Warning("[NetProbe] F9 pressed, but you are not currently in a Steam lobby.");
            return;
        }

        if (InstanceFinder.IsServer)
        {
            LoggerInstance.Msg("[NetProbe] F9 pressed on host (no-op; press F9 on a non-host client to send a ping).");
            return;
        }

        _pingCounter++;
        string steamName = SanitizeField(SteamFriends.GetPersonaName());
        string wire = $"{Prefix}C2H|ping|{steamName}|{_pingCounter}";

        if (SendLobbyMessage(lobbyId, wire))
        {
            LoggerInstance.Msg($"[NetProbe] Sent ping #{_pingCounter} ('{steamName}') to host via lobby {lobbyId.m_SteamID}.");
        }
        else
        {
            LoggerInstance.Warning($"[NetProbe] Failed to send ping #{_pingCounter} to lobby {lobbyId.m_SteamID}.");
        }
    }

    public override void OnApplicationQuit()
    {
        if (_lobbyChatCallback != null)
        {
            _lobbyChatCallback.Dispose();
            _lobbyChatCallback = null;
        }
    }

    private void EnsureCallbackRegistered()
    {
        if (_lobbyChatCallback != null || !GameSteamManager.Initialized)
        {
            return;
        }

#if IL2CPP
        _lobbyChatCallback = Callback<LobbyChatMsg_t>.Create((Action<LobbyChatMsg_t>)OnLobbyChatMessage);
#else
        _lobbyChatCallback = Callback<LobbyChatMsg_t>.Create(OnLobbyChatMessage);
#endif
        LoggerInstance.Msg("[NetProbe] Registered Steam Callback<LobbyChatMsg_t>.");
    }

    private void OnLobbyChatMessage(LobbyChatMsg_t ev)
    {
        if (!TryGetCurrentLobbyId(out CSteamID lobbyId) || ev.m_ulSteamIDLobby != lobbyId.m_SteamID)
        {
            return;
        }

        if (!TryReadLobbyMessage(lobbyId, (int)ev.m_iChatID, out CSteamID sender, out string text))
        {
            return;
        }

        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return;
        }

        string body = text.Substring(Prefix.Length);
        string[] parts = body.Split('|');
        if (parts.Length < 3)
        {
            return;
        }

        string direction = parts[0];
        string msgType = parts[1];

        if (direction == "C2H" && msgType == "ping" && parts.Length >= 4)
        {
            if (!InstanceFinder.IsServer)
            {
                return;
            }

            if (!IsLobbyMember(lobbyId, sender))
            {
                LoggerInstance.Warning($"[NetProbe][HOST] Rejected ping from non-member SteamID {sender.m_SteamID}.");
                return;
            }

            string senderName = parts[2];
            string counter = parts[3];
            LoggerInstance.Msg($"[NetProbe][HOST] Received ping #{counter} from '{senderName}' (SteamID: {sender.m_SteamID}).");

            string pong = $"{Prefix}H2A|pong|{counter}";
            if (SendLobbyMessage(lobbyId, pong))
            {
                LoggerInstance.Msg($"[NetProbe][HOST] Broadcasted pong #{counter} to lobby.");
            }
            else
            {
                LoggerInstance.Warning($"[NetProbe][HOST] Failed to broadcast pong #{counter}.");
            }
        }
        else if (direction == "H2A" && msgType == "pong")
        {
            CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobbyId);
            if (sender != owner)
            {
                LoggerInstance.Warning($"[NetProbe] Ignored pong from non-owner SteamID {sender.m_SteamID} (owner: {owner.m_SteamID}).");
                return;
            }

            string counter = parts[2];
            LoggerInstance.Msg($"[NetProbe] Received pong #{counter} from host (SteamID: {sender.m_SteamID}).");
        }
    }

    private static bool TryGetCurrentLobbyId(out CSteamID lobbyId)
    {
        lobbyId = CSteamID.Nil;
        if (!GameSteamManager.Initialized || !Singleton<Lobby>.InstanceExists)
        {
            return false;
        }

        Lobby lobby = Singleton<Lobby>.Instance;
        if (!lobby.IsInLobby)
        {
            return false;
        }

#if IL2CPP
        ILobbyService? service = lobby._lobbyService;
        if (service == null)
        {
            return false;
        }

        SteamLobbyService? steamService = service.TryCast<SteamLobbyService>();
        if (steamService == null)
        {
            return false;
        }

        ulong rawId = steamService._lobbyID;
#else
        object? service = LobbyServiceField?.GetValue(lobby);
        if (service is not SteamLobbyService steamService)
        {
            return false;
        }

        object? rawObj = SteamLobbyIdProp?.GetValue(steamService);
        if (rawObj is not ulong rawId)
        {
            return false;
        }
#endif

        if (rawId == 0)
        {
            return false;
        }

        lobbyId = new CSteamID(rawId);
        return true;
    }

    private static bool IsLobbyMember(CSteamID lobbyId, CSteamID userId)
    {
        int count = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
        for (int i = 0; i < count; i++)
        {
            if (SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i) == userId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SendLobbyMessage(CSteamID lobbyId, string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        if (utf8.Length == 0 || utf8.Length > MaxMessageBytes)
        {
            return false;
        }

#if IL2CPP
        Il2CppStructArray<byte> buffer = new Il2CppStructArray<byte>(utf8.Length);
        for (int i = 0; i < utf8.Length; i++)
        {
            buffer[i] = utf8[i];
        }
        return SteamMatchmaking.SendLobbyChatMsg(lobbyId, buffer, utf8.Length);
#else
        return SteamMatchmaking.SendLobbyChatMsg(lobbyId, utf8, utf8.Length);
#endif
    }

    private static bool TryReadLobbyMessage(
        CSteamID lobbyId,
        int chatId,
        out CSteamID sender,
        out string text)
    {
        sender = CSteamID.Nil;
        text = string.Empty;

#if IL2CPP
        Il2CppStructArray<byte> buffer = new Il2CppStructArray<byte>(MaxMessageBytes);
        int read = SteamMatchmaking.GetLobbyChatEntry(
            lobbyId,
            chatId,
            out sender,
            buffer,
            MaxMessageBytes,
            out _);

        if (read <= 0)
        {
            return false;
        }

        int len = Math.Min(read, MaxMessageBytes);
        byte[] managed = new byte[len];
        for (int i = 0; i < len; i++)
        {
            managed[i] = buffer[i];
        }

        text = Encoding.UTF8.GetString(managed, 0, len).TrimEnd('\0');
        return true;
#else
        byte[] buffer = new byte[MaxMessageBytes];
        int read = SteamMatchmaking.GetLobbyChatEntry(
            lobbyId,
            chatId,
            out sender,
            buffer,
            MaxMessageBytes,
            out _);

        if (read <= 0)
        {
            return false;
        }

        int len = Math.Min(read, MaxMessageBytes);
        text = Encoding.UTF8.GetString(buffer, 0, len).TrimEnd('\0');
        return true;
#endif
    }

    private static string SanitizeField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "Unknown";
        }

        return value.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
    }
}
