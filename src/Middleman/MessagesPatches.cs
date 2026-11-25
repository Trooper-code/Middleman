using System.Collections.Generic;
using HarmonyLib;
#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.Persistence.Datas;
using Il2CppScheduleOne.Quests;
using GameResponseList = Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.Messaging.Response>;
#else
using ScheduleOne.Economy;
using ScheduleOne.Messaging;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Quests;
using GameResponseList = System.Collections.Generic.List<ScheduleOne.Messaging.Response>;
#endif

namespace Middleman;

/// <summary>
/// Harmony patches on MSGConversation to surface [Pass to Dealer] on deal offers
/// and [Pass deal to dealer] in the Messages Compose menu for active deals.
/// </summary>
[HarmonyPatch]
internal static class MessagesPatches
{
    internal const string PassResponseLabel = "MIDDLEMAN_PASS";
    private const string PassResponseText = "[Pass to Dealer]";
    private const string PassAcceptedText = "[Pass deal to dealer]";

    private static readonly HashSet<string> RegisteredConversations = new();

    public static void ResetState()
    {
        RegisteredConversations.Clear();
    }

    /// <summary>
    /// Injects [Pass to Dealer] right after ACCEPT_CONTRACT when local responses are built.
    /// Running on !network avoids sending custom response objects over FishNet RPCs.
    /// </summary>
    [HarmonyPatch(typeof(MSGConversation), nameof(MSGConversation.ShowResponses))]
    [HarmonyPrefix]
    private static void ShowResponses_Prefix(
        MSGConversation __instance,
        GameResponseList _responses,
        bool network)
    {
        if (network || __instance == null || _responses == null || _responses.Count == 0)
        {
            return;
        }

        int acceptIndex = -1;
        for (int i = 0; i < _responses.Count; i++)
        {
            Response existing = _responses[i];
            if (existing == null)
            {
                continue;
            }

            if (existing.label == PassResponseLabel)
            {
                return;
            }

            if (existing.label == "ACCEPT_CONTRACT" && acceptIndex == -1)
            {
                acceptIndex = i;
            }
        }

        if (acceptIndex == -1)
        {
            return;
        }

        Customer? customer = FindCustomerForConversation(__instance);
        if (!GameCompat.IsAlive(customer) || customer!.OfferedContractInfo == null)
        {
            return;
        }

        Response passResponse = new Response(
            PassResponseText,
            PassResponseLabel,
            GameCompat.ToGameAction(() => MiddlemanController.TryPassOfferedContract(customer)),
            _disableDefaultResponseBehaviour: true);

        _responses.Insert(acceptIndex + 1, passResponse);
    }

    /// <summary>
    /// Strips our injected response from saved conversation data so vanilla saves stay clean.
    /// </summary>
    [HarmonyPatch(typeof(MSGConversation), nameof(MSGConversation.GetSaveData))]
    [HarmonyPostfix]
    private static void GetSaveData_Postfix(MSGConversationData? __result)
    {
        if (__result == null || __result.ActiveResponses == null || __result.ActiveResponses.Length == 0)
        {
            return;
        }

        List<TextResponseData> filtered = new(__result.ActiveResponses.Length);
        bool removed = false;
        for (int i = 0; i < __result.ActiveResponses.Length; i++)
        {
            TextResponseData entry = __result.ActiveResponses[i];
            if (entry != null && entry.Label == PassResponseLabel)
            {
                removed = true;
                continue;
            }

            if (entry != null)
            {
                filtered.Add(entry);
            }
        }

        if (removed)
        {
            __result.ActiveResponses = filtered.ToArray();
        }
    }

    /// <summary>
    /// Registers the [Pass deal to dealer] Compose option on each customer's conversation UI.
    /// </summary>
    [HarmonyPatch(typeof(MSGConversation), nameof(MSGConversation.EnsureUIExists))]
    [HarmonyPostfix]
    private static void EnsureUIExists_Postfix(MSGConversation __instance)
    {
        if (__instance == null || string.IsNullOrEmpty(__instance.ConversationId))
        {
            return;
        }

        if (RegisteredConversations.Contains(__instance.ConversationId))
        {
            return;
        }

        Customer? customer = FindCustomerForConversation(__instance);
        if (!GameCompat.IsAlive(customer))
        {
            return;
        }

        RegisteredConversations.Add(__instance.ConversationId);

        SendableMessage sendable = __instance.CreateSendableMessage(PassAcceptedText);
        sendable.disableDefaultSendBehaviour = true;
        sendable.ShouldShowCheck = GameCompat.ToBoolCheck(_ =>
            MiddlemanConfig.AllowPassingAcceptedDeals.Value
            && GameCompat.IsAlive(customer)
            && GameCompat.IsAlive(customer!.CurrentContract)
            && !GameCompat.IsAlive(customer.CurrentContract.Dealer)
            && customer.CurrentContract.State == EQuestState.Active);
        sendable.onSelected = GameCompat.ToGameAction(() => MiddlemanController.TryPassAcceptedContract(customer!));
    }

    private static Customer? FindCustomerForConversation(MSGConversation conversation)
    {
        Customer? match = FindInList(Customer.UnlockedCustomers, conversation);
        if (GameCompat.IsAlive(match))
        {
            return match;
        }

        return FindInList(Customer.LockedCustomers, conversation);
    }

#if IL2CPP
    private static Customer? FindInList(
        Il2CppSystem.Collections.Generic.List<Customer>? list,
        MSGConversation conversation)
#else
    private static Customer? FindInList(
        List<Customer>? list,
        MSGConversation conversation)
#endif
    {
        if (list == null)
        {
            return null;
        }

        for (int i = 0; i < list.Count; i++)
        {
            Customer customer = list[i];
            if (!GameCompat.IsAlive(customer) || !GameCompat.IsAlive(customer.NPC))
            {
                continue;
            }

            MSGConversation? candidate = customer.NPC.MSGConversation;
            if (candidate != null && candidate.ConversationId == conversation.ConversationId)
            {
                return customer;
            }
        }

        return null;
    }
}
