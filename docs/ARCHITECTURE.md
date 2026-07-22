# PulseQueue 0.3.4 architecture

## Objective

PulseQueue combines a bounded one-shot action buffer with a native held-input
repeat layer while preserving one unambiguous control rule:

> The newest genuine player input is authoritative. A generated repeat is not a
> new player decision.

This rule solves the concrete Viper case. Repeated small-weave input may improve
responsiveness, but a later Recuperate, Purify, Guard, or other standard-hotbar
press must immediately cancel the older pending intent and prevent the older
held input from returning.

Version 0.3.4 is a replacement architecture, not an extension of 0.3.3's manual
Turbo runtime. The 0.3.3 live sample found 155 ReAction compatibility rejections,
21 manual macro pulses with no observed actions, and only two direct pulses.
The active 0.3.4 repeat path has no manual slot/action/macro dispatcher and no
action-effect acknowledgement gate.

## Two independent layers

### 1. Smart one-shot buffer

The smart buffer observes a genuine action attempt after vanilla has received
it. If the client did not accept or queue the action, and the failure is only a
short local readiness boundary, it can retain one immutable action intent for
at most 180 ms and issue one queue-mode call when ready.

The pending state is a single replaceable token, not a FIFO:

```text
no token -> physical action rejected locally -> one pending exact token
one token -> genuine newer press             -> old token canceled
one token -> client becomes ready            -> token consumed before one call
one token -> safety/context change            -> token canceled
```

One token cannot feed another. A rejection or server non-acknowledgement does
not authorize a retry. The buffer never selects another action or target.

If an original player press creates a native queue entry, PulseQueue records
ownership only when the complete before/after tuple and action sequence prove
that exact outcome belongs to that input generation. A newer genuine input may
preempt only that exact unchanged owned tuple. Foreign, changed, or ambiguous
queue state is never mutated.

### 2. Native logical-input repeat

FFXIV's standard-hotbar scanner asks `InputData::IsInputIDPressed` for logical
hotbar bindings. PulseQueue scopes its pressed observation to the native
standard-hotbar scan and reads `InputData::IsInputIDHeld` for the same `InputId`.
When a held owner's cadence is due, it changes only the answer for that same
logical input from not-pressed to pressed.

```text
CheckHotbarBindings
  -> IsInputIDPressed(InputId)
       -> original native pressed result first
       -> native held result for that same InputId
       -> repeat state machine
       -> original result or one same-InputId pressed result
  -> FFXIV resolves binding and executes the current slot normally
```

The settings snapshot is captured once per binding scan. The detour is active
only inside that scan and only for `HOTBAR_1_1` through `HOTBAR_10_B`.
Everything else returns the original result unchanged.

The hook is fail-open. A null pointer, unsupported input ID, callback failure,
unexpected exception, or teardown race returns the original native result. The
implementation never substitutes another `InputId`.

## Repeat state machine

The dependency-free repeat engine accepts these facts for one logical input:

- logical input ID;
- original native `pressed` result;
- native `held` result;
- monotonic time;
- whether PulseQueue repeat is enabled; and
- whether an external repeat owner is active.

It returns one of the following decisions:

| Decision | Meaning |
|---|---|
| `PhysicalPress` | A genuine fresh native edge passes through and becomes the newest owner. |
| `InjectedRepeat` | PulseQueue reports the same owning `InputId` pressed for one due interval. |
| `DelegatedRepeat` | ReAction already produced/owns this held-input pulse; PulseQueue does not inject another. |
| `SuppressedOlderHold` | The input is still held but lost to a newer physical owner. Its pressed result is forced false where hook order permits. |
| `Released` | The input is no longer held. Its ownership/tombstone state is cleared. |
| `None` | No new press is reported. |

Important state rules:

- A key already held when the hook starts cannot silently acquire repeat
  ownership. A release followed by a fresh press is required.
- A genuine new edge always passes through, even if repeat is disabled.
- The newest genuine edge atomically owns repeat and preempts the older owner.
- A preempted input remains suppressed while continuously held. It cannot
  regain ownership merely because the newer key was released.
- Each cadence emits at most one press. Large time jumps advance to one future
  due time and never create a catch-up burst.
- Initial delay and interval are normalized independently to 0..1000 ms.
- With zero initial delay, the engine still waits one interval after the
  physical edge, preventing a duplicate in the original scan.
- State transitions and counters are guarded so concurrent observations cannot
  issue two repeats for the same due instant.

## Activation provenance

Logical presses are correlated with the standard-hotbar slot execution that
follows in the same native scan. The activation carries one of three kinds:

- genuine physical press;
- PulseQueue-injected repeat; or
- ReAction-delegated repeat.

That distinction is the boundary between player priority and repetition:

- only a genuine physical activation advances the newest-input generation,
  cancels the previous smart-buffer token, and requests an exact owned-queue
  preemption;
- an injected or delegated repeat reaches vanilla execution but does not begin
  a new player generation and cannot cancel a newer buffer token; and
- a delegated repeat from a superseded continuously held input is suppressed at
  the exact correlated slot boundary when outer hook order prevents suppression
  at `IsInputIDPressed` itself.

Correlation is short-lived and exact to one standard hotbar/slot pair. It is
not inferred from an action ID, because combo transformations and identical
actions on multiple slots make action-ID inference ambiguous.

## Native execution ownership

After the logical input is reported pressed, FFXIV remains responsible for:

- selecting the current bound standard-hotbar slot;
- resolving the current adjusted/combo action;
- deciding normal client readiness and native queue behavior;
- resolving hard, soft, mouseover, party, focus, and macro-authored targets; and
- running the complete player-authored macro.

The active repeat path never calls `ExecuteSlot`, `ExecuteSlotById`, or
`UseAction` to manufacture a Turbo pulse. It does not capture an action tuple
for repeating, parse macro commands, impose an action-count budget, suppress a
macro tail, or wait for an action-effect acknowledgement before the next input
cadence.

This is intentionally different from the one-shot buffer. The one-shot buffer
may issue one exact queue-mode action call; native Turbo issues only a logical
input press and lets the game resolve the current slot.

## Macro behavior

Because Turbo repeats the logical binding rather than a captured action, any
macro bound to that standard-hotbar slot repeats as the complete native slot.
Multi-action and arbitrary macros do not need a PulseQueue whitelist or static
transcript. FFXIV owns command order, waits, condition resolution, action
selection, targets, and all non-action side effects.

This power is also a user-visible risk: chat, target, marker, gearset, sound, or
other commands in a held macro can run on every native repeat. PulseQueue does
not silently remove them.

Macro action queueing is a separate opt-in. When enabled and when ReAction Macro
Queue is not active, an action call made in native Macro mode is passed to the
single original `UseAction` boundary in normal queueable mode. All other
arguments remain unchanged. The conversion does not parse the macro, create a
second macro executor, or make the macro itself repeat.

## ReAction integration

Compatibility is capability-granular:

- **Turbo Hotbars active:** PulseQueue marks ReAction as the external repeat
  owner. It observes and classifies native held pulses but does not inject a
  second pulse stream.
- **Turbo Hotbars inactive or ReAction absent:** PulseQueue's logical repeat
  engine supplies the held press.
- **Macro Queue active:** ReAction owns Macro-to-normal action-mode conversion;
  PulseQueue leaves it alone.
- **Macro Queue inactive or ReAction absent:** PulseQueue may perform its own
  optional conversion.

Supported ReAction features are integrations, not global conflicts. A loaded
unsupported or unreadable ReAction build is fail-closed for PulseQueue cadence,
smart replay, and macro-mode mutation so two unknown hook owners cannot compete;
native/ReAction input remains pass-through. Removing ReAction restores the
standalone PulseQueue path.

Hook order can vary. PulseQueue therefore classifies an external `pressed`
signal on an input that remained held since its previous observation as a
delegated repeat, not a fresh physical decision. It also leaves an exact
scan-scoped slot candidate so an outer ReAction hook can be classified after it
turns PulseQueue's false return into a held pulse.

## NoClippy integration

NoClippy remains downstream of input production and owns its animation-lock
correction. PulseQueue observes normal client readiness for the one-shot buffer
but never writes animation lock, packets, cooldowns, or resources. Native Turbo
has no animation-lock writer at all; it produces input and lets the normal game
plus NoClippy path decide the action outcome.

## Cancellation and lifecycle

A genuine newer physical edge performs ordering before the new slot runs:

1. advance the physical-input generation;
2. cancel the older pending one-shot token;
3. cancel/suppress the older logical repeat owner;
4. exact-clear only a proven older PulseQueue-owned native queue if present;
5. let the new physical standard-hotbar input run through vanilla; and
6. consider the new action for a fresh one-shot buffer only after its original
   native outcome is known.

Terminal events cancel pending work: death, stun, forced movement/knockback,
mounting, target/context change, zoning, logout, job/PvP change, unsafe frame
gap, disable, and disposal. Hook teardown stops input injection before managed
state is released.

Accepted vanilla queue state is not cleared merely because a repeat key was
released or Turbo was disabled. Exact queue clearing is reserved for newer-input
preemption or terminal safety and requires unchanged ownership proof.

## Telemetry

Live diagnostics distinguish:

- physical logical edges;
- PulseQueue-injected repeats;
- ReAction-delegated repeats;
- releases and newest-owner preemptions;
- suppressed older held inputs;
- fail-open events; and
- the current logical input/hotbar/slot owner.

The counters make a short test falsifiable. A working standalone hold must show
physical edges followed by injected repeats. With ReAction Turbo active it must
show delegated repeats and zero duplicate PulseQueue injection. A later heal or
defensive press must show one physical preemption while the old held input moves
only the suppression counter.

## Scope and non-goals

Version 0.3.4 repeat scope is the ten standard hotbars. Cross-hotbar/controller
input, direct mouse clicks on a slot, and plugin-originated direct slot/action
calls have no held standard-hotbar `InputId` in this path and do not receive
Turbo.

PulseQueue is not a rotation engine, macro interpreter, targeter, skill-priority
system, packet modifier, animation-lock modifier, or guarantee that a server
will accept an action. It aims to preserve and repeat the player's own input at
the earliest native boundary while keeping the newest genuine choice in control.
