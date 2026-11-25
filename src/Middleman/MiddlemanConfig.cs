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
    public static MelonPreferences_Entry<bool> AllowDealerMeetups { get; private set; } = null!;
    public static MelonPreferences_Entry<int> DealerMeetupWaitMinutes { get; private set; } = null!;
    public static MelonPreferences_Entry<bool> AutoDismissAfterMeetup { get; private set; } = null!;

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

        AllowDealerMeetups = _category.CreateEntry(
            "AllowDealerMeetups",
            true,
            display_name: "Allow Dealer Property Meetups",
            description: "If enabled, you can message any recruited dealer via Compose to have them meet you at one of your owned properties.");

        DealerMeetupWaitMinutes = _category.CreateEntry(
            "DealerMeetupWaitMinutes",
            360,
            display_name: "Dealer Meetup Wait (In-Game Minutes)",
            description: "How many in-game minutes a dealer waits at your property after arriving before heading back.");

        AutoDismissAfterMeetup = _category.CreateEntry(
            "AutoDismissAfterMeetup",
            true,
            display_name: "Auto-Dismiss After Meetup",
            description: "Automatically send a dealer back to their drops after you talk or trade with them at your property and walk away.");
    }
}
