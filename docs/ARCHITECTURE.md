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
A foreign or changed queue can never satisfy that proof. Direct-action Turbo
adds one bounded operation: one invocation of the exact captured native action
tuple for each current, revalidated pulse token. Macro Turbo has a different
bounded operation: one native execution of the same physically certified
standard-hotbar macro slot for each current pulse token. It never turns a macro
line into a PulseQueue-selected action call.

Native Turbo is a separate, opt-in input source rather than an extension of a
one-shot token. Its trust boundary begins with a physical keyboard binding to a
standard hotbar slot. Only a direct `Action` slot with one exactly correlated
`Action` or `PvPAction` invocation may become a direct repeat owner. A `Macro`
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

Static analysis also freezes the macro's exact `ActionCount`. Before the first
native macro command runs, the certified root creates a transcript builder with
that expected count. The complete physical execution must then emit exactly
that many eligible Macro-mode action calls. Each ordered entry stores action
type, requested and diagnostic resolved action IDs, target, extra parameter,
combo route, and resolver-target fingerprint. Duplicate commands are preserved
as separate ordered entries; no set conversion or deduplication is permitted.
A synchronous execution freezes on slot return. An asynchronous execution may
finish only when its owned native `MacroLocked` interval ends within two
seconds. Incomplete, extra, invalid, or unowned entries prevent the transcript
from freezing and therefore prevent repeat ownership.

Every transcript entry must be a Macro-mode `Action` or `PvPAction` and must
pass the same live action-profile boundary at capture and use: a nonzero current
resolution, no adjusted cast time, no area/ground targeting, no player-position
effect, no excluded movement exception, a safe and still-existing target, and
no static or live MOAction ownership. The resolved ID is diagnostic rather than
part of semantic transcript equality so a combo or level adjustment may change
it. Such a change is accepted only after the same requested entry is resolved
again and all eligibility, target, compatibility, and MOAction checks pass at
the due/final boundary and at the emitted call itself. Action type, requested
ID, target, parameter, route, resolver fingerprint, order, and count remain
exact.

A direct Turbo owner stores the exact slot, captured action/target tuple, and
physical key/chord identity. Its key may produce multiple exact-action
invocations while held, but a changed adjusted action ID terminates ownership.
A Macro Turbo owner stores the exact certified slot, macro identity/content,
physical key/chord, safety context, and frozen ordered transcript instead of a
selected action tuple. It may execute only that slot. Raw key release, a newer
certified physical edge, macro/binding mutation, or a target or context change
invalidates either owner before another pulse. Any newer native hotbar/action
invocation also cancels the old owner, even when that new input is ineligible
to become an owner. The older owner cannot resume afterward. Direct Turbo is
exact-action repetition; Macro Turbo is explicit same-slot repetition whose
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

Macro Turbo inserts an ownership-proof phase before `InitialDelay` can produce
a slot pulse:

```text
Certified root macro press
          |
          v
Vanilla slot execution --> ordered transcript build (expected ActionCount)
          |                                  |
          | synchronous unlock               | owned MacroLocked
          v                                  v
 exact freeze                         wait for unlock (max 2 s)
          \__________________________________/
                         |
                         v
              same certified slot may pulse
```

No later or unrelated `MacroLocked` transition can be adopted as provenance.

The initial delay is normalized to 0–1000 ms and the repeat interval to
60–1000 ms. Defaults are 180 ms and 80 ms respectively. Out-of-combat repeat is
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
physical hold and same-slot pulse token.

Every newly observed standard/cross-hotbar invocation advances the input
generation and cancels the pending token before its native call executes. This
is a replacement register, not a FIFO: a heal, Purify, or Guard pressed after a
weave becomes the only candidate PulseQueue may retain. PulseQueue does not
assign skills priorities or choose an alternative action.

When an earlier certified hotbar generation created the exact current native
queue entry, a newer eligible generation may consume that ownership only when
its action is ready or inside the current hold window, then clear the queued
flag before its own original call. A completion check handles the
opposite ReAction hook order, where ReAction temporarily hides and then restores
the older owned queue. Ownership is dropped as soon as the tuple or action
sequence changes, so coincidentally similar and plugin-created queues remain
untouched.

## One-shot capture proof

A candidate must satisfy every gate:

1. It occurs inside a standard/cross-hotbar slot execution scope.
2. It uses normal `UseActionMode.None` and a supported action type.
3. It is instant and not ground-targeted.
4. The vanilla call produces neither immediate acceptance nor a new exact
   matching native queue entry.
5. The native action sequence does not advance.
6. The native queue is empty, either originally or after an exact older
   certified entry was consumed by the newest valid input. Foreign or
   pre-existing unowned queue state is never attributed to the current press.
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

## Macro Turbo dispatch proof

Macro Turbo never borrows the one-shot action token. Before every slot pulse it
revalidates the original raw key/chord, newest physical press generation,
standard-hotbar slot and binding, macro identifier and content fingerprint,
target/context snapshot, macro-executor availability, hold deadline, and
compatibility signature. It also re-resolves every frozen transcript entry and
rechecks its complete live eligibility and MOAction exclusion. The static
allowlist must still classify every non-empty command as either an action
command or icon/error metadata. Only then may one pulse token invoke the
certified macro slot once.

That slot execution opens a new monotonically ordered execution epoch with a
fresh cursor over the frozen transcript. Each nested Macro-mode action call may
match only the next entry. Semantic matching ignores only the resolved ID;
every other identity field, duplicates, order, and the final exact count remain
mandatory. A missing entry at synchronous return or owned `MacroLocked` unlock,
an extra/reordered/mismatched entry, an ineligible live resolution, or stale
epoch/token provenance cancels the owner. An unauthorized call inside the
synthetic slot chain returns failure without invoking native `UseAction`.
Physical/original macro execution is not subject to that synthetic suppression.

Cancellation can race an asynchronous native macro executor that still owns
`MacroLocked`. If the frozen epoch is incomplete at that boundary, PulseQueue
keeps only a tombstone quarantine for that synthetic epoch and suppresses its
subsequent Macro-mode calls. Normal- and Queue-mode calls continue through
native handling. The quarantine clears when native `MacroLocked` becomes false,
after a hard two-second bound, when a newer unlocked certified root macro press
supersedes it, or on disposal. It cannot authorize a new owner or resume the
canceled hold. Key-up, a newer press, any fingerprint or target/context
mismatch, or any other unsafe transition consumes ownership without a further
slot call. FFXIV alone evaluates which authored action line and target, if any,
succeeds during an authorized native execution.

## Compatibility boundary

Compatibility is an allowlist, not a best-effort name check:

- NoClippy 0.5.0.24 is supported as the sole animation-lock correction owner.
  PulseQueue does not alter or second-guess its timing writes.
- ReAction 1.3.5.1 is supported only when Turbo Hotbars, Macro Queue, Auto
  Target, Auto Dismount, and Camera Relative Directionals are off and Action
  Stacks is empty. Queue adjustments remain native-authoritative except for
  exact older queue state owned by a certified PulseQueue-observed hotbar
  generation.
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
with that permission off. Known-schema timing values are normalized before
persistence. If a configuration has a version newer than this build
understands, both Turbo permissions are disabled in memory and the file is not
rewritten, preserving unknown data for the newer build that owns it. An
explicit reset deliberately creates a fresh current-schema configuration.
