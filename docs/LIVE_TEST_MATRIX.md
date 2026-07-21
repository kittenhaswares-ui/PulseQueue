# Live validation matrix

Run on the current FFXIV patch in the Wolves' Den first, with detailed logging
enabled and no competitive match in progress. Redact character and target names
before sharing logs.

## Required safety cases

| Case | Procedure | Pass condition |
|---|---|---|
| Vanilla accepts | Press an immediately ready action | No custom token or replay |
| Native queue | Press a GCD inside vanilla's normal queue window | Native handles it; custom token stays empty |
| Short rejection | Press Guard/instant ability 20, 80, and 150 ms before its GCD/local recast or animation lock ends | At most one replay, only after readiness |
| Outside cap | Press more than 180 ms early | No token and no later replay |
| New input | Arm A, then press B | A is canceled before B is processed |
| Same input | Arm A, then press A again | Old generation is canceled; at most one new token |
| Bad target/range/resource | Try while invalid | Never arms |
| Adjusted action changes | Arm a transforming button, then change its state | Cancels; never sends a different action |
| Target change | Arm, then change hard or soft target | Cancels before dispatch |
| Resolver target | Arm a target-capable sentinel-target action, then change mouseover/nameplate target | Cancels before dispatch |
| Stun/death/knockback | Arm immediately before each transition | Cancels before dispatch |
| Zone/instance/job/PvP change | Arm immediately before transition | Cancels and suspends |
| Replay rejection | Make the exact action invalid at dispatch boundary | Token is consumed; zero retries |
| Frame stall | Inject or observe a frame longer than the remaining lifetime | Cancels; no late dispatch |
| Disable/unload | Disable or unload while pending | Clears and never calls later |

## Input and environment coverage

- Keyboard standard hotbar
- Mouse-click standard hotbar
- Controller cross hotbar
- Key repeat/held input
- 30, 60, 120, 144, and 240 FPS
- Low, medium, and high effective RTT with jitter
- Self, friendly, enemy, and no-current-target actions
- PvE instant actions and PvP Guard/instant actions
- NoClippy, NoClippyUnchained, ReAction, and ReActionEx loaded/unloaded while
  idle and while pending

## Release gates

- Zero wrong-action, wrong-target, duplicate, post-cancel, or post-expiry sends.
- Every custom generation maps to zero or one new local source sequence.
- Every known conflict clears first and suspends within one framework frame.
- Eligible actions are never sent before full client readiness.
- Two-hour soak across deaths, zoning, target churn, and plugin toggles without a
  stale replay or hook-disposal failure.

Record the FFXIV build, Dalamud API/build, plugin version, FPS, input device,
conflict-plugin versions, sample count, and effective RTT for every run.
