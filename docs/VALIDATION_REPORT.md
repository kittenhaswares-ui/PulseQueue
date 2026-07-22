# Validation report — 0.3.1.0 testing release

- Date: 2026-07-22
- Target: Dalamud API 15 / .NET 10
- Release channel: custom repository, testing-exclusive

## 0.3.1 testing status

Version 0.3.1 fixes the live Turbo hold detector and changes every synthetic
pulse to exactly one immutable captured native action/target tuple. Direct
`Action` slots and strictly verified single-action `Macro` slots share that one
dispatch path. The slot and macro are never synthetically executed again, and
an adjusted-action or combo transformation terminates the hold instead of
following a different action. `PvPCombo` remains excluded until route identity
can be proven end to end.

Turbo and Macro Turbo are separate opt-ins and both default off. The default
initial delay is 180 ms, the repeat interval is 80 ms, and out-of-combat repeat
defaults off. Macro analysis permits exactly one action command plus narrowly
defined metadata and an optional original-only `/assist`; multi-action and
unknown-command macros fail closed. Items, mouse clicks, controller/cross-hotbar
input, and plugin-originated calls are outside the repeat source.

This changes the meaning of one hardware hold deliberately: one held eligible
slot may execute multiple actions over time. It does not change the one-shot
buffer contract. Every newer certified physical hotbar edge cancels an older
owner before the new slot executes, even when the new input cannot own Turbo.
Key release plus every safety transition must terminate repetition without a
late call.

ReAction Turbo Hotbars and Macro Queue must remain disabled so they cannot
compete with or rewrite the native source. NoClippy remains the sole
animation-lock correction owner. The 0.3.1 artifact is built and verified as
testing-exclusive; live gameplay validation remains required.

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

One Viper button was observed resolving across several adjusted action IDs.
Version 0.3.1 deliberately ends ownership when the current resolved ID differs
from the one captured by the physical press; it never follows the transformed
combo action.

The anonymized post-install NoClippy sample contained 7,514 accepted actions.
Observed action-response time had a median of about 313 ms, a 95th percentile
of about 420 ms, and a maximum of about 512 ms. NoClippy reported positive
animation-lock savings on every parsed action, with a median near 270 ms. This
supports keeping NoClippy authoritative for animation-lock correction. The
PulseQueue one-shot path remains bounded intent retention; the separate
opt-in Turbo path only invokes its exact captured action after current readiness and
acknowledgement gates pass.

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

## 0.3.1 automated release evidence

- The complete dependency-free suite passes: 106/106 tests.
- The new cases cover certified-release provenance, startup/typematic physical
  hold identity, strict single-action macro analysis, exact queue-drain ownership,
  newest-owner replacement,
  hard timing bounds, no catch-up, concurrent single-pulse issuance, terminal
  pulse rejection, every Turbo cancellation boundary, the 30-second cap,
  schema-1/schema-2 migration, timing normalization, future-schema no-rewrite
  behavior, and explicit reset.
- Exact Turbo acknowledgement tests require matching action type,
  requested/resolved action ID, and either the exact immediate source sequence
  or a wrap-safe sequence newer than the exact queued baseline.
- The full solution builds against the locally installed Dalamud API 15 / .NET
  10 assemblies with zero build warnings and zero errors.
- Core, self-test, and plugin format verification pass. The static safety
  contract and `git diff --check` pass.
- The hardened release workflow deletes only the validated generated package
  before building, requires a freshly produced archive, and then verifies the
  exact ZIP allowlist, manifests, API level, assembly version, source
  fingerprint, and archive hash.
- Release ZIP SHA-256:
  `02d4f6a48ab6e2539ebbfdd5af00e7b5c9d7d6501696e461116912552d59d8f7`
- Source fingerprint:
  `bb8cc5a236c84341779ad461fae2901f3af78da9cff847db592860f2970d619c`

These automated checks authorize a testing-only package; they do not replace
the native in-game cases below.

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
Turbo ownership and cancellation, safe/unsafe Macro Turbo, plugin load/unload,
and the two-hour soak test.

Until those gates pass, version 0.3.1.0 must remain testing-exclusive and must
not be described as production-validated, sanctioned, undetectable, or
account-safe.
