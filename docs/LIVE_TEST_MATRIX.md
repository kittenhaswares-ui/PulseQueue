# Live validation matrix

Run on the current FFXIV patch in the Wolves' Den first, with detailed logging
enabled and no competitive match in progress. Redact character and target names
before sharing logs.

Every row below is a pending live gate unless a dated run is explicitly recorded
in the validation report. Its presence in this matrix is not evidence that it
has passed.

## Required safety cases

| Case | Procedure | Pass condition |
|---|---|---|
| Vanilla accepts | Press an immediately ready action | No custom token or replay |
| Native queue claim | Press an eligible GCD inside vanilla's normal queue window | Native handles it; the custom token stays empty and only the complete exact queue tuple is credited to that certified generation |
| Heal/Guard/Purify absolute control | Queue an eligible Viper weave, then separately tap and immediately release each of a certified direct heal, Purify, and Guard; repeat for ready, too-early/outside-horizon, zero-`UseAction`, and certified action-only-macro roots | Every newer certified edge owns priority over the exact older PulseQueue-owned queue; the older weave never executes afterward, even when the tap is no longer held and never starts Turbo, and the result does not depend on new-action readiness or Macro Turbo being enabled |
| Action-only macro absolute control | Queue an eligible weave, then press a certified action-only macro whose current execution emits zero action calls; repeat with a later authored line locally valid | The macro root clears the exact older PulseQueue-owned queue before native macro evaluation in both cases; PulseQueue does not select a line or target |
| Foreign native queue | Create queue state that PulseQueue did not observe as a certified hotbar generation | PulseQueue neither clears nor credits it and does not arm over it |
| Accepted Viper queue transformation | Let a certified Viper press create an exact owned native queue entry, then make the same button's current adjusted ID transform before that accepted entry executes | The accepted exact native tuple remains authoritative and is neither re-resolved nor falsely invalidated; a later Direct Turbo pulse may resolve the transformed ID only after the accepted entry is consumed/acknowledged |
| Standalone accepted-queue watcher | Create an exact owned native queue, end the pending token/hold, then separately change the explicit target, resolver target, territory/instance, job/PvP context, local-player identity, compatibility state, and trigger death, stun, mounting, `BeingMoved`, or a frame gap while PulseQueue remains loaded | Each terminal semantic change clears only the exact unchanged owned entry even with no active scheduler; a consumed, changed, or foreign tuple is never cleared |
| Short rejection | Press Guard/instant ability 20, 80, and 150 ms before its GCD/local recast or animation lock ends | At most one replay, only after readiness |
| Outside cap | Press more than 180 ms early | No token and no later replay |
| New input | Arm A, then press B | A is canceled before B is processed |
| Same input | Arm A, then press A again | Old generation is canceled; at most one new token |
| Weave replacement | Press two short Viper weaves, then press Recuperate, Purify, or Guard while one intent is pending | The newest observed press replaces the pending weave; PulseQueue never replays the older weave afterward |
| Bad target/range/resource | Try while invalid | Never arms |
| Adjusted action changes | Arm a transforming button, then change its state | Cancels; never sends a different action |
| Target change | Arm, then change hard or soft target | Cancels before dispatch |
| Resolver target | Arm a target-capable sentinel-target action, then change mouseover/nameplate target | Cancels before dispatch |
| Stun/death/knockback | Arm immediately before each transition | Cancels before dispatch |
| Mount transition | Arm immediately before mounting or dismounting | Cancels; no replay while mounted |
| Movement action | Press an action that affects player position at a temporal boundary | Never arms |
| Zone/instance/job/PvP change | Arm immediately before transition | Cancels and suspends |
| Replay rejection | Make the exact action invalid at dispatch boundary | Token is consumed; zero retries |
| Frame stall | Inject or observe a frame longer than the remaining lifetime | Cancels; no late dispatch |
| Disable | Disable while pending or while an exact owned native queue outcome is hidden/in flight | The token and hold clear; while the plugin remains loaded, the exact-clear request stays armed until the owned queue is visible, and no PulseQueue-delayed call occurs; foreign or changed queue state remains untouched |
| Full unload boundary | Unload while pending or while an exact owned native queue is currently visible; separately record an unload while an outer hook hides that entry or a native asynchronous macro is still in flight | PulseQueue scheduling stops and a currently visible exact owner is cleared before disposal. The hidden/in-flight repetition documents the limitation: after hooks are disposed, PulseQueue cannot promise to observe or clear a later restoration/outcome; use the loaded Disable case when deferred exact clearing is required |

## Required native Turbo cases

Run these only with PulseQueue native Turbo explicitly enabled and ReAction
Turbo Hotbars and Macro Queue disabled. Start with the default 0 ms initial
delay, 60 ms interval, out-of-combat setting off, and Macro Turbo separately
disabled unless the case explicitly enables it.

| Case | Procedure | Pass condition |
|---|---|---|
| Eligible keyboard slot | Hold keyboard-bound standard-hotbar direct `Action` slots that invoke a normal `Action`, then Guard, Recuperate, or Purify as a direct `PvPAction` | The original press runs normally; each due pulse executes only the same certified slot once, permits at most one matching native action call, and still requires normal client readiness |
| Initial zero-call direct press | Press and hold a safe direct slot early enough that its physical slot execution emits no `UseAction` call | The exact slot can still own Turbo; once ready, a due same-slot pulse may produce one validated native action rather than remaining inert |
| Multiple actions per hold | Hold an eligible direct slot across two or more readiness/acknowledgement cycles | More than one action may execute over the hold, but every pulse uses the same certified slot and each accepted action requires its exact action effect before another pulse |
| Viper/combo transformation | Hold one certified direct slot while its legitimate adjusted/combo action ID changes | The next pulse resolves the new current ID from the same fixed slot/base command, revalidates its exact tuple/target/context, and may execute it once; the transformation alone does not cancel |
| Original/one-shot acknowledgement barrier | Use a short-recast or charged action with effective RTT above the initial delay; separately trigger the one-shot buffer before Turbo becomes due | No Turbo pulse occurs before the exact original or one-shot send receives its matching local-player action effect |
| Acknowledgement barrier | Hold an eligible zero/short-recast action while adding latency/jitter | No second Turbo pulse occurs before the preceding local-player action sequence receives its matching action effect |
| Key release | Release immediately before and during the repeat phase | Ownership clears on key-up and no later scheduled invocation occurs |
| Release preserves vanilla queue | Let the physical original create an accepted native queue entry, then release its Turbo key before execution | The hold ends, but the accepted vanilla queue intent remains; ordinary release is not treated as a terminal safety clear |
| Newest physical press | Hold A until repeating, then physically press and hold eligible B | A is canceled before B can own repeat; A never resumes after B or after B is released |
| Purify/Guard at terminal edge | Let an older certified Viper/action queue be exactly PulseQueue-owned, then press both direct and certified action-only-macro Purify while already stunned; repeat with Guard while `BeingMoved` is already visible, before the next framework update | The newer certified root retains priority and clears only the older exact owned queue. The unsafe new slot still runs through vanilla, starts no buffer/Turbo, and its outcome is not claimed by PulseQueue |
| Pre-execution takeover | Arrange B's physical edge immediately before its slot execution while A is due | The physical edge invalidates A before B executes; A produces no pulse in that interval |
| Quick tap | Tap and release before the first due framework evaluation | Only the original physical press occurs; no delayed repeat |
| Physical-original Turbo decline | Let a physical direct or action-only macro original create a native queue entry, but make that same press decline Turbo ownership (for example out-of-combat with repetition off, or Macro Turbo off) | The original remains vanilla and its accepted queue intent is preserved; declining Turbo does not retroactively safety-clear it |
| Re-press same key | Release an owner, then press the same key again | Fresh ownership and cadence are required; no pulse state leaks from the earlier hold |
| Macro default-off gate | Hold a keyboard-bound standard-hotbar macro while native Turbo is on but Macro Turbo is off | The macro runs only from the original input and never becomes a Turbo owner |
| Single-action macro slot | Enable Macro Turbo and hold a macro containing one action command plus `/micon`/`/merror` metadata | The original press remains vanilla; every pulse executes the same certified macro slot once with `ActionCount=1`, and at most one live-validated accepted/queued action may pass |
| Multi-action macro slot | Hold an action-only macro with two or more fallback commands; make the first line invalid and a later line valid, then reverse availability | Each pulse invokes only the same certified slot; FFXIV evaluates authored order, zero through static `ActionCount` calls are allowed, and only the first accepted/queued outcome may pass |
| Zero-outcome macro epoch | Hold an eligible action-only macro while every authored action is locally invalid, then make one valid | A zero-accepted epoch is a local no-op; the held slot may run again on a later 60 ms cadence, and exactly one newly valid outcome may then pass |
| Accepted macro tail suppression | Use a macro where an early line is accepted/queued and native execution would emit later action calls in the same epoch | The first accepted outcome is marked; all later tail calls are suppressed before original `UseAction`, so the epoch produces at most one native accepted action |
| Macro acknowledgement barrier | Let one macro pulse action be accepted, then keep holding through the next cadence | No later slot pulse occurs until the exact local-player action effect matches the accepted action type, requested/resolved ID, and source sequence |
| Macro budget overflow | Inject or reproduce an `(ActionCount+1)`th Macro-mode action call in one synthetic epoch | The over-budget call is suppressed before native execution, the owner cancels, and no later slot pulse occurs without a fresh certified press |
| Macro target resolution | Use allowed action lines with `<t>`, `<mo>`, `<tt>`, `<f>`, party targets, and `<me>` | FFXIV resolves each authored target at execution time exactly as for the unchanged physical macro. PulseQueue never writes or chooses a target; a different authored fallback may legitimately resolve differently |
| Macro profile exclusions | Put a cast-time action, area/ground-target action, player-movement action, and MOAction-retargeted requested/resolved ID in otherwise action-only macros | The physical macro remains vanilla, but Macro Turbo does not arm or cancels before another pulse; no excluded entry is forwarded by a synthetic epoch |
| Physical macro with zero observed calls | Hold a statically safe macro whose original physical execution emits zero observable action calls | Static same-slot certification may still arm Macro Turbo; no exact physical transcript is required |
| Pulse asynchronous MacroLocked epoch | Let an authorized repeated slot return while its native macro remains `MacroLocked` | No second slot execution overlaps it; the same epoch keeps its single bounded call budget until native unlock |
| MacroLocked cancellation quarantine | While a synthetic epoch remains locked, trigger key-up, target/context change, budget/eligibility failure, or a newer physical press, then let the old executor emit another Macro-mode call | The hold cancels immediately and the stale Macro-mode call is suppressed; quarantine cannot revive ownership |
| Quarantine mode isolation and lifetime | During the previous quarantine, produce unrelated Normal- and Queue-mode calls; keep `MacroLocked` true beyond two seconds, then observe native unlock and plugin disposal | Only quarantined Macro-mode calls are suppressed; Normal/Queue calls remain native. Two seconds emits a diagnostic but never unseals suppression; quarantine clears only after unlock or disposal |
| Unsafe macro rejection | While an older exact owned queue exists, separately test a zero-action macro and add `/assist`, a wait, chat, target, marker, item, gearset, hotbar, and unknown commands | The original macro remains vanilla, but none gains Turbo or owned-queue preemption authority; the older queue is preserved and no later synthetic slot execution occurs |
| Macro content/binding change | While a Macro Turbo owner is held, edit or replace its exact slot/macro | Ownership clears before another slot pulse and requires a fresh physical press |
| Macro key-up/new press | While an action-only multi-action macro owns Turbo, release its raw key; repeat and press a different eligible or ineligible control | Key-up or the newer certified edge cancels the macro owner before another slot execution; the old owner never resumes |
| Ineligible slot types | Hold items, mounts, pets, duty actions, crafting slots, and other unsupported indirect slot types | None becomes a Turbo owner |
| PvPCombo exclusion | Hold a `PvPCombo` hotbar slot | The original input remains vanilla; it never becomes a Turbo owner because its route identity is not yet certified end to end |
| Ineligible action profiles | Hold direct cast-time, ground-target, and player-movement actions | None becomes a Turbo owner |
| Mouse and controller | Click the slot, use controller/cross hotbar, and hold equivalent inputs | No native Turbo ownership or repeat |
| Exact chord and alternate bind | Start with a modified primary/secondary keyboard binding; press/release its main key and modifiers, then try the alternate and a gamepad binding | Only one unambiguous keyboard key/chord owns the hold; releasing or changing any captured chord part cancels, and gamepad never owns |
| Plugin-originated invocation | Trigger the same slot/action through another plugin | It cannot establish physical Turbo ownership |
| Ineligible takeover | While A repeats, invoke a macro with Macro Turbo off, an item, mouse/controller slot, or plugin/native action | A cancels before another pulse; the ineligible input does not become an owner and A never resumes |
| Out-of-combat default | Hold an eligible slot outside combat with the setting off | Original input is untouched; no repeat owner is retained |
| Out-of-combat opt-in | Enable the setting and repeat the previous case | Same-slot repeat is possible only while every other safety gate remains valid |
| Schema 2 migration | Load a schema-2 configuration with native Turbo enabled | Existing native Turbo settings are preserved, schema becomes 4, and Macro Turbo is explicitly reset off and saved once |
| Schema 3 default timing migration | Load schema 3 with the exact legacy 180 ms/80 ms pair | Schema becomes 4 and only that exact pair migrates to the 0 ms/60 ms defaults |
| Schema 3 custom timing preservation | Load schema 3 with any customized valid timing pair | Schema becomes 4 and both custom values remain unchanged |
| Timing bounds | Load negative/oversized initial delay and sub-60/oversized interval values in schema 4 | Values normalize to 0–1000 ms and 60–1000 ms; no faster-than-60 ms configured cadence |
| Future schema | Load a configuration version newer than 4 with Turbo and Macro Turbo enabled and extra unknown JSON data | Both Turbo permissions fail closed; ordinary saves do not rewrite or erase the future file |
| Safety cancellation exact-clear | While an exact PulseQueue-owned native queue entry exists, test death, stun, knockback/forced movement, zoning, logout, job/PvP-context change, and plugin disable | Ownership clears before another repeat; the exact owned queue entry clears, the safety-clear diagnostic increments, and foreign/changed queue state remains untouched |
| Deferred safety clear while loaded | Trigger a terminal safety event while an outer hook hides the owned queue, while a physical original is in flight, and while an asynchronous vanilla macro may produce its outcome later; keep PulseQueue loaded | The exact-clear request stays armed until the owned tuple becomes visible and is cleared; no stale action escapes and no unowned entry is modified |
| Additional safety cancellation | While repeating, mount, change the owned slot/key binding, change targets, and force a >100 ms framework stall | Ownership clears, any exact owned queue state is safety-cleared, and a fresh physical press is required |
| Hard hold lifetime | Hold an eligible key beyond 30 seconds | Ownership ends at 30 seconds; keeping the key down cannot resume it |
| ReAction Turbo conflict | Enable ReAction Turbo while a PulseQueue hold exists | PulseQueue cancels its owner and fails closed; the two repeat sources never run together |
| NoClippy coexistence | Repeat eligible holds with NoClippy 0.5.0.24 enabled | No duplicate source, no premature execution, and no NoClippy lock-mismatch warning; NoClippy remains the sole lock owner |
| Rejected/server-denied call | Cause an accepted direct or macro pulse action to be rejected, or withhold its exact action effect for two seconds | The active hold ends, its pulse token is invalidated, and the action is never retried; a fresh physical release/press is required |

## Required compatibility cases

Use the exact versions listed below. Repeat load/unload and setting changes both
while idle and while a token is pending.

| Case | Procedure | Pass condition |
|---|---|---|
| NoClippy 0.5.0.24 | Enable NoClippy, exercise low/high RTT action bursts, then toggle it while pending | PulseQueue remains available after a clean frame; no duplicate or premature send; NoClippy remains the sole animation-lock correction owner |
| ReAction 1.3.5.1 safe profile | Disable Turbo Hotbars, Macro Queue, and Auto Target; leave Action Stacks empty; test Queue Adjustments and Requeuing on and off | PulseQueue is available; native acceptance remains authoritative, while any newer certified direct or certified action-only macro root clears only an exact older PulseQueue-owned queue regardless of its own readiness or nested action-call count |
| ReAction rejected-drain restoration | Under the safe profile, create an exact older PulseQueue-owned queue, then use a newer certified Heal, Guard, or Purify whose nested native call is rejected while ReAction Queue Adjustments/Requeuing temporarily hides and restores the old entry; repeat after changing plugin load order | PulseQueue does not consume ownership merely because the entry was hidden. If the identical tuple is restored, the two-phase drain retains/defer-reconciles the proof and exact-clears that old entry before it can execute later; a changed or foreign restoration is untouched |
| ReAction Turbo guard | Enable Turbo Hotbars | Pending token clears and PulseQueue suspends with an actionable conflict; no custom replay |
| ReAction Macro Queue guard | Enable Macro Queue | Pending token clears and PulseQueue suspends with an actionable conflict; changing the field while active is caught by the live snapshot guard |
| ReAction Action Stack guard | Add any Action Stack entry | Pending token clears and PulseQueue suspends; removing all entries allows recovery after a clean frame |
| ReAction Auto Target guard | Enable Auto Target | Pending token clears and PulseQueue suspends; no automatic-target action is captured |
| ReAction Auto Dismount coexistence | Enable Auto Dismount and attempt actions while mounted and immediately after native dismount | PulseQueue remains available, but mounted input is excluded/pass-through and no delayed post-dismount action becomes a PulseQueue owner |
| ReAction camera-direction coexistence | Enable Camera Relative Directionals; separately test dash action 29494 with Camera Relative Dashes on | PulseQueue remains available, while action 29494 and every movement-affecting action stay excluded/pass-through and never arm |
| MOAction 4.10.1 | Configure a retargeted action, trigger it at a temporal boundary, and toggle MOAction while pending | Its reported retargeted IDs bypass PulseQueue; MOAction alone owns the action/target transformation |
| Exact native queue identity | Arrange a pre-existing or foreign queue entry while pressing another eligible action | PulseQueue does not credit that queue entry to the new press and does not arm |
| Unsupported version | Load a different/locally modified version of any named integration | PulseQueue fails closed and reports the unsupported version |
| Topology change | Load or unload any integration while pending | Pending generation invalidates immediately; capture resumes only after a clean framework frame and successful reassessment |

## Input and environment coverage

- One-shot buffer: keyboard standard hotbar, mouse-click standard hotbar, and
  controller cross hotbar
- Native Turbo: keyboard-bound standard hotbar only; cover direct-action slots
  and the separately enabled, action-only Macro Turbo path; explicitly
  negative-test mouse, controller/cross-hotbar, unsafe macro, and item paths
- Physical key tap, hold, release, same-key re-press, and newest-key takeover
- Synchronous and asynchronous (`MacroLocked`) action-only macros with zero,
  one, and multiple observed calls within the static maximum
- 30, 60, 120, 144, and 240 FPS
- Low, medium, and high effective RTT with jitter
- Self, friendly, enemy, and no-current-target actions
- PvE instant actions and PvP Guard/instant actions
- No plugin, exact NoClippy 0.5.0.24, the safe ReAction 1.3.5.1 profile, both
  together, and MOAction 4.10.1 in the combinations above

## Release gates

- Zero wrong-action, wrong-target, duplicate, post-cancel, or post-expiry sends.
- Every one-shot input generation maps to zero or one new local source
  sequence. Every individual Turbo pulse token/ordinal maps to zero or one new
  local source sequence; a hold may contain multiple acknowledged ordinals.
- Plugin topology changes are serialized with dispatch and invalidate
  immediately. Critical ReAction fields and MOAction ownership are rechecked
  before arming/final dispatch; a mismatch clears first. Full profile changes
  are reflected by the bounded 500 ms reassessment and clean-frame quarantine.
- With the supported ReAction profile, a newer heal, Purify, or Guard certified
  as a direct root or certified action-only macro hotbar root clears an older
  exact PulseQueue-owned queue before its own slot runs, even when it is too
  early or emits zero `UseAction` calls. It is never followed by the older owned
  weave.
- Newest-root preemption and terminal safety clearing require exact unchanged
  PulseQueue ownership. Foreign, changed, MOAction-owned, and unproven queues are
  never cleared. Ordinary release, maximum hold expiry, and physical-original
  Turbo decline preserve accepted vanilla intent. An unsafe/non-action-only
  macro root never receives native-queue preemption authority.
- ReAction hide/restore is exercised in both hook orders. A rejected nested call
  that restores the identical older owner cannot lose its proof or later execute
  the old weave; a changed/foreign restored entry is never cleared.
- Accepted queue safety remains active with no pending token or Turbo hold. The
  standalone watcher exact-clears terminal semantic changes, while an adjusted-
  ID-only Viper transformation leaves the accepted native tuple authoritative.
- Native Turbo never starts from a macro unless the separate Macro Turbo opt-in
  is enabled and strict action-only analysis finds one or more action commands
  plus only icon/error metadata. Each pulse executes the same certified macro
  slot once; FFXIV alone evaluates the authored line order and targets.
  PulseQueue never selects or rewrites a line, action, fallback, or target.
  Turbo never starts from an unsafe macro, item, mouse click, controller input,
  cross hotbar, or plugin-originated call. ReAction Turbo Hotbars and Macro
  Queue remain off.
- Static `ActionCount` is a maximum for each synthetic epoch, not an exact
  transcript requirement. Zero through that many calls are allowed; each is
  independently live-validated, at most the first accepted/queued outcome may
  pass, and every later tail or over-budget call is suppressed before native
  execution. A zero-outcome epoch is a local no-op.
- Each currently resolved macro action must pass live eligibility again. No
  cast-time, area/ground-target, player-movement, or MOAction-owned call becomes
  or remains a Macro Turbo owner.
- Cancellation during asynchronous `MacroLocked` execution leaves at most one
  stale-epoch quarantine. It suppresses only unauthorized Macro-mode calls,
  never Normal/Queue or original physical calls. A two-second diagnostic cannot
  authorize stale execution; suppression remains sealed until native unlock or
  disposal.
- Key release and every safety transition leave zero delayed repeat calls. A
  newer physical press permanently invalidates the older held owner.
- Full unload clears only a currently visible exact owner before disposal. No
  release claim extends to an outer-hook restoration or asynchronous native
  outcome that becomes observable only after PulseQueue's hooks are gone.
- A held direct-action key may produce multiple actions, but each pulse reruns
  exactly the same certified direct slot once, live-resolves its current
  combo/transformed ID, validates its exact tuple/target/context, and permits at
  most one native call. A Macro Turbo hold may produce different authored
  actions only through repeated execution of the same certified macro slot and
  vanilla FFXIV macro evaluation. Every accepted direct or macro action requires
  an exact action-effect acknowledgement; rejection or a two-second timeout is
  terminal. No configured interval below 60 ms is accepted.
- No MOAction-retargeted ID is captured or replayed by PulseQueue.
- Eligible actions are never sent before full client readiness.
- Two-hour soak across deaths, zoning, target churn, and plugin toggles without a
  stale replay or hook-disposal failure.

Record the FFXIV build, Dalamud API/build, plugin version, FPS, input device,
conflict-plugin versions, sample count, and effective RTT for every run.
