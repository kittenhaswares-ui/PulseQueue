# PulseQueue

PulseQueue is a conservative smart input buffer for Final Fantasy XIV. If a
direct hotbar action is pressed a little too early and vanilla FFXIV rejects it
only because of a short GCD/local recast or animation lock, PulseQueue can
retain that exact action briefly and submit it once when the client reports it
ready. This includes the intended PvP Guard cooldown-edge use case.

Version 0.3.3 also contains two optional Turbo modes for a tightly limited
keyboard testing scope. Both are disabled by default. Direct Turbo repeats only
the same physically certified standard-hotbar slot, resolving its current
combo/transformed action again on every pulse. The separately enabled Macro
Turbo repeats the same certified action-only macro slot; FFXIV's native macro
executor, not PulseQueue, evaluates its authored lines and targets. Neither
mode changes the exact, at-most-once one-shot buffer contract.

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
  native queue identity field. Its sole native-queue mutation is clearing the
  queued flag after the complete tuple and unchanged action sequence prove exact
  PulseQueue ownership. Any newer certified direct or certified action-only
  macro hotbar root preempts that older owned entry before its own slot runs,
  regardless of the new action's readiness, current stun/forced-movement state,
  or whether the slot emits `UseAction`. That takeover is input priority only:
  an unsafe new snapshot starts no buffer/Turbo and its vanilla outcome is not
  claimed by PulseQueue. While the plugin remains loaded, terminal safety
  cancellation also exact-clears owned queue state, including an entry
  temporarily hidden by an outer hook or produced later by an in-flight or
  asynchronous original call. Accepted queue ownership keeps its own semantic
  safety snapshot and remains watched even after the pending token or Turbo
  hold has ended. On full unload, only a currently visible exact owned queue
  can be cleared before hooks are disposed. A queue still hidden by an outer
  hook, or created/restored asynchronously after disposal, is outside
  PulseQueue's control. Foreign or changed queues are never cleared.

## Native Turbo safety contract

- Turbo is opt-in and defaults off. Its default initial delay is 0 ms, its
  default repeat interval is 60 ms, and out-of-combat operation defaults off.
- Configuration schema 4 changes only the exact schema-3 legacy default pair
  (180 ms/80 ms) to 0 ms/60 ms. Any customized timing values are preserved.
- Only physical keyboard bindings that resolve to a standard hotbar slot can
  become an owner. The Turbo testing scope accepts certified direct `Action`
  slots whose native calls are `Action` or `PvPAction`. A safe direct press can
  arm its slot even when that first execution emits no `UseAction` call because
  the action was still too early. `PvPCombo` slots remain excluded until their
  route identity can be proven end to end.
- Macro Turbo has its own explicit opt-in, also defaulting off. It accepts an
  exact keyboard-bound standard-hotbar `Macro` slot containing one or more
  `/ac`, `/action`, `/pvpac`, or `/pvpaction` commands plus only icon/error
  metadata. Enabling native Turbo alone does not enable Macro Turbo.
- Newest-input takeover of an older exact PulseQueue-owned queue is independent
  of Turbo and Macro Turbo opt-in. A certified action-only macro press can
  therefore give its vanilla Recuperate, Purify, or Guard line priority without
  enabling repeated macro execution.
- A zero-action macro, `/assist`, waits, chat, target-changing commands, items,
  markers, gearsets, hotbar mutation, and unknown or otherwise non-metadata
  commands reject Macro Turbo ownership. The original physical press remains
  vanilla. Each later pulse invokes that same certified macro slot exactly
  once; it does not invoke a captured action tuple.
- FFXIV evaluates the allowed action lines in their authored order and resolves
  their authored targets. PulseQueue does not select a line, skill, fallback,
  or target and does not rewrite the macro. Consequently, an eligible
  multi-action macro can produce a different action than its previous pulse
  when FFXIV's own native macro rules choose a different authored line. This is
  Macro Turbo slot repetition, not the direct-action exact-tuple contract.
- Static analysis supplies a maximum `ActionCount`, not an expected runtime
  transcript. During each synthetic macro epoch, FFXIV may emit zero through
  that maximum number of authored Macro-mode calls. Every emitted call is
  checked live before native execution. At most the first native call that is
  accepted or queued may pass; any later macro tail in that epoch is suppressed
  before the original function. Exceeding the maximum cancels ownership.
- A macro epoch with no accepted action is a local no-op; while the key remains
  held, the next bounded cadence may execute the same slot again. Once an action
  is accepted, its exact local-player action effect must arrive before another
  pulse. A server rejection or two-second acknowledgement timeout is terminal
  for that hold and is never retried.
- Items, mouse clicks, controller and cross-hotbar input, every cast-time,
  area/ground-target, or player-movement action, MOAction-owned action IDs, and
  calls originating from another plugin never start or continue native Turbo
  ownership. These exclusions apply to every emitted Macro Turbo action call as
  well as to direct actions.
- A held eligible direct-action slot may execute multiple times. Every pulse
  reruns exactly that certified slot once and permits at most one matching
  native `UseAction` call. The slot's requested command stays fixed, while its
  current adjusted/combo action ID may evolve (for example, Viper transformations)
  only after the new exact action tuple, target, and safety context pass every
  live check. PulseQueue never chooses another slot, skill, or target and never
  creates a FIFO rotation.
- Releasing the exact owning key/chord cancels it. A newer certified physical
  edge preempts an older hold before the newer slot executes. Any newer native
  hotbar/action invocation also cancels the old hold even when the new input
  cannot become a Turbo owner; the older key never resumes automatically.
- A newer certified direct or certified action-only macro hotbar root also
  clears an older exact PulseQueue-owned native queue entry unconditionally.
  This newest-input takeover is readiness-independent and does not require the
  new slot to produce a `UseAction` call, so a newly pressed heal, Purify, or
  Guard cannot be displaced by an older owned weave. It never applies to a
  foreign or changed queue, and an unsafe/non-action-only macro never receives
  this authority.
- After a direct-action original, one-shot replay, direct Turbo pulse, or
  accepted Macro Turbo action is sent, the applicable next pulse is blocked
  until a local-player action effect matches its exact action type, requested/
  resolved ID, and source sequence. A local rejection or two-second timeout
  ends that hold. Macro Turbo never converts an observed macro result into a
  one-shot replay or action retry.
- Every hold has a hard 30-second lifetime and then requires a fresh physical
  release/press, even if the key remains down.
- Key-up, any newer certified physical press, target/context change, death,
  stun, knockback or forced movement, zoning, logout, job/PvP-context change,
  plugin disable, unsafe compatibility state, and the existing fail-closed
  safety transitions cancel either kind of active hold. A canceled owner never
  resumes without a fresh physical press.
- Ordinary key release, the 30-second hold limit, replacement by a newer root,
  and a physical original that runs normally but does not qualify for Turbo
  preserve any accepted vanilla queue intent. Terminal safety events instead
  request an exact owned-queue clear and retain that request across hidden,
  in-flight, or asynchronous outcomes until it can be reconciled safely.
- A synthetic macro-slot epoch may observe no more than the statically bounded
  number of authored action calls. An ineligible, over-budget, stale, or later
  call after an accepted/queued outcome is suppressed before native action
  execution; the player's original physical macro path is never suppressed by
  this rule.
- If a synthetic slot remains asynchronous under native `MacroLocked` when its
  owner is canceled or provenance fails, PulseQueue quarantines that one
  synthetic executor epoch. Only its later Macro-mode action calls are
  suppressed. Normal- and Queue-mode calls remain native. Two seconds is a
  diagnostic deadline, not an authorization boundary: suppression stays sealed
  while `MacroLocked` remains true and clears only after native unlock or plugin
  disposal.
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
is narrower: keyboard-bound standard hotbars and direct `Action` slots with at
most one authorized `Action`/`PvPAction` call per pulse, plus statically verified
action-only `Macro` slots when the separate Macro Turbo opt-in is enabled.
`PvPCombo`, items, mouse, controller, cross-hotbar input, and macros containing
resolver/state-changing or unknown commands cannot own Turbo. A direct pulse
reruns only the certified standard-hotbar slot and live-resolves its current
transformed action; a Macro Turbo pulse repeats only the certified macro slot
and lets FFXIV evaluate its authored lines. The macro's static `ActionCount` is
a hard per-epoch maximum. Zero through that many calls are allowed, but every
call must be live-eligible and only one accepted/queued native outcome may pass
in an epoch.

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
| ReAction 1.3.5.1 | Supported when **Turbo Hotbars**, **Macro Queue**, and **Auto Target** are off and **Action Stacks** is empty. **Auto Dismount** and **Camera Relative Directionals** may remain on: mounted inputs and movement-affecting actions are excluded or passed through rather than buffered/repeated. ReAction Turbo and Macro Queue must remain off so only one repeat source and one macro execution owner exist. |
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
queue mode and would create a second macro execution owner. ReAction Auto
Dismount and Camera Relative Directionals do not suspend PulseQueue in 0.3.3.
Inputs while mounted and every movement-affecting action, including ReAction's
explicit action 29494 exception, remain outside PulseQueue ownership and pass
through without buffering or Turbo repetition.

PulseQueue compares the complete native queue tuple before and after the
original press; only a new exact matching queue entry is credited to that
certified hotbar generation. Any later certified direct or certified action-only
macro hotbar root takes priority over that one unchanged owned entry,
independent of its readiness or nested `UseAction` result, so the newest manual
heal, Purify, or Guard has absolute control over an older owned weave. A visible
entry is cleared before the new slot runs. The drain is two-phase: PulseQueue
does not consume its proof before the nested native/ReAction call finishes. If
ReAction temporarily hides the old entry and restores it after the new call is
rejected, the unchanged proof is retained and the replacement clear is retried.
With the opposite hook order, the old entry stays hidden during the new slot and
the stable-frame watcher clears it after restoration.

Every accepted owned entry also carries a standalone safety snapshot of the
certified player, input, target/resolver, territory/map/instance, job/PvP, and
compatibility context. The stable-frame watcher keeps checking that snapshot
even when no one-shot token or Turbo hold remains, and a terminal semantic
change can clear only its exact unchanged queue tuple. Once native acceptance
has created that tuple, it is authoritative: a later Viper adjusted/combo-ID
transformation does not re-resolve or invalidate the accepted entry. Actual
tuple/sequence changes relinquish the proof instead. Ordinary release and a
physical original that merely declines Turbo preserve accepted vanilla intent;
foreign, changed, MOAction-owned, or unproven queue state is never modified.

These deferred guarantees require PulseQueue to remain loaded. During full
unload it can synchronously clear a currently visible exact owner, but after its
hooks are disposed it cannot observe a queue that an outer hook restores or an
asynchronous native macro creates later.

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
in-game validation is made for version 0.3.3.0.

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

The 0.3.2 live log proves why its Turbo path felt inactive: 111 macro attempts
were all rejected as `Incomplete`, producing zero macro pulses; three direct
owners started but produced zero pulses. One Viper hold was canceled when its
adjusted action ID changed, and two other holds lasted only 97 ms and 118 ms,
shorter than the old 180 ms initial delay. Version 0.3.3 replaces those exact-
transcript and immutable-adjusted-ID assumptions with bounded same-slot
execution and changes the default cadence to 0/60 ms. It remains deliberately
testing-exclusive; physical ownership, key-up, same-slot cadence,
acknowledgement, and every Turbo transition remain explicit live-test gates
rather than claimed results.
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
