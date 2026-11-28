using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
#if IL2CPP
using Il2CppFishNet;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Networking;
using Il2CppScheduleOne.Property;
using Il2CppScheduleOne.Quests;
#if HAS_STEAMNETWORKLIB
using Il2CppSteamworks;
using SteamNetworkLib;
using SteamNetworkLib.Core;
using SteamNetworkLib.Exceptions;
using SteamNetworkLib.Models;
#endif
#else
using FishNet;
using ScheduleOne.DevUtilities;
using ScheduleOne.Economy;
using ScheduleOne.Networking;
using ScheduleOne.Property;
using ScheduleOne.Quests;
#if HAS_STEAMNETWORKLIB
using Steamworks;
using SteamNetworkLib;
using SteamNetworkLib.Core;
using SteamNetworkLib.Exceptions;
using SteamNetworkLib.Models;
#endif
#endif

namespace Middleman;

/// <summary>
/// Handles client-to-host requests and host-to-client synchronization over optional SteamNetworkLib.
/// Single-player sessions never require SteamNetworkLib in UserLibs\ and fall back cleanly to local execution.
/// </summary>
internal static class MiddlemanNetwork
{
#if HAS_STEAMNETWORKLIB
    private const string Prefix = "MM2|";
    private const double InitRetryIntervalSeconds = 2.0;

    private interface ISyncBackend
    {
        bool Initialize(out string? errorMessage);
        void ProcessIncoming();
        void Dispose();
        bool IsInLobby { get; }
        ulong HostPlayerId64 { get; }
        ulong LocalPlayerId64 { get; }
        void SendText(ulong targetSteamId, string wire);
        void BroadcastText(string wire);
        bool TryGetLobbyMember(ulong steamId);
    }

    private static ISyncBackend? _backend;
    private static bool _permanentlyUnavailable;
    private static bool _loggedUnavailable;
    private static string? _lastInitWarning;
    private static DateTime _nextInitAttemptUtc = DateTime.MinValue;

    public static bool IsAvailable => _backend != null;

    public static void Initialize()
    {
        TryInitializeClient();
    }

    public static void Update()
    {
        if (!TryInitializeClient() || _backend == null)
        {
            return;
        }

        _backend.ProcessIncoming();
    }

    public static void Dispose()
    {
        _nextInitAttemptUtc = DateTime.MinValue;
        if (_backend != null)
        {
            _backend.Dispose();
            _backend = null;
        }
    }

    public static void RequestStateSyncIfClient()
    {
        if (InstanceFinder.IsServer || !IsMultiplayerSession())
        {
            return;
        }

        SendToHost($"{Prefix}SYNC_STATE", notifyOnFailure: false);
    }

    public static void SendPassOffer(Customer customer)
    {
        string npcId = customer.NPC?.ID ?? string.Empty;
        if (string.IsNullOrEmpty(npcId))
        {
            MiddlemanController.Notify("Middleman", "Customer identifier is not ready yet.");
            return;
        }

        SendToHost($"{Prefix}PASS_OFFER|{SanitizeField(npcId)}", notifyOnFailure: true);
    }

    public static void SendPassAccepted(Customer customer)
    {
        string npcId = customer.NPC?.ID ?? string.Empty;
        if (string.IsNullOrEmpty(npcId))
        {
            MiddlemanController.Notify("Middleman", "Customer identifier is not ready yet.");
            return;
        }

        SendToHost($"{Prefix}PASS_ACCEPTED|{SanitizeField(npcId)}", notifyOnFailure: true);
    }

    public static void SendSummonDealer(Dealer dealer, Property property)
    {
        if (string.IsNullOrEmpty(dealer.ID))
        {
            MiddlemanController.Notify("Middleman", "Dealer identifier is not ready yet.");
            return;
        }

        string propKey = GetPropertyKey(property);
        SendToHost($"{Prefix}SUMMON|{SanitizeField(dealer.ID)}|{SanitizeField(propKey)}", notifyOnFailure: true);
    }

    public static void SendCancelMeetup(Dealer dealer)
    {
        if (string.IsNullOrEmpty(dealer.ID))
        {
            return;
        }

        SendToHost($"{Prefix}HEAD_BACK|{SanitizeField(dealer.ID)}", notifyOnFailure: true);
    }

    public static void SendCheckStatus(Dealer dealer)
    {
        if (string.IsNullOrEmpty(dealer.ID))
        {
            return;
        }

        SendToHost($"{Prefix}CHECK_STATUS|{SanitizeField(dealer.ID)}", notifyOnFailure: true);
    }

    public static void SendToastToClient(ulong targetSteamId, string title, string subtitle)
    {
        if (targetSteamId == 0 || !InstanceFinder.IsServer || !TryInitializeClient() || _backend == null)
        {
            return;
        }

        string wire = $"{Prefix}TOAST|{SanitizeField(title)}|{SanitizeField(subtitle)}";
        _backend.SendText(targetSteamId, wire);
    }

    public static void BroadcastContractAssigned(string contractGuid, string dealerId)
    {
        if (!InstanceFinder.IsServer || string.IsNullOrEmpty(contractGuid) || string.IsNullOrEmpty(dealerId))
        {
            return;
        }

        BroadcastToClients($"{Prefix}CONTRACT_ASSIGNED|{SanitizeField(contractGuid)}|{SanitizeField(dealerId)}");
    }

    public static void BroadcastMeetupState(string dealerId, bool active, ulong? targetSteamId = null)
    {
        if (!InstanceFinder.IsServer || string.IsNullOrEmpty(dealerId))
        {
            return;
        }

        string flag = active ? "1" : "0";
        string wire = $"{Prefix}MEETUP_STATE|{SanitizeField(dealerId)}|{flag}";

        if (targetSteamId.HasValue && targetSteamId.Value != 0)
        {
            if (TryInitializeClient() && _backend != null)
            {
                _backend.SendText(targetSteamId.Value, wire);
            }
            return;
        }

        BroadcastToClients(wire);
    }

    private static bool TryInitializeClient()
    {
        if (_backend != null)
        {
            return true;
        }

        if (_permanentlyUnavailable)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (now < _nextInitAttemptUtc)
        {
            return false;
        }
        _nextInitAttemptUtc = now.AddSeconds(InitRetryIntervalSeconds);

        try
        {
            ISyncBackend? candidate = CreateAndInitBackend(out string? errorMessage);
            if (candidate != null)
            {
                _backend = candidate;
                _lastInitWarning = null;
                MiddlemanMod.Log.Msg("SteamNetworkLib client initialized for Middleman multiplayer.");
                return true;
            }

            if (!string.IsNullOrEmpty(errorMessage) && errorMessage != _lastInitWarning)
            {
                _lastInitWarning = errorMessage;
                MiddlemanMod.Log.Warning($"SteamNetworkLib not ready yet: {errorMessage}");
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is TypeLoadException || ex is BadImageFormatException)
        {
            _permanentlyUnavailable = true;
            if (!_loggedUnavailable)
            {
                _loggedUnavailable = true;
                MiddlemanMod.Log.Msg(
                    "SteamNetworkLib was not found in UserLibs\\; single-player works normally, " +
                    "and co-op multiplayer sync is disabled.");
            }
        }
        catch (Exception ex)
        {
            if (ex.Message != _lastInitWarning)
            {
                _lastInitWarning = ex.Message;
                MiddlemanMod.Log.Warning($"SteamNetworkLib init deferred: {ex.Message}");
            }
        }

        return false;
    }

    private static bool IsMultiplayerSession()
    {
        if (_backend != null && _backend.IsInLobby)
        {
            return true;
        }

        return Singleton<Lobby>.InstanceExists && Singleton<Lobby>.Instance.IsInLobby;
    }

    private static bool TryGetHostSteamId(out ulong hostId)
    {
        hostId = 0;

        if (_backend != null && _backend.HostPlayerId64 != 0)
        {
            hostId = _backend.HostPlayerId64;
            return true;
        }

        if (Singleton<Lobby>.InstanceExists && Singleton<Lobby>.Instance.IsInLobby)
        {
            string? raw = Singleton<Lobby>.Instance.GetSessionConnectionIdentifier();
            if (!string.IsNullOrEmpty(raw) && ulong.TryParse(raw, out ulong parsed) && parsed != 0)
            {
                hostId = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool SendToHost(string wire, bool notifyOnFailure)
    {
        if (!TryInitializeClient() || _backend == null)
        {
            if (notifyOnFailure)
            {
                string message = _permanentlyUnavailable
                    ? "Co-op actions require SteamNetworkLib in UserLibs\\."
                    : "Could not reach the host right now. Make sure you're in a Steam co-op lobby.";
                MiddlemanController.Notify("Middleman", message);
            }
            return false;
        }

        if (!TryGetHostSteamId(out ulong hostId))
        {
            if (notifyOnFailure)
            {
                MiddlemanController.Notify("Middleman", "Could not reach the host right now. Make sure you're in a Steam co-op lobby.");
            }
            return false;
        }

        _backend.SendText(hostId, wire);
        return true;
    }

    private static void BroadcastToClients(string wire)
    {
        if (!TryInitializeClient() || _backend == null || !IsMultiplayerSession())
        {
            return;
        }

        if (_backend.IsInLobby)
        {
            _backend.BroadcastText(wire);
            return;
        }

        if (!Singleton<Lobby>.InstanceExists || !Singleton<Lobby>.Instance.IsInLobby)
        {
            return;
        }

        ulong localId = _backend.LocalPlayerId64;
        var members = Singleton<Lobby>.Instance.GetLobbyMemberIDs();
        if (members == null)
        {
            return;
        }

        for (int i = 0; i < members.Count; i++)
        {
            if (ulong.TryParse(members[i], out ulong memberId) && memberId != 0 && memberId != localId)
            {
                _backend.SendText(memberId, wire);
            }
        }
    }

    private static void OnWireMessageReceived(string content, ulong senderSteamId)
    {
        if (string.IsNullOrEmpty(content) || !content.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return;
        }

        string[] parts = content.Substring(Prefix.Length).Split('|');
        if (parts.Length == 0 || string.IsNullOrEmpty(parts[0]))
        {
            return;
        }

        string msgType = parts[0];

        if (InstanceFinder.IsServer)
        {
            HandleServerMessage(msgType, parts, senderSteamId);
        }
        else
        {
            HandleClientMessage(msgType, parts, senderSteamId);
        }
    }

    private static void HandleServerMessage(string msgType, string[] parts, ulong senderSteamId)
    {
        if (!IsAuthorizedLobbyMember(senderSteamId))
        {
            MiddlemanMod.Log.Warning($"Ignored '{msgType}' from non-lobby SteamID {senderSteamId}.");
            return;
        }

        switch (msgType)
        {
            case "PASS_OFFER":
                if (parts.Length >= 2)
                {
                    Customer? customer = FindCustomerByNpcId(parts[1]);
                    if (GameCompat.IsAlive(customer))
                    {
                        MiddlemanController.TryPassOfferedContractOnHost(customer!, senderSteamId);
                    }
                    else
                    {
                        SendToastToClient(senderSteamId, "Middleman", "Customer was not found on the host.");
                    }
                }
                break;

            case "PASS_ACCEPTED":
                if (parts.Length >= 2)
                {
                    Customer? customer = FindCustomerByNpcId(parts[1]);
                    if (GameCompat.IsAlive(customer))
                    {
                        MiddlemanController.TryPassAcceptedContractOnHost(customer!, senderSteamId);
                    }
                    else
                    {
                        SendToastToClient(senderSteamId, "Middleman", "Customer was not found on the host.");
                    }
                }
                break;

            case "SUMMON":
                if (parts.Length >= 3)
                {
                    Dealer? dealer = FindDealerById(parts[1]);
                    Property? property = FindPropertyByKey(parts[2]);
                    if (GameCompat.IsAlive(dealer) && GameCompat.IsAlive(property))
                    {
                        DealerMeetupManager.RequestMeetupOnHost(dealer!, property!, senderSteamId);
                    }
                    else
                    {
                        SendToastToClient(senderSteamId, "Middleman", "Dealer or property was not found on the host.");
                    }
                }
                break;

            case "HEAD_BACK":
                if (parts.Length >= 2)
                {
                    Dealer? dealer = FindDealerById(parts[1]);
                    if (GameCompat.IsAlive(dealer))
                    {
                        DealerMeetupManager.CancelMeetupOnHost(dealer!, sendChat: true, requesterSteamId: senderSteamId);
                    }
                    else
                    {
                        SendToastToClient(senderSteamId, "Middleman", "Dealer was not found on the host.");
                    }
                }
                break;

            case "CHECK_STATUS":
                if (parts.Length >= 2)
                {
                    Dealer? dealer = FindDealerById(parts[1]);
                    if (GameCompat.IsAlive(dealer))
                    {
                        DealerMeetupManager.SendStatusReportOnHost(dealer!);
                    }
                    else
                    {
                        SendToastToClient(senderSteamId, "Middleman", "Dealer was not found on the host.");
                    }
                }
                break;

            case "SYNC_STATE":
                DealerMeetupManager.SendActiveMeetupsToClient(senderSteamId);
                break;
        }
    }

    private static void HandleClientMessage(string msgType, string[] parts, ulong senderSteamId)
    {
        if (!TryGetHostSteamId(out ulong hostId) || senderSteamId != hostId)
        {
            MiddlemanMod.Log.Warning($"Ignored '{msgType}' from non-host SteamID {senderSteamId}.");
            return;
        }

        switch (msgType)
        {
            case "TOAST":
                if (parts.Length >= 3)
                {
                    MiddlemanController.Notify(parts[1], parts[2]);
                }
                break;

            case "CONTRACT_ASSIGNED":
                if (parts.Length >= 3)
                {
                    MiddlemanController.ApplyRemoteContractAssigned(parts[1], parts[2]);
                }
                break;

            case "MEETUP_STATE":
                if (parts.Length >= 3)
                {
                    bool active = parts[2] == "1";
                    DealerMeetupManager.SetClientMeetupState(parts[1], active);
                }
                break;
        }
    }

    private static bool IsAuthorizedLobbyMember(ulong senderSteamId)
    {
        if (_backend == null || senderSteamId == 0 || senderSteamId == _backend.LocalPlayerId64)
        {
            return false;
        }

        if (_backend.IsInLobby)
        {
            return _backend.TryGetLobbyMember(senderSteamId);
        }

        if (Singleton<Lobby>.InstanceExists && Singleton<Lobby>.Instance.IsInLobby)
        {
            var members = Singleton<Lobby>.Instance.GetLobbyMemberIDs();
            return members != null && members.Contains(senderSteamId.ToString());
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ISyncBackend? CreateAndInitBackend(out string? errorMessage)
    {
        var backend = new SteamBackend();
        if (backend.Initialize(out errorMessage))
        {
            return backend;
        }

        backend.Dispose();
        return null;
    }

    private sealed class SteamBackend : ISyncBackend
    {
        private readonly SteamNetworkClient _client = new(new NetworkRules
        {
            EnableRelay = true,
            AcceptOnlyFriends = false
        });

        public bool IsInLobby => _client.IsInLobby;
        public ulong HostPlayerId64 => _client.HostPlayerId64;
        public ulong LocalPlayerId64 => _client.LocalPlayerId64;

        public bool Initialize(out string? errorMessage)
        {
            if (!_client.TryInitialize(out SteamNetworkException? error))
            {
                errorMessage = error?.Message;
                return false;
            }

            errorMessage = null;
            _client.RegisterMessageHandler<TextMessage>(OnTextMessageReceived);
            return true;
        }

        public void ProcessIncoming()
        {
            if (!_client.IsInitialized)
            {
                return;
            }

            _client.ProcessIncomingMessages();
        }

        public void Dispose()
        {
            _client.Dispose();
        }

        public void SendText(ulong targetSteamId, string wire)
        {
            if (!_client.IsInitialized || targetSteamId == 0)
            {
                return;
            }

            ObserveSend(_client.SendTextMessageAsync(new CSteamID(targetSteamId), wire));
        }

        public void BroadcastText(string wire)
        {
            if (!_client.IsInitialized)
            {
                return;
            }

            _client.BroadcastTextMessage(wire);
        }

        public bool TryGetLobbyMember(ulong steamId)
        {
            return _client.TryGetMember(steamId, out _);
        }

        private static void OnTextMessageReceived(TextMessage message, CSteamID sender)
        {
            if (message == null || string.IsNullOrEmpty(message.Content))
            {
                return;
            }

            OnWireMessageReceived(message.Content, sender.m_SteamID);
        }

        private static void ObserveSend(Task<bool> sendTask)
        {
            sendTask.ContinueWith(
                t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        MiddlemanMod.Log.Warning($"Co-op P2P send failed: {t.Exception.GetBaseException().Message}");
                    }
                },
                TaskScheduler.Default);
        }
    }

    private static string SanitizeField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("|", "/")
            .Replace("\"", "'")
            .Replace("\\", "/")
            .Replace("\r", " ")
            .Replace("\n", " ");
    }
#else
    public static bool IsAvailable => false;

    public static void Initialize() { }

    public static void Update() { }

    public static void Dispose() { }

    public static void RequestStateSyncIfClient() { }

    public static void SendPassOffer(Customer customer)
    {
        MiddlemanController.Notify("Middleman", "Co-op actions require SteamNetworkLib in UserLibs\\.");
    }

    public static void SendPassAccepted(Customer customer)
    {
        MiddlemanController.Notify("Middleman", "Co-op actions require SteamNetworkLib in UserLibs\\.");
    }

    public static void SendSummonDealer(Dealer dealer, Property property)
    {
        MiddlemanController.Notify("Middleman", "Co-op actions require SteamNetworkLib in UserLibs\\.");
    }

    public static void SendCancelMeetup(Dealer dealer)
    {
        MiddlemanController.Notify("Middleman", "Co-op actions require SteamNetworkLib in UserLibs\\.");
    }

    public static void SendCheckStatus(Dealer dealer)
    {
        MiddlemanController.Notify("Middleman", "Co-op actions require SteamNetworkLib in UserLibs\\.");
    }

    public static void SendToastToClient(ulong targetSteamId, string title, string subtitle) { }

    public static void BroadcastContractAssigned(string contractGuid, string dealerId) { }

    public static void BroadcastMeetupState(string dealerId, bool active, ulong? targetSteamId = null) { }
#endif

    internal static Customer? FindCustomerByNpcId(string npcId)
    {
        if (string.IsNullOrEmpty(npcId))
        {
            return null;
        }

        return FindCustomerInList(Customer.UnlockedCustomers, npcId)
            ?? FindCustomerInList(Customer.LockedCustomers, npcId);
    }

#if IL2CPP
    private static Customer? FindCustomerInList(Il2CppSystem.Collections.Generic.List<Customer>? list, string npcId)
#else
    private static Customer? FindCustomerInList(List<Customer>? list, string npcId)
#endif
    {
        if (list == null)
        {
            return null;
        }

        for (int i = 0; i < list.Count; i++)
        {
            Customer customer = list[i];
            if (GameCompat.IsAlive(customer) && GameCompat.IsAlive(customer.NPC) && customer.NPC.ID == npcId)
            {
                return customer;
            }
        }

        return null;
    }

    internal static Dealer? FindDealerById(string dealerId)
    {
        if (string.IsNullOrEmpty(dealerId) || Dealer.AllPlayerDealers == null)
        {
            return null;
        }

        for (int i = 0; i < Dealer.AllPlayerDealers.Count; i++)
        {
            Dealer dealer = Dealer.AllPlayerDealers[i];
            if (GameCompat.IsAlive(dealer) && dealer.ID == dealerId)
            {
                return dealer;
            }
        }

        return null;
    }

    internal static Property? FindPropertyByKey(string key)
    {
        if (string.IsNullOrEmpty(key) || Property.OwnedProperties == null)
        {
            return null;
        }

        for (int i = 0; i < Property.OwnedProperties.Count; i++)
        {
            Property prop = Property.OwnedProperties[i];
            if (!GameCompat.IsAlive(prop))
            {
                continue;
            }

            if (GetPropertyKey(prop) == key || prop.PropertyCode == key || prop.PropertyName == key)
            {
                return prop;
            }
        }

        return null;
    }

    internal static Contract? FindContractByGuid(string contractGuid)
    {
        if (string.IsNullOrEmpty(contractGuid) || Contract.Contracts == null)
        {
            return null;
        }

        for (int i = 0; i < Contract.Contracts.Count; i++)
        {
            Contract contract = Contract.Contracts[i];
            if (GameCompat.IsAlive(contract) && string.Equals(contract.GUID.ToString(), contractGuid, StringComparison.OrdinalIgnoreCase))
            {
                return contract;
            }
        }

        return null;
    }

    private static string GetPropertyKey(Property property)
    {
        return !string.IsNullOrEmpty(property.PropertyCode)
            ? property.PropertyCode
            : property.PropertyName;
    }
}
