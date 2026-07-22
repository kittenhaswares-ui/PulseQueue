# PulseQueue

PulseQueue is a conservative smart input buffer for Final Fantasy XIV. If a
direct hotbar action is pressed a little too early and vanilla FFXIV rejects it
only because of a short GCD/local recast or animation lock, PulseQueue can
retain that exact action briefly and submit it once when the client reports it
ready. This includes the intended PvP Guard cooldown-edge use case.

Version 0.3.1 also contains an optional exact-action Turbo mode for a tightly
limited keyboard testing scope. It is disabled by default. Unlike the one-shot
buffer, holding an eligible key may intentionally execute its one captured
action multiple times over the lifetime of that hold. The slot itself is never
synthetically rerun. Macro slots require a second, separate opt-in and a strict
single-action safety analysis.

This is an open testing release, not an official Dalamud plugin.

## One-shot buffer safety contract

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
  automatic retry, or retry after a server rejection. The one-shot buffer does
  not generate key repeat; the separate opt-in Turbo source is described below.
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

## Native Turbo safety contract

- Turbo is opt-in and defaults off. Its default initial delay is 180 ms, its
  default repeat interval is 80 ms, and out-of-combat operation defaults off.
- Only physical keyboard bindings that resolve to a standard hotbar slot can
  become an owner. The Turbo testing scope accepts direct `Action` slots whose
  single observed invocation is an `Action` or `PvPAction`. `PvPCombo` slots
  remain excluded until their route identity can be proven end to end.
- Macro Turbo has its own explicit opt-in, also defaulting off. It can repeat
  from an exact keyboard-bound standard-hotbar `Macro` slot containing one
  `/ac`, `/action`, `/pvpac`, or `/pvpaction` command. Icon/error metadata and
  at most one `/assist` before the action are allowed. Enabling native Turbo
  alone does not enable Macro Turbo.
- Wait directives, a second action, chat, target, marker, item, gearset, hotbar,
  and unknown commands reject the macro. The physical press runs the verified
  macro once; later pulses repeat only the exact observed native action tuple
  and target. They never replay the macro, its `/assist`, or metadata lines.
- Items, mouse clicks, controller and cross-hotbar input, every
  cast-time/ground-target/player-movement direct action, and calls originating
  from another plugin never start native Turbo ownership.
- A held eligible slot may execute its captured action multiple times. Every
  repeat invokes exactly one immutable action/target tuple; PulseQueue never
  reruns the slot, follows a later combo transformation, chooses another skill
  or target, or creates a FIFO rotation.
- Releasing the exact owning key/chord cancels it. A newer certified physical
  edge preempts an older hold before the newer slot executes. Any newer native
  hotbar/action invocation also cancels the old hold even when the new input
  cannot become a Turbo owner; the older key never resumes automatically.
- After the original press, a one-shot replay, or a Turbo pulse is sent, the
  next pulse is blocked until a local-player action effect matches its exact
  action type, requested/resolved ID, and immediate or queue-relative source
  sequence. A locally rejected or unprovable send, or a missing
  acknowledgement, ends the hold without retry.
- Every hold has a hard 30-second lifetime and then requires a fresh physical
  release/press, even if the key remains down.
- Death, stun, knockback or forced movement, zoning, logout, job/PvP-context
  change, plugin disable, unsafe compatibility state, and the existing
  fail-closed safety transitions cancel the active hold.
- ReAction Turbo Hotbars and Macro Queue must remain off. PulseQueue will not
  run competing repeat owners or accept rewritten macro queue provenance.
- NoClippy remains the only animation-lock correction owner. Native Turbo and
  the one-shot buffer read the resulting client readiness and never apply a
  second lock correction.

## Supported testing scope

The one-shot buffer accepts only instant, non-ground-target `Action` and `PvPAction`
attempts reached through the standard or cross hotbar with the normal action
mode. Macros, items, casts, combo-mode calls, ground placement, mounts, pets,
duty actions, crafting, player-movement actions, and direct calls from other
plugins are excluded.

That one-shot path continues to observe keyboard, mouse, and controller paths
that run through FFXIV's standard slot executor. The optional native Turbo path
is narrower: keyboard-bound standard hotbars and direct `Action` slots with one
exactly correlated `Action`/`PvPAction` invocation only, plus exact `Macro`
slots when the separate Macro Turbo opt-in is enabled. `PvPCombo`, items,
mouse, controller, cross-hotbar input, and arbitrary macros cannot own Turbo. A
safe action-macro pulse repeats only the immutable native action tuple observed
from the original physical execution.

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
| ReAction 1.3.5.1 | Supported only with **Turbo Hotbars**, **Macro Queue**, **Auto Target**, **Auto Dismount**, and **Camera Relative Directionals off**, plus an empty **Action Stacks** list. ReAction Turbo and Macro Queue must remain off even when PulseQueue native Turbo is enabled, so only one repeat source and one provable macro execution mode exist. ReAction queue adjustments remain authoritative except for the exact older certified queue entry that a newer valid hotbar input explicitly replaces. |
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
PulseQueue's native Turbo instead starts from its own restricted physical
keyboard ownership boundary and therefore does not make ReAction Turbo safe to
enable alongside it.
ReAction Macro Queue must also remain disabled because it rewrites macro action
queue mode. That removes the exact native provenance required to distinguish a
PulseQueue-owned macro execution from a foreign or queued action.
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
/pulsequeue turbo on
/pulsequeue turbo off
/pulsequeue turbo macros on
/pulsequeue turbo macros off
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

This implementation and its review were substantially AI-assisted. The
repository publishes the complete source, deterministic tests, build scripts,
release fingerprint, and mandatory live-test matrix so a human maintainer can
audit and validate every native interaction. No claim of complete human
in-game validation is made for version 0.3.1.0.

The compatibility design was checked against the exact upstream implementations
used for this testing profile: [NoClippy 0.5.0.24 animation-lock handling](https://github.com/UnknownX7/NoClippy/blob/3f4b37739bbdccd1833b042018b01be84a7d382b/Modules/AnimationLock.cs)
and [ReAction 1.3.5.1 Turbo Hotbars](https://github.com/UnknownX7/ReAction/blob/9436e0c72c569b5518c67bda2e29f44822c68ea0/Modules/TurboHotbars.cs).
PulseQueue's implementation is clean-room and uses the public API-15 client
structures; no source was copied from either project.
The exact acknowledgement mapping is checked against the locally pinned
FFXIVClientStructs commit: [`ActionEffectHandler.Header`](https://github.com/aers/FFXIVClientStructs/blob/0ce3f0220901a7c9f16d3fec526558e7829ca3b3/FFXIVClientStructs/FFXIV/Client/Game/Character/ActionEffectHandler.cs)
provides type/ID/sequence, while [`CastInfo.ResponseActionType` and
`ResponseActionId`](https://github.com/aers/FFXIVClientStructs/blob/0ce3f0220901a7c9f16d3fec526558e7829ca3b3/FFXIVClientStructs/FFXIV/Client/Game/Character/CastInfo.cs)
use the native `ActionType` and action-ID domains matched by PulseQueue.

## Validation status

The dependency-free state machines and runtime safety helpers have deterministic
invariant coverage, including a seeded adversarial trace, exact native-queue
classification, newest-generation replacement, mounted cancellation, a
concurrent consume race, and terminal rejection of a Turbo pulse. The packaged
0.3.1 artifact is deliberately testing-exclusive; physical ownership, key-up,
exact-action cadence, acknowledgement, and every Turbo transition remain explicit
live-test gates rather than claimed results.
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
