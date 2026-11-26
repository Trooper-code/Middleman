using System;
using System.Collections.Generic;
using SteamNetworkLib;
using SteamNetworkLib.Core;
using SteamNetworkLib.Models;
#if IL2CPP
using Il2CppFishNet;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Networking;
using Il2CppScheduleOne.Property;
using Il2CppScheduleOne.Quests;
using Il2CppSteamworks;
#else
using FishNet;
using ScheduleOne.DevUtilities;
using ScheduleOne.Economy;
using ScheduleOne.Networking;
using ScheduleOne.Property;
using ScheduleOne.Quests;
using Steamworks;
#endif

namespace Middleman;

/// <summary>
/// Handles client-to-host requests and host-to-client synchronization over SteamNetworkLib.
/// Single-player sessions never require a Steam lobby and fall back cleanly to local execution.
/// </summary>
internal static class MiddlemanNetwork
{
    private const string Prefix = "MM2|";

    private static SteamNetworkClient? _client;
    private static bool _initialized;

    public static void Initialize()
    {
        TryInitializeClient();
    }

    public static void Update()
    {
        if (!TryInitializeClient() || _client == null)
        {
            return;
        }

        _client.ProcessIncomingMessages();
    }

    public static void Dispose()
    {
        _initialized = false;
        if (_client != null)
        {
            _client.Dispose();
            _client = null;
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
        if (targetSteamId == 0 || !InstanceFinder.IsServer || !TryInitializeClient() || _client == null)
        {
            return;
        }

        string wire = $"{Prefix}TOAST|{SanitizeField(title)}|{SanitizeField(subtitle)}";
        _ = _client.SendTextMessageAsync(new CSteamID(targetSteamId), wire);
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
            if (TryInitializeClient() && _client != null)
            {
                _ = _client.SendTextMessageAsync(new CSteamID(targetSteamId.Value), wire);
            }
            return;
        }

        BroadcastToClients(wire);
    }

    private static bool TryInitializeClient()
    {
        if (_initialized)
        {
            return true;
        }

        _client ??= new SteamNetworkClient(new NetworkRules
        {
            EnableRelay = true,
            AcceptOnlyFriends = false
        });

        if (!_client.TryInitialize(out _))
        {
            return false;
        }

        _client.RegisterMessageHandler<TextMessage>(OnTextMessageReceived);
        _initialized = true;
        MiddlemanMod.Log.Msg("SteamNetworkLib client initialized for Middleman multiplayer.");
        return true;
    }

    private static bool IsMultiplayerSession()
    {
        if (_client != null && _client.IsInLobby)
        {
            return true;
        }

        return Singleton<Lobby>.InstanceExists && Singleton<Lobby>.Instance.IsInLobby;
    }

    private static bool TryGetHostSteamId(out ulong hostId)
    {
        hostId = 0;

        if (_client != null && _client.HostPlayerId64 != 0)
        {
            hostId = _client.HostPlayerId64;
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
        if (!TryInitializeClient() || _client == null || !TryGetHostSteamId(out ulong hostId))
        {
            if (notifyOnFailure)
            {
                MiddlemanController.Notify("Middleman", "Could not reach the host right now. Make sure you're in a Steam co-op lobby.");
            }
            return false;
        }

        _ = _client.SendTextMessageAsync(new CSteamID(hostId), wire);
        return true;
    }

    private static void BroadcastToClients(string wire)
    {
        if (!TryInitializeClient() || _client == null || !IsMultiplayerSession())
        {
            return;
        }

        if (_client.IsInLobby)
        {
            _client.BroadcastTextMessage(wire);
            return;
        }

        if (!Singleton<Lobby>.InstanceExists || !Singleton<Lobby>.Instance.IsInLobby)
        {
            return;
        }

        ulong localId = _client.LocalPlayerId64;
        var members = Singleton<Lobby>.Instance.GetLobbyMemberIDs();
        if (members == null)
        {
            return;
        }

        for (int i = 0; i < members.Count; i++)
        {
            if (ulong.TryParse(members[i], out ulong memberId) && memberId != 0 && memberId != localId)
            {
                _ = _client.SendTextMessageAsync(new CSteamID(memberId), wire);
            }
        }
    }

    private static void OnTextMessageReceived(TextMessage message, CSteamID sender)
    {
        if (message == null || string.IsNullOrEmpty(message.Content) || !message.Content.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return;
        }

        string[] parts = message.Content.Substring(Prefix.Length).Split('|');
        if (parts.Length == 0 || string.IsNullOrEmpty(parts[0]))
        {
            return;
        }

        string msgType = parts[0];

        if (InstanceFinder.IsServer)
        {
            HandleServerMessage(msgType, parts, sender);
        }
        else
        {
            HandleClientMessage(msgType, parts, sender);
        }
    }

    private static void HandleServerMessage(string msgType, string[] parts, CSteamID sender)
    {
        if (!IsAuthorizedLobbyMember(sender))
        {
            MiddlemanMod.Log.Warning($"Ignored '{msgType}' from non-lobby SteamID {sender.m_SteamID}.");
            return;
        }

        ulong requesterId = sender.m_SteamID;

        switch (msgType)
        {
            case "PASS_OFFER":
                if (parts.Length >= 2)
                {
                    Customer? customer = FindCustomerByNpcId(parts[1]);
                    if (GameCompat.IsAlive(customer))
                    {
                        MiddlemanController.TryPassOfferedContractOnHost(customer!, requesterId);
                    }
                    else
                    {
                        SendToastToClient(requesterId, "Middleman", "Customer was not found on the host.");
                    }
                }
                break;

            case "PASS_ACCEPTED":
                if (parts.Length >= 2)
                {
                    Customer? customer = FindCustomerByNpcId(parts[1]);
                    if (GameCompat.IsAlive(customer))
                    {
                        MiddlemanController.TryPassAcceptedContractOnHost(customer!, requesterId);
                    }
                    else
                    {
                        SendToastToClient(requesterId, "Middleman", "Customer was not found on the host.");
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
                        DealerMeetupManager.RequestMeetupOnHost(dealer!, property!, requesterId);
                    }
                    else
                    {
                        SendToastToClient(requesterId, "Middleman", "Dealer or property was not found on the host.");
                    }
                }
                break;

            case "HEAD_BACK":
                if (parts.Length >= 2)
                {
                    Dealer? dealer = FindDealerById(parts[1]);
                    if (GameCompat.IsAlive(dealer))
                    {
                        DealerMeetupManager.CancelMeetupOnHost(dealer!, sendChat: true);
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
                }
                break;

            case "SYNC_STATE":
                DealerMeetupManager.SendActiveMeetupsToClient(requesterId);
                break;
        }
    }

    private static void HandleClientMessage(string msgType, string[] parts, CSteamID sender)
    {
        if (!TryGetHostSteamId(out ulong hostId) || sender.m_SteamID != hostId)
        {
            MiddlemanMod.Log.Warning($"Ignored '{msgType}' from non-host SteamID {sender.m_SteamID}.");
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

    private static bool IsAuthorizedLobbyMember(CSteamID sender)
    {
        if (_client == null || sender.m_SteamID == 0 || sender.m_SteamID == _client.LocalPlayerId64)
        {
            return false;
        }

        if (_client.IsInLobby)
        {
            return _client.TryGetMember(sender.m_SteamID, out _);
        }

        if (Singleton<Lobby>.InstanceExists && Singleton<Lobby>.Instance.IsInLobby)
        {
            var members = Singleton<Lobby>.Instance.GetLobbyMemberIDs();
            return members != null && members.Contains(sender.m_SteamID.ToString());
        }

        return false;
    }

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
}
