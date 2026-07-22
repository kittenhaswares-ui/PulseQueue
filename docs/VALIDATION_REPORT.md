# Validation report — 0.2.0.0 candidate

- Date: 2026-07-22
- Target: Dalamud API 15 / .NET 10
- Release channel: custom repository, testing-exclusive

## Evidence behind the compatibility update

The analyzed play session used NoClippy 0.5.0.24 and ReAction 1.3.5.1. The
previous PulseQueue 0.1 build classified both as hard conflicts, so it could not
have provided buffering while either was active; the responsive feel in that
part of the session came from those integrations rather than PulseQueue.

The anonymized post-install NoClippy sample contained 7,514 accepted actions.
Observed action-response time had a median of about 313 ms, a 95th percentile
of about 420 ms, and a maximum of about 512 ms. NoClippy reported positive
animation-lock savings on every parsed action, with a median near 270 ms. This
supports keeping NoClippy authoritative for animation-lock correction while
PulseQueue limits itself to bounded one-shot intent retention.

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

## Completed automated evidence

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

## Completed release checks

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

## Still required before any production claim

A human maintainer must run every case in `LIVE_TEST_MATRIX.md` on the current
FFXIV patch, starting in the Wolves' Den outside a competitive match. This now
includes the safe and unsafe ReAction profiles, NoClippy plus ReAction together,
MOAction retarget exclusions, newest-input Viper-to-heal replacement, plugin
load/unload, and the two-hour soak test.

Until those gates pass, version 0.2.0.0 must remain testing-exclusive and must
not be described as production-validated, sanctioned, undetectable, or
account-safe.
