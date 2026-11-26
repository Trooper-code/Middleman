# Middleman Mod for Schedule I

A lightweight MelonLoader mod for **Schedule I** that lets you hand off customer deals to your recruited dealers and summon dealers straight to your properties—all inside the native in-game **Messages** phone app, in both single-player and co-op multiplayer.

No extra phone apps, no S1API dependency, and completely save-safe.

## Features
- **Pass Pending Deal Offers:** Whenever a customer texts you an offer (or accepts your counter-offer), a **`[Pass to Dealer]`** button appears right alongside Accept, Counter-offer, and Decline.
- **Pass Already-Accepted Deals:** Already accepted a deal and changed your mind? Open that customer's text thread, tap **Compose**, and choose **`[Pass deal to dealer]`**. The deal transfers to a dealer, clears from your Journal, HUD, and map, and keeps its scheduled delivery window.
- **Automatic Dealer Selection:** Picks an eligible, conscious recruited dealer who has enough packaged product of acceptable quality in their inventory (preferring the dealer with the fewest active drops, breaking ties by closest distance to the meet spot).
- **Natural Chat Confirmations:** You text *"I'll have \<Dealer\> take care of you."*, the customer replies with a confirmation, and the dealer texts you *"Got it boss, I'll take care of \<Customer\>."*
- **Summon Dealers to Your Properties:** Open any recruited dealer's thread, tap **Compose → `[Meet me at a property]`**, and pick any property you own. They pause active drops, jog over (with a NavMesh warp fallback if they get stuck), and text you when they arrive. Once you talk/trade and walk away (or pick **Compose → `[Head back]`**), they head right back to work.
- **Check Dealer Stock & Cash Remotely:** Open any recruited dealer's thread and pick **Compose → `[Check stock & cash]`** to have them text back their cash on hand, active drop count, and packaged product inventory.
- **Out-of-Stock Protection:** If no recruited dealer has enough product in stock, the deal stays untouched and a plain-English notification tells you what's missing.
- **Cross-Runtime & Save-Safe:** Custom buttons are stripped before saving so your save files stay 100% vanilla-compatible. Supports both **IL2CPP** (normal Steam branch) and **Mono** (`alternate` beta branch).
- **Co-op Multiplayer Support:** Friends in a Steam co-op lobby can pass pending offers, pass accepted deals, summon dealers, dismiss meetups, and check dealer stock/cash right from their own phones. The host executes the handoff/movement and syncs deal state, meetup controls, and personal notifications back to the asking player.

## How It Works
1. **Passing an offer:** Open any customer offer in Messages and tap **`[Pass to Dealer]`**. Middleman finds a stocked dealer, schedules the deal in their queue, and notifies both chats.
2. **Passing an accepted deal:** Open a customer's thread while you have an active deal with them, tap the **Compose** icon, and choose **`[Pass deal to dealer]`**.
3. **Calling a dealer over:** Open a recruited dealer's thread → **Compose → `[Meet me at a property]`** → pick a property (e.g., Barn, Bungalow, Docks Warehouse). Use **Compose → `[Head back]`** anytime to dismiss them early, or simply walk away after interacting.
4. **Checking inventory:** Open a dealer's thread → **Compose → `[Check stock & cash]`** before deciding who to summon or restock.

## Requirements
- **Schedule I** (tested on v0.4.7f12; single-player and Steam co-op multiplayer)
- **MelonLoader** `0.7.3`+ ([MelonLoader Installer](https://melonwiki.xyz/))
- **[SteamNetworkLib](https://github.com/ifBars/SteamNetworkLib/releases)** `v1.6.0`+ (matching runtime DLL placed in `UserLibs\`)
- **No S1API required**

> **Multiplayer note:** Install both **Middleman** (in `Mods\`) and **SteamNetworkLib** (in `UserLibs\`) on the **host and every friend** using the DLL that matches each PC's Steam branch. Unmodded friends won't crash, but won't see Middleman buttons and may still see already-accepted deals in their Journal after a host transfer.

## Installation
Download the latest release from [**Releases**](https://github.com/Trooper-code/Middleman/releases/latest) and `SteamNetworkLib` `v1.6.0`+ from [**SteamNetworkLib Releases**](https://github.com/ifBars/SteamNetworkLib/releases):

### Normal Steam Branch (IL2CPP)
1. Install **MelonLoader 0.7.3+** into your Schedule I folder and launch the game once to the main menu.
2. Place `SteamNetworkLib-IL2Cpp.dll` in `<Schedule I>\UserLibs\`.
3. Download `Middleman.IL2CPP.dll` from the [latest release](https://github.com/Trooper-code/Middleman/releases/latest) and place it in `<Schedule I>\Mods\`.
4. Launch the game and load your save (or join a co-op lobby).

### `alternate` Beta Branch (Mono)
1. Switch Schedule I to the `alternate` beta in Steam and install **MelonLoader 0.7.3+**.
2. Place `SteamNetworkLib-Mono.dll` in `<Schedule I>\UserLibs\`.
3. Download `Middleman.Mono.dll` from the [latest release](https://github.com/Trooper-code/Middleman/releases/latest) and place it in `<Schedule I>\Mods\`.

## Configuration (`UserData/MelonPreferences.cfg`)
After launching once, a `[Middleman]` section is generated in `<Schedule I>\UserData\MelonPreferences.cfg`:

| Setting | Default | Description |
|---------|---------|-------------|
| `AllowPassingAcceptedDeals` | `true` | Show `[Pass deal to dealer]` in the Compose menu for deals you already accepted |
| `ShowNotifications` | `true` | Show phone toast notifications when a deal is handed off or when no dealer has stock |
| `AllowDealerMeetups` | `true` | Show `[Meet me at a property]` and `[Head back]` in recruited dealers' Compose menus |
| `DealerMeetupWaitMinutes` | `360` | How many in-game minutes a dealer waits at your property after arriving before heading back |
| `AutoDismissAfterMeetup` | `true` | Automatically send a dealer back to their drops after you interact with them at your property and walk away |

## Building from Source
1. Install the **.NET 10 SDK** (or .NET 6+ SDK supporting `netstandard2.1` and `net6.0`).
2. Clone this repo:
   ```powershell
   git clone https://github.com/Trooper-code/Middleman.git
   cd Middleman
   ```
3. Copy `Directory.Build.props.user.example` to `Directory.Build.props.user` at the repo root and point:
   - `ScheduleIManagedDir` → `<Schedule I Mono>\Schedule I_Data\Managed`
   - `ScheduleIIl2CppDir` → `<Schedule I>\MelonLoader\Il2CppAssemblies`
   - `ScheduleIMelonNet6Dir` → `<Schedule I>\MelonLoader\net6`
   - `SteamNetworkLibMonoDll` → path to `SteamNetworkLib-Mono.dll` (`v1.6.0`+)
   - `SteamNetworkLibIl2CppDll` → path to `SteamNetworkLib-IL2Cpp.dll` (`v1.6.0`+)
4. Build both targets:
   ```powershell
   dotnet build src/Middleman/Middleman.csproj -c Release
   ```
   Outputs:
   - `src/Middleman/bin/Release/netstandard2.1/Middleman.Mono.dll`
   - `src/Middleman/bin/Release/net6.0/Middleman.IL2CPP.dll`

## Research & Changelog
- Schedule I messaging, contract, dealer, and meetup internals are documented in [RESEARCH.md](RESEARCH.md).
- Release history is in [CHANGELOG.md](CHANGELOG.md).

## Legal
This project is open source under the [MIT License](LICENSE) and contains only original code and documentation. It never includes game files, decompiled code, or proprietary assets. Schedule I is made by TVGS.
