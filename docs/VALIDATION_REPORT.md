# Validation report — 0.3.2.0 testing release

- Date: 2026-07-22
- Target: Dalamud API 15 / .NET 10
- Release channel: custom repository, testing-exclusive

## 0.3.2 testing status

Version 0.3.2 keeps the one-shot smart buffer exact and at most once. It also
keeps direct-action Turbo on the 0.3.1 immutable action/target tuple path: a
changed adjusted action or combo transformation terminates that direct owner.
`PvPCombo` remains excluded until route identity can be proven end to end.

The separately enabled Macro Turbo path now owns a physically certified
keyboard-bound standard-hotbar macro slot instead of a captured native action.
Static analysis permits one or more `/ac`, `/action`, `/pvpac`, or `/pvpaction`
commands plus only icon/error metadata. A zero-action macro, `/assist`, waits,
chat, target mutation, markers, items, gearsets, hotbar mutation, unknown
commands, and every other non-allowlisted command fail closed. The original
physical press always remains vanilla.

Each Macro Turbo pulse executes the same certified macro slot exactly once.
FFXIV's native macro executor evaluates the authored action lines in order and
resolves their authored targets; PulseQueue does not select or rewrite a line,
skill, fallback, or target. This deliberately means that different pulses may
produce different authored actions when vanilla FFXIV chooses a different
valid line. That behavior is isolated behind the separate Macro Turbo opt-in
and is not part of the one-shot or direct-action contracts.

The frozen runtime does not trust static text alone. The certified physical
execution must emit an exact ordered action transcript whose observed entry
count equals the statically analyzed `ActionCount`. Duplicate action commands
remain duplicate entries. Action type, requested ID, target, extra parameter,
route, resolver fingerprint, order, and count must match on every later
execution. A resolved ID may change only when the same requested action is
re-resolved and passes all current eligibility checks again.

Every Macro Turbo transcript entry excludes cast-time, area/ground-target, and
player-movement actions as well as statically or currently MOAction-owned IDs.
An ineligible, extra, reordered, mismatched, or stale call in the synthetic slot
chain is suppressed rather than forwarded to native action execution. Original
physical macro calls remain vanilla.

If cancellation or provenance failure occurs while a synthetic epoch still
owns native `MacroLocked`, a bounded tombstone quarantine suppresses only that
stale executor's later Macro-mode calls. Normal- and Queue-mode calls remain
native. The quarantine clears on unlock, after at most two seconds, on a newer
unlocked certified root macro press, or on disposal; it cannot restore the
canceled hold.

Raw key-up, every newer certified physical press, macro/binding mutation,
target/context change, and every existing safety transition cancel Macro Turbo
before another slot execution. A canceled owner cannot resume without a fresh
physical press. ReAction Turbo Hotbars and Macro Queue must remain disabled so
they cannot compete with or rewrite this source. NoClippy remains the sole
animation-lock correction owner. The 0.3.2 artifact remains testing-exclusive;
live gameplay validation is required.

## Evidence behind the compatibility update

The analyzed play session used NoClippy 0.5.0.24 and ReAction 1.3.5.1. The
previous PulseQueue 0.1 build classified both as hard conflicts, so it could not
have provided buffering while either was active; the responsive feel in that
part of the session came from those integrations rather than PulseQueue.

The short 0.3 Turbo test produced 44 apparent starts, one pulse, one matching
acknowledgement, and 18 active cancellations. Logical key-state gaps and
typematic callbacks repeatedly replaced the same physical hold before the
initial delay elapsed. Raw key-up is now the sole authority that can certify a
fresh press, startup begins release-gated, and owned held repeats are suppressed
instead of restarting ownership. The four observed tested slots were direct
`Action` slots; the sample therefore contained no Macro Turbo execution.
ReAction unloaded near the start of the meaningful interval, so that sample
validates PulseQueue with NoClippy rather than simultaneous ReAction operation.

The subsequent 0.3.1 live test explains why Macro Turbo still felt inactive:
40 start attempts produced 10 direct starts, 30 start rejections, one confirmed
pulse, seven original/buffer acknowledgements, and one pulse acknowledgement.
Of the 30 rejections, 18 were `MultipleActions`, five occurred while Macro
Turbo was still disabled, five direct slots produced no exactly eligible
action invocation, and two direct actions were structurally unavailable. The
23 macro attempts used shared macros 67–69, all of which contained multiple
action commands; the former single-action allowlist therefore rejected every
one. The known safe single-action shared macro 66 was not exercised. NoClippy
was active for this test (31 action samples, 162–384 ms RTT, 226 ms mean), while
ReAction remained unloaded. PulseQueue logged no warning, fault, or exception.

Version 0.3.2 addresses that observed functional gap by certifying and repeating
the one action-only macro slot, including authored multi-action fallbacks,
rather than selecting and replaying the one action seen during the physical
execution. FFXIV remains authoritative for which authored line and target
succeeds on every slot invocation.

One Viper button was observed resolving across several adjusted action IDs.
Version 0.3.1 deliberately ends ownership when the current resolved ID differs
from the one captured by the physical press; it never follows the transformed
combo action.

The anonymized post-install NoClippy sample contained 7,514 accepted actions.
Observed action-response time had a median of about 313 ms, a 95th percentile
of about 420 ms, and a maximum of about 512 ms. NoClippy reported positive
animation-lock savings on every parsed action, with a median near 270 ms. This
supports keeping NoClippy authoritative for animation-lock correction. The
PulseQueue one-shot path remains bounded exact intent retention. Direct Turbo
only invokes its exact captured action after current readiness and
acknowledgement gates pass. Macro Turbo is separately authorized same-slot
execution and never turns its native macro result into a one-shot replay.

Viper sequences repeatedly showed two accepted short weaves before Recuperate,
including 11 `Uncoiled Twinfang -> Uncoiled Twinblood -> Recuperate` sequences
within three seconds. The logs record accepted action effects, not physical
keydown provenance, so they cannot alone prove which input was pressed first.
Source inspection supplied the missing explanation: ReAction Turbo Hotbars can
synthesize later standard-slot executions from an older held key. ReAction
1.3.5.1 exposes no provenance marker that lets PulseQueue distinguish those
calls from a new physical heal, Purify, or Guard press.

The saved ReAction profile also had `EnableAutoDismount=true` and
`EnableCameraRelativeDashes=true`. Auto Dismount stores an action and invokes it
later after dismounting, so the guarded profile now requires it off. Camera
Relative Dashes may remain on because PulseQueue excludes all position-affecting
actions plus ReAction's explicit action 29494 exception. The saved subordinate
`EnableAutoChangeTarget=true` is inert while `EnableAutoTarget=false`; the
AutoTarget module itself is therefore disabled.

The resulting fail-closed contract is therefore:

- NoClippy 0.5.0.24 is supported and remains the only animation-lock correction
  owner.
- ReAction 1.3.5.1 is supported only with Turbo Hotbars, Macro Queue, Auto
  Target, Auto Dismount, and Camera Relative Directionals off and Action Stacks empty.
  Queue adjustments remain authoritative except that the newest valid manual
  input can consume one exact older native queue entry previously certified by
  PulseQueue.
- MOAction 4.10.1 retargeted IDs are obtained from its published IPC and are
  excluded from PulseQueue capture.
- Unknown versions, unreadable settings, missing required integration data, and
  topology/configuration changes fail closed.

## 0.3.2 automated release evidence

- The complete dependency-free suite passes: **119/119 self-tests**.
- The required 0.3.2 cases cover certified-release provenance,
  startup/typematic physical hold identity, action-only macro analysis with
  one-or-more action commands, rejection of every non-allowlisted command,
  same-certified-slot pulse identity, exact `ActionCount`, ordered transcript
  freeze, duplicate preservation, missing/extra/reordered/mismatch failure,
  dynamic resolved-ID semantics, terminal execution cursors, exact queue-drain ownership,
  newest-owner replacement,
  hard timing bounds, no catch-up, concurrent single-pulse issuance, terminal
  pulse rejection, every Turbo cancellation boundary, the 30-second cap,
  schema-1/schema-2 migration, timing normalization, future-schema no-rewrite
  behavior, and explicit reset.
- Exact direct-Turbo acknowledgement tests require matching action type,
  requested/resolved action ID, and either the exact immediate source sequence
  or a wrap-safe sequence newer than the exact queued baseline.
- The full API-15/.NET-10 solution build completed with zero warnings and zero
  errors. Format verification, the static safety contract, and
  `git diff --check` all pass.
- The hardened release workflow deletes only the validated generated package
  before building, requires a freshly produced archive, and then verifies the
  exact ZIP allowlist, manifests, API level, assembly version, source
  fingerprint, and archive hash.
- Release ZIP SHA-256:
  `252b79c90791329208b95b1c13c8d7901597b6036206a5e1330218fc9bce193f`
- Source fingerprint:
  `dd4bba7ab0f42fcc6c8d7fa1a573f9fa8e4d02542efef18686c4c4dd1de04f5c`

These completed automated checks authorize only a testing package; they do not
replace the native in-game cases below.

## Inherited 0.2 automated evidence

- 65/65 dependency-free state-machine, timing, native-outcome, ownership, and generation
  tests pass.
- Coverage includes the 180 ms cap, exact action/target preservation, every
  cancellation reason, no-retry rejection behavior, RTT outlier/rebase
  behavior, a seeded 10,000-step adversarial trace, exact before/after native
  queue matching, exact native-queue ownership, temporary ReAction queue hiding,
  next-charge timing, newest-generation replacement, mounted cancellation, and
  a 32-way concurrent consume race that permits exactly one winner.
- The runtime compatibility implementation uses exact plugin versions and
  configuration gates, excludes MOAction-retargeted IDs, cancels on plugin
  topology/signature changes, and quarantines capture for a clean framework
  frame after a change.

## Historical 0.2 release checks

- The full solution builds against the locally installed Dalamud API 15
  development assemblies with zero warnings and zero errors.
- `dotnet format --verify-no-changes` completed cleanly.
- The updated static safety-contract checks pass, including the sole
  ownership-guarded native queue clear and the absence of lock, target, queue
  identity, or retry writes.
- The 0.2.0.0 release ZIP passed manifest, API-level, assembly-version, source
  fingerprint, contents, and hash verification.
- Release ZIP SHA-256:
  `a969766e06b6f32538645392c6f41f53250043d81762ad738a1fbce2ffd50469`
- Source fingerprint:
  `fc3023d85e81d3f1a9227b8d4d977022c8379dbb0b9ce46937604a6adb277b50`

These hashes describe only the historical 0.2 artifact and are not 0.3 release
evidence.

## Still required before promotion beyond testing

A human maintainer must run every case in `LIVE_TEST_MATRIX.md` on the current
FFXIV patch, starting in the Wolves' Den outside a competitive match. This now
includes the safe and unsafe ReAction profiles, NoClippy plus ReAction together,
MOAction retarget exclusions, newest-input Viper-to-heal replacement, native
Turbo ownership and cancellation, single- and multi-action action-only Macro
Turbo, exact ordered counts and duplicate entries, dynamic resolved-ID
revalidation, rejection of non-allowlisted and ineligible action profiles,
unauthorized synthetic-call suppression, synchronous and asynchronous
`MacroLocked` completion, quarantine mode isolation/timeout/clear boundaries,
vanilla line/target selection equivalence, plugin load/unload, and the two-hour
soak test.

Until those gates pass, version 0.3.2.0 must remain testing-exclusive and must
not be described as production-validated, sanctioned, undetectable, or
account-safe.
