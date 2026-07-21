# Architecture

## Trust boundary

PulseQueue treats the game client as authoritative. It observes a standard
hotbar-slot execution scope and the nested `ActionManager.UseAction` call, but
always lets the original call run first. Native acceptance, a sequence advance,
area-target activation, or any native queued action prevents custom capture.

The plugin never edits native queue or timing memory. Its only mutating game
operation is one call to the original `UseAction` function for a consumed token.

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
The runtime consumes that command and performs one non-requeueing native call.
Its result is recorded only for diagnostics and is never fed back into a retry.

## Capture proof

A candidate must satisfy every gate:

1. It occurs inside a standard/cross-hotbar slot execution scope.
2. It uses normal `UseActionMode.None` and a supported action type.
3. It is instant and not ground-targeted.
4. The vanilla call returns rejected.
5. The native action sequence does not advance.
6. The native action queue remains empty.
7. Status with recast/casting checks disabled is usable.
8. A positive animation-lock, GCD, or local-recast remainder is at most the
   adaptive horizon.
9. Player, target, territory, instance, job, and plugin context are stable.

An unknown or failing check makes the candidate ineligible.

## Dispatch proof

The runtime rechecks the complete context, native queue, adjusted action ID,
structural status, full action status, cooldown/charge state, animation lock,
deadline, target identities, forced-movement signal, and cancellation epoch.
Only then does it consume the token and invoke the exact tuple with
`UseActionMode.Queue`. Dispatch and cancellation are serialized behind a final
generation check, so a consumed stale token cannot be revived.

## Conflict boundary

Dalamud's exposed loaded-plugin list is checked every frame. Known timing/action replacement plugins suspend only PulseQueue's
buffer; PulseQueue never attempts to disable another plugin. External tools and
unknown native hooks cannot be enumerated reliably, which is why this remains a
testing release and why anomaly handling is fail-closed.
