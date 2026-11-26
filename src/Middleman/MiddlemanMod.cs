using MelonLoader;

[assembly: MelonInfo(typeof(Middleman.MiddlemanMod), "Middleman", "1.1.0", "Trooper-code")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace Middleman;

/// <summary>
/// Main MelonMod entrypoint for Middleman Mod:
/// Lets the player pass pending deal offers and already-accepted deals to an auto-selected dealer
/// directly from the Messages phone app, in both single-player and co-op multiplayer.
/// </summary>
public sealed class MiddlemanMod : MelonMod
{
    internal static MelonLogger.Instance Log { get; private set; } = null!;

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;
        MiddlemanConfig.Initialize();
        MiddlemanNetwork.Initialize();
        LoggerInstance.Msg("Middleman v1.1.0 loaded.");
    }

    public override void OnUpdate()
    {
        MiddlemanNetwork.Update();
        DealerMeetupManager.Update();
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        if (sceneName == "Main")
        {
            MiddlemanNetwork.RequestStateSyncIfClient();
        }
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
    {
        MessagesPatches.ResetState();
        DealerMeetupManager.ResetState();
    }

    public override void OnApplicationQuit()
    {
        MiddlemanNetwork.Dispose();
    }
}
