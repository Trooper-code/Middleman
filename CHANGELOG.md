# Changelog

All notable changes to **Middleman Mod** are documented here.

## [1.1.0] - Unreleased
### Added
- **Optional Co-op Multiplayer Support (`SteamNetworkLib` v1.6.0, unverified live):** Non-host players in a Steam co-op lobby can now pass pending offers (`[Pass to Dealer]`), pass already-accepted deals (`[Pass deal to dealer]`), summon dealers (`[Meet me at a property]`), dismiss meetups (`[Head back]`), and check dealer stock/cash (`[Check stock & cash]`) directly from their own phone. `SteamNetworkLib` (`UserLibs\`) is now loaded as an optional soft dependency behind `ISyncBackend` + `NoInlining` isolation so single-player works with no `UserLibs\` DLL.
- **Host-to-Client Sync & Feedback (unverified live):** Routed outcome/error toast notifications back to the requesting player—including when host config disables passing accepted deals or dealer meetups, or when `HEAD_BACK`/`CHECK_STATUS` cannot find the dealer on the host—synced accepted-deal dealer transfers (`CONTRACT_ASSIGNED`) so deals clear from every player's Journal/HUD/map, and synced active meetup state (`MEETUP_STATE` + `SYNC_STATE`) so `[Head back]` shows accurately for clients.

### Fixed
- **Multiplayer Dealer Meetup Proximity (unverified live):** Updated `DealerMeetupManager` to check `Player.PlayerList` instead of `Player.Local` so summoned dealers face whichever player approaches them and only auto-dismiss once all players have walked away.
- **Throttled `SteamNetworkLib` Init Retries (unverified live):** Throttled `MiddlemanNetwork.TryInitializeClient()` retries to once every 2 seconds (and stop permanently if `SteamNetworkLib` is missing from `UserLibs\`) instead of retrying every frame in `OnUpdate()`.

## [1.0.0] - 2026-11-25
### Added
- **`[Pass to Dealer]` on Pending Offers:** Pass customer deal offers (and accepted counter-offers) directly to an auto-selected dealer from the Messages app, including pre-existing offers loaded from a save.
- **`[Pass deal to dealer]` on Accepted Deals:** Hand off active player-owned deals via the customer's Messages Compose menu, clearing them from your Journal, HUD, and map while keeping the scheduled window.
- **Automatic Dealer Selection:** Picks an eligible, conscious recruited dealer with enough acceptable-quality packaged stock (preferring fewest active deals, tie-breaking by distance to the meet spot).
- **In-Chat Confirmations:** Posts natural player, customer, and dealer confirmations in Messages alongside plain-English out-of-stock notifications.
- **Dealer Property Meetups:** Call any recruited dealer to any owned property via **Compose → `[Meet me at a property]`**, with automatic drop-pausing, NavMesh warp fallback, arrival pings, **`[Head back]`** early dismissal, and auto-dismiss after you interact and walk away.
- **Dealer Stock & Cash Check:** Ask any recruited dealer **Compose → `[Check stock & cash]`** to get a live text summary of their cash on hand, active drops, and packaged inventory.
- **Configuration & Dual-Runtime Builds:** Added `[Middleman]` MelonPreferences settings and native builds for both **IL2CPP** (`Middleman.IL2CPP.dll`) and **Mono** (`Middleman.Mono.dll`).
