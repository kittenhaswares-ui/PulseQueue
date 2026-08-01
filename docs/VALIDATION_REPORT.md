# Validation report — PulseQueue 0.3.5.0 testing candidate

Date: 2026-08-01

Status: **automated checks, API-15 build, and release artifact verification
passed; current-patch live verification is pending.**

This report separates four kinds of evidence:

1. historical live evidence that invalidated the 0.3.3 manual Turbo model;
2. deterministic proof of the 0.3.5 repeat and ownership state machine;
3. a Dalamud API-15 plugin build and release artifact; and
4. current-patch in-game evidence for signatures, hook order, ReAction, and
   NoClippy coexistence.

The package checksums below describe the final locally verified ZIP. No
successful live result is asserted until the mandatory matrix has actually been
run.

## Historical reason for the native-input design

The short 0.3.3 test was technically stable but functionally ineffective:

- 324 certified roots were observed;
- 155 inputs were rejected by the ReAction compatibility policy;
- 53 certified macro starts began with zero observed actions;
- 21 logged manual macro pulses returned locally but observed zero actions;
- 95 direct starts produced only two actual Turbo pulses; and
- 73 of 95 direct holds ended in under 200 ms.

Version 0.3.4 replaced manual slot/action/macro replay with native logical-input
repeat. Version 0.3.5 removes its remaining global ReAction delegation gate,
which made PulseQueue inherit ReAction's combo and macro exclusions.

## 0.3.5 contract under test

### Native repeat and newest-input authority

PulseQueue observes `IsInputIDPressed` only during FFXIV's standard-hotbar
binding scan, reads held state for that same logical input, and can report only
that same standard-hotbar `InputId` as pressed. FFXIV remains responsible for
the current slot, adjusted/combo action, target, and complete macro.

The first eligible native press can claim without a startup release cycle. The
newest genuine press always wins: either a held press or a fast tap immediately
preempts the older held owner. A preempted hold remains suppressed until its own
release. A pulse from an older input already known held is external/typematic
continuation and cannot steal ownership.

The Turbo path does not replay `ExecuteSlot`, `ExecuteSlotById`, a captured
action tuple, or parsed macro lines. It requires no action-effect acknowledgement
for the next cadence.

### ReAction coexistence

PulseQueue retains a same-`InputId` gap-filler while ReAction Turbo is active.
ReAction pulses pass through as external activations:

- every observed current-owner external/native pulse resets PulseQueue's
  fallback deadline to `now + interval`;
- PulseQueue stays silent while ReAction pulses and resumes exactly one interval
  after the last observed pulse;
- an outer-hook delegated `ExecuteSlot` is correlated and coalesced, so it also
  resets the deadline instead of being followed by an immediate duplicate; and
- an external pulse never becomes a newer player generation.

Auto Target and Action Stacks do not gate native cadence. An unsupported or
unreadable ReAction profile may still make smart action/queue mutation fail
closed, but does not disable action-agnostic same-input repeat.

### Smart buffer

The smart buffer is one exact, replaceable, at-most-once token with a 350 ms
hard maximum. It creates no FIFO weave backlog, alternative skill, target
fallback, or server-rejection retry. Native queue clearing is limited to an
exact unchanged tuple proven to belong to the older PulseQueue input generation.
An unsafe frame gap over 1000 ms cancels pending smart-buffer work; the repeat
state machine does not authorize a catch-up burst or a new physical generation.
Target changes also cancel the one-shot token, but no longer release-gate a
valid native held-input owner. ReAction Auto Target therefore cannot kill an
active combo hold merely by changing target resolution.

### Macros

A held macro binding repeats as a complete native standard-hotbar slot.
PulseQueue does not parse or approve individual lines and does not provide a
separate Macro Queue setting or command. It preserves native Macro action mode;
FFXIV's `MacroLocked` state governs execution. ReAction Macro Queue may transform
that mode downstream when enabled.

### Scope

Held-input Turbo is limited to FFXIV's ten standard hotbars. Cross-hotbar and
controller input, direct hotbar-slot mouse clicks, and plugin-originated direct
slot/action calls remain outside this repeat path.

NoClippy remains the animation-lock correction owner. PulseQueue performs no
animation-lock, packet, cooldown, resource, or target write.

## Deterministic verification

The dependency-free suite and static contract cover:

- first eligible press claiming without a prior release;
- configured initial delay, repeat cadence, and 0..1000 ms normalization;
- at most one result for a due slot and no catch-up burst;
- newest held press and newest fast tap preempting an older hold;
- a preempted input remaining suppressed until release;
- an older held external pulse being unable to steal ownership;
- every current-owner external/native pulse moving fallback to `now + interval`;
- gap recovery exactly one interval after the last external pulse;
- outer-hook delegated slot execution being coalesced;
- injected/external activations not becoming physical generations;
- complete native macro-slot repetition with Macro mode preserved;
- release, disable, and terminal cancellation behavior;
- absence of active manual Turbo slot/action/macro dispatch; and
- absence of animation-lock writes.

### Automated result

Current 0.3.5 source result:

```text
dotnet run --project tests/PulseQueue.Core.SelfTest -c Release
./scripts/Verify-SafetyContract.ps1
```

- Core self-tests: **171/171 passed**.
- Static native-input/one-shot safety contract: **passed**.
- Dalamud API-15 plugin build: **passed with 0 warnings and 0 errors**.
- C# format verification: **passed**.
- Release artifact verification: **passed**.

## Current-patch live verification

Status: **Pending.**

The complete procedure and required outcomes are in
[`LIVE_TEST_MATRIX.md`](LIVE_TEST_MATRIX.md). At minimum, the live run must show:

1. standalone actions, transforming combos, and arbitrary/multi-action macros
   receive repeated native presses;
2. ReAction pulses pass through while PulseQueue stays silent, with fallback
   resuming one interval after the final observed current-owner pulse;
3. an older held external pulse cannot steal control;
4. a fast tap or held Recuperate, Purify, Guard, or other newer binding
   suppresses an older Viper hold;
5. FFXIV `MacroLocked` remains authoritative and PulseQueue preserves Macro
   mode;
6. NoClippy remains the sole animation-lock correction owner;
7. cross-hotbar/controller and direct slot clicks remain outside native Turbo;
   and
8. no hook exception, access violation, recursion, catch-up burst, stale repeat,
   or foreign native-queue clear appears in the log.

Subjective responsiveness is useful tuning evidence, but does not replace the
counter sequence and action outcomes above.

## Release artifact verification

The final source revision was built and verified with:

```text
./scripts/Build-Release.ps1
./scripts/Verify-Release.ps1
```

| Field | Current value |
|---|---|
| Git commit | This release commit, recorded in Git history |
| Plugin version | `0.3.5.0` |
| Dalamud API | `15` |
| ZIP SHA-256 | `38057cad6caba8427e82acc4e3ae3327f97fe628c9da851e704d21ba0f3877d0` |
| DLL SHA-256 | `090e4950bb362ebf05239a0b27e465846b34bbea9cff92c7ccf360badb7f7a26` |
| Self-test result | `171/171 passed` |
| Static safety contract | Passed |
| Plugin build | Passed, 0 warnings / 0 errors |
| Current-patch live matrix | Pending |

The project, embedded manifest, custom-repository metadata, ZIP contents, and
hashes agree on `0.3.5.0`.

## Safety and account-risk conclusion

The architecture limits Turbo to the player's same logical standard-hotbar
input and keeps newest genuine input authoritative. These properties reduce
implementation risk but do not make the plugin official, sanctioned,
undetectable, or account-safe.

Square Enix prohibits third-party tools and unauthorized gameplay-modifying
software. This remains a testing-only custom-repository release used entirely at
the user's own risk.
