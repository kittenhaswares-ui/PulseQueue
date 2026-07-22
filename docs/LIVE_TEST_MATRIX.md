# Live validation matrix

Run on the current FFXIV patch in the Wolves' Den first, with detailed logging
enabled and no competitive match in progress. Redact character and target names
before sharing logs.

## Required safety cases

| Case | Procedure | Pass condition |
|---|---|---|
| Vanilla accepts | Press an immediately ready action | No custom token or replay |
| Native queue claim | Press an eligible GCD inside vanilla's normal queue window | Native handles it; custom token stays empty and the exact certified queue becomes replaceable only by a newer valid hotbar input |
| Owned native replacement | Queue an eligible Viper action, then press ready/near-ready Recuperate, Purify, or Guard before it executes | The exact older certified queue is cleared and only the newest eligible input can be retained; no older PulseQueue-owned action executes afterward |
| Far-future replacement guard | Queue an eligible action, then press an action whose local readiness is outside the hold window | PulseQueue does not clear the older native queue for an action it cannot deliver within its bound |
| Foreign native queue | Create queue state that PulseQueue did not observe as a certified hotbar generation | PulseQueue neither clears nor credits it and does not arm over it |
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
| Disable/unload | Disable or unload while pending | Clears and never calls later |

## Required native Turbo cases

Run these only with PulseQueue native Turbo explicitly enabled and ReAction
Turbo Hotbars and Macro Queue disabled. Start with the default 180 ms initial
delay, 80 ms interval, out-of-combat setting off, and Macro Turbo separately
disabled unless the case explicitly enables it.

| Case | Procedure | Pass condition |
|---|---|---|
| Eligible keyboard slot | Hold keyboard-bound standard-hotbar direct `Action` slots that invoke a normal `Action`, then Guard, Recuperate, or Purify as a direct `PvPAction` | The original press runs normally; after the initial delay only its exact captured action/target tuple may run at the configured interval while held; the slot is not synthetically rerun |
| Multiple actions per hold | Hold an eligible action across two or more readiness cycles | More than one action may execute, but every execution is the same immutable captured action/target tuple and each requires normal client readiness |
| Original/one-shot acknowledgement barrier | Use a short-recast or charged action with effective RTT above the initial delay; separately trigger the one-shot buffer before Turbo becomes due | No Turbo pulse occurs before the exact original or one-shot send receives its matching local-player action effect |
| Acknowledgement barrier | Hold an eligible zero/short-recast action while adding latency/jitter | No second Turbo pulse occurs before the preceding local-player action sequence receives its matching action effect |
| Key release | Release immediately before and during the repeat phase | Ownership clears on key-up and no later scheduled invocation occurs |
| Newest physical press | Hold A until repeating, then physically press and hold eligible B | A is canceled before B can own repeat; A never resumes after B or after B is released |
| Pre-execution takeover | Arrange B's physical edge immediately before its slot execution while A is due | The physical edge invalidates A before B executes; A produces no pulse in that interval |
| Quick tap | Tap and release before the initial delay | Only the original physical press occurs; no delayed repeat |
| Re-press same key | Release an owner, then press the same key again | A fresh initial delay is required; no cadence leaks from the earlier hold |
| Macro default-off gate | Hold a keyboard-bound standard-hotbar macro while native Turbo is on but Macro Turbo is off | The macro runs only from the original input and never becomes a Turbo owner |
| Single-action macro slot | Enable Macro Turbo and hold a macro containing one action command plus `/micon`/`/merror` metadata | The original press remains vanilla; its one-entry transcript freezes with `ActionCount=1`, and every later pulse executes the same certified standard-hotbar macro slot once while FFXIV resolves the authored action/target |
| Multi-action macro slot | Hold an action-only macro with two or more action commands; make the first line invalid and a later line valid, then reverse their availability | The physical execution freezes every emitted action call in authored order with observed count exactly equal to static `ActionCount`; every pulse invokes only the same certified macro slot once, and the action that succeeds matches vanilla FFXIV evaluation |
| Duplicate transcript entries | Use an action-only macro containing the same eligible action line twice, including identical targets and parameters | Both occurrences remain separate ordered transcript entries and must both be observed on every execution; PulseQueue never deduplicates them |
| Transcript count/order failure | Exercise missing, extra, reordered, and semantically changed nested action-call test cases during a synthetic macro epoch | The current unauthorized call is suppressed before native execution, the owner cancels, and no later slot pulse occurs without a fresh certified press |
| Dynamic resolved action ID | Use the same requested macro action across a legitimate combo/level adjustment that changes only its current resolved ID | A later pulse is allowed only if the newly resolved ID passes live profile, target, compatibility, and MOAction checks again; any other transcript-field change cancels |
| Macro target resolution | Use allowed action lines with `<t>`, `<mo>`, and `<me>` in separate stable-context tests | FFXIV resolves the authored target exactly as it does for a physical macro press; PulseQueue does not substitute a target, and any captured target/context change cancels before another pulse |
| Macro profile exclusions | Put a cast-time action, area/ground-target action, player-movement action, and MOAction-retargeted requested/resolved ID in otherwise action-only macros | The physical macro remains vanilla, but Macro Turbo does not arm or cancels before another pulse; no excluded entry is forwarded by a synthetic epoch |
| Initial asynchronous MacroLocked transcript | Use an eligible macro whose certified physical execution remains `MacroLocked` before all `ActionCount` calls arrive | The exact ordered transcript may freeze only on owned unlock within two seconds; timeout, missing/extra entry, foreign lock ownership, key-up, or a newer press cancels without adopting a later lock |
| Pulse asynchronous MacroLocked epoch | Let an authorized repeated slot return while its native macro remains `MacroLocked` | No second slot execution overlaps it; the execution completes only after the same epoch emits the entire ordered transcript and unlocks |
| MacroLocked cancellation quarantine | While a synthetic epoch remains locked, trigger key-up, target/context change, transcript mismatch, or a newer physical press, then let the old executor emit another Macro-mode call | The hold cancels immediately and the stale Macro-mode call is suppressed; quarantine cannot revive ownership |
| Quarantine mode isolation and lifetime | During the previous quarantine, produce unrelated Normal- and Queue-mode action calls; then separately observe unlock, the two-second bound, a newer unlocked certified root macro press, and plugin disposal | Only quarantined Macro-mode calls are suppressed; Normal/Queue calls remain native, and each listed boundary clears the quarantine without a stale macro call escaping |
| Unsafe macro rejection | Separately test a zero-action macro and add `/assist`, a wait, chat, target, marker, item, gearset, hotbar, and unknown commands | The original macro remains vanilla, but none becomes a Turbo owner; no later synthetic slot execution occurs |
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
| Out-of-combat opt-in | Enable the setting and repeat the previous case | Exact-action repeat is possible only while every other safety gate remains valid |
| Schema 2 migration | Load a schema-2 configuration with native Turbo enabled | Existing native Turbo settings are preserved, schema becomes 3, and Macro Turbo is explicitly reset off and saved once |
| Timing bounds | Load negative/oversized initial delay and sub-60/oversized interval values in schema 3 | Values normalize to 0–1000 ms and 60–1000 ms; no faster-than-60 ms configured cadence |
| Future schema | Load a configuration version newer than 3 with Turbo and Macro Turbo enabled and extra unknown JSON data | Both Turbo permissions fail closed; ordinary saves do not rewrite or erase the future file |
| Safety cancellation | While repeating, test death, stun, knockback/forced movement, zoning, logout, job/PvP-context change, and plugin disable/unload | Ownership clears before another repeat and never resumes without a new physical press |
| Additional safety cancellation | While repeating, mount, change the owned slot/key binding, change targets, and force a >100 ms framework stall | Ownership clears before another repeat and requires a fresh physical press |
| Hard hold lifetime | Hold an eligible key beyond 30 seconds | Ownership ends at 30 seconds; keeping the key down cannot resume it |
| ReAction Turbo conflict | Enable ReAction Turbo while a PulseQueue hold exists | PulseQueue cancels its owner and fails closed; the two repeat sources never run together |
| NoClippy coexistence | Repeat eligible holds with NoClippy 0.5.0.24 enabled | No duplicate source, no premature execution, and no NoClippy lock-mismatch warning; NoClippy remains the sole lock owner |
| Rejected/server-denied call | Cause a held slot invocation to fail or be rejected | The active hold ends immediately, its pulse token is invalidated, and no later call occurs until a fresh physical release/press establishes a new owner |

## Required compatibility cases

Use the exact versions listed below. Repeat load/unload and setting changes both
while idle and while a token is pending.

| Case | Procedure | Pass condition |
|---|---|---|
| NoClippy 0.5.0.24 | Enable NoClippy, exercise low/high RTT action bursts, then toggle it while pending | PulseQueue remains available after a clean frame; no duplicate or premature send; NoClippy remains the sole animation-lock correction owner |
| ReAction 1.3.5.1 safe profile | Disable Turbo Hotbars, Macro Queue, Auto Target, Auto Dismount, and Camera Relative Directionals; leave Action Stacks empty; test Queue Adjustments and Requeuing on and off | PulseQueue is available; native acceptance remains authoritative, while the newest valid manual input may clear only an exact older queue entry certified by PulseQueue |
| ReAction Turbo guard | Enable Turbo Hotbars | Pending token clears and PulseQueue suspends with an actionable conflict; no custom replay |
| ReAction Macro Queue guard | Enable Macro Queue | Pending token clears and PulseQueue suspends with an actionable conflict; changing the field while active is caught by the live snapshot guard |
| ReAction Action Stack guard | Add any Action Stack entry | Pending token clears and PulseQueue suspends; removing all entries allows recovery after a clean frame |
| ReAction Auto Target guard | Enable Auto Target | Pending token clears and PulseQueue suspends; no automatic-target action is captured |
| ReAction Auto Dismount guard | Enable Auto Dismount | Pending token clears and PulseQueue suspends; no delayed post-dismount action can be treated as a manual generation |
| ReAction camera-direction guard | Enable Camera Relative Directionals; separately test dash action 29494 with Camera Relative Dashes on | Directionals suspend PulseQueue; action 29494 and all movement actions never arm |
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
- Synchronous and asynchronous (`MacroLocked`) action-only macros with one,
  multiple, and duplicate transcript entries
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
- With the supported ReAction profile, a newer observed heal, Purify, or Guard
  is never followed by a replay of an older PulseQueue-owned weave.
- Native Turbo never starts from a macro unless the separate Macro Turbo opt-in
  is enabled and strict action-only analysis finds one or more action commands
  plus only icon/error metadata. Each pulse executes the same certified macro
  slot once; FFXIV alone evaluates the authored line order and targets.
  PulseQueue never selects or rewrites a line, action, fallback, or target.
  Turbo never starts from an unsafe macro, item, mouse click, controller input,
  cross hotbar, or plugin-originated call. ReAction Turbo Hotbars and Macro
  Queue remain off.
- The certified physical macro execution and every synthetic epoch emit exactly
  static `ActionCount` entries in the same order. Duplicate entries are
  preserved. Missing, extra, reordered, or semantically mismatched calls yield
  zero unauthorized native action sends and terminate the owner.
- A changed resolved ID is accepted only after the same requested transcript
  entry passes current live eligibility again. No cast-time, area/ground-target,
  player-movement, or MOAction-owned entry becomes or remains a Macro Turbo
  owner.
- Cancellation during asynchronous `MacroLocked` execution leaves at most one
  bounded stale-epoch quarantine. It suppresses only unauthorized Macro-mode
  calls, never Normal/Queue or original physical calls, and clears on unlock,
  a newer certified unlocked root macro press, disposal, or two seconds.
- Key release and every safety transition leave zero delayed repeat calls. A
  newer physical press permanently invalidates the older held owner.
- A held direct-action key may produce multiple actions, but each maps to the
  same immutable captured action/target tuple and its direct slot is never
  synthetically rerun. A Macro Turbo hold may produce different authored
  actions only through repeated execution of the same certified macro slot and
  vanilla FFXIV macro evaluation. No configured interval below 60 ms is
  accepted.
- No MOAction-retargeted ID is captured or replayed by PulseQueue.
- Eligible actions are never sent before full client readiness.
- Two-hour soak across deaths, zoning, target churn, and plugin toggles without a
  stale replay or hook-disposal failure.

Record the FFXIV build, Dalamud API/build, plugin version, FPS, input device,
conflict-plugin versions, sample count, and effective RTT for every run.
