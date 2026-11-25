using System.Collections.Generic;
using HarmonyLib;
#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.Persistence.Datas;
using Il2CppScheduleOne.Property;
using Il2CppScheduleOne.Quests;
using GameResponseList = Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.Messaging.Response>;
#else
using ScheduleOne.Economy;
using ScheduleOne.Messaging;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Property;
using ScheduleOne.Quests;
using GameResponseList = System.Collections.Generic.List<ScheduleOne.Messaging.Response>;
#endif

namespace Middleman;

/// <summary>
/// Harmony patches on MSGConversation and Dealer to surface:
/// - [Pass to Dealer] on customer deal offers
/// - [Pass deal to dealer] in customer Compose menus for active deals
/// - [Meet me at a property] / [Head back] in recruited dealer Compose menus
/// </summary>
[HarmonyPatch]
internal static class MessagesPatches
{
    internal const string PassResponseLabel = "MIDDLEMAN_PASS";
    private const string MeetupResponsePrefix = "MIDDLEMAN_MEETUP_";
    private const string PassResponseText = "[Pass to Dealer]";
    private const string PassAcceptedText = "[Pass deal to dealer]";
    private const string MeetAtPropertyText = "[Meet me at a property]";
    private const string HeadBackText = "[Head back]";

    private static readonly HashSet<string> RegisteredConversations = new();
    private static readonly HashSet<string> RegisteredDealerConversations = new();

    public static void ResetState()
    {
        RegisteredConversations.Clear();
        RegisteredDealerConversations.Clear();
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
        if (!GameCompat.IsAlive(customer))
        {
            return;
        }

        Response passResponse = new Response(
            PassResponseText,
            PassResponseLabel,
            GameCompat.ToGameAction(() => MiddlemanController.TryPassOfferedContract(customer!)),
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
            if (entry != null
                && !string.IsNullOrEmpty(entry.Label)
                && (entry.Label == PassResponseLabel || entry.Label.StartsWith(MeetupResponsePrefix)))
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
    /// Registers Compose options on customer and recruited dealer conversations when UI is created.
    /// </summary>
    [HarmonyPatch(typeof(MSGConversation), nameof(MSGConversation.EnsureUIExists))]
    [HarmonyPostfix]
    private static void EnsureUIExists_Postfix(MSGConversation __instance)
    {
        EnsureAcceptedDealSendable(__instance);
        EnsureDealerMeetupSendables(__instance);
    }

    /// <summary>
    /// Ensures existing conversations opened from a loaded save have Compose options
    /// and (if a customer offer is pending) [Pass to Dealer].
    /// </summary>
    [HarmonyPatch(typeof(MSGConversation), nameof(MSGConversation.SetOpen))]
    [HarmonyPrefix]
    private static void SetOpen_Prefix(MSGConversation __instance, bool open)
    {
        if (!open || __instance == null)
        {
            return;
        }

        EnsureAcceptedDealSendable(__instance);
        EnsureDealerMeetupSendables(__instance);

        GameResponseList? responses = __instance.currentResponses;
        if (responses == null || responses.Count == 0)
        {
            return;
        }

        bool hasAccept = false;
        bool hasPass = false;
        for (int i = 0; i < responses.Count; i++)
        {
            Response r = responses[i];
            if (r == null)
            {
                continue;
            }

            if (r.label == "ACCEPT_CONTRACT")
            {
                hasAccept = true;
            }
            else if (r.label == PassResponseLabel)
            {
                hasPass = true;
            }
        }

        if (!hasAccept || hasPass)
        {
            return;
        }

        Customer? customer = FindCustomerForConversation(__instance);
        if (!GameCompat.IsAlive(customer) || customer!.OfferedContractInfo == null)
        {
            return;
        }

        GameResponseList copy = new GameResponseList();
        for (int i = 0; i < responses.Count; i++)
        {
            copy.Add(responses[i]);
        }

        __instance.ShowResponses(copy, 0f, network: false);
    }

    private static void EnsureAcceptedDealSendable(MSGConversation conversation)
    {
        if (conversation == null || string.IsNullOrEmpty(conversation.ConversationId))
        {
            return;
        }

        if (RegisteredConversations.Contains(conversation.ConversationId))
        {
            return;
        }

        Customer? customer = FindCustomerForConversation(conversation);
        if (!GameCompat.IsAlive(customer))
        {
            return;
        }

        RegisteredConversations.Add(conversation.ConversationId);

        SendableMessage sendable = conversation.CreateSendableMessage(PassAcceptedText);
        sendable.disableDefaultSendBehaviour = true;
        sendable.ShouldShowCheck = GameCompat.ToBoolCheck(_ =>
            MiddlemanConfig.AllowPassingAcceptedDeals.Value
            && GameCompat.IsAlive(customer)
            && GameCompat.IsAlive(customer!.CurrentContract)
            && !GameCompat.IsAlive(customer.CurrentContract.Dealer)
            && customer.CurrentContract.State == EQuestState.Active);
        sendable.onSelected = GameCompat.ToGameAction(() => MiddlemanController.TryPassAcceptedContract(customer!));
    }

    private static void EnsureDealerMeetupSendables(MSGConversation conversation)
    {
        if (conversation == null || string.IsNullOrEmpty(conversation.ConversationId))
        {
            return;
        }

        if (RegisteredDealerConversations.Contains(conversation.ConversationId))
        {
            return;
        }

        Dealer? dealer = FindDealerForConversation(conversation);
        if (!GameCompat.IsAlive(dealer))
        {
            return;
        }

        RegisteredDealerConversations.Add(conversation.ConversationId);

        SendableMessage meetSendable = conversation.CreateSendableMessage(MeetAtPropertyText);
        meetSendable.disableDefaultSendBehaviour = true;
        meetSendable.ShouldShowCheck = GameCompat.ToBoolCheck(_ =>
            MiddlemanConfig.AllowDealerMeetups.Value
            && GameCompat.IsAlive(dealer)
            && dealer!.IsRecruited
            && dealer.IsConscious
            && Property.OwnedProperties != null
            && Property.OwnedProperties.Count > 0
            && !DealerMeetupManager.HasActiveMeetup(dealer));
        meetSendable.onSelected = GameCompat.ToGameAction(() => ShowPropertyPicker(dealer!, conversation));

        SendableMessage headBackSendable = conversation.CreateSendableMessage(HeadBackText);
        headBackSendable.disableDefaultSendBehaviour = true;
        headBackSendable.ShouldShowCheck = GameCompat.ToBoolCheck(_ =>
            MiddlemanConfig.AllowDealerMeetups.Value
            && GameCompat.IsAlive(dealer)
            && dealer!.IsRecruited
            && DealerMeetupManager.HasActiveMeetup(dealer));
        headBackSendable.onSelected = GameCompat.ToGameAction(() => DealerMeetupManager.CancelMeetup(dealer!, sendChat: true));
    }

    private static void ShowPropertyPicker(Dealer dealer, MSGConversation conversation)
    {
        if (!GameCompat.IsAlive(dealer) || conversation == null)
        {
            return;
        }

        var owned = Property.OwnedProperties;
        if (owned == null || owned.Count == 0)
        {
            MiddlemanController.Notify("Middleman", "You don't own any properties yet.");
            return;
        }

        GameResponseList options = new GameResponseList();
        for (int i = 0; i < owned.Count; i++)
        {
            Property prop = owned[i];
            if (!GameCompat.IsAlive(prop) || !prop.IsOwned)
            {
                continue;
            }

            string code = string.IsNullOrEmpty(prop.PropertyCode) ? i.ToString() : prop.PropertyCode;
            options.Add(new Response(
                prop.PropertyName,
                MeetupResponsePrefix + code,
                GameCompat.ToGameAction(() =>
                {
                    conversation.ClearResponses(network: false);
                    DealerMeetupManager.RequestMeetup(dealer, prop);
                }),
                _disableDefaultResponseBehaviour: true));
        }

        options.Add(new Response(
            "Nevermind",
            MeetupResponsePrefix + "CANCEL",
            GameCompat.ToGameAction(() => conversation.ClearResponses(network: false)),
            _disableDefaultResponseBehaviour: true));

        conversation.ShowResponses(options, 0f, network: false);
    }

    /// <summary>
    /// Keeps Dealer.OnTick from re-enabling HomeEvent while a dealer is meeting the player at a property.
    /// </summary>
    [HarmonyPatch(typeof(Dealer), "OnTick")]
    [HarmonyPostfix]
    private static void DealerOnTick_Postfix(Dealer __instance)
    {
        if (DealerMeetupManager.HasActiveMeetup(__instance) && GameCompat.IsAlive(__instance.HomeEvent))
        {
            __instance.HomeEvent.gameObject.SetActive(false);
        }
    }

    private static Dealer? FindDealerForConversation(MSGConversation conversation)
    {
        var allDealers = Dealer.AllPlayerDealers;
        if (allDealers == null)
        {
            return null;
        }

        for (int i = 0; i < allDealers.Count; i++)
        {
            Dealer dealer = allDealers[i];
            if (!GameCompat.IsAlive(dealer) || dealer.DealerData == null)
            {
                continue;
            }

            if (dealer.DealerData.DealerType != EDealerType.PlayerDealer)
            {
                continue;
            }

            if (dealer.MSGConversation != null && dealer.MSGConversation.ConversationId == conversation.ConversationId)
            {
                return dealer;
            }
        }

        return null;
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
