# Changelog

## 0.3.2.0 — 2026-07-22

- Reworks the separate, default-off Macro Turbo mode around native macro-slot
  ownership. A pulse now executes the same physically certified keyboard-bound
  standard-hotbar macro slot once instead of replaying a captured action tuple.
- Allows action-only macros with one or more `/ac`, `/action`, `/pvpac`, or
  `/pvpaction` lines plus icon/error metadata. FFXIV evaluates those authored
  lines and their authored targets in its normal order on every slot execution;
  PulseQueue does not select a line, fallback action, skill, or target.
- Freezes the certified physical execution into an exact ordered action
  transcript whose entry count must equal the statically analyzed
  `ActionCount`. Duplicate action lines remain duplicate entries; missing,
  extra, reordered, or semantically changed calls fail closed.
- Allows a transcript entry's resolved action ID to change only after the same
  requested action is re-resolved and passes the complete live eligibility
  checks again. Cast-time, area/ground-target, player-movement, and
  MOAction-owned actions are excluded from Macro Turbo as well as direct Turbo.
- Rejects zero-action macros and macros containing `/assist`, waits, chat,
  target mutation, markers, items, gearsets, hotbar mutation, unknown commands,
  or any other command outside the action and icon/error allowlist. The original
  physical press always remains vanilla even when Turbo ownership is rejected.
- Keeps the one-shot smart buffer and direct-action Turbo contracts exact and
  unchanged. Multi-action behavior exists only behind the separate Macro Turbo
  opt-in and only for the one certified macro slot held by the player.
- Cancels Macro Turbo on raw key-up, any newer certified physical press, macro
  content or binding change, target/context change, and every existing
  death/stun/forced-movement/zone/plugin compatibility boundary. A canceled
  macro owner cannot resume without a fresh physical press.
- Suppresses an unauthorized action call inside the synthetic macro-slot chain
  instead of forwarding it to native execution. If a canceled or mismatched
  synthetic execution continues asynchronously under `MacroLocked`, a bounded
  quarantine suppresses only its Macro-mode calls until unlock, a newer
  certified unlocked root macro press, plugin disposal, or the two-second cap;
  Normal- and Queue-mode calls remain native.
- Retains the compatibility requirement that ReAction Turbo Hotbars and Macro
  Queue remain disabled. NoClippy remains the sole animation-lock correction
  owner.
- Documents the 0.3.1 live failure that motivated the change: the short test
  contained 23 macro attempts, but all tested macro slots were multi-action and
  therefore none could own the former single-action Macro Turbo path.
- Expands the dependency-free safety suite to 119/119 passing self-tests,
  including exact-count transcript freeze, duplicate/order preservation,
  dynamic resolved IDs, incomplete/extra/mismatch failure, and terminal cursor
  behavior.

## 0.3.1.0 — 2026-07-22

- Fixes the live Turbo failure shown by the short test: 44 apparent starts
  produced only one pulse because logical input gaps and typematic callbacks
  repeatedly replaced the physical hold. Ownership now survives every logical
  gap until the original raw key is actually released, and owned native held
  repeats are suppressed instead of restarting the delay.
- Turbo pulses now invoke exactly one immutable captured action/target tuple
  for both direct and macro slots. The slot itself is never synthetically rerun,
  and any adjusted-action/combo transformation ends the hold.
- Extends the acknowledgement barrier to the original physical send and any
  one-shot replay. A Turbo pulse cannot overtake an unacknowledged original or
  buffered action; rejected, ambiguous, or missing acknowledgements end the
  hold without retry.
- Adds a second, explicit Macro Turbo opt-in. Schema 3 migrates every older
  configuration with Macro Turbo off, preserves ordinary Turbo settings from
  schema 2, and keeps both permissions inert for unknown future schemas.
- Adds `/pulsequeue turbo macros on|off` and an in-game warning that only a
  strictly verified single-action macro is eligible. The original macro runs
  once; Turbo repeats only its exact captured native action and target.
- Requires ReAction 1.3.5.1 Macro Queue to remain disabled and includes that
  field in the lightweight live compatibility snapshot guard.
- Adds exact one-shot native-queue drain authorization, startup release
  baselining, safe individual macro ID 0 support, strict observed macro-ID
  decoding, early ActionEffect correlation, and held-repeat diagnostics.

## 0.3.0.0 — 2026-07-22

- Adds an opt-in native same-slot repeat source for keyboard-bound standard
  hotbar slots. It is disabled by default and accepts only direct `Action`
  slots with one correlated `Action`/`PvPAction` invocation. `PvPCombo`, macros,
  items, mouse clicks, controller/cross-hotbar input, and plugin-originated
  calls are outside its testing scope.
- Uses explicit physical key ownership: releasing the key stops its repeat,
  and the newest physical hotbar press cancels an older held slot before the
  new slot can become the repeat owner.
- Resolves the exact keyboard key, primary/secondary binding index, and modifier
  chord from the native keybind record. Gamepad/mouse virtual keys and ambiguous
  simultaneous bindings fail closed. Any newer native action also cancels the
  old owner even if it cannot start Turbo itself.
- Allows a held slot to execute more than one action over time. Each repeat is
  still a fresh exact invocation of that same slot; it is not a one-shot
  replay, action substitution, target selector, server-rejection retry, or
  multi-action queue.
- Defaults to a 180 ms initial delay and 80 ms repeat interval. Configuration
  schema 2 normalizes the initial delay to 0–1000 ms and the interval to
  60–1000 ms; out-of-combat repeat is disabled by default.
- Blocks every later pulse until a local-player action effect matches the
  preceding pulse's action type, requested/resolved ID, and source sequence.
  Local rejection, missing acknowledgement, or the hard 30-second hold limit
  ends ownership without retry.
- Cancels repeat ownership on key release, newer physical input, death, stun,
  knockback/forced movement, zoning, logout, job/PvP-context change, plugin
  disable, unsafe compatibility state, and other existing safety transitions.
- Requires ReAction Turbo Hotbars to remain disabled so two independent repeat
  sources can never compete. NoClippy remains the sole animation-lock writer.
- Preserves unknown future configuration files without rewriting them and
  disables Turbo in memory until a compatible build or explicit reset is used.

This is a packaged testing-only 0.3 release. The native live matrix remains
required before any promotion or production-readiness claim.

## 0.2.0.0 — 2026-07-22

- Supports NoClippy 0.5.0.24 while leaving animation-lock correction entirely
  under NoClippy's control.
- Supports ReAction 1.3.5.1 only when Turbo Hotbars, Auto Target, Auto Dismount,
  and Camera Relative Directionals are off and Action Stacks is empty.
- Excludes action IDs retargeted through MOAction 4.10.1's published IPC.
- Makes every newly observed standard/cross-hotbar input invalidate the pending
  generation so the newest input replaces an older buffered weave.
- Lets a newer valid hotbar input replace one exact unchanged native queue entry
  only when that entry was proven to come from an older certified hotbar input;
  foreign/native integration queues remain untouched.
- Classifies native acceptance using an exact before/after queue tuple rather
  than crediting unrelated or pre-existing queue activity.
- Clears pending input on plugin/configuration changes and mounting; movement
  actions and ReAction's camera-relative action 29494 exception are no longer
  eligible for buffering.
- Handles the next charge boundary for multi-charge recasts and linearizes
  topology/knockback cancellation with final dispatch.
- Fails closed for unknown integration versions or unreadable/unsafe settings.

This remains a testing-only custom-repository release pending completion of the
expanded live validation matrix.

## 0.1.0.0 — 2026-07-21

- Initial one-shot buffer with a 180 ms hard cap, adaptive local response
  timing, immutable action/target capture, and fail-closed cancellation.
- Suspended entirely while NoClippy or ReAction variants were loaded.
