# Validation report — PulseQueue 0.3.4.0 testing candidate

Date: 2026-07-22

Status: **pre-publication; automated and current-patch live results must be
recorded before the candidate is described as validated.**

This report separates three kinds of evidence:

1. the live failure evidence that invalidated the 0.3.3 manual Turbo model;
2. deterministic proof obligations for the replacement state machine; and
3. current-patch in-game results for native signatures, hook order, and plugin
   coexistence.

No package checksum or successful live result is asserted until the final ZIP
has been built and the mandatory matrix has actually been run.

## Why 0.3.3 was replaced

The short 0.3.3 test was technically stable but functionally ineffective:

- 324 certified roots were observed;
- 155 inputs were rejected by the ReAction compatibility policy;
- 53 certified macro starts all began with zero observed actions;
- 21 logged manual macro pulses returned locally but still observed zero actions,
  produced zero accepted outcomes, and produced zero macro acknowledgements;
- 95 direct starts produced only two actual Turbo pulses;
- 73 of 95 direct holds ended in under 200 ms; and
- the plugin log contained no PulseQueue fault that could explain the absence of
  behavior.

This is stronger evidence than a settings/UI problem: the old active design was
over-gated and its manual slot/action/macro execution path did not deliver the
intended held-input effect. Version 0.3.4 therefore removes that path from Turbo
instead of adding another exception to it.

## 0.3.4 contract under test

### Native repeat

PulseQueue observes `IsInputIDPressed` only while FFXIV performs its native
standard-hotbar binding scan. It reads the matching logical held state and can
report only that same standard-hotbar `InputId` as pressed when cadence is due.
FFXIV remains responsible for resolving the current slot, transformed action,
target, and complete macro.

The Turbo path must not manually replay `ExecuteSlot`, `ExecuteSlotById`, a
captured action tuple, or parsed macro lines. It must not require an action-effect
acknowledgement to produce the next held-input cadence.

### Newest genuine input

Only a fresh physical logical edge is allowed to:

- become the newest repeat owner;
- cancel an older smart-buffer token;
- suppress a previous continuously held input; or
- preempt an exact older PulseQueue-owned native queue.

A PulseQueue-injected or ReAction-delegated repeat must never perform those
operations. Once A is preempted by B, A remains suppressed until A is released
and freshly pressed; releasing B cannot resurrect A.

### Smart buffer

The existing smart buffer remains a single exact, replaceable, at-most-once
token with a 180 ms hard maximum. It creates no FIFO weave backlog, alternative
skill, target fallback, or server-rejection retry. Native queue clearing remains
limited to an exact unchanged tuple proven to belong to the older PulseQueue
input generation.

### Macros

A held macro binding repeats as a complete native standard-hotbar slot. Arbitrary
and multi-action macros are not parsed or statically approved by PulseQueue.
FFXIV owns the authored command order, targets, waits, and side effects.

The optional macro action queue feature changes only the action-call mode when
ReAction Macro Queue is not already active. It does not create or repeat a macro
executor.

### Other plugins

- ReAction Turbo Hotbars active: classify/delegate its pulses and inject none.
- ReAction Turbo Hotbars inactive or ReAction absent: PulseQueue supplies its
  own logical-input pulses.
- ReAction Macro Queue active: ReAction is the sole macro queue-mode owner.
- ReAction Macro Queue inactive or ReAction absent: PulseQueue may provide its
  own optional mode conversion.
- NoClippy active: supported; NoClippy remains the animation-lock correction
  owner and PulseQueue performs no lock write.

Compatibility is feature-granular for an audited ReAction profile. A loaded
unsupported or unreadable profile fails closed for PulseQueue cadence/replay and
passes native/ReAction input through, avoiding two unknown mutation owners.

## Deterministic verification

The dependency-free core suite must cover at least:

- released-before-owning startup behavior;
- one fresh physical edge passing through and claiming ownership;
- configured initial delay and repeat cadence;
- 0..1000 ms normalization;
- at most one result per due instant under concurrent observation;
- no catch-up burst after a large time jump;
- newest owner preempting the previous hold;
- the old continuously held owner staying suppressed after the newer release;
- physical native presses remaining authoritative;
- external repeat ownership delegating without injection;
- release clearing ownership/tombstone state;
- disabled repeat preserving vanilla physical input; and
- randomized counter/state invariants.

The static integration contract must additionally prove that the active Turbo
path:

- is scoped by the native standard-hotbar scan;
- calls the original pressed function before making a repeat decision;
- queries the native held state for the same logical input;
- returns the native result on unsupported IDs and exceptions;
- distinguishes physical, injected, and delegated activations;
- cannot let generated repeats begin a new physical input generation;
- has no active manual slot/action/macro Turbo dispatch;
- delegates ReAction Turbo and Macro Queue independently; and
- performs no animation-lock write.

### Automated result

Final pre-publication integration run completed on 2026-07-22:

```text
dotnet run --project tests/PulseQueue.Core.SelfTest -c Release
dotnet restore src/PulseQueue.Plugin/PulseQueue.Plugin.csproj --use-lock-file
dotnet build src/PulseQueue.Plugin/PulseQueue.Plugin.csproj -c Release --no-restore
./scripts/Verify-SafetyContract.ps1
```

- Core self-tests: **166/166 passed**.
- API-15 Release build: **0 warnings, 0 errors**.
- C# format verification: **passed**.
- Static native-input/one-shot safety contract: **passed**.
- Release artifact verification: **passed**.

## Native live verification

The complete procedure and expected results are in
[`LIVE_TEST_MATRIX.md`](LIVE_TEST_MATRIX.md). Publication requires, at minimum:

1. PulseQueue-alone action and arbitrary/multi-macro holds produce multiple
   **injected** repeats after one physical edge.
2. ReAction Turbo on produces **delegated** repeats and zero duplicate
   PulseQueue injection.
3. ReAction Turbo off or absent returns to PulseQueue injection without a
   global compatibility suspension.
4. A held Viper weave followed by Recuperate, Purify, or Guard records a genuine
   physical preemption; the older held input stays suppressed until release.
5. Injected and delegated repeats never cancel/replace a newer smart-buffer
   intent or count as a physical generation.
6. PulseQueue Macro Queue and ReAction Macro Queue each work as the sole owner
   when selected, without duplicate conversion.
7. NoClippy remains the sole animation-lock correction owner.
8. Cross-hotbar/controller and direct slot clicks remain outside native Turbo.
9. No hook exception, access violation, recursion, catch-up burst, stale repeat,
   or foreign native-queue clear appears in the log.

### Current-patch live result

Pending. Do not convert the requirements above into success statements based on
automated tests or subjective feel alone. Attach the tested configuration and
the relevant PulseQueue diagnostic/log excerpt when completed.

## Release artifact verification

After code and live gates pass, build and verify the release:

```text
./scripts/Build-Release.ps1
./scripts/Verify-Release.ps1
```

Record all of the following from the final commit and generated artifact:

| Field | Required value |
|---|---|
| Git commit | Publication commit for this report |
| Plugin version | `0.3.4.0` |
| Dalamud API | `15` |
| ZIP SHA-256 | `86451ed941bd953b07555f077d4a83daaf628f09b682abcad9fe76574e3b429c` |
| DLL SHA-256 | `9ac90624c493ca8fe69aaf58191e4643d8f189d4579883f2a7eb332b9e78a7b3` |
| Self-test result | `166/166 passed` |
| Static safety contract | Passed |
| Current-patch live matrix | Pending |

The version in the project, embedded plugin manifest, and custom repository
metadata must all be `0.3.4.0`. JSON must parse, the release ZIP must contain the
expected manifest/DLL, and repository hashes must describe that exact artifact.

## Safety and account-risk conclusion

The replacement architecture materially narrows PulseQueue's authority: Turbo
produces a same-logical-input press and lets FFXIV own the slot/action/macro
execution. It also explicitly prevents generated repeats from becoming newer
player intent. These properties reduce implementation risk but do not make the
plugin official, sanctioned, undetectable, or account-safe.

Square Enix prohibits third-party tools and unauthorized gameplay-modifying
software. This remains a testing-only custom-repository release used entirely at
the user's own risk.
