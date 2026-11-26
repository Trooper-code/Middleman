# MP2 Design — Middleman Multiplayer (and Overhead Sprinkler M6b Hook)

This design covers MP2 for **Middleman** (and the shared wire format for **Overhead Sprinkler M6b**), based on `mp1-net-research` findings in [RESEARCH.md](../RESEARCH.md).

## 1. Shared Transport Helper & Wire Protocol

### Channel
- **Steam lobby chat** via `SteamMatchmaking.SendLobbyChatMsg` and `Callback<LobbyChatMsg_t>` + `SteamMatchmaking.GetLobbyChatEntry`.
- **Lobby ID lookup:** Read `SteamLobbyService._lobbyID` from `Singleton<Lobby>.Instance._lobbyService` (do **not** use `Lobby.Instance.LobbyID`, which remains `0`).
- **Framing:** UTF-8 pipe-delimited text envelope:
  ```text
  S1NET|1|<modId>|<dir>|<msgType>|<arg1>|<arg2>|...
  ```
  - `1`: protocol version.
  - `<modId>`: `middleman` or `sprinkler` (shared helper class, e.g., `LobbyNetChannel`, can live in each mod with identical framing so messages never collide).
  - `<dir>`: `C2H` (client → host), `H2A` (host → all clients), `H2C` (host → specific SteamID64).
  - Escaping: field values sanitize `|` → `/` and newlines → space (IDs/GUIDs contain neither).

### Security & Validation Rules
1. Only process `LobbyChatMsg_t` where `ev.m_ulSteamIDLobby == activeLobbyId`.
2. Ignore any message not starting with `S1NET|1|<modId>|`.
3. For `C2H` messages: process **only** when `InstanceFinder.IsServer` is true and `senderSteamId` is a current member of the lobby (`SteamMatchmaking.GetNumLobbyMembers` / `GetLobbyMemberByIndex`).
4. For `H2A` / `H2C` messages: process **only** when `senderSteamId == SteamMatchmaking.GetLobbyOwner(activeLobbyId)`.
5. For `H2C` messages: `<targetSteamId64>` is the first payload argument; non-matching clients ignore it immediately, and the host skips Steam lobby chat if the requester is the host itself (local execution path).

---

## 2. Middleman Messages (`modId = middleman`)

### Client → Host (`C2H`)

| `msgType` | Payload | Triggered on client by | Host handler |
|-----------|---------|------------------------|--------------|
| `PASS_OFFER` | `<customerNpcId>` | Pressing `[Pass to Dealer]` (`MessagesPatches.ShowResponses_Prefix`) when `!InstanceFinder.IsServer` | Look up `Customer` by `NPC.ID`; validate `OfferedContractInfo != null && CurrentContract == null`; run `TryPassOfferedContractCore(customer, requesterSteamId)` |
| `PASS_ACCEPTED` | `<customerNpcId>` | Selecting `[Pass deal to dealer]` (`EnsureAcceptedDealSendable`) when `!InstanceFinder.IsServer` | Look up `Customer` by `NPC.ID`; validate `CurrentContract != null && CurrentContract.State == Active && CurrentContract.Dealer == null`; run `TryPassAcceptedContractCore(customer, requesterSteamId)` |
| `SUMMON` | `<dealerId>\|<propertyCode>` | Picking a property in `ShowPropertyPicker` when `!InstanceFinder.IsServer` | Look up `Dealer` (`Dealer.AllPlayerDealers` by `dealer.ID`) and `Property` (`Property.OwnedProperties` by `PropertyCode`); run `RequestMeetupCore(dealer, prop, requesterSteamId)` |
| `HEAD_BACK` | `<dealerId>` | Selecting `[Head back]` (`EnsureDealerMeetupSendables`) when `!InstanceFinder.IsServer` | Look up `Dealer` by `dealer.ID`; call `DealerMeetupManager.CancelMeetup(dealer, sendChat: true)` and broadcast `MEETUP_STATE\|<dealerId>\|0` |
| `CHECK_STATUS` | `<dealerId>` | Selecting `[Check stock & cash]` (`EnsureDealerMeetupSendables`) when `!InstanceFinder.IsServer` | Look up `Dealer` by `dealer.ID`; run `DealerMeetupManager.SendStatusReport(dealer)` (host has authoritative `Dealer.ActiveContracts` and inventory) |

### Host → Client (`H2C`)

| `msgType` | Payload | Purpose |
|-----------|---------|---------|
| `TOAST` | `<targetSteamId64>\|<title>\|<subtitle>` | Deliver personal outcome/error feedback (e.g., `"Passed Kyle Cooley's deal to Benji Coleman."` or `"No dealer has 2x OG Kush in stock."`) to the friend who clicked the button, instead of showing the toast only on the host's screen |

### Host → All (`H2A`)

| `msgType` | Payload | Purpose |
|-----------|---------|---------|
| `CONTRACT_ASSIGNED` | `<contractGuid>\|<dealerId>` | Fixes **M10** for already-accepted deals: clients find the `Contract` in `Contract.Contracts` by `GUID.ToString()` and the `Dealer` in `Dealer.AllPlayerDealers` by `ID`, then call `contract.SetIsTracked(false)`, `contract.SetDealer(dealer)`, and hide `entry.PoI` (`HideContractPoIs`) so the deal disappears from every player's Journal, HUD, and map |
| `MEETUP_STATE` | `<dealerId>\|<0 or 1>` | Keeps a lightweight `HashSet<string>` of active meetup dealer IDs on clients so `DealerMeetupManager.HasActiveMeetup(dealer)` returns the right state for `[Meet me at a property]` vs `[Head back]` Compose visibility (`MessagesPatches.cs:273, 282`) |

---

## 3. Host-Side Reuse & Reply Routing

- **Reuse existing host logic:** Refactor each action in `MiddlemanController` and `DealerMeetupManager` into `...Core(..., ulong requesterSteamId)`:
  - When `InstanceFinder.IsServer` is true locally (host initiated): execute directly and pass `0` (local toast via `Notify`).
  - When `!InstanceFinder.IsServer` (client initiated): send the `C2H` message; host executes the same `...Core` method and routes `NotifyForRequester(requesterSteamId, title, subtitle)` either locally (if `0`) or via `H2C|TOAST|<requesterSteamId>|<title>|<subtitle>`.
- **Chat messages (M12):** Keep `MSGConversation.SendMessage(..., notify: false)` and `SendMessageChain(..., notify: false)` running on the host only. Because `network` defaults to `true`, `MessagingManager` replicates all chat confirmations and stock/cash replies to every player automatically (`MSGConversation.cs:428-480`).
- **Duplicate-click protection:** On the client, `ShowPropertyPicker` already calls `conversation.ClearResponses(network: false)` locally when a property is chosen. For `[Pass to Dealer]`, clear local responses immediately or let the host's `convo.ClearResponses(network: true)` clear them; host-side guards (`OfferedContractInfo != null`, `CurrentContract.Dealer == null`, `!HasActiveMeetup`) remain authoritative if two players click in the same tick.
- **`[Check stock & cash]` (`SendStatusReport`):** Must route `C2H` → host because `Dealer.ActiveContracts` is maintained only on the host (`Dealer.cs:744`).

---

## 4. Fixing M10, M11, and M13

### M10 — Journal / HUD / Map Sync
- **Pending offers (`TryPassOfferedContract`):** Vanilla `QuestManager.ContractAccepted` already sends `dealerObj` in `CreateContract_Networked` (`QuestManager.cs:190`), and clients already call `component.SetDealer(dealer)` in `CreateContract_Local` (`QuestManager.cs:226`). No extra sync message needed for pending offers.
- **Already-accepted deals (`TryPassAcceptedContract`):** Host runs `SetIsTracked(false)`, `SetDealer(dealer)`, `HideContractPoIs(contract)`, and `AssignContractToDealer(dealer, contract)`, then broadcasts `H2A|CONTRACT_ASSIGNED|<contract.GUID>|<dealer.ID>`. Connected clients apply the same local visual/state updates (`SetIsTracked(false)`, `SetDealer(dealer)`, `HideContractPoIs`).
- **Late joiners:** Already covered by vanilla `QuestManager.OnSpawnServer` (`QuestManager.cs:114-119`), which sends `dealerObj` for active contracts. For meetups, host can either respond to a client `SYNC_MEETUPS` request on scene load or include active meetup dealer IDs when a player joins.

### M11 — `Player.Local` in `DealerMeetupManager`
- Replace `Player.Local` at `DealerMeetupManager.cs:356-365` with a helper over `Player.PlayerList` (`ScheduleOne.PlayerScripts/Player.cs:76`):
  - **Face nearest player:** Find the alive `Player` in `Player.PlayerList` with minimum distance to `dealer.Movement.FootPosition`; if that minimum distance is `<= 6f`, call `dealer.Movement.FacePoint(nearestPlayer.transform.position)`.
  - **Auto-dismiss:** Only auto-dismiss when `meetup.HasInteractedWithPlayer` is true **and** the nearest alive player is `>= AutoDismissDistance` (`15f`) — so if one friend walks away while another is still trading with the dealer, the dealer stays put.
- Keep `PlayerSingleton<MessagesApp>.Instance` at `MiddlemanController.cs:261-262` as-is (local icon lookup for local toast rendering).

### M13 — Local UI & `[Head back]` State
- All buttons continue injecting locally on every client (`MessagesPatches.cs`).
- Replace `DealerMeetupManager.HasActiveMeetup(dealer)`'s reliance on host-only `ActiveMeetups` with checking `ActiveMeetups.ContainsKey(dealer.ID) || ClientKnownActiveMeetups.Contains(dealer.ID)` populated by `MEETUP_STATE|<dealerId>|<0|1>`.
- When `DealerMeetupManager` starts (`RequestMeetupCore`), cancels (`CancelMeetup`), auto-dismisses, or times out a meetup on the host, broadcast `MEETUP_STATE|<dealerId>|<0|1>`.

---

## 5. Handling a Friend Without the Mod

- **What works automatically:**
  - Any pending offer passed by the host (or a modded friend) uses vanilla `QuestManager.ContractAccepted(..., dealer)` and `MessagingManager` RPCs, so an unmodded friend still sees the chat confirmation and **does not** get a stuck Journal/HUD entry for pending offers.
  - Dealer movement and dialogue during meetups are FishNet-synced by the game, so unmodded friends see the dealer walk up and can talk/trade with them.
- **What stays gracefully hidden or limited:**
  - An unmodded friend simply won't have the `[Pass to Dealer]` or Compose mod buttons injected, and won't receive `H2C|TOAST` notifications.
  - Because `S1NET|1|...` lobby chat messages are ignored by `SteamLobbyService.OnLobbyChatMessage` once `IsGameLoaded` is true (`SteamLobbyService.cs:299-314`), sending mod messages causes no errors or UI spam on unmodded clients.
  - **Known caveat:** If the host/modded player passes an *already-accepted* deal (`TryPassAcceptedContract`), an unmodded friend won't run the `CONTRACT_ASSIGNED` handler and will still see that deal in their Journal until it completes or they rejoin. Document this in README ("all players should install Middleman for full sync, though unmodded clients won't crash").

---

## 6. Overhead Sprinkler M6b Reuse (`modId = sprinkler`)

Overhead Sprinkler M6b can drop in the exact same `LobbyNetChannel` framing with `modId = sprinkler` to close gaps **S8** and **S9** (`FINDINGS-Sprinkler.md`):

- **Spray FX sync (`S8`):** When any player triggers `PlaySprayOnHeads` (watering or additive), send `S1NET|1|sprinkler|C2H|SPRAY|<propertyCode>|<mode>` (or host sends `H2A|SPRAY|<propertyCode>|<mode>` directly if host); host broadcasts `H2A|SPRAY|<propertyCode>|<mode>|<originSteamId>` so all other clients play local particles and audio on that property's sprinkler heads (skipping `originSteamId` to avoid double-playing).
- **Watering lock (`S9`):** Clients request/announce lock state via `S1NET|1|sprinkler|C2H|LOCK|<propertyCode>|<1|0>`, and the host arbitrates `ActiveWateringPropertyIds` and broadcasts `H2A|LOCK|<propertyCode>|<1|0>` so two players cannot simultaneously start watering or applying additives to the same property.
