using System;
using UnityEngine;
#if IL2CPP
using Il2CppInterop.Runtime;
using Il2CppScheduleOne.Messaging;
using Il2CppAction = Il2CppSystem.Action;
#else
using ScheduleOne.Messaging;
using Il2CppAction = System.Action;
#endif

namespace Middleman;

/// <summary>
/// Cross-runtime helpers bridging Mono (netstandard2.1) and IL2CPP (net6.0) proxy differences.
/// </summary>
internal static class GameCompat
{
    /// <summary>
    /// Converts a managed System.Action into the Action type expected by game APIs.
    /// </summary>
    public static Il2CppAction ToGameAction(Action action)
    {
#if IL2CPP
        return (Il2CppSystem.Action)action;
#else
        return action;
#endif
    }

    /// <summary>
    /// Converts a predicate into SendableMessage.BoolCheck on both Mono and IL2CPP.
    /// </summary>
    public static SendableMessage.BoolCheck ToBoolCheck(Func<SendableMessage, bool> predicate)
    {
#if IL2CPP
        return DelegateSupport.ConvertDelegate<SendableMessage.BoolCheck>(predicate)!;
#else
        return new SendableMessage.BoolCheck(predicate);
#endif
    }

    /// <summary>
    /// Null/destroyed check compatible with both Unity Mono and IL2CPP proxy objects.
    /// </summary>
    public static bool IsAlive(UnityEngine.Object? obj) => obj != null;
}
