# MP1-net-research: a client-to-host message channel, and Middleman's multiplayer gaps

## Read first
- `AGENTS.md`: this repo has none yet. Use `E:\Sprinkler\S1-Overhead-Sprinkler\AGENTS.md` as your standing rules
  (its "Always", "Never", "Stop and report", "Project-specific rules: Schedule I mods" and "Agent-specific
  constraints: Antigravity" sections all apply here). Where it names files this repo doesn't have
  (`docs/HANDOFF.md`, `docs/research/INDEX.md`, `docs/reports/TEMPLATE.md`), use the ones listed below.
- `RESEARCH.md` (this repo's research notes; your findings go here)
- `README.md`
- `E:\Sprinkler\Multiplayer-Plan\FINDINGS-Middleman.md` (the coordinator's findings; M1–M13 below refer to its rows)
- `E:\Sprinkler\Multiplayer-Plan\FINDINGS-Sprinkler.md`, rows S8–S9 (the sprinkler will reuse your channel)
- Report format: `E:\Sprinkler\S1-Overhead-Sprinkler\docs\reports\TEMPLATE.md`

## Goal
Pick and prove one way for a non-host player's copy of a mod to send a small request to the host, and for the
host to send a small message to every player, that works on both Mono and IL2CPP with no new dependencies. Also
find out which of Middleman's host-side actions don't show correctly for other players. Then write the MP2 design.
This is research plus a tiny throwaway probe mod; no changes to Middleman's features.

## Branch
`mp1-net-research`, from `feat/m1-middleman-mvp` at `b1bae68` (the released code; the `v1.0.0` tag points at the
wrong commit, so don't branch from it). If `feat/m1-middleman-mvp` is not at `b1bae68`, stop and report.

## Known facts
| Fact | Status | Evidence |
|------|--------|----------|
| Game: Schedule I v0.4.7f12; MelonLoader 0.7.3; .NET SDK 10.0.404; Mono copy at `E:\Sprinkler\Schedule I Mono`; main (IL2CPP) install at `E:\SteamLibrary\steamapps\common\Schedule I` | confirmed | `E:\Sprinkler\S1-Overhead-Sprinkler\docs\HANDOFF.md` "Setup" and "Tool versions" |
| Decompiled Mono game code already exists at `E:\Sprinkler\S1-Overhead-Sprinkler\_decompiled\` | confirmed | coordinator listed it, 2026-11-25 |
| `Customer.ContractAccepted` refuses to run on a client | unverified | `_decompiled/ScheduleOne.Economy/Customer.cs` line 1054 |
| `Customer.SendContractAccepted(window, trackContract)` is the game's client→host accept and has no dealer parameter | unverified | `Customer.cs` line 1049 |
| `Dealer.AddContract` (private) and `Contract.SetDealer` are plain methods, not RPCs | unverified | `Dealer.cs` line 744; `ScheduleOne.Quests/Contract.cs` line 217 |
| The game defines its own FishNet broadcasts (`HostPasswordBroadcast : IBroadcast`, `CheckIfUserIsFriendBroadcast`, `FriendCheckResponseBroadcast`, `ResponseBroadcast`) | unverified | `_decompiled/ScheduleOne.Networking/` |
| The game listens for Steam lobby chat (`Callback<LobbyChatMsg_t>`) | unverified | `_decompiled/ScheduleOne.Networking/SteamLobbyService.cs` line 26 |
| `com.rlabrecque.steamworks.net.dll`, `FishNet.Runtime.dll` ship with the game | confirmed | coordinator listed `Schedule I Mono\Schedule I_Data\Managed`, 2026-11-25 |
| Middleman's host checks | unverified | code read: `src/Middleman/MiddlemanController.cs` lines 71, 136; `DealerMeetupManager.cs` lines 125, 298 |
| Meetups use `Player.Local` for the proximity check | unverified | code read: `DealerMeetupManager.cs` lines 356–361 |

## Tasks
1. Copy this prompt to `docs/prompts/MP1-net-research.md` and commit it on your branch.
2. **Channel options.** For each option in FINDINGS-Middleman "Options for the message channel" (FishNet
   broadcasts, Steam lobby chat, Steam P2P messages, S1API networking), find out from the decompiled code, the
   IL2CPP proxy assemblies (`<main install>\MelonLoader\Il2CppAssemblies`) and library docs:
   - can a mod send client→host and host→all with it on **Mono** and on **IL2CPP**? What has to be registered, and
     when (before/after the network starts)?
   - does it need a serializer or a new type, and is that possible from a mod on IL2CPP?
   - can it clash with the game's own use (its broadcasts, its lobby-chat handler, FishySteamworks transport)?
   - how does the host know who sent a message, and can it reject one from outside the lobby?
   Record each as labeled findings in `RESEARCH.md` under a new "Multiplayer" section.
3. **Pick one** (or a fallback pair) and write why in `RESEARCH.md`. Prefer: works on both runtimes, no new
   dependency, smallest amount of code, reusable by Overhead Sprinkler.
4. **Probe mod.** Under `tools/netprobe/`, a separate MelonLoader mod (own csproj, Mono and IL2CPP targets like
   `src/Middleman/Middleman.csproj`, game DLLs referenced through `Directory.Build.props.user` only) that:
   - on a non-host, F9 sends `"ping <steam name> <counter>"` to the host;
   - on the host, logs every received message with sender, and replies to all with `"pong <counter>"`;
   - logs every received pong on every PC.
   Debug-only, logs only, changes nothing in the game. It must not ship in Middleman's release.
5. **Host-side gaps (code reading).** For M10 (does a passed deal still show in a friend's Journal/HUD/map?), trace
   what `QuestManager.ContractAccepted(..., dealer)` and the quest/contract sync send to clients, and whether the
   `Dealer` on a `Contract` is ever replicated. For M11, list every `Player.Local` use in Middleman and what it
   should be in multiplayer. For M12 and M13, confirm or correct. Label each.
6. **MP2 design** in `docs/MP2-design.md`: the request/response messages Middleman needs (pass offer, pass accepted
   deal, summon, head back, check stock), what the host does for each (reusing current methods), how replies and
   "no dealer has stock" notices reach the asking player, how M10 and M11 get fixed, and how a friend without the
   mod is handled. Also one short section on what Overhead Sprinkler M6b needs from the same channel (spray effect
   start/stop, watering lock).
7. Write Josh's probe test steps (below) into your report.

## Tools
- superpowers `writing-plans`: for the MP2 design in task 6.
- superpowers `systematic-debugging`: if the probe doesn't build or a runtime behaves differently.
- superpowers `verification-before-completion`: before reporting done.
- `ilspycmd`: to read any game or FishNet/Steamworks type not already in `_decompiled/`. Output stays in a
  git-ignored folder.
- REA: only if a question can only be answered on the IL2CPP side (e.g. whether a type can be registered there).
  Read-only on game files; never its debugger or process tools on the running game. Its output is unverified.
- Not needed: /swarm (this is one linear research task).

## Task-specific constraints
- Don't change anything under `src/Middleman/` in this milestone.
- Add `_decompiled/` and any probe build output to `.gitignore` if they aren't covered already.
- No traffic to live game services or Steam outside a normal co-op session that Josh starts himself.
- The probe sends only short test strings; no player data beyond the Steam display name.

## Stop and report if
- Any condition in the borrowed `AGENTS.md` "Stop and report".
- No option works on IL2CPP without adding a dependency: report what each one would need.
- An option needs Harmony patches on the game's own networking code: describe the patch and stop before writing it.

## Acceptance checks
- `RESEARCH.md` "Multiplayer" section: every finding labeled confirmed/unverified/wrong, each with evidence (file and
  line, proxy assembly type name, or library doc link). Confirmed ones stamped
  `[confirmed @ mp1-net-research | Schedule I=v0.4.7f12, MelonLoader=0.7.3]`.
- The probe builds for Mono and IL2CPP in Debug; paste the build summary lines.
- `docs/MP2-design.md` exists and covers every item in task 6.
- Josh's two-player probe test (he runs it; you only write the steps):
  1. Host and friend both put the probe DLL (matching runtime) in `Mods\`, and join the same co-op game.
  2. Friend presses F9 three times.
  3. Expect: host's `Latest.log` shows three pings with the friend's name; both logs show three pongs.
  4. Host presses F9. Expect: say in the steps what should happen (no-op or loopback).
  5. Send `MelonLoader\Latest.log` from both PCs, and say which branch (Mono/IL2CPP) each PC was on.

## Report
`docs/reports/MP1-net-research.md`, using the template named in "Read first". If
`E:\Sprinkler\S1-Overhead-Sprinkler\scripts\check-report.py` exists, run it on the report. Commit on
`mp1-net-research`, push the branch (this repo has a GitHub remote; never push to `main`), then reply with the path
and a 3-line summary.
