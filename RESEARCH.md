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
