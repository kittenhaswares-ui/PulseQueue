# Validation report — 0.3.3.0 testing release

- Date: 2026-07-22
- Target: Dalamud API 15 / .NET 10
- Release channel: custom repository, testing-exclusive

## 0.3.3 contract under test

The one-shot smart buffer remains unchanged: one immutable captured intent can
produce at most one replay, its hard lifetime cap remains 180 ms, and it never
selects an alternative action or target. A newly observed input replaces the
older pending generation. Rejection, expiry, or any safety transition is
terminal; the token is never retried.

Native queue priority is intentionally stricter than one-shot readiness. Any
newer certified direct or certified action-only macro hotbar root preempts an
older exact PulseQueue-owned native queue entry before its own slot runs,
regardless of whether the new action is ready, inside the one-shot horizon, or
emits a nested `UseAction` call. This gives a newly pressed heal, Purify, or Guard
absolute control over an older owned Viper weave without introducing skill
priorities: physical root order alone decides. A foreign or changed queue cannot
satisfy the exact ownership proof and is never cleared. A macro root receives
this authority only after its unchanged slot passes the action-only static
allowlist.

Direct Turbo is a separate, explicit opt-in. It owns the physically certified
keyboard-bound standard-hotbar slot and its base command, not one permanently
adjusted action ID. A safe direct press may establish that owner even when its
initial slot execution emits zero `UseAction` calls. Each due pulse executes
exactly the same slot once, live-resolves its current combo/transformed action
ID, and revalidates the exact action tuple, target, context, readiness,
compatibility, and MOAction exclusion. This permits a legitimate Viper
transformation while still rejecting a changed slot or unrelated action.

A direct pulse may forward at most one matching normal-mode `Action` or
`PvPAction` call. An accepted or queued action creates an exact local-player
action-effect expectation tied to its type, requested/resolved ID, source
sequence, hold, and pulse. No later direct pulse may run until that
acknowledgement arrives. Local/server rejection or a two-second acknowledgement
timeout ends the hold without retry.

Macro Turbo has its own independent opt-in and owns only the same unchanged,
physically certified action-only macro slot. Static analysis permits one or
more `/ac`, `/action`, `/pvpac`, or `/pvpaction` commands plus icon/error
metadata. Zero-action macros, waits, chat, target mutation, markers, items,
gearsets, hotbar mutation, `/assist`, and unknown or otherwise non-allowlisted
commands fail closed. The player's original physical press remains vanilla.

Static `ActionCount` is a hard maximum for a synthetic macro epoch, not an exact
runtime transcript. Each same-slot epoch may observe zero through that maximum
number of Macro-mode calls. Every emitted call is independently re-resolved and
live-validated before native execution. At most the first action that native
execution accepts or queues may pass; every later macro tail in that epoch is
suppressed before the original `UseAction` function. An over-budget, ineligible,
stale, or otherwise unauthorized call is likewise suppressed and terminates
ownership.

An epoch with no accepted action is a local no-op, so a still-held key may run
the same slot again on the next bounded 60 ms cadence. One accepted macro action
creates the same exact action-effect barrier used by Direct Turbo. A rejection
or two-second acknowledgement timeout is terminal for the hold and is never an
action retry. FFXIV remains authoritative for authored line order and target
resolution; PulseQueue never writes, chooses, or substitutes a macro target.
Different authored fallback lines may therefore resolve different targets in
otherwise stable context.

Cast-time, area/ground-target, player-movement, and MOAction-owned actions remain
outside both Turbo paths. `PvPCombo`, items, mouse clicks, controller/cross-
hotbar input, and plugin-originated slot calls cannot establish ownership. Raw
key-up, any newer certified physical press, slot/macro/binding mutation,
target/context change, death, stun, forced movement, mounting, zoning, logout,
job/PvP change, plugin disable, compatibility change, or the 30-second hold cap
cancels ownership. A canceled hold never resumes without a fresh physical
press.

Cancellation has two explicit native-queue policies. Ordinary key release,
maximum hold expiry, replacement by a newer root, and a physical original that
runs normally but declines Turbo ownership preserve accepted vanilla queue
intent. Terminal safety events request an exact owned-queue clear. That request
survives a queue temporarily hidden by an outer hook and an outcome created only
after an in-flight physical original or asynchronous vanilla macro returns. It
can clear only the exact unchanged owned tuple; diagnostics count these safety
clears separately from newer-input replacements.

Owned queue drain authorization is two-phase. PulseQueue begins a
non-consuming lease for the exact visible tuple, invokes the nested native/hook
chain, and only then classifies the result as retained, consumed, or invalidated.
This prevents ReAction from destroying the ownership proof when it temporarily
hides an older queue, the newer native call returns rejected, and ReAction
restores the unchanged tuple. With the opposite hook order, the proven queue is
hidden during PulseQueue's call and the generation-bounded replacement request
remains armed for stable-frame reconciliation after restoration.

An accepted queue also retains a standalone semantic safety context after its
one-shot token or Turbo hold ends. The stable-frame watcher continues checking
player identity/state, target and resolver identity, territory/map/instance,
job/PvP context, frame continuity, and compatibility state, and can clear only that
exact unchanged owned tuple on a terminal change. The accepted native tuple,
not a later hotbar re-resolution, is authoritative: a Viper adjusted-ID
transformation after acceptance does not invalidate the stored queue owner.

These deferred guarantees require the plugin to remain loaded. Full unload can
clear a currently visible exact owner before hook disposal, but cannot observe
or clear an outer-hook restoration or asynchronous native outcome that appears
after disposal.

If cancellation races an asynchronous native macro executor under
`MacroLocked`, a bounded tombstone quarantine suppresses only that stale
synthetic epoch's later Macro-mode calls. Normal- and Queue-mode calls remain
native. The quarantine cannot restore ownership. Crossing two seconds reports a
diagnostic but never authorizes the stale executor; suppression remains sealed
while `MacroLocked` is true and clears only after native unlock or plugin
disposal.

## Timing and configuration

Turbo remains disabled by default. Version 0.3.3 changes its default initial
delay from 180 ms to 0 ms and its default interval from 80 ms to the enforced
minimum of 60 ms. Configuration schema 4 migrates only an exact schema-3
180/80 pair to 0/60. Customized timing pairs are preserved. Older-schema Macro
Turbo permission remains fail-closed/off, and an unknown future schema disables
both Turbo permissions in memory without rewriting the file.

The 0 ms initial delay does not bypass readiness, ownership, or acknowledgement
gates. It removes the previous artificial wait before the first eligible
same-slot pulse; native execution still occurs only after the complete live
proof passes.

## Compatibility boundary

The current fail-closed profile is:

- NoClippy 0.5.0.24 is supported and remains the sole animation-lock correction
  owner. PulseQueue reads the resulting client readiness and never applies a
  second animation-lock correction.
- ReAction 1.3.5.1 is supported only while Turbo Hotbars, Macro Queue, and Auto
  Target are off and Action Stacks is empty. ReAction Turbo and Macro Queue are
  competing execution owners and therefore suspend PulseQueue rather than run
  concurrently.
- ReAction Auto Dismount and Camera Relative Directionals may remain on. Inputs
  while mounted and every movement-affecting action, including action 29494,
  remain excluded or pass through without PulseQueue buffering/repetition.
- MOAction 4.10.1 retargeted action IDs are read through its published IPC and
  excluded so MOAction owns those action/target transformations end to end.
- Unknown versions, unreadable configuration, missing required integration
  data, unsafe settings, and topology/configuration changes fail closed and
  invalidate current ownership.

ReAction queue adjustments remain native-authoritative except for one exact
older queue entry previously certified as PulseQueue-owned. Any newer certified
direct or certified action-only macro root replaces it before its own slot
execution, without a readiness or observed-call prerequisite. Terminal safety
cancellation also exact-clears owned state, including deferred hidden/in-flight/
asynchronous outcomes. Drain ownership is finalized only after the nested hook
chain returns, so a rejected call that restores the identical ReAction-hidden
queue retains its proof. Foreign or changed queue state is never cleared.

## Live evidence that motivated 0.3.3

The short installed-0.3.2 test proves that Turbo did not deliver a pulse:

- **111 macro attempts** were observed. All 111 were rejected because the
  synchronous macro transcript was reported `Incomplete`; **zero macro Turbo
  pulses** occurred.
- **Three direct owners started and zero direct pulses occurred.**
- The tested Viper direct slot resolved from action 39183 to 39181. The 0.3.2
  immutable-adjusted-ID rule canceled it as `ResolvedActionChange` eight
  milliseconds after acknowledgement instead of retaining the same slot.
- Two other direct holds ended after **97 ms** and **118 ms**, both shorter than
  the old **180 ms** initial delay, so neither could reach its first pulse.

The hook and installation path were active: 0.3.2 saw certified physical slot
events and started direct owners. The failure was therefore not a missing DLL,
wrong API signature, or absent input hook. It came from three explicit design
assumptions now changed in 0.3.3: exact observed macro transcripts, immutable
direct adjusted IDs, and the 180 ms first-pulse delay.

NoClippy 0.5.0.24 was active during the relevant test. When ReAction was loaded
with Turbo Hotbars, Macro Queue, and Auto Dismount enabled, 0.3.2 suspended and
produced no PulseQueue events because that release treated those settings as
hard compatibility conflicts. After ReAction unloaded, the 111 `Incomplete`
macro rejections became visible. In 0.3.3, ReAction Turbo Hotbars and Macro
Queue remain deliberate blockers, while Auto Dismount and Camera Relative no
longer block the plugin because their mounted/movement paths are excluded.

### Historical evidence

The earlier 0.3.1 test produced 40 start attempts, 10 direct starts, 30 start
rejections, one confirmed pulse, seven original/buffer acknowledgements, and one
pulse acknowledgement. Twenty-three macro attempts used shared multi-action
macros 67–69, which the former single-action allowlist could not own. That
historical result motivated 0.3.2's same-macro-slot direction but did not
validate its exact-transcript implementation.

The earlier 0.3 Turbo test produced 44 apparent starts, one pulse, one matching
acknowledgement, and 18 active cancellations. Logical key-state gaps and
typematic callbacks repeatedly replaced the physical hold. That historical
evidence motivated raw key-up authority and held-repeat suppression; it is not
0.3.3 live proof.

An anonymized historical NoClippy sample contained 7,514 accepted actions, with
about 313 ms median, 420 ms 95th-percentile, and 512 ms maximum observed local
action-response time. NoClippy reported positive animation-lock savings on the
parsed actions, supporting the contract that NoClippy remains the only
animation-lock correction owner. Those values are contextual evidence, not a
claim that PulseQueue improves network RTT.

## 0.3.3 automated evidence

- The current dependency-free PulseQueue.Core suite passes: **137/137
  self-tests**.
- Current Core coverage includes the immutable one-shot contract, exact native
  queue ownership/replacement, newest-generation cancellation, Direct Turbo
  cadence and terminal acknowledgement behavior, macro `ActionCount` budgets,
  zero-through-maximum completion, at-most-one accepted outcome, tail/overflow
  rejection, timing normalization, schema migration, and fail-closed future
  configuration behavior. Runtime safety-contract verification additionally
  covers readiness-independent certified-root takeover, two-phase exact-drain
  leases and restoration outcomes, hidden-owner identity, deferred exact safety
  clearing, standalone accepted-queue context monitoring, and preserve
  semantics for release/physical-original Turbo decline.
- The API-15/.NET-10 plugin project currently compiles with **0 warnings and 0
  errors**.
- Release ZIP SHA-256:
  `a6975b1b3492987ba9d8b2450e31308c7c6fdefdcd3ece86e37b87f8a1da20ed`
- Source fingerprint:
  `f72b7553f861735a04cc2dfdcddc7303ff61f9824fa3aecb287360480aa153e9`

These automated checks authorize only a testing package. They do not prove
native hotbar execution, live target behavior, server acknowledgement,
cross-plugin hook order, or long-session safety.

## Inherited historical evidence

The 0.2 suite previously passed 65/65 dependency-free state-machine, timing,
native-outcome, ownership, and generation tests. Its coverage included the 180
ms one-shot cap, immutable action/target preservation, cancellation reasons,
no-retry rejection, RTT outlier/rebase behavior, adversarial traces, exact
native queue matching/ownership, next-charge timing, mounting cancellation, and
concurrent single-consume behavior.

The historical 0.2.0.0 release ZIP and source fingerprint were verified for
that artifact. Those hashes do not describe 0.3.3 and are intentionally omitted
from the current release fields above.

## Still required before promotion beyond testing

A human maintainer must run every case in `LIVE_TEST_MATRIX.md` on the current
FFXIV patch, starting in the Wolves' Den outside a competitive match. The
remaining gates include:

- Direct Turbo ownership after an initial zero-call press, exact same-slot
  execution, Viper/combo adjusted-ID evolution, maximum one native call per
  pulse, exact action-effect acknowledgement, rejection/timeout termination,
  key release, and newest-input takeover.
- Heal/Purify/Guard takeover of an older exact PulseQueue-owned Viper weave for
  ready, too-early, zero-`UseAction`, and action-only macro roots, including
  already-stunned Purify and `BeingMoved` Guard; foreign and changed queue plus
  unsafe-macro negative controls; both ReAction hook orders and rejected-call
  restoration of a hidden owned queue; deferred terminal safety clears across
  in-flight originals and asynchronous vanilla macros; ordinary-release and
  physical-original Turbo-decline preservation.
- Standalone accepted-queue safety after all pending/hold state has ended,
  including target/resolver, player, world, job/PvP, compatibility, and terminal
  player-state changes; accepted native-tuple authority across a Viper adjusted-
  ID transformation; and the documented no-guarantee boundary for hidden or
  asynchronous outcomes that appear only after full unload.
- Macro Turbo single- and multi-action slots with zero through static
  `ActionCount` observed calls, zero-outcome 60 ms cadence, maximum one
  accepted/queued outcome, tail and over-budget suppression before native
  execution, authored target behavior, acknowledgement, and terminal rejection.
- Cast, ground/area, movement, item, mouse, controller, cross-hotbar,
  plugin-originated, unsafe-macro, MOAction-owned, and `PvPCombo` exclusions.
- Synchronous and asynchronous `MacroLocked` epochs, cancellation quarantine,
  mode isolation, diagnostic-timeout plus unlock/disposal clear boundaries, and
  no stale post-cancel call.
- NoClippy alone and with the supported ReAction profile; every ReAction blocker;
  Auto Dismount/Camera Relative pass-through; topology/configuration changes;
  death, stun, forced movement, mounting, zoning, target/context change, plugin
  disable/unload, 30-second holds, frame stalls, and the two-hour soak.

Until those native gates pass, version 0.3.3.0 must remain testing-exclusive
and must not be described as production-validated, sanctioned, undetectable, or
account-safe.
