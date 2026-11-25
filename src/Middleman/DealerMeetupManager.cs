using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
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
    private const int MaxPathingFailuresBeforeWarp = 3;
    private const string SpeedControlId = "middleman_meetup";

    private static readonly FieldInfo? DealerCurrentContractField =
        AccessTools.Field(typeof(Dealer), "currentContract");

    private static readonly PropertyInfo? DealerCurrentContractProp =
        AccessTools.Property(typeof(Dealer), "currentContract");

    private sealed class ActiveMeetup
    {
        public Dealer Dealer { get; }
        public Property TargetProperty { get; }
        public Vector3 Destination { get; }
        public bool HasArrived { get; set; }
        public bool HasAttemptedMove { get; set; }
        public int PathingFailures { get; set; }
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

        PauseActiveDealForMeetup(dealer);

        if (dealer.MSGConversation != null)
        {
            dealer.MSGConversation.SendMessage(
                new Message($"Meet me at {property.PropertyName}.", Message.ESenderType.Player, _endOfGroup: true),
                notify: false);

            MessageChain reply = new();
            reply.Messages.Add($"On my way to {property.PropertyName}, boss.");
            dealer.MSGConversation.SendMessageChain(reply, initialDelay: 0.5f, notify: false);
        }

        BeginWalk(meetup);
        MiddlemanMod.Log.Msg($"Meetup started: {dealer.FullName} -> {property.PropertyName} ({destination})");
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

        RestoreNormalState(dealer);
        MiddlemanMod.Log.Msg($"Meetup cancelled for {dealer.FullName}.");

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
                RestoreNormalState(dealer);
            }
            return;
        }

        if (IsInDialogue(dealer))
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
                CompleteArrival(meetup);
                return;
            }

            EnsureJogSpeed(dealer);

            if (!dealer.Movement.IsMoving && meetup.HasAttemptedMove && Time.time >= meetup.NextRepathTime)
            {
                meetup.PathingFailures++;
                if (meetup.PathingFailures >= MaxPathingFailuresBeforeWarp)
                {
                    MiddlemanMod.Log.Warning($"{dealer.FullName} could not path to {meetup.TargetProperty.PropertyName}; warping to meet spot.");
                    dealer.Movement.Warp(meetup.Destination);
                    CompleteArrival(meetup);
                    return;
                }
            }

            if (!dealer.Movement.IsMoving || Time.time >= meetup.NextRepathTime)
            {
                TrySetMeetupDestination(meetup);
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
                RestoreNormalState(dealer);
                MiddlemanMod.Log.Msg($"Meetup timed out for {dealer.FullName}; returning to schedule.");
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

        EnsureJogSpeed(dealer);

        if (!IsInDialogue(dealer))
        {
            TrySetMeetupDestination(meetup);
        }
    }

    private static void TrySetMeetupDestination(ActiveMeetup meetup)
    {
        Dealer dealer = meetup.Dealer;
        if (!dealer.Movement.CanMove)
        {
            meetup.NextRepathTime = Time.time + 1f;
            return;
        }

        Vector3 target = meetup.Destination;
        if (dealer.Movement.GetClosestReachablePoint(meetup.Destination, out Vector3 reachable))
        {
            target = reachable;
        }
        else
        {
            meetup.PathingFailures++;
        }

        dealer.Movement.SetDestination(target);
        meetup.HasAttemptedMove = true;
        meetup.NextRepathTime = Time.time + RepathIntervalSeconds;
    }

    private static void CompleteArrival(ActiveMeetup meetup)
    {
        Dealer dealer = meetup.Dealer;
        meetup.HasArrived = true;
        meetup.ArrivalTotalMins = NetworkSingleton<TimeManager>.InstanceExists
            ? NetworkSingleton<TimeManager>.Instance.GetTotalMinSum()
            : 0;

        RemoveJogSpeed(dealer);
        dealer.Movement.Stop();

        if (dealer.MSGConversation != null)
        {
            MessageChain arrived = new();
            arrived.Messages.Add($"I'm at {meetup.TargetProperty.PropertyName}, boss.");
            dealer.MSGConversation.SendMessageChain(arrived, initialDelay: 0f, notify: true);
        }

        MiddlemanMod.Log.Msg($"Meetup arrived: {dealer.FullName} at {meetup.TargetProperty.PropertyName}.");
        MiddlemanController.Notify("Middleman", $"{dealer.FullName} arrived at {meetup.TargetProperty.PropertyName}.");
    }

    private static void PauseActiveDealForMeetup(Dealer dealer)
    {
        if (GameCompat.IsAlive(dealer.Behaviour))
        {
            DealerAttendDealBehaviour? attend = dealer.Behaviour.GetBehaviour<DealerAttendDealBehaviour>();
            if (GameCompat.IsAlive(attend) && attend!.Active)
            {
                attend.Disable_Server();
            }
        }

        // Clear currentContract without clearing Contract.Dealer so ActiveContracts stays queued for after the meetup.
        if (DealerCurrentContractField != null)
        {
            DealerCurrentContractField.SetValue(dealer, null);
        }
        else if (DealerCurrentContractProp != null && DealerCurrentContractProp.CanWrite)
        {
            DealerCurrentContractProp.SetValue(dealer, null);
        }
    }

    private static void EnsureJogSpeed(Dealer dealer)
    {
        if (GameCompat.IsAlive(dealer.Movement) && dealer.Movement.SpeedController != null)
        {
            dealer.Movement.SpeedController.AddSpeedControl(SpeedControlId, 0.7f, 10);
        }
    }

    private static void RemoveJogSpeed(Dealer dealer)
    {
        if (GameCompat.IsAlive(dealer.Movement) && dealer.Movement.SpeedController != null)
        {
            dealer.Movement.SpeedController.RemoveSpeedControl(SpeedControlId);
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

    private static void RestoreNormalState(Dealer dealer)
    {
        RemoveJogSpeed(dealer);

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
