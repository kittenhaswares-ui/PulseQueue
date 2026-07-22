# Validation report — 0.3.0.0 testing release

- Date: 2026-07-22
- Target: Dalamud API 15 / .NET 10
- Release channel: custom repository, testing-exclusive

## 0.3 testing status

Version 0.3 introduces an opt-in native same-slot repeat source limited to
physical keyboard bindings on standard hotbars and direct `Action` slots with
one exactly correlated `Action`/`PvPAction` invocation. `PvPCombo` is excluded
until its route identity can be proven end to end. Turbo defaults off, uses a
180 ms initial delay and 80 ms repeat interval, and does not repeat out of
combat unless explicitly enabled. Macros, items, mouse clicks,
controller/cross-hotbar input, and plugin-originated calls are not part of the
repeat source.

This changes the meaning of one hardware hold deliberately: one held eligible
slot may execute multiple actions over time. It does not change the one-shot
buffer contract. Every newer certified physical hotbar edge cancels an older
owner before the new slot executes, even when the new input cannot own Turbo.
Key release plus every safety transition must terminate repetition without a
late call.

ReAction Turbo Hotbars must remain disabled so it cannot compete with the native
source. NoClippy remains the sole animation-lock correction owner. The 0.3
artifact is built and verified as testing-exclusive; live gameplay validation
and any promotion beyond the testing channel remain intentionally deferred.

## Evidence behind the compatibility update

The analyzed play session used NoClippy 0.5.0.24 and ReAction 1.3.5.1. The
previous PulseQueue 0.1 build classified both as hard conflicts, so it could not
have provided buffering while either was active; the responsive feel in that
part of the session came from those integrations rather than PulseQueue.

The anonymized post-install NoClippy sample contained 7,514 accepted actions.
Observed action-response time had a median of about 313 ms, a 95th percentile
of about 420 ms, and a maximum of about 512 ms. NoClippy reported positive
animation-lock savings on every parsed action, with a median near 270 ms. This
supports keeping NoClippy authoritative for animation-lock correction. The
PulseQueue one-shot path remains bounded intent retention; the separate
opt-in Turbo path only invokes its exact owned slot after current readiness and
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
- ReAction 1.3.5.1 is supported only with Turbo Hotbars, Auto Target, Auto
  Dismount, and Camera Relative Directionals off and Action Stacks empty.
  Queue adjustments remain authoritative except that the newest valid manual
  input can consume one exact older native queue entry previously certified by
  PulseQueue.
- MOAction 4.10.1 retargeted IDs are obtained from its published IPC and are
  excluded from PulseQueue capture.
- Unknown versions, unreadable settings, missing required integration data, and
  topology/configuration changes fail closed.

## 0.3 automated release evidence

- The complete dependency-free suite passes: 86/86 tests.
- The new cases cover certified-release provenance, newest-owner replacement,
  hard timing bounds, no catch-up, concurrent single-pulse issuance, terminal
  pulse rejection, every Turbo cancellation boundary, the 30-second cap,
  schema-1 migration, timing normalization, future-schema no-rewrite behavior,
  and explicit reset.
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
  `b6449a460318d21a5792e5ccb7cf48335273995012ddc809daee21fe92613eb6`
- Source fingerprint:
  `9b35fd0121afd7d05890968e0a27fc81378d285ebd81ebccdc7e25c151900e44`

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
Turbo ownership and cancellation, plugin load/unload, and the two-hour soak
test.

Until those gates pass, version 0.3.0.0 must remain testing-exclusive and must
not be described as production-validated, sanctioned, undetectable, or
account-safe.
