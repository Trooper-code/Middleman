# Report: MP1-net-research

**Prompt:** `docs/prompts/MP1-net-research.md`
**Branch:** `mp1-net-research` (from `feat/m1-middleman-mvp` at `b1bae68`) · **Last commit:** `3b91ccc`
**Status:** done

## Tool versions
| Tool | Version used | Matches HANDOFF.md? |
|------|--------------|---------------------|
| Schedule I (Mono & IL2CPP) | v0.4.7f12 | yes |
| MelonLoader | 0.7.3 | yes |
| .NET SDK | 10.0.404 | yes |
| Python | 3.12.10 | yes (not listed in HANDOFF.md) |
| ilspycmd | installed at `C:\Users\Josh\.dotnet\tools\ilspycmd.cmd` | yes (not listed in HANDOFF.md) |
| S1API.Forked (checked in NuGet cache) | 3.2.3 | yes |

Skills/tools used: `ilspycmd`, `writing-plans`, `verification-before-completion`.

## What changed
- `docs/prompts/MP1-net-research.md`: copied milestone prompt into the repository (`872c36a`)
- `RESEARCH.md`: added `## Multiplayer` documenting all four channel options, why Steam lobby chat was selected, the `Lobby.LobbyID == 0` quirk, and host-side gaps M10–M13 (`bfc7aa5`, `3b91ccc`)
- `docs/MP2-design.md`: added full MP2 message design, M10/M11/M13 fixes, unmodded-friend handling, and Overhead Sprinkler M6b reuse section (`4e95671`)
- `tools/netprobe/NetProbe.csproj`, `tools/netprobe/NetProbeMod.cs`: added debug-only Steam lobby chat ping/pong probe mod targeting both Mono (`netstandard2.1`) and IL2CPP (`net6.0`) (`c05b43b`)
- `src/Middleman/`: untouched (verified via `git diff --name-only b1bae68..HEAD`)

## Findings
| Finding | Status | Evidence |
|---------|--------|----------|
| `NetProbe.Mono.dll` and `NetProbe.IL2CPP.dll` compile cleanly against `SteamMatchmaking.SendLobbyChatMsg`, `GetLobbyChatEntry`, and `Callback<LobbyChatMsg_t>.Create` with zero new dependencies | confirmed | `dotnet build tools/netprobe/NetProbe.csproj -c Debug` (0 warnings, 0 errors); `[confirmed @ mp1-net-research \| Schedule I=v0.4.7f12, MelonLoader=0.7.3]` |
| FishNet custom `IBroadcast` structs require `struct, IBroadcast` and weaver-generated serializers, blocking new mod-defined structs on IL2CPP; game broadcasts (`HostPasswordBroadcast` etc.) cannot be safely reused | unverified | `FishNet.Runtime.dll` & `Il2CppFishNet.Runtime.dll` (`ServerManager.RegisterBroadcast<T>`); `HostAuthenticator.cs:39-57`; `ScheduleOne.Networking/*.cs` |
| Steam lobby chat (`SendLobbyChatMsg` + `Callback<LobbyChatMsg_t>`) works on both runtimes, uses existing `Callback<LobbyChatMsg_t>` instantiation, avoids clashing with `SteamLobbyService.OnLobbyChatMessage`, and provides Steam-authenticated sender IDs + lobby membership checks | unverified | `SteamLobbyService.cs:26,68-76,150-159,287-315`; `Il2Cppcom.rlabrecque.steamworks.net.dll` (`SteamMatchmaking`, `LobbyChatMsg_t`, `Callback`1`) |
| `Lobby.LobbyID` is never assigned and stays `0`; the active lobby ID lives on `SteamLobbyService._lobbyID` inside `Lobby._lobbyService` | unverified | `Lobby.cs:32`; `SteamLobbyService.cs:58,210,237`; `Il2CppScheduleOne.Networking.SteamLobbyService` |
| `SteamNetworkingMessages` (P2P) is available on both runtimes as a fallback, but requires `IntPtr` buffers, per-frame polling, per-peer fan-out, and an uninstantiated `Callback<SteamNetworkingMessagesSessionRequest_t>` | unverified | `com.rlabrecque.steamworks.net.dll`; `Il2Cppcom.rlabrecque.steamworks.net.dll` (`SteamNetworkingMessages`) |
| `S1API.Forked 3.2.3` has no general-purpose mod messaging or broadcast API (only internal product/NPC helpers) | unverified | `S1API.dll` v3.2.3 type inspection via `ilspycmd -l c` |
| M10 is split: pending offers (`Customer.ContractAccepted` → `QuestManager.ContractAccepted`) already replicate `dealerObj` to clients via `CreateContract_Networked`, whereas already-accepted deals (`Contract.SetDealer`, `Quest.SetIsTracked`, `Dealer.AddContract`) are local-only on the host | unverified | `Customer.cs:1070`; `QuestManager.cs:109-120,190,226`; `Contract.cs:107-114,217,246-253`; `Quest.cs:460`; `Dealer.cs:744` |
| M11: `DealerMeetupManager.cs:356,358,361` uses `Player.Local` for face-player and auto-dismiss (should iterate `Player.PlayerList`); `MiddlemanController.cs:261-262` uses `PlayerSingleton<MessagesApp>.Instance` only for local toast icon | unverified | `DealerMeetupManager.cs:356-365`; `MiddlemanController.cs:261-262`; `Player.cs:76` |
| M12: `MSGConversation.SendMessage` and `SendMessageChain` default to `network = true` and replicate via `MessagingManager` ServerRpc → ObserversRpc | unverified | `MSGConversation.cs:428-480`; `MessagingManager.cs:85-151` |
| M13: UI buttons inject locally on every client, but `[Head back]` visibility checks host-only `DealerMeetupManager.HasActiveMeetup(dealer)` and needs `MEETUP_STATE` sync in MP2 | unverified | `MessagesPatches.cs:51-101,143-292` |

Written to: `RESEARCH.md` (`## Multiplayer`; compile-confirmed finding stamped `[confirmed @ mp1-net-research | Schedule I=v0.4.7f12, MelonLoader=0.7.3]`; note: this repo does not use `docs/research/INDEX.md`)

## Tests
```text
dotnet build 'E:\Sprinkler\S1-Middleman\tools\netprobe\NetProbe.csproj' -c Debug
  Determining projects to restore...
  All projects are up-to-date for restore.
  NetProbe -> E:\Sprinkler\S1-Middleman\tools\netprobe\bin\Debug\net6.0\NetProbe.IL2CPP.dll
  NetProbe -> E:\Sprinkler\S1-Middleman\tools\netprobe\bin\Debug\netstandard2.1\NetProbe.Mono.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Two-player probe test steps (for Josh on Windows)
1. **Install the probe on both PCs** (matching each PC's Steam branch; remove `NetProbe.*.dll` after testing):
   - **IL2CPP (normal Steam branch):** copy `tools/netprobe/bin/Debug/net6.0/NetProbe.IL2CPP.dll` into `<Schedule I>\Mods\`.
   - **Mono (`alternate` beta branch):** copy `tools/netprobe/bin/Debug/netstandard2.1/NetProbe.Mono.dll` into `<Schedule I>\Mods\`.
2. **Join the same co-op game** (host loads the save and invites the friend through Steam).
3. **Friend presses `F9` three times.**
   - **Expected:** Friend's `MelonLoader\Latest.log` logs 3 sent pings; host's `MelonLoader\Latest.log` logs `[NetProbe][HOST] Received ping #1/#2/#3 from '<Steam name>'` and 3 broadcasted pongs; both PCs' `Latest.log` files log `[NetProbe] Received pong #1/#2/#3 from host`.
4. **Host presses `F9`.**
   - **Expected (no-op):** Host's `MelonLoader\Latest.log` logs `[NetProbe] F9 pressed on host (no-op; press F9 on a non-host client to send a ping).` and no ping/pong is sent.
5. **Send results back:** Share `MelonLoader\Latest.log` from both PCs and note which branch (Mono or IL2CPP) each PC was running.

## Open questions
- Live two-player Steam lobby delivery (`NetProbe` test above) needs to be confirmed by Josh on Windows before starting MP2 implementation.
- Git housekeeping noted in `FINDINGS-Middleman.md`: `v1.0.0` tag currently points at `c76ba72` ("Initial commit") rather than `b1bae68`; confirm whether/when to move it (not touched on this branch).
