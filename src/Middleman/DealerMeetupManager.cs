using System;
using System.Collections.Generic;
using UnityEngine;
#if IL2CPP
using Il2CppFishNet;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Property;
using Il2CppScheduleOne.UI.Phone.Messages;
#else
using FishNet;
using ScheduleOne.DevUtilities;
using ScheduleOne.Economy;
using ScheduleOne.GameTime;
using ScheduleOne.Messaging;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Property;
using ScheduleOne.UI.Phone.Messages;
#endif

namespace Middleman;

/// <summary>
/// Manages player-requested dealer meetups at owned properties.
/// </summary>
internal static class DealerMeetupManager
{
    private const float ArrivalDistance = 2.5f;
    private const float UpdateIntervalSeconds = 0.5f;
    private const float RepathIntervalSeconds = 3f;

    private sealed class ActiveMeetup
    {
        public Dealer Dealer { get; }
        public Property TargetProperty { get; }
        public Vector3 Destination { get; }
        public bool HasArrived { get; set; }
        public float NextRepathTime { get; set; }
        public int ArrivalTotalMins { get; set; }

        public ActiveMeetup(Dealer dealer, Property targetProperty, Vector3 destination)
        {
            Dealer = dealer;
            TargetProperty = targetProperty;
            Destination = destination;
        }
    }

    private static readonly Dictionary<string, ActiveMeetup> ActiveMeetups = new();
    private static readonly List<string> KeysBuffer = new();
    private static float _nextUpdateTime;

    public static void ResetState()
    {
        ActiveMeetups.Clear();
        KeysBuffer.Clear();
        _nextUpdateTime = 0f;
    }

    public static bool HasActiveMeetup(Dealer? dealer)
    {
        return GameCompat.IsAlive(dealer)
            && !string.IsNullOrEmpty(dealer!.ID)
            && ActiveMeetups.ContainsKey(dealer.ID);
    }

    /// <summary>
    /// Picks a sensible outdoor/entrance standing spot for the given property.
    /// </summary>
    public static Vector3 GetMeetPoint(Property property)
    {
        if (GameCompat.IsAlive(property.NPCSpawnPoint))
        {
            return property.NPCSpawnPoint.position;
        }

        if (GameCompat.IsAlive(property.SpawnPoint))
        {
            return property.SpawnPoint.position;
        }

        if (property.EmployeeIdlePoints != null && property.EmployeeIdlePoints.Length > 0 && GameCompat.IsAlive(property.EmployeeIdlePoints[0]))
        {
            return property.EmployeeIdlePoints[0].position;
        }

        return property.transform.position;
    }

    /// <summary>
    /// Instructs a recruited dealer to walk to one of the player's owned properties and wait there.
    /// </summary>
    public static void RequestMeetup(Dealer dealer, Property property)
    {
        if (!MiddlemanConfig.AllowDealerMeetups.Value || !GameCompat.IsAlive(dealer) || !GameCompat.IsAlive(property))
        {
            return;
        }

        if (!InstanceFinder.IsServer)
        {
            MiddlemanController.Notify("Middleman", "Dealer meetups are only supported by the host.");
            return;
        }

        if (!dealer.IsRecruited || !dealer.IsConscious || !property.IsOwned)
        {
            return;
        }

        Vector3 destination = GetMeetPoint(property);
        ActiveMeetup meetup = new(dealer, property, destination);
        ActiveMeetups[dealer.ID] = meetup;

        bool busyWithDrop = IsAttendingDeal(dealer);

        if (dealer.MSGConversation != null)
        {
            dealer.MSGConversation.SendMessage(
                new Message($"Meet me at {property.PropertyName}.", Message.ESenderType.Player, _endOfGroup: true),
                notify: false);

            MessageChain reply = new();
            reply.Messages.Add(busyWithDrop
                ? $"I'm finishing a drop right now, boss. I'll head to {property.PropertyName} right after."
                : $"On my way to {property.PropertyName}, boss.");
            dealer.MSGConversation.SendMessageChain(reply, initialDelay: 0.5f, notify: false);
        }

        if (!busyWithDrop)
        {
            BeginWalk(meetup);
        }

        MiddlemanController.Notify("Middleman", $"{dealer.FullName} is heading to {property.PropertyName}.");
    }

    /// <summary>
    /// Releases a dealer from an active property meetup and restores their normal schedule.
    /// </summary>
    public static void CancelMeetup(Dealer dealer, bool sendChat = true)
    {
        if (!GameCompat.IsAlive(dealer) || string.IsNullOrEmpty(dealer.ID))
        {
            return;
        }

        if (!ActiveMeetups.Remove(dealer.ID))
        {
            return;
        }

        RestoreSchedule(dealer);

        if (sendChat && dealer.MSGConversation != null)
        {
            dealer.MSGConversation.SendMessage(
                new Message("You're good to head back.", Message.ESenderType.Player, _endOfGroup: true),
                notify: false);

            MessageChain reply = new();
            reply.Messages.Add("Alright boss, heading back.");
            dealer.MSGConversation.SendMessageChain(reply, initialDelay: 0.5f, notify: false);
        }
    }

    public static void Update()
    {
        if (ActiveMeetups.Count == 0 || Time.time < _nextUpdateTime)
        {
            return;
        }

        _nextUpdateTime = Time.time + UpdateIntervalSeconds;

        KeysBuffer.Clear();
        foreach (string key in ActiveMeetups.Keys)
        {
            KeysBuffer.Add(key);
        }

        for (int i = 0; i < KeysBuffer.Count; i++)
        {
            string id = KeysBuffer[i];
            if (!ActiveMeetups.TryGetValue(id, out ActiveMeetup? meetup))
            {
                continue;
            }

            TickMeetup(id, meetup);
        }
    }

    private static void TickMeetup(string id, ActiveMeetup meetup)
    {
        Dealer dealer = meetup.Dealer;
        if (!GameCompat.IsAlive(dealer) || !dealer.IsConscious || !InstanceFinder.IsServer || !GameCompat.IsAlive(meetup.TargetProperty))
        {
            ActiveMeetups.Remove(id);
            if (GameCompat.IsAlive(dealer))
            {
                RestoreSchedule(dealer);
            }
            return;
        }

        if (IsAttendingDeal(dealer) || IsInDialogue(dealer))
        {
            return;
        }

        SuppressHomeSchedule(dealer);

        if (dealer.isInBuilding)
        {
            dealer.ExitBuilding();
        }

        float distance = Vector3.Distance(dealer.Movement.FootPosition, meetup.Destination);

        if (!meetup.HasArrived)
        {
            if (distance <= ArrivalDistance)
            {
                meetup.HasArrived = true;
                meetup.ArrivalTotalMins = NetworkSingleton<TimeManager>.InstanceExists
                    ? NetworkSingleton<TimeManager>.Instance.GetTotalMinSum()
                    : 0;
                dealer.Movement.Stop();

                if (dealer.MSGConversation != null)
                {
                    MessageChain arrived = new();
                    arrived.Messages.Add($"I'm at {meetup.TargetProperty.PropertyName}, boss.");
                    dealer.MSGConversation.SendMessageChain(arrived, initialDelay: 0f, notify: true);
                }

                MiddlemanController.Notify("Middleman", $"{dealer.FullName} arrived at {meetup.TargetProperty.PropertyName}.");
                return;
            }

            if (!dealer.Movement.IsMoving || Time.time >= meetup.NextRepathTime)
            {
                dealer.Movement.SetDestination(meetup.Destination);
                meetup.NextRepathTime = Time.time + RepathIntervalSeconds;
            }

            return;
        }

        if (GameCompat.IsAlive(Player.Local))
        {
            float distToPlayer = Vector3.Distance(dealer.Movement.FootPosition, Player.Local.transform.position);
            if (distToPlayer <= 6f)
            {
                dealer.Movement.FacePoint(Player.Local.transform.position);
            }
        }

        int waitMinutes = Math.Max(30, MiddlemanConfig.DealerMeetupWaitMinutes.Value);
        if (NetworkSingleton<TimeManager>.InstanceExists)
        {
            int elapsed = NetworkSingleton<TimeManager>.Instance.GetTotalMinSum() - meetup.ArrivalTotalMins;
            if (elapsed >= waitMinutes)
            {
                ActiveMeetups.Remove(id);
                RestoreSchedule(dealer);
            }
        }
    }

    private static void BeginWalk(ActiveMeetup meetup)
    {
        Dealer dealer = meetup.Dealer;
        SuppressHomeSchedule(dealer);

        if (dealer.isInBuilding)
        {
            dealer.ExitBuilding();
        }

        if (!IsInDialogue(dealer))
        {
            dealer.Movement.SetDestination(meetup.Destination);
            meetup.NextRepathTime = Time.time + RepathIntervalSeconds;
        }
    }

    private static void SuppressHomeSchedule(Dealer dealer)
    {
        if (GameCompat.IsAlive(dealer.HomeEvent))
        {
            dealer.HomeEvent.gameObject.SetActive(false);
        }

        if (GameCompat.IsAlive(dealer.Behaviour)
            && GameCompat.IsAlive(dealer.Behaviour.ScheduleManager)
            && dealer.Behaviour.ScheduleManager.ScheduleEnabled)
        {
            dealer.Behaviour.ScheduleManager.DisableSchedule();
        }
    }

    private static void RestoreSchedule(Dealer dealer)
    {
        if (GameCompat.IsAlive(dealer.HomeEvent))
        {
            dealer.HomeEvent.gameObject.SetActive(true);
        }

        if (GameCompat.IsAlive(dealer.Behaviour)
            && GameCompat.IsAlive(dealer.Behaviour.ScheduleManager)
            && !dealer.Behaviour.ScheduleManager.ScheduleEnabled)
        {
            dealer.Behaviour.ScheduleManager.EnableSchedule();
        }
    }

    private static bool IsAttendingDeal(Dealer dealer)
    {
        if (!GameCompat.IsAlive(dealer.Behaviour))
        {
            return false;
        }

        DealerAttendDealBehaviour? attend = dealer.Behaviour.GetBehaviour<DealerAttendDealBehaviour>();
        return GameCompat.IsAlive(attend) && attend!.Active;
    }

    private static bool IsInDialogue(Dealer dealer)
    {
        if (GameCompat.IsAlive(dealer.DialogueHandler) && dealer.DialogueHandler.IsDialogueInProgress)
        {
            return true;
        }

        return GameCompat.IsAlive(dealer.Behaviour)
            && GameCompat.IsAlive(dealer.Behaviour.GenericDialogueBehaviour)
            && dealer.Behaviour.GenericDialogueBehaviour.Active;
    }
}
