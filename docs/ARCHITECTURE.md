# Architecture

## Trust boundary

PulseQueue treats the game client as authoritative. It observes a standard
hotbar-slot execution scope and the nested `ActionManager.UseAction` call, but
always lets the original call run first. It snapshots the complete native queue
tuple before and after that call: queued state, action type and ID, target,
parameter, mode, and combo route. Native acceptance, a sequence advance,
area-target activation, a matching new queue entry, or foreign/pre-existing
queue activity prevents custom capture.

The plugin never edits animation-lock, cooldown, target, resource, or native
queue identity memory. The one-shot buffer has two bounded game mutations: one
call to the original `UseAction` function for a consumed token, and one
`ActionQueued=false` write when an ownership proof matches the complete queue
tuple, its unchanged sequence marker, and an older certified hotbar generation.
A foreign or changed queue can never satisfy that proof. The write is authorized
only to let a newer certified direct or certified action-only macro root
supersede an older owned queue, or to exact-clear owned state after terminal
safety cancellation.
Direct Turbo adds one bounded operation: one execution of the exact same
physically certified standard-hotbar slot for each current, revalidated pulse
token. That execution may forward at most one matching native action call.
Macro Turbo uses the same bounded same-slot operation for a certified action-only
macro. It never turns a macro line into a PulseQueue-selected action call.

Native Turbo is a separate, opt-in input source rather than an extension of a
one-shot token. Its trust boundary begins with a physical keyboard binding to a
standard hotbar slot. A safe direct `Action` slot may become a repeat owner even
when its original press emits no `UseAction` call; every later slot pulse may
forward at most one matching `Action` or `PvPAction` call. A `Macro`
slot can become an owner only through the second, separately persisted Macro
Turbo opt-in and remains identified by its hotbar location, macro identity,
content fingerprint, and physical key/chord. `PvPCombo`, items, mouse clicks,
controller/cross-hotbar input, and plugin-originated slot calls cannot establish
ownership.

A Macro Turbo candidate must pass a fail-closed static analysis: one or more
`/ac`, `/action`, `/pvpac`, or `/pvpaction` commands and only optional icon/error
metadata. Zero-action macros, `/assist`, waits, chat, explicit target mutation,
markers, items, gearsets, hotbar mutation, unknown commands, and every other
non-allowlisted command are rejected. The physical press always runs through
FFXIV normally. A later pulse executes that same certified macro slot once;
FFXIV evaluates its authored action lines in order and resolves their authored
targets. PulseQueue neither chooses nor rewrites a line, action, fallback, or
target. Configuration migration always resets this extra permission to off
rather than inferring consent from the ordinary Turbo setting.

Static analysis supplies the macro's maximum `ActionCount`; it is a safety
budget, not an expected native transcript. A certified physical macro press may
emit zero through that maximum number of calls and still establish same-slot
ownership. Every synthetic epoch gets a fresh budget. Each emitted Macro-mode
`Action` or `PvPAction` call must independently pass live resolution, cast,
area/ground, movement, target, context, compatibility, and MOAction checks
before the original function may run. An `(N+1)`th call is suppressed and
terminates ownership.

An epoch may forward at most one call that native execution accepts or queues.
Once that outcome is observed, the epoch is closed and every later macro tail
call is suppressed before native execution. Zero accepted calls are a valid
local no-op and do not create an acknowledgement expectation. One accepted
outcome creates an exact action-effect expectation; server rejection or a
two-second timeout terminates the hold without retry. FFXIV still chooses which
authored line and target resolves; PulseQueue never substitutes either.

A direct Turbo owner stores the exact slot/base command and physical key/chord
identity. Each pulse reruns that slot once. The current adjusted/combo action ID
may change while held, but the new exact tuple, target, context, and profile must
all pass fresh checks before its single native call can run. A Macro Turbo owner
stores the exact certified slot, macro identity/content, physical key/chord, and
safety context instead of a selected action tuple. It may execute only that
slot. Raw key release, a newer certified physical edge, macro/binding mutation,
or a target or context change invalidates either owner before another pulse.
Any newer native hotbar/action
invocation also cancels the old owner, even when that new input is ineligible
to become an owner. The older owner cannot resume afterward. Direct Turbo is
same-direct-slot repetition; Macro Turbo is explicit same-slot repetition whose
line and target result is owned by FFXIV's macro executor. Neither path is a
PulseQueue FIFO or priority selector.

## State flow

```text
Off/Suspended/Faulted
          |
          v
        Idle -- eligible temporal rejection --> Pending
          ^                                    /  |  \
          |                     cancellation --   |   -- expiry
          |                                        |
          +------ consume before call <--- Ready --+
```

Cancellation is evaluated before readiness on every frame. The core engine has
no game dependencies and returns a dispatch command at most once per intent.
The runtime consumes that command and performs one queue-mode native call.
Its result is recorded only for diagnostics and is never fed back into a retry.

The independent Turbo lifecycle is:

```text
Disabled/Unsafe
       |
       v
     Idle -- eligible physical key down --> InitialDelay --> Repeating
       ^                                      |               |
       +---- key up / newer physical press / safety cancel ---+
```

Macro Turbo inserts a static same-slot certification before `InitialDelay` can
produce a slot pulse:

```text
Certified root macro press
          |
          v
Static allowlist + ActionCount maximum + unchanged slot/content
          |
          v
Same certified slot may pulse (fresh 0..N budget per epoch)
          |
          v
0 accepted = no-op | 1 accepted = exact acknowledgement barrier
```

No later or unrelated `MacroLocked` transition can be adopted as provenance.

The initial delay is normalized to 0–1000 ms and the repeat interval to
60–1000 ms. Defaults are 0 ms and 60 ms respectively. Out-of-combat repeat is
off by default. Every scheduled invocation must still pass the current safety
and compatibility gates; a hold is canceled rather than paused across an
unsafe transition. Holds expire after 30 seconds. The direct-action original,
any one-shot replay, and every direct Turbo pulse establish an exact
acknowledgement barrier: no later direct pulse is eligible until a local-player
action effect matches the action type, requested/resolved action ID, and
immediate source sequence. If vanilla creates an exact newly owned native queue
instead, the same action identity plus a wrap-safe newer local source sequence
than the pre-pulse baseline is required. The expectation also retains the
current hold/pulse token so cancellation cannot acknowledge a later owner.
Macro Turbo never feeds a macro-selected result into the one-shot engine or
converts it into an action-level retry; its authority remains the current
physical hold and same-slot pulse token. An accepted macro action uses the same
exact local action-effect acknowledgement barrier; rejection or a two-second
timeout ends the hold without retry.

Every newly observed standard/cross-hotbar invocation advances the input
generation and cancels the pending token before its native call executes. This
is a replacement register, not a FIFO: a heal, Purify, or Guard pressed after a
weave becomes the only candidate PulseQueue may retain. PulseQueue does not
assign skills priorities or choose an alternative action.

When an earlier certified hotbar generation created the exact current native
queue entry, any newer certified direct or certified action-only macro hotbar
root supersedes that ownership. When the entry is visible, PulseQueue begins a
non-consuming exact-drain lease before its own original slot call. The new slot
need not be ready, inside the one-shot horizon, or emit `UseAction`:
physical certification plus direct-slot identity, or full action-only macro
certification, is the takeover boundary. The lease is completed only after the
nested native/hook chain returns. An unchanged tuple and sequence retain the
proof, an empty queue consumes it, and a visible changed tuple invalidates it.
Competing drains cannot acquire the same proof while the lease is active.

This two-phase rule covers ReAction's queue hide/restore behavior. If ReAction
is inside PulseQueue's hook, a rejected nested call may restore the exact old
tuple before lease completion; PulseQueue retains the proof and applies the
newer-input exact clear. With the opposite hook order, PulseQueue sees the
proven entry temporarily hidden, lets the new slot run while that old entry is
unavailable, and leaves the generation-bounded replacement request armed. The
stable-frame watcher clears the exact tuple if ReAction restores it. A changed
tuple/sequence instead relinquishes ownership, so coincidentally similar,
foreign, and plugin-created queues remain untouched. A macro slot that fails the
action-only static allowlist never receives this preemption authority.

Terminal safety cancellation uses a separate exact-clear tombstone. It can be
raised before an in-flight physical original claims its outcome and remains
pending across a temporarily hidden queue or later asynchronous vanilla macro
line. Once the complete owned tuple becomes visible it clears exactly that
entry. Ordinary key release, the maximum hold lifetime, replacement by a newer
root, and a physical original that remains vanilla but fails Turbo certification
use preserve semantics: accepted vanilla queue intent is not retroactively
removed. The diagnostics count newer-input replacements separately from owned
queues cleared by terminal safety cancellation.

Accepted native-queue ownership has a standalone semantic safety context; it
does not depend on a pending one-shot token, active Turbo runtime, or held key
remaining alive. The context binds the certified generation and exact queue
tuple to the physical/root and action-invocation snapshots, resolver inclusion,
explicit target object, local-player identity, world/instance/job/PvP state,
and compatibility profile. A stable framework watcher runs even while the
schedulers are idle. Death/unconscious state, mounting, stun, `BeingMoved`, a
frame gap, player or explicit-target disappearance/replacement, target/resolver
change, territory/map/instance/job/PvP change, or a compatibility conflict
requests a generation-bounded exact clear. A changed or consumed native tuple
only relinquishes the proof and is never overwritten.

After native acceptance, the stored native tuple is the ownership authority.
The watcher deliberately does not re-resolve the hotbar's adjusted action ID:
for example, a Viper button changing from 39183 to 39181 after acceptance does
not convert the exact queued 39183 entry into a mismatch. Adjusted-ID
re-resolution remains a pre-dispatch check for a future Direct Turbo pulse, not
a post-acceptance ownership rule.

Deferred replacement and terminal-clear requests are guarantees only while the
plugin remains loaded. Disposal can synchronously clear an exact owner that is
visible at that boundary. After the hooks are gone, PulseQueue cannot observe or
clear an entry that an outer hook restores or an asynchronous native macro
creates later; it makes no post-unload guarantee for that native state.

## One-shot capture proof

A candidate must satisfy every gate:

1. It occurs inside a standard/cross-hotbar slot execution scope.
2. It uses normal `UseActionMode.None` and a supported action type.
3. It is instant and not ground-targeted.
4. The vanilla call produces neither immediate acceptance nor a new exact
   matching native queue entry.
5. The native action sequence does not advance.
6. The native queue is empty, either originally or after an exact older
   certified entry was consumed by the newest certified direct or certified
   action-only macro root. Foreign or pre-existing unowned queue state is never
   attributed to the current press.
7. The player is not mounted, and the action does not affect player position.
8. Status with recast/casting checks disabled is usable.
9. A positive animation-lock, GCD, or local-recast remainder is at most the
   adaptive horizon.
10. Player, target, territory, instance, job, and compatibility context are
    stable.

An unknown or failing check makes the candidate ineligible.

## One-shot dispatch proof

The runtime rechecks the complete context, native queue, adjusted action ID,
structural status, full action status, cooldown/charge state, animation lock,
deadline, target identities, forced-movement signal, and input generation.
Only then does it consume the token and invoke the exact tuple with
`UseActionMode.Queue`. Dispatch and cancellation are serialized behind a final
input-generation check, so a consumed stale token cannot be revived. Death,
stun, forced movement, mounting, target changes, zone/job/PvP changes, native
queue activity, plugin topology changes, and compatibility-setting changes all
invalidate the generation.

## Direct Turbo dispatch proof

Direct Turbo owns the certified standard-hotbar slot and its base command, not
one forever-adjusted action ID. Before a pulse it revalidates the raw key/chord,
newest input generation, slot/binding identity, target/context, hold deadline,
readiness, compatibility, and MOAction exclusion. It then resolves the slot's
current adjusted/combo ID. The new exact action tuple and target must pass every
profile and context check; this permits legitimate transformations such as a
Viper combo while rejecting a changed slot or unrelated action.

If that call is accepted into the native queue, its exact accepted tuple becomes
authoritative for that queue lifetime. Later slot transformations affect only a
future pulse; they do not rewrite or invalidate the already accepted queue
owner.

The pulse executes that same slot exactly once inside a scoped budget that may
forward at most one matching normal-mode `Action`/`PvPAction` call. A second,
mismatched, stale, or ineligible nested call is suppressed before native
execution and terminates ownership. An accepted or queued call creates an exact
action-effect acknowledgement expectation. Local/server rejection or a
two-second timeout ends the hold without retry. A safe initial physical press
may establish the slot owner even if it emitted zero `UseAction` calls.

## Macro Turbo dispatch proof

Macro Turbo never borrows the one-shot action token. Before every slot pulse it
revalidates the original raw key/chord, newest physical press generation,
standard-hotbar slot and binding, macro identifier and content fingerprint,
target/context snapshot, macro-executor availability, hold deadline, and
compatibility signature. The static allowlist must still classify every
non-empty command as either an action command or icon/error metadata, and its
`ActionCount` must still match the unchanged macro content. Only then may one
pulse token invoke the certified macro slot once.

That slot execution opens a new monotonically ordered execution epoch with a
fresh `ActionCount` budget and an initially empty accepted-outcome marker. Each
nested Macro-mode call live-resolves its requested action and rechecks complete
profile, target, context, compatibility, generation, hold, and MOAction state.
Zero through `ActionCount` observed calls are structurally valid. The first
native accepted/queued outcome marks the epoch complete; every later call is
suppressed before the original function. An over-budget, ineligible, stale, or
otherwise unauthorized call is likewise suppressed and cancels the owner.
Physical/original macro execution is not subject to synthetic suppression.

Cancellation can race an asynchronous native macro executor that still owns
`MacroLocked`. PulseQueue keeps only a tombstone quarantine for that synthetic
epoch and suppresses its subsequent Macro-mode calls. Normal- and Queue-mode
calls continue through native handling. Two seconds is only a diagnostic timeout:
the tombstone remains sealed for as long as native `MacroLocked` stays true and
clears after unlock or on disposal. It cannot authorize a new owner or resume the
canceled hold. Key-up, a newer press, any fingerprint or target/context mismatch,
or any other unsafe transition consumes ownership without a further slot call.
FFXIV alone evaluates which authored action line and target, if any, succeeds
during an authorized native execution.

## Compatibility boundary

Compatibility is an allowlist, not a best-effort name check:

- NoClippy 0.5.0.24 is supported as the sole animation-lock correction owner.
  PulseQueue does not alter or second-guess its timing writes.
- ReAction 1.3.5.1 is supported only when Turbo Hotbars, Macro Queue, and Auto
  Target are off and Action Stacks is empty. Auto Dismount and Camera Relative
  Directionals may remain on because inputs while mounted and actions that
  affect player position are excluded or passed through. Queue adjustments
  remain native-authoritative except for exact older PulseQueue-owned queue state:
  a newer certified direct or certified action-only macro root supersedes it
  unconditionally, and terminal safety cancellation exact-clears it. A
  non-consuming drain lease plus the stable-frame watcher preserves that proof
  across both ReAction hide/restore hook orders.
- MOAction 4.10.1 is supported through `MOAction.RetargetedActions`. IDs
  reported by that IPC are excluded from capture so MOAction owns their action
  and target transformation end to end.

ReAction Turbo Hotbars cannot be made physical-input-authoritative from the
outside: it synthesizes standard slot executions for held keys without a public
provenance marker. PulseQueue therefore fails closed while Turbo is enabled,
rather than guessing that one action category should outrank another. This
remains mandatory when PulseQueue native Turbo is enabled: two independent
repeat sources are never allowed to compete.

ReAction Macro Queue rewrites the action mode used by macro commands. It must
remain off because that rewrite creates a second macro queueing owner and
removes the provenance needed to distinguish the certified PulseQueue slot
pulse from a foreign queued action. The field is part of both the periodic
compatibility signature and the lightweight live ReAction configuration guard.

NoClippy remains the sole animation-lock correction owner for both paths.
PulseQueue may use the final client lock as a readiness condition but never
writes, subtracts, or feeds its response estimate back into that lock.

Unknown versions, unreadable configuration, missing required IPC, and unsafe
settings suspend only PulseQueue's buffer. Plugin load/unload is serialized with
the final dispatch boundary. ReAction's critical fields are also re-read through
a weak, lightweight per-input/final guard; a mismatch cancels before replay and
forces full reassessment. The complete compatibility profile, including
MOAction's IPC exclusions, is polled every 500 ms and changes quarantine capture
for one clean framework frame. PulseQueue never unloads or reconfigures another
plugin. Unknown native hooks still cannot be enumerated reliably, so this
remains a testing release.

## Turbo configuration compatibility

Configuration schema 2 introduced direct Turbo disabled, a 180 ms initial
delay, an 80 ms interval, and out-of-combat operation disabled. Schema 3 added
the independent Macro Turbo permission and migrates every older configuration
with that permission off. Schema 4 changes the defaults to 0 ms initial delay
and 60 ms interval. Only an exact schema-3 180/80 pair is migrated to the new
defaults; custom timing values are preserved. Known-schema timing values are
normalized before persistence. If a configuration has a version newer than
this build understands, both Turbo permissions are disabled in memory and the
file is not rewritten, preserving unknown data for the newer build that owns
it. An explicit reset deliberately creates a fresh current-schema configuration.
