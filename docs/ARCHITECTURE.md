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
queue identity memory. It has two bounded game mutations: one call to the
original `UseAction` function for a consumed token, and one `ActionQueued=false`
write when an ownership proof matches the complete queue tuple, its unchanged
sequence marker, and an older certified hotbar generation. A foreign or changed
queue can never satisfy that proof.

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

## Capture proof

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

## Dispatch proof

The runtime rechecks the complete context, native queue, adjusted action ID,
structural status, full action status, cooldown/charge state, animation lock,
deadline, target identities, forced-movement signal, and input generation.
Only then does it consume the token and invoke the exact tuple with
`UseActionMode.Queue`. Dispatch and cancellation are serialized behind a final
input-generation check, so a consumed stale token cannot be revived. Death,
stun, forced movement, mounting, target changes, zone/job/PvP changes, native
queue activity, plugin topology changes, and compatibility-setting changes all
invalidate the generation.

## Compatibility boundary

Compatibility is an allowlist, not a best-effort name check:

- NoClippy 0.5.0.24 is supported as the sole animation-lock correction owner.
  PulseQueue does not alter or second-guess its timing writes.
- ReAction 1.3.5.1 is supported only when Turbo Hotbars, Auto Target, Auto
  Dismount, and Camera Relative Directionals are off and Action Stacks is empty.
  Queue adjustments remain native-authoritative except for exact older queue
  state owned by a certified PulseQueue-observed hotbar generation.
- MOAction 4.10.1 is supported through `MOAction.RetargetedActions`. IDs
  reported by that IPC are excluded from capture so MOAction owns their action
  and target transformation end to end.

ReAction Turbo Hotbars cannot be made physical-input-authoritative from the
outside: it synthesizes standard slot executions for held keys without a public
provenance marker. PulseQueue therefore fails closed while Turbo is enabled,
rather than guessing that one action category should outrank another.

Unknown versions, unreadable configuration, missing required IPC, and unsafe
settings suspend only PulseQueue's buffer. Plugin load/unload is serialized with
the final dispatch boundary. ReAction's critical fields are also re-read through
a weak, lightweight per-input/final guard; a mismatch cancels before replay and
forces full reassessment. The complete compatibility profile, including
MOAction's IPC exclusions, is polled every 500 ms and changes quarantine capture
for one clean framework frame. PulseQueue never unloads or reconfigures another
plugin. Unknown native hooks still cannot be enumerated reliably, so this
remains a testing release.
