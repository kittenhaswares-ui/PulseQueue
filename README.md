# PulseQueue

PulseQueue is a conservative one-shot smart input buffer for Final Fantasy XIV.
If a direct hotbar action is pressed a little too early and vanilla FFXIV rejects
it only because of a short GCD/local recast or animation lock, PulseQueue can
retain that exact action briefly and submit it once when the client reports it
ready. This includes the intended PvP Guard cooldown-edge use case.

This is an open testing release, not an official Dalamud plugin.

## Safety contract

- One captured hotbar intent can produce at most one replay.
- Vanilla FFXIV always receives the original press first. An action the game
  accepts or queues is never buffered again.
- The action type, base and adjusted action IDs, target, extra parameter, combo
  route, hard target, and soft target are immutable while pending. Mouseover
  resolver targets are also bound when an action arrives without a concrete
  target ID and can target something other than self.
- Every newly observed standard/cross-hotbar input invalidates the old pending
  generation before the new input runs. The newest observed input therefore
  wins; PulseQueue does not maintain a FIFO weave backlog or a skill-priority
  list.
- There is no alternative-action selection, target selection, target fallback,
  key repeat, automatic retry, or retry after a server rejection.
- The token is consumed before one queue-mode call. Its result is never fed
  back into another attempt, so a failed replay is terminal.
- The hard lifetime cap is 180 ms. Latency detection may shorten that window but
  can never extend it.
- Death, stun, forced movement, mounting, target change, action transformation,
  native queue activity, logout, job/PvP context change, instance or territory
  change, a frame stall, plugin disable, or any uncertain state clears the
  token. Actions that move the player are never buffered.
- PulseQueue never writes animation lock, cooldowns, resources, targets, or any
  native queue identity field. Its one narrow exception is clearing the queued
  flag when the complete entry and unchanged action sequence prove that the
  entry came from an older certified hotbar input and the newer eligible action
  is ready or inside the learned hold window. Foreign queues are never cleared.

## Supported testing scope

Version 0.2 accepts only instant, non-ground-target `Action` and `PvPAction`
attempts reached through the standard or cross hotbar with the normal action
mode. Macros, items, casts, combo-mode calls, ground placement, mounts, pets,
duty actions, crafting, player-movement actions, and direct calls from other
plugins are excluded.

The hotbar scope supports keyboard, mouse, and controller paths that run through
FFXIV's standard slot executor. It cannot prove that an invocation was a
physical electrical key event, so the strict guarantee is one certified hotbar
intent, not one hardware event.

## Adaptive timing

PulseQueue does not ping a server and does not inspect packet opcodes. It matches
the local action sequence with the later local-player action effect and keeps a
small in-memory rolling estimate of effective action-response time. That value
only selects a hold window between the conservative floor and the 180 ms cap;
actual client readiness is still mandatory before replay.

Timing samples are session-only and never leave the computer.

## Compatibility contract

Compatibility is deliberately version- and configuration-specific:

| Plugin | Supported contract |
|---|---|
| NoClippy 0.5.0.24 | Supported. NoClippy remains the sole animation-lock correction owner; PulseQueue observes the resulting readiness and never writes animation-lock state. |
| ReAction 1.3.5.1 | Supported only with **Turbo Hotbars**, **Auto Target**, **Auto Dismount**, and **Camera Relative Directionals off**, plus an empty **Action Stacks** list. ReAction queue adjustments remain authoritative except for the exact older certified queue entry that a newer valid hotbar input explicitly replaces. |
| MOAction 4.10.1 | Supported through its published retargeted-action IPC. Reported retargeted action IDs bypass PulseQueue and continue through MOAction normally. |

Unknown versions, inaccessible configuration, a missing required integration,
or an unsafe ReAction setting suspends buffering and clears the pending token.
Plugin load/unload and relevant configuration changes also clear the token and
require a clean framework frame before capture resumes.

ReAction Turbo Hotbars must be disabled because its held-key calls are
synthetic hotbar invocations with no public provenance marker. At PulseQueue's
hook boundary they cannot be distinguished safely from a newer physical press;
leaving Turbo enabled could let an older held weave displace a newly pressed
heal, Purify, or Guard. PulseQueue will not guess using action priorities.
Auto Dismount is also disabled in the supported profile because ReAction stores
an action and invokes it later after dismounting. Camera Relative Directionals
is disabled because a delayed replay cannot safely preserve its original camera
direction across every hook load order. Camera-relative movement actions,
including ReAction's explicit action 29494 exception, are never buffered.

PulseQueue compares the complete native queue tuple before and after the
original press; only a new exact matching queue entry is credited to that
certified hotbar generation. A later eligible certified hotbar input that is
ready or inside the current hold window may clear that one unchanged owned
entry so the newest manual input gets its native attempt. Foreign, changed,
MOAction-owned, or unproven queue state blocks custom capture and is never
modified.

## Install

After a testing release has been published, add this URL under **Dalamud
Settings -> Experimental -> Custom Plugin Repositories**:

```text
https://raw.githubusercontent.com/kittenhaswares-ui/PulseQueue/main/repo.json
```

Enable testing plugins in Dalamud, then install **PulseQueue**.

Use `/pulsequeue` to open the status window. Useful commands:

```text
/pulsequeue
/pulsequeue on
/pulsequeue off
/pulsequeue status
/pulsequeue dry on
/pulsequeue dry off
/pulsequeue log on
/pulsequeue reset
```

## Important account-risk notice

Square Enix prohibits third-party tools and unauthorized gameplay-modifying
software. A bounded, direct-input design can reduce technical mistakes, but it
cannot make plugin use sanctioned, undetectable, or account-safe. PulseQueue is
published as transparent source and a testing-only custom-repository build; use
is at the user's own risk.

The official Dalamud repository also restricts combat automation and PvP
advantages, so this project does not claim eligibility for that repository.

## Development disclosure

This initial implementation and its review were substantially AI-assisted. The
repository publishes the complete source, deterministic tests, build scripts,
release fingerprint, and mandatory live-test matrix so a human maintainer can
audit and validate every native interaction. No claim of complete human
in-game validation is made for version 0.2.0.0.

## Validation status

The dependency-free state machine and runtime safety helpers are covered by 65
deterministic invariant tests, including a seeded 10,000-step adversarial trace,
exact native-queue classification, newest-generation replacement, mounted
cancellation, and a concurrent consume race.
The native integration must additionally pass the live matrix in
[`docs/LIVE_TEST_MATRIX.md`](docs/LIVE_TEST_MATRIX.md) on the current FFXIV
patch before this testing flag can be removed. Until that evidence exists, do
not describe the plugin as production-validated. The exact automated results
and release hash are recorded in
[`docs/VALIDATION_REPORT.md`](docs/VALIDATION_REPORT.md).
The published ZIP checksum is also stored beside it as
[`dist/latest.zip.sha256`](dist/latest.zip.sha256).
Release changes are summarized in [`CHANGELOG.md`](CHANGELOG.md).

## Development

The project targets Dalamud API 15 and .NET 10.

```powershell
dotnet run --project tests/PulseQueue.Core.SelfTest -c Release
dotnet restore src/PulseQueue.Plugin/PulseQueue.Plugin.csproj --use-lock-file
dotnet build src/PulseQueue.Plugin/PulseQueue.Plugin.csproj -c Release --no-restore
./scripts/Verify-SafetyContract.ps1
./scripts/Build-Release.ps1
./scripts/Verify-Release.ps1
```

## License

MIT. The implementation is original and does not copy code from NoClippy or
ReAction.
