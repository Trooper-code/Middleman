# Middleman Mod for Schedule I

A lightweight MelonLoader mod for **Schedule I** that lets you pass customer deals straight to one of your recruited dealers—either when a customer texts you an offer, or after you've already accepted the deal.

## Features
- **`[Pass to Dealer]` on Deal Offers:** Whenever a customer texts you a deal offer (or accepts your counter-offer), a `[Pass to Dealer]` button appears right alongside Accept, Counter-offer, and Decline.
- **Pass Already-Accepted Deals:** Open the **Compose** menu inside any customer's text thread while you have an active deal with them and choose **`[Pass deal to dealer]`**. The deal transfers to a dealer, clears from your personal quest tracker/map, and keeps its scheduled delivery window.
- **Call Dealers to Your Properties:** Open any recruited dealer's text thread in Messages and pick **Compose → `[Meet me at a property]`** to choose any property you own. They'll walk over, text you when they arrive, and wait there so you can restock them or collect cash without hunting them down (use **Compose → `[Head back]`** to dismiss them early).
- **Automatic Dealer Selection:** Automatically picks an eligible recruited dealer who is conscious and has enough packaged product of acceptable quality in their inventory (preferring the dealer with the fewest active deals, breaking ties by closest distance to the meet spot).
- **Clear In-Game Feedback:** Confirmation replies appear in both the customer's and dealer's Messages threads. If no recruited dealer has the requested product in stock, the deal stays untouched and a plain-English phone notification tells you what's missing.
- **Save-Safe & Cross-Runtime:** Never writes custom button data into save files. No S1API dependency required. Builds for both the normal **IL2CPP** Steam branch and the `alternate` **Mono** branch.

## Requirements
- **Schedule I** (tested against v0.4.7f12 assemblies)
- **MelonLoader** `0.7.3`+ ([MelonLoader Installer](https://melonwiki.xyz/))

## Installation

### Normal Steam Branch (IL2CPP)
1. Install **MelonLoader 0.7.3** into your Schedule I game folder and launch the game once to the main menu.
2. Copy `Middleman.IL2CPP.dll` into `<Schedule I>\Mods\`.
3. Launch the game and load your save (works as host / single-player).

### `alternate` Beta Branch (Mono)
1. Switch Schedule I to the `alternate` beta in Steam and install **MelonLoader 0.7.3**.
2. Copy `Middleman.Mono.dll` into `<Schedule I>\Mods\`.

## Configuration (`UserData/MelonPreferences.cfg`)
After launching once, a `[Middleman]` section is created in `<Schedule I>\UserData\MelonPreferences.cfg`:

| Setting | Default | Description |
|---------|---------|-------------|
| `AllowPassingAcceptedDeals` | `true` | Show `[Pass deal to dealer]` in the Messages Compose menu for deals you already accepted |
| `ShowNotifications` | `true` | Show phone toast notifications when a deal is handed off or when no dealer has stock |
| `AllowDealerMeetups` | `true` | Show `[Meet me at a property]` and `[Head back]` in recruited dealers' Compose menus |
| `DealerMeetupWaitMinutes` | `360` | How many in-game minutes a dealer waits at your property after arriving before heading back |

## Building from Source
1. Install the **.NET 10 SDK** (or .NET 6+ SDK supporting `netstandard2.1` and `net6.0`).
2. Copy `Directory.Build.props.user.example` to `Directory.Build.props.user` at the repo root and set:
   - `ScheduleIManagedDir` → `<Schedule I Mono>\Schedule I_Data\Managed`
   - `ScheduleIIl2CppDir` → `<Schedule I>\MelonLoader\Il2CppAssemblies`
   - `ScheduleIMelonNet6Dir` → `<Schedule I>\MelonLoader\net6`
3. Build both targets:
   ```powershell
   dotnet build src/Middleman/Middleman.csproj -c Release
   ```
   Outputs:
   - `src/Middleman/bin/Release/netstandard2.1/Middleman.Mono.dll`
   - `src/Middleman/bin/Release/net6.0/Middleman.IL2CPP.dll`

## Research
Schedule I's messaging, contract, and dealer handoff mechanics are documented in [RESEARCH.md](RESEARCH.md).

## Legal
This project contains only original code and documentation. It never includes game files, decompiled code, or proprietary assets. Schedule I is made by TVGS.
