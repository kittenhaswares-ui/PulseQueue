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

## Required compatibility cases

Use the exact versions listed below. Repeat load/unload and setting changes both
while idle and while a token is pending.

| Case | Procedure | Pass condition |
|---|---|---|
| NoClippy 0.5.0.24 | Enable NoClippy, exercise low/high RTT action bursts, then toggle it while pending | PulseQueue remains available after a clean frame; no duplicate or premature send; NoClippy remains the sole animation-lock correction owner |
| ReAction 1.3.5.1 safe profile | Disable Turbo Hotbars, Auto Target, Auto Dismount, and Camera Relative Directionals; leave Action Stacks empty; test Queue Adjustments and Requeuing on and off | PulseQueue is available; native acceptance remains authoritative, while the newest valid manual input may clear only an exact older queue entry certified by PulseQueue |
| ReAction Turbo guard | Enable Turbo Hotbars | Pending token clears and PulseQueue suspends with an actionable conflict; no custom replay |
| ReAction Action Stack guard | Add any Action Stack entry | Pending token clears and PulseQueue suspends; removing all entries allows recovery after a clean frame |
| ReAction Auto Target guard | Enable Auto Target | Pending token clears and PulseQueue suspends; no automatic-target action is captured |
| ReAction Auto Dismount guard | Enable Auto Dismount | Pending token clears and PulseQueue suspends; no delayed post-dismount action can be treated as a manual generation |
| ReAction camera-direction guard | Enable Camera Relative Directionals; separately test dash action 29494 with Camera Relative Dashes on | Directionals suspend PulseQueue; action 29494 and all movement actions never arm |
| MOAction 4.10.1 | Configure a retargeted action, trigger it at a temporal boundary, and toggle MOAction while pending | Its reported retargeted IDs bypass PulseQueue; MOAction alone owns the action/target transformation |
| Exact native queue identity | Arrange a pre-existing or foreign queue entry while pressing another eligible action | PulseQueue does not credit that queue entry to the new press and does not arm |
| Unsupported version | Load a different/locally modified version of any named integration | PulseQueue fails closed and reports the unsupported version |
| Topology change | Load or unload any integration while pending | Pending generation invalidates immediately; capture resumes only after a clean framework frame and successful reassessment |

## Input and environment coverage

- Keyboard standard hotbar
- Mouse-click standard hotbar
- Controller cross hotbar
- Key repeat/held input
- 30, 60, 120, 144, and 240 FPS
- Low, medium, and high effective RTT with jitter
- Self, friendly, enemy, and no-current-target actions
- PvE instant actions and PvP Guard/instant actions
- No plugin, exact NoClippy 0.5.0.24, the safe ReAction 1.3.5.1 profile, both
  together, and MOAction 4.10.1 in the combinations above

## Release gates

- Zero wrong-action, wrong-target, duplicate, post-cancel, or post-expiry sends.
- Every custom generation maps to zero or one new local source sequence.
- Plugin topology changes are serialized with dispatch and invalidate
  immediately. Critical ReAction fields and MOAction ownership are rechecked
  before arming/final dispatch; a mismatch clears first. Full profile changes
  are reflected by the bounded 500 ms reassessment and clean-frame quarantine.
- With the supported ReAction profile, a newer observed heal, Purify, or Guard
  is never followed by a replay of an older PulseQueue-owned weave.
- No MOAction-retargeted ID is captured or replayed by PulseQueue.
- Eligible actions are never sent before full client readiness.
- Two-hour soak across deaths, zoning, target churn, and plugin toggles without a
  stale replay or hook-disposal failure.

Record the FFXIV build, Dalamud API/build, plugin version, FPS, input device,
conflict-plugin versions, sample count, and effective RTT for every run.
