# Schedule I — Middleman Mod Research Notes

Findings below are documented from inspecting Schedule I's Mono `Assembly-CSharp.dll` (v0.4.7f12). In keeping with project policy, only class/method/field names and plain-English descriptions are recorded here.

## Deal Offer Flow (`Customer` & `MSGConversation`)
- `Customer.NotifyPlayerOfContract(ContractInfo, MessageChain, bool, bool, bool)` sends the offer text and calls `MSGConversation.ShowResponses(List<Response>, float, bool)` with response labels `"ACCEPT_CONTRACT"`, `"COUNTEROFFER"`, and `"REJECT_CONTRACT"`.
- `Customer.ProcessCounterOfferServerSide` (when `EvaluateCounteroffer` returns `true`) shows `"ACCEPT_CONTRACT"` (`[Schedule Deal]`) and `"REJECT_CONTRACT"` (`Nevermind`).
- `MSGConversation.ShowResponses(..., bool network = true)` delegates over FishNet via `MessagingManager.ShowResponses_Server` → `ShowResponses_Client` → `MSGConversation.ShowResponses(..., network: false)`.
  - Patching `MSGConversation.ShowResponses` on `!network` injects `[Pass to Dealer]` (`label = "MIDDLEMAN_PASS"`) locally without sending custom response callbacks over FishNet RPCs.
- `MSGConversation.GetSaveData()` serializes `currentResponses` into `MSGConversationData.ActiveResponses`. Stripping `"MIDDLEMAN_PASS"` in a `GetSaveData` postfix keeps vanilla save files free of mod-specific response labels.
- `Customer.SetUpResponseCallbacks()` rebinds `"ACCEPT_CONTRACT"`, `"REJECT_CONTRACT"`, and `"COUNTEROFFER"` on save load (`MSGConversation.onLoaded`) and on `onResponsesShown`. Because `[Pass to Dealer]` is injected inside `ShowResponses(!network)`, its callback is attached on both live offers and loaded saves.

## Passing an Offer to a Dealer
- `Customer.ContractAccepted(EDealWindow window, bool trackContract, Dealer dealer)` accepts `Customer.OfferedContractInfo` on the server, creates the `Contract` via `QuestManager.ContractAccepted`, calls `Customer.AssignContract(contract)` (setting `Customer.CurrentContract`), and clears `OfferedContractInfo` on all observers via `ReceiveContractAccepted()`.
- `Dealer.GetDealWindow()` selects an `EDealWindow` that has capacity (at least 120 in-game minutes remaining if using the current window, otherwise balancing across upcoming windows based on `ActiveContracts`).
- `Dealer.AddContract(Contract)` registers the contract in `Dealer.ActiveContracts`, calls `Contract.SetDealer(this)`, subscribes to `onQuestEnd` for cleanup, and sorts active contracts by expiry.

## Passing an Already-Accepted Deal
- `MSGConversation.CreateSendableMessage(string)` registers a `SendableMessage` into `MessageSenderInterface` (the Messages app's **Compose** bar). Its `ShouldShowCheck` delegate controls whether the Compose button appears and whether the option is shown.
- `MSGConversation.EnsureUIExists()` initializes `senderInterface` (`MessageSenderInterface`) and adds registered `_sendables`.
- Transferring an active `Contract` (`CurrentContract != null && CurrentContract.Dealer == null && CurrentContract.State == EQuestState.Active`) from player to dealer:
  - `Contract.SetIsTracked(false)` hides the HUD quest UI, compass marker, and updates `QuestEntry` PoI visibility (`QuestEntry.ShouldShowPoI()` checks `ParentQuest.IsTracked`).
  - `Contract.SetDealer(dealer)` sets `Contract.Dealer` and hides the Journal entry (`Contract.ShouldShowJournalEntry()` returns `false` when `Dealer != null`).
  - `Dealer.AddContract(contract)` adds the contract to `Dealer.ActiveContracts`, which `Dealer.GetSaveData()` persists as `ActiveContractGUIDs` and `Dealer.Load(...)` restores on reload.

## Auto-Selecting a Dealer (`Dealer`)
- `Dealer.AllPlayerDealers` lists all spawned `Dealer` instances. Player-hirable dealers have `DealerData.DealerType == EDealerType.PlayerDealer` and `Dealer.IsRecruited == true`.
- `Dealer.ShouldAcceptContract(ContractInfo, Customer)` checks every entry in `ProductList.entries` against `Dealer.GetOrderableProductQuantity(productID, minQuality, maxQuality)`, where:
  - `minQuality = ItemQuality.ShiftQuality(customer.CustomerData.Standards.GetCorrespondingQuality(), Dealer.NegativeQualityTolerance)` (`-2`)
  - `maxQuality = ItemQuality.ShiftQuality(customer.CustomerData.Standards.GetCorrespondingQuality(), Dealer.PositiveQualityTolerance)` (`+5`)
  - `GetAvailableProducts()` already subtracts quantities reserved by `Dealer.ActiveContracts`.
- `DealerAttendDealBehaviour` handles the actual walk-up and handover via `Dealer.RemoveContractItems` and `Customer.OfferDealItems(items, offeredByPlayer: false, out accepted)`, and `Dealer.SubmitPayment` credits `Cash` minus `DealerData.SalesCutPercentage`.

## Dealer Property Meetups (`Dealer`, `NPCScheduleManager`, `Property`)
- `Property.OwnedProperties` holds every owned `Property`. `Property.NPCSpawnPoint` (falling back to `SpawnPoint` or `EmployeeIdlePoints[0]`) provides a reachable outdoor point at each property.
- `Dealer.HomeEvent` (`NPCEvent_StayInBuilding`) is re-enabled every tick in `Dealer.OnTick()` (`HomeEvent.gameObject.SetActive(true)`). Disabling `Dealer.Behaviour.ScheduleManager.DisableSchedule()` and suppressing `HomeEvent` in a `Dealer.OnTick` postfix lets a dealer walk to a property via `NPCMovement.SetDestination(...)` and wait there without snapping back inside their home building.
- While `DealerAttendDealBehaviour.Active` or `DialogueHandler.IsDialogueInProgress` is true, pausing meetup navigation updates lets the dealer finish customer handovers and player conversations cleanly before resuming or dismissing.

## Multiplayer

Findings below were gathered on `mp1-net-research` for Schedule I `v0.4.7f12` (Mono `Assembly-CSharp.dll` + IL2CPP proxy assemblies under `MelonLoader\Il2CppAssemblies`).

### Message Channel Options

| Option | Client→Host & Host→All (Mono / IL2CPP) | Registration & Timing | Serializer / New Types on IL2CPP | Game Clash Risk | Sender Auth & Lobby Filtering | Status & Evidence |
|--------|----------------------------------------|-----------------------|----------------------------------|-----------------|-------------------------------|-------------------|
| **FishNet broadcasts** (`IBroadcast`, `ServerManager` / `ClientManager`) | Works on Mono; blocked for new mod structs on IL2CPP without custom IL2CPP serializer registration | `ServerManager.RegisterBroadcast<T>` / `ClientManager.RegisterBroadcast<T>` after `NetworkManager` initializes | Requires `T : struct, IBroadcast` (`FishNet.Runtime.dll` `ServerManager.RegisterBroadcast<T>`) and FishNet-generated reader/writer (`GeneratedReaders___Internal` / `GeneratedWriters___Internal`); IL2CPP AOT cannot instantiate generic broadcast methods over new mod-defined structs, and `Il2CppFishNet.Managing.Server.ServerManager.RegisterBroadcast<T>` omits the `IBroadcast` constraint while still dispatching through `ParseBroadcast(PooledReader, ...)` | Reusing `HostPasswordBroadcast` disconnects authenticated clients (`HostAuthenticator.cs:39-57`); `CheckIfUserIsFriendBroadcast`, `FriendCheckResponseBroadcast`, and `ResponseBroadcast` only carry `ulong`/`bool` (`ScheduleOne.Networking/*.cs`) | Server handler receives authenticated `NetworkConnection` (`requireAuthentication = true` default) | `unverified` (`FishNet.Runtime.dll`, `Il2CppFishNet.Runtime.dll`, `HostAuthenticator.cs:21,39-57`, `FishNetSteamAuthenticator.cs:43-45`) |
| **Steam lobby chat** (`SteamMatchmaking.SendLobbyChatMsg` + `Callback<LobbyChatMsg_t>`) | Works on **both Mono and IL2CPP** (`C2H` via client→chat, `H2A`/`H2C` via host→chat) | Register `Callback<LobbyChatMsg_t>.Create(...)` once `SteamManager.Initialized` is true (before or after network start) | Plain UTF-8 byte buffer (`byte[]` on Mono, `Il2CppStructArray<byte>` on IL2CPP); **no new IL2CPP types or serializers**, and `Callback<LobbyChatMsg_t>` is already instantiated by `SteamLobbyService.Initialize()` (`SteamLobbyService.cs:74`) | `SteamLobbyService.OnLobbyChatMessage` (`SteamLobbyService.cs:287-315`) only acts when `!IsHost && !LoadManager.IsGameLoaded` and exact text equals `"ready"`, `"load_tutorial"`, or `"host_loading"`. A versioned `S1NET\|1\|...` prefix never matches | `GetLobbyChatEntry` outputs Steam-authenticated `CSteamID pSteamIDUser`; verify `m_ulSteamIDLobby` matches active lobby, verify `C2H` sender is a member via `GetNumLobbyMembers`/`GetLobbyMemberByIndex`, and verify `H2A`/`H2C` sender equals `GetLobbyOwner` | `unverified` (`SteamLobbyService.cs:26,68-76,150-159,287-315`, `Il2Cppcom.rlabrecque.steamworks.net.dll`) |
| **Steam P2P** (`SteamNetworkingMessages`) | Available on both Mono and IL2CPP (`SendMessageToUser` / `ReceiveMessagesOnChannel`) | Must accept session requests and poll `ReceiveMessagesOnChannel` every frame on a chosen channel | Uses raw `IntPtr` byte buffers + `SteamNetworkingIdentity`; however `Callback<SteamNetworkingMessagesSessionRequest_t>` is not instantiated by the game, risking IL2CPP AOT generic failure | Separate API from FishySteamworks' `SteamNetworkingSockets`, though requires managing P2P session lifecycle and per-peer fan-out loops for `H2A` | Peer `SteamNetworkingIdentity` verified by Steam; mod must manually track lobby membership to reject non-lobby peers | `unverified` (`com.rlabrecque.steamworks.net.dll`, `Il2Cppcom.rlabrecque.steamworks.net.dll` `SteamNetworkingMessages`) |
| **S1API networking** (`S1API.Forked 3.2.3`) | No general-purpose mod messaging / broadcast API found | N/A | N/A | N/A (would add an unwanted S1API dependency) | N/A | `unverified` (`S1API.dll` v3.2.3 type list: only internal `CustomProductMultiplayer`, `NPCNetworkBootstrap`, `NPCMessaging`) |

### Chosen Channel

- **Primary choice: Steam lobby chat** (`SteamMatchmaking.SendLobbyChatMsg` + `Callback<LobbyChatMsg_t>` + `SteamMatchmaking.GetLobbyChatEntry`) carrying a versioned text envelope `S1NET|1|<modId>|<dir>|<msgType>|<payload...>`.
  - **Why:** Works on both Mono and IL2CPP with zero new dependencies, zero custom FishNet serializers, zero Harmony patches on game networking, and one shared pattern reusable by Overhead Sprinkler (`S1NET|1|sprinkler|...`). `unverified` (pending live two-player probe test).
  - **Fallback:** `SteamNetworkingMessages` if Steam lobby chat ever hits unexpected length/rate limits in live testing (`unverified`).
  - **Game quirk — `Lobby.LobbyID` stays `0`:** `ScheduleOne.Networking.Lobby.LobbyID` (`Lobby.cs:32`) is never assigned in `Lobby.cs` or `SteamLobbyService.cs`. The real active lobby ID is stored on `SteamLobbyService._lobbyID` (`SteamLobbyService.cs:58, 210, 237`), reachable via `Singleton<Lobby>.Instance._lobbyService` (cast to `SteamLobbyService`). On Mono `_lobbyService` and `_lobbyID` are private, so read via Harmony `AccessTools`; on IL2CPP proxies they are exposed as fields/properties (`unverified`).

### Host-Side Gaps (M10–M13)

- **M10 (split behavior):**
  - *Pending offers (`TryPassOfferedContract`):* **Vanilla already replicates the assigned dealer.** `Customer.ContractAccepted` calls `QuestManager.ContractAccepted(customer, info, track, guid, dealer)` (`Customer.cs:1070`), which calls `CreateContract_Networked(..., dealerObj: dealer.NetworkObject)` (`QuestManager.cs:190`). Clients run `CreateContract_Local(..., dealer)` (`QuestManager.cs:209-230`), calling `Contract.SetDealer(dealer)` (`QuestManager.cs:226`), which hides the Journal entry (`Contract.ShouldShowJournalEntry` at `Contract.cs:246-253`) and HUD (`Contract.ShouldQuestShowUI` at `Contract.cs:107-114`). `Dealer.ActiveContracts` (`Dealer.cs:744`) stays host-only, so stock/drop checks must run on the host (`unverified`).
  - *Already-accepted deals (`TryPassAcceptedContract`):* **Not replicated to connected clients.** `Contract.SetDealer` (`Contract.cs:217`), `Quest.SetIsTracked` (`Quest.cs:460`), and `Dealer.AddContract` (`Dealer.cs:744`) are plain local methods with no RPCs. Connected clients will still see the active contract in their Journal/HUD/PoI until the host broadcasts `CONTRACT_ASSIGNED|<contractGuid>|<dealerId>`. Late-joining clients *do* receive `dealerObj` via `QuestManager.OnSpawnServer` (`QuestManager.cs:109-120`) (`unverified`).
- **M11 (`Player.Local` inventory):**
  - `DealerMeetupManager.cs:356, 358, 361`: `TickMeetup` checks `Player.Local` for face-player (`<= 6f`) and auto-dismiss (`>= 15f`), ignoring non-host players. Fix in MP2 by iterating `Player.PlayerList` (`Player.cs:76`) to face the nearest alive player and auto-dismissing only when all players are `>= AutoDismissDistance` (`unverified`).
  - `MiddlemanController.cs:261-262`: `PlayerSingleton<MessagesApp>.Instance` is used only for the local toast notification icon. Keep as-is for local UI and forward remote caller notifications via `H2C` `TOAST` (`unverified`).
- **M12 (chat replication):** `MSGConversation.SendMessage` (`MSGConversation.cs:428-443`), `MSGConversation.SendMessageChain` (`MSGConversation.cs:467-480`), and `MSGConversation.ClearResponses(..., network: true)` default to `network = true` and route through `MessagingManager` ServerRpc → ObserversRpc (`MessagingManager.cs:85-151`), replicating chat lines to all clients (`unverified`).
- **M13 (local UI buttons & `[Head back]` gap):** `MessagesPatches.cs` (`lines 51-101, 143-292`) injects `[Pass to Dealer]` and Compose options locally on every client (`unverified`). However, `[Head back]` visibility (`MessagesPatches.cs:282`) checks `DealerMeetupManager.HasActiveMeetup(dealer)`, which is currently populated only on the host; MP2 must sync active meetup flags via `H2A` `MEETUP_STATE|<dealerId>|<0|1>` (`unverified`).
