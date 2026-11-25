using MelonLoader;

namespace Middleman;

/// <summary>
/// MelonPreferences configuration for Middleman Mod.
/// Persisted under the [Middleman] section in UserData/MelonPreferences.cfg.
/// </summary>
internal static class MiddlemanConfig
{
    private static MelonPreferences_Category? _category;

    public static MelonPreferences_Entry<bool> AllowPassingAcceptedDeals { get; private set; } = null!;
    public static MelonPreferences_Entry<bool> ShowNotifications { get; private set; } = null!;

    public static void Initialize()
    {
        _category = MelonPreferences.CreateCategory("Middleman", "Middleman Mod");

        AllowPassingAcceptedDeals = _category.CreateEntry(
            "AllowPassingAcceptedDeals",
            true,
            display_name: "Allow Passing Accepted Deals",
            description: "If enabled, a [Pass deal to dealer] option appears in the Messages Compose menu for deals you have already accepted.");

        ShowNotifications = _category.CreateEntry(
            "ShowNotifications",
            true,
            display_name: "Show Phone Notifications",
            description: "Show an in-game phone notification when a deal is handed off to a dealer or when no dealer can take it.");
    }
}
