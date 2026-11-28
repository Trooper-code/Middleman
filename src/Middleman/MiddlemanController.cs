using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
#if IL2CPP
using Il2CppFishNet;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.UI.Phone.Messages;
using GameGuidManager = Il2Cpp.GUIDManager;
using GameGuid = Il2CppSystem.Guid;
#else
using FishNet;
using ScheduleOne.DevUtilities;
using ScheduleOne.Economy;
using ScheduleOne.Messaging;
using ScheduleOne.Quests;
using ScheduleOne.UI;
using ScheduleOne.UI.Phone.Messages;
using GameGuidManager = GUIDManager;
using GameGuid = System.Guid;
#endif

namespace Middleman;

/// <summary>
/// Handles handing off pending offers and active player-owned deals to an auto-selected dealer.
/// </summary>
internal static class MiddlemanController
{
    private static readonly string[] CustomerConfirmationLines =
    {
        "Sounds good, I'll look out for them.",
        "Works for me, see them there.",
        "Cool, appreciate it.",
        "Alright, I'll meet them there.",
    };

    private static readonly MethodInfo? DealerAddContractMethod =
        AccessTools.Method(typeof(Dealer), "AddContract", new[] { typeof(Contract) });

    private static readonly MethodInfo? DealerGetDealWindowMethod =
        AccessTools.Method(typeof(Dealer), "GetDealWindow", Type.EmptyTypes);

    /// <summary>
    /// Hands off a customer's currently offered (pending) contract to an eligible dealer.
    /// Non-host clients forward the request to the host.
    /// </summary>
    public static void TryPassOfferedContract(Customer customer)
    {
        if (!GameCompat.IsAlive(customer))
        {
            return;
        }

        if (!InstanceFinder.IsServer)
        {
            if (customer.OfferedContractInfo == null)
            {
                MiddlemanMod.Log.Warning($"Cannot pass deal for {customer.NPC?.FullName ?? "customer"}: no active offer.");
                return;
            }

            if (GameCompat.IsAlive(customer.CurrentContract))
            {
                Notify("Middleman", $"{customer.NPC?.FullName ?? "Customer"} already has an active deal.");
                return;
            }

            MiddlemanNetwork.SendPassOffer(customer);
            return;
        }

        TryPassOfferedContractOnHost(customer, requesterSteamId: 0);
    }

    internal static void TryPassOfferedContractOnHost(Customer customer, ulong requesterSteamId)
    {
        if (!InstanceFinder.IsServer || !GameCompat.IsAlive(customer))
        {
            return;
        }

        ContractInfo? info = customer.OfferedContractInfo;
        if (info == null)
        {
            MiddlemanMod.Log.Warning($"Cannot pass deal for {customer.NPC?.FullName ?? "customer"}: no active offer.");
            if (requesterSteamId != 0)
            {
                NotifyRequester(requesterSteamId, "Middleman", $"{customer.NPC?.FullName ?? "Customer"} no longer has a pending offer.");
            }
            return;
        }

        if (GameCompat.IsAlive(customer.CurrentContract))
        {
            NotifyRequester(requesterSteamId, "Middleman", $"{customer.NPC?.FullName ?? "Customer"} already has an active deal.");
            return;
        }

        DeliveryLocation? location = ResolveDeliveryLocation(info);
        if (!DealerSelector.TrySelectDealer(customer, info.Products, location, out Dealer? dealer, out string reason) || dealer == null)
        {
            NotifyRequester(requesterSteamId, "Middleman", reason);
            return;
        }

        EDealWindow window = GetDealerWindow(dealer);
        Contract? contract = customer.ContractAccepted(window, trackContract: false, dealer);
        if (!GameCompat.IsAlive(contract))
        {
            MiddlemanMod.Log.Warning($"Customer.ContractAccepted returned null for {customer.NPC?.FullName ?? "customer"}.");
            NotifyRequester(requesterSteamId, "Middleman", "Could not hand off this deal right now.");
            return;
        }

        if (!AssignContractToDealer(dealer, contract!))
        {
            NotifyRequester(requesterSteamId, "Middleman", "Could not assign contract to dealer.");
            return;
        }

        dealer.HasChanged = true;
        contract!.HasChanged = true;

        MSGConversation? convo = customer.NPC?.MSGConversation;
        if (convo != null)
        {
            convo.ClearResponses(network: true);
        }

        string customerName = customer.NPC?.FullName ?? "Customer";
        SendHandoffMessages(convo, dealer, customerName);
        NotifyRequester(requesterSteamId, "Middleman", $"Passed {customerName}'s deal to {dealer.FullName}.");
    }

    /// <summary>
    /// Transfers an already-accepted, player-owned deal to an eligible dealer.
    /// Non-host clients forward the request to the host.
    /// </summary>
    public static void TryPassAcceptedContract(Customer customer)
    {
        if (!MiddlemanConfig.AllowPassingAcceptedDeals.Value || !GameCompat.IsAlive(customer))
        {
            return;
        }

        if (!InstanceFinder.IsServer)
        {
            Contract? clientContract = customer.CurrentContract;
            if (!GameCompat.IsAlive(clientContract) || clientContract!.State != EQuestState.Active)
            {
                Notify("Middleman", "This customer does not have an active deal.");
                return;
            }

            if (GameCompat.IsAlive(clientContract.Dealer))
            {
                Notify("Middleman", $"This deal is already assigned to {clientContract.Dealer.FullName}.");
                return;
            }

            MiddlemanNetwork.SendPassAccepted(customer);
            return;
        }

        TryPassAcceptedContractOnHost(customer, requesterSteamId: 0);
    }

    internal static void TryPassAcceptedContractOnHost(Customer customer, ulong requesterSteamId)
    {
        if (!InstanceFinder.IsServer || !GameCompat.IsAlive(customer))
        {
            return;
        }

        if (!MiddlemanConfig.AllowPassingAcceptedDeals.Value)
        {
            NotifyRequester(requesterSteamId, "Middleman", "The host has disabled passing already-accepted deals.");
            return;
        }

        Contract? contract = customer.CurrentContract;
        if (!GameCompat.IsAlive(contract) || contract!.State != EQuestState.Active)
        {
            NotifyRequester(requesterSteamId, "Middleman", "This customer does not have an active deal.");
            return;
        }

        if (GameCompat.IsAlive(contract.Dealer))
        {
            NotifyRequester(requesterSteamId, "Middleman", $"This deal is already assigned to {contract.Dealer.FullName}.");
            return;
        }

        if (!DealerSelector.TrySelectDealer(customer, contract.ProductList, contract.DeliveryLocation, out Dealer? dealer, out string reason) || dealer == null)
        {
            NotifyRequester(requesterSteamId, "Middleman", reason);
            return;
        }

        contract.SetIsTracked(false);
        contract.SetDealer(dealer);
        HideContractPoIs(contract);

        if (!AssignContractToDealer(dealer, contract))
        {
            NotifyRequester(requesterSteamId, "Middleman", "Could not assign contract to dealer.");
            return;
        }

        dealer.HasChanged = true;
        contract.HasChanged = true;

        MiddlemanNetwork.BroadcastContractAssigned(contract.GUID.ToString(), dealer.ID);

        MSGConversation? convo = customer.NPC?.MSGConversation;
        string customerName = customer.NPC?.FullName ?? "Customer";
        SendHandoffMessages(convo, dealer, customerName);
        NotifyRequester(requesterSteamId, "Middleman", $"Handed {customerName}'s active deal to {dealer.FullName}.");
    }

    /// <summary>
    /// Applies a host-assigned dealer transfer on non-host clients so the contract clears
    /// from the client's Journal, HUD, and map markers.
    /// </summary>
    internal static void ApplyRemoteContractAssigned(string contractGuid, string dealerId)
    {
        if (InstanceFinder.IsServer)
        {
            return;
        }

        Contract? contract = MiddlemanNetwork.FindContractByGuid(contractGuid);
        Dealer? dealer = MiddlemanNetwork.FindDealerById(dealerId);
        if (!GameCompat.IsAlive(contract) || !GameCompat.IsAlive(dealer))
        {
            return;
        }

        contract!.SetIsTracked(false);
        contract.SetDealer(dealer!);
        HideContractPoIs(contract);
    }

    private static void SendHandoffMessages(MSGConversation? customerConvo, Dealer dealer, string customerName)
    {
        if (customerConvo != null)
        {
            customerConvo.SendMessage(
                new Message($"I'll have {dealer.FullName} take care of you.", Message.ESenderType.Player, _endOfGroup: true),
                notify: false);

            MessageChain reply = new MessageChain();
            int index = UnityEngine.Random.Range(0, CustomerConfirmationLines.Length);
            reply.Messages.Add(CustomerConfirmationLines[index]);
            customerConvo.SendMessageChain(reply, initialDelay: 0.5f, notify: false);
        }

        if (dealer.MSGConversation != null)
        {
            dealer.MSGConversation.SendMessage(
                new Message($"Got it boss, I'll take care of {customerName}.", Message.ESenderType.Other, _endOfGroup: true),
                notify: false);
        }
    }

    private static DeliveryLocation? ResolveDeliveryLocation(ContractInfo info)
    {
        if (GameCompat.IsAlive(info.DeliveryLocation))
        {
            return info.DeliveryLocation;
        }

        if (!string.IsNullOrEmpty(info.DeliveryLocationGUID) && GameGuidManager.IsGUIDValid(info.DeliveryLocationGUID))
        {
            return GameGuidManager.GetObject<DeliveryLocation>(new GameGuid(info.DeliveryLocationGUID));
        }

        return null;
    }

    private static EDealWindow GetDealerWindow(Dealer dealer)
    {
        if (DealerGetDealWindowMethod != null)
        {
            object? result = DealerGetDealWindowMethod.Invoke(dealer, null);
            if (result is EDealWindow window)
            {
                return window;
            }
        }

        return EDealWindow.Morning;
    }

    private static bool AssignContractToDealer(Dealer dealer, Contract contract)
    {
        if (DealerAddContractMethod == null)
        {
            MiddlemanMod.Log.Error("Failed to reflect Dealer.AddContract(Contract).");
            return false;
        }

        DealerAddContractMethod.Invoke(dealer, new object[] { contract });
        return true;
    }

    internal static void HideContractPoIs(Contract contract)
    {
        if (contract.Entries == null)
        {
            return;
        }

        for (int i = 0; i < contract.Entries.Count; i++)
        {
            QuestEntry entry = contract.Entries[i];
            if (GameCompat.IsAlive(entry) && GameCompat.IsAlive(entry.PoI))
            {
                entry.PoI.gameObject.SetActive(false);
            }
        }
    }

    internal static void NotifyRequester(ulong requesterSteamId, string title, string subtitle)
    {
        if (requesterSteamId == 0)
        {
            Notify(title, subtitle);
            return;
        }

        MiddlemanMod.Log.Msg($"{title} (for {requesterSteamId}): {subtitle}");
        MiddlemanNetwork.SendToastToClient(requesterSteamId, title, subtitle);
    }

    public static void Notify(string title, string subtitle)
    {
        MiddlemanMod.Log.Msg($"{title}: {subtitle}");

        if (!MiddlemanConfig.ShowNotifications.Value)
        {
            return;
        }

        if (!Singleton<NotificationsManager>.InstanceExists)
        {
            return;
        }

        Sprite? icon = PlayerSingleton<MessagesApp>.InstanceExists
            ? PlayerSingleton<MessagesApp>.Instance.AppIcon
            : null;

        Singleton<NotificationsManager>.Instance.SendNotification(title, subtitle, icon);
    }
}
