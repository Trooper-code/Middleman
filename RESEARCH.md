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

## Multiplayer (`SteamNetworkLib` v1.6.0)

### Why `SteamNetworkLib` Over Custom FishNet / Lobby Chat
- **FishNet `IBroadcast` (`ServerManager.RegisterBroadcast<T>`):** Blocked for mod-defined structs on IL2CPP without custom IL2CPP serializer registration, and the game's built-in broadcasts (`HostPasswordBroadcast`, `CheckIfUserIsFriendBroadcast`, `FriendCheckResponseBroadcast`, `ResponseBroadcast`) only carry auth/friend payloads (`HostAuthenticator.cs:39-57`, `FishNetSteamAuthenticator.cs:43-45`).
- **`S1API.Forked 3.2.3`:** Does not expose a general-purpose mod messaging API (`CustomProductManifestRuntime` is internal).
- **`SteamNetworkLib` v1.6.0 (`ifBars/SteamNetworkLib`):** Wraps Steamworks P2P packets (`SteamNetworking.SendP2PPacket` with `k_EP2PSendReliable`) and Steam lobby membership tracking cleanly on both **Mono** (`SteamNetworkLib-Mono.dll` using `Steamworks`) and **IL2CPP** (`SteamNetworkLib-IL2Cpp.dll` using `Il2CppSteamworks`), with delayed `TryInitialize()` and `ProcessIncomingMessages()` in `OnUpdate()`. `SteamNetworkLib` has no `[MelonInfo]` attribute, so it installs into `<Schedule I>\UserLibs\`. `[confirmed @ feat/mp2-multiplayer | Schedule I=v0.4.7f12, MelonLoader=0.7.3, SteamNetworkLib=1.6.0]` (compile-time verified; live two-player session pending).

### Game Restrictions & Host-Side Sync Findings
- **Vanilla deal acceptance (`Customer.ContractAccepted`, `Dealer.AddContract`, `Contract.SetDealer`, NPC movement):**
  - `Customer.ContractAccepted(EDealWindow, bool, Dealer)` refuses to run on non-host clients (`Customer.cs:1054-1060`), and `Customer.SendContractAccepted` (`Customer.cs:1049`) has no `Dealer` parameter.
  - `Dealer.AddContract` (`Dealer.cs:744`) and `Contract.SetDealer` (`Contract.cs:217`) are plain non-RPC methods, and NPC movement (`SetDestination` / `Warp`) is host-driven.
  - Therefore all Middleman actions (`PASS_OFFER`, `PASS_ACCEPTED`, `SUMMON`, `HEAD_BACK`, `CHECK_STATUS`) are forwarded to the host via `MiddlemanNetwork` when `!InstanceFinder.IsServer`.
- **M10 (Journal / HUD / Map sync):**
  - *Pending offers (`TryPassOfferedContract`):* Vanilla `QuestManager.ContractAccepted` passes `dealerObj` in `CreateContract_Networked` (`QuestManager.cs:190`), and clients invoke `Contract.SetDealer(dealer)` in `CreateContract_Local` (`QuestManager.cs:226`), hiding Journal/HUD/PoI automatically.
  - *Already-accepted deals (`TryPassAcceptedContract`):* `Contract.SetDealer`, `Quest.SetIsTracked(false)`, and `Dealer.AddContract` are local-only. In multiplayer, the host broadcasts `MM2|CONTRACT_ASSIGNED|<contractGuid>|<dealerId>` so connected clients also clear the deal from their Journal, HUD, and map. Late-joiners already receive `dealerObj` from `QuestManager.OnSpawnServer` (`QuestManager.cs:109-120`).
- **M11 (`Player.Local` in `DealerMeetupManager`):**
  - `DealerMeetupManager.TickMeetup` now iterates `Player.PlayerList` (`Player.cs:76`) to face the nearest alive player within `6f` and only auto-dismisses once `HasInteractedWithPlayer` is true and **all** alive players are at least `AutoDismissDistance` (`15f`) away.
- **M12 & M13 (Messages & Compose buttons):**
  - `MSGConversation.SendMessage`, `SendMessageChain`, and `ClearResponses(network: true)` route through `MessagingManager` RPCs (`MessagingManager.cs:85-151`) to all players.
  - Compose `[Head back]` visibility (`MessagesPatches.cs:282`) uses `DealerMeetupManager.HasActiveMeetup`, synced to clients via `MM2|MEETUP_STATE|<dealerId>|<0|1>` (and `MM2|SYNC_STATE` when a client loads into `Main`).
