# PulseQueue 0.3.5 live-test matrix

This matrix is mandatory for the 0.3.5 native-input/ReAction fallback. Automated
tests can prove the repeat state machine, ownership transitions, and timing
arithmetic; only a current-patch in-game run can prove the native signatures,
hook ordering, slot correlation, and interaction with FFXIV, ReAction, and
NoClippy.

Do not call the release production-validated until every required row has a
recorded result and the log shows no PulseQueue hook fault.

Current status: **Pending live run.** The dependency-free core suite is
**171/171 passed**, the static safety contract is **passed**, and the API-15
release package is **built and verified**. Current-patch in-game results remain
pending.

## Test profiles

Run the relevant cases under all four profiles:

1. PulseQueue alone;
2. PulseQueue + NoClippy 0.5.0.24;
3. PulseQueue + ReAction 1.3.5.1, Turbo Hotbars and Macro Queue off; and
4. PulseQueue + ReAction 1.3.5.1, Turbo Hotbars and Macro Queue on.

For the standalone baseline, enable PulseQueue and native held-input Turbo,
disable dry run, use 0 ms initial delay and 60 ms repeat interval, and enable
detailed logging. Test in combat first. Exercise the separate out-of-combat
toggle explicitly.

Record before/after values for:

- physical logical edges;
- PulseQueue-injected repeats;
- external/ReAction repeats;
- newest-owner preemptions;
- releases;
- suppressed older held inputs;
- fail-open events;
- smart-buffer captures/dispatches/rejections;
- replaced pending inputs; and
- exact owned native queues replaced by newer input.

## Native hook and standalone repeat

| Case | Procedure | Required result |
|---|---|---|
| Plugin load | Load 0.3.5 at character select, log in, then reload once in world | Both native hooks install without fault. The first eligible native press can claim immediately; no artificial startup release/press cycle is required. |
| Simple hold | Hold a ready standard-hotbar action for at least two seconds | One physical edge is followed by multiple injected repeats at the configured cadence. The native slot executes normally and no manual `ExecuteSlot`/action replay event appears. |
| Too-early hold | Begin holding an action before GCD/local recast or animation lock ends | Logical repeat continues to reach the native binding path. The action executes when normal client/plugin readiness permits; PulseQueue does not wait for a manual Turbo acknowledgement. |
| Viper transformation | Hold a standard-hotbar Viper combo/weave slot through at least two native transformations | Every pulse resolves the current slot state. The hold does not cancel merely because the adjusted action ID changes, and no stale captured action is replayed. |
| Zero initial delay | Use 0 ms initial delay with 60 ms interval | The original scan produces one physical activation, not an immediate duplicate. The first repeat is due one interval later. |
| Timing bounds | Test 0/0, 0/1000, 1000/0, and 1000/1000; reload the plugin after saving each | Values remain in 0..1000 ms. No due instant emits more than one repeat, and a long frame stall produces no catch-up burst. |
| Release | Hold until at least two repeats, then release between frames | Repetition stops. Release increments once; no delayed PulseQueue press occurs afterward. |
| Disable/re-enable while held | Disable Turbo while holding, then re-enable without releasing | Repeats stop immediately. Re-enable does not silently treat the old hold as a fresh player press; release/press restores ownership. |
| Long frame gap | Hold an action, suspend/lag the game thread for more than 1000 ms, then resume | Pending smart-buffer work cancels. Native repeat produces no catch-up burst and does not relabel the old hold as a new physical press. |
| Out of combat | Repeat with the out-of-combat option off and on | Off: physical press remains vanilla but PulseQueue injects no out-of-combat repeats. On: normal cadence resumes. |
| Standard hotbars 1 and 10 | Bind distinct harmless actions/macros to first/last slots and hold each | Both boundary `InputId` ranges correlate to the correct slot; no neighboring slot runs. |
| Unknown input | Use movement, chat, system, and non-hotbar bindings while Turbo is enabled | Original behavior is unchanged and no PulseQueue repeat owner is created. |

## Newest-input control

These are release-blocking because they are the user's primary control
requirement.

| Case | Procedure | Required result |
|---|---|---|
| Held Viper -> Recuperate | Hold a repeating Viper oGCD, then press and release Recuperate while keeping Viper held | Recuperate is a new physical edge, becomes authoritative before its slot runs, and the old Viper hold is suppressed. No Viper repeat preempts or follows it until Viper is released and freshly pressed. |
| Held Viper -> Purify | Repeat the previous case near a debuff/stun boundary | Purify passes through as the newest vanilla press even if it cannot currently execute. The old hold remains suppressed. |
| Held Viper -> Guard | Repeat during movement/knockback and close to Guard readiness | Guard is never blocked by the old repeat owner. Unsafe state may prevent a new smart-buffer capture, but cannot preserve or revive the older intent. |
| Held owner -> fast tap | Hold A until it repeats, then quickly tap B so B is no longer held when observed | B still passes as the newest physical intent and suppresses A. A cannot resume until released and freshly pressed. |
| Two quick oGCDs -> defensive | Quickly press two Viper weaves, then press a heal/defensive before either pending intent could dispatch | PulseQueue keeps no FIFO. Each genuine press replaces the preceding pending token; only the newest eligible token can remain. |
| New ineligible binding | Hold an old repeat owner, then press a different standard-hotbar slot whose content cannot be buffered | The new physical input still preempts/suppresses the old hold. Eligibility affects only fresh smart-buffer capture, not player-input priority. |
| Injected repeat classification | Create a smart-buffer pending intent, then let the same held key repeat | The injected repeat does not advance the physical generation, cancel the pending token, or count as a newer player press. |
| External repeat classification | Repeat the previous case with ReAction Turbo active | ReAction pulses count as external and never advance the physical generation or cancel the newer player intent. |
| Old external pulse cannot steal | Hold A, press/hold B, then allow ReAction to pulse still-held A | A is suppressed rather than classified as a fresh press. B remains authoritative and A cannot steal ownership. |
| Old key resurrection | Hold A, press/hold B, release B while continuing to hold A | A remains suppressed. It does not resume when B releases; A must be released and pressed again. |
| Simultaneous edges | Press two standard-hotbar bindings as nearly simultaneously as possible and repeat at different frame rates | The last observed genuine edge owns. There is never a FIFO, double owner, or catch-up burst. Record ordering from diagnostics. |

## Smart one-shot buffer

| Case | Procedure | Required result |
|---|---|---|
| Vanilla accepted | Press an eligible action while the client can accept or queue it | Vanilla owns the original result. PulseQueue does not create a delayed duplicate. |
| Local early rejection | Tap an eligible instant action within the measured readiness horizon | At most one exact pending intent is captured and at most one queue-mode call is dispatched before the 350 ms cap. |
| Outside horizon | Tap too early for the 350 ms maximum | No replay occurs. |
| Replay rejected | Force the one authorized replay to return rejected | The token is already consumed and is never retried, regardless of Turbo state. |
| New physical cancellation | Capture A, then press B before A dispatches | A is canceled before B's original slot execution. Only B may create a new token. |
| Repeat non-cancellation | Capture a token and allow an injected/external pulse that is not a new physical edge | The pulse does not cancel or replace the token. |
| Exact owned queue preemption | Let physical A create a provably owned native queue, then physically press B | Only A's exact unchanged tuple is cleared before B runs. The counter increments once. |
| Foreign/changed queue | Arrange native queue state not proven to belong to A, or mutate the tuple before B | PulseQueue does not clear it or claim ownership. |
| Server rejection | Produce a locally sent action with no matching server outcome | No server-rejection retry is generated. Turbo may continue only as independent player-held input, never as a retry of the exact failed action call. |
| Target change | Capture an intent, then change hard/soft/resolver target while a native Turbo key remains held | The one-shot token cancels and the old target is never substituted or retried. The held-input owner is not release-gated and its same-`InputId` cadence can continue. |
| Terminal events | While pending, separately trigger death, stun, forced movement/knockback, mount, zone, logout, job/PvP change, long frame gap, disable, and unload | Pending work cancels. Only a currently visible exact owned tuple may be cleared; foreign state remains untouched. |

## Complete native macro repetition

Use macros whose repeated side effects are safe in the chosen test area.

| Case | Procedure | Required result |
|---|---|---|
| Single-action macro | Hold a standard-hotbar macro containing `/ac` plus icon/error metadata | Each due logical press executes the entire native macro slot. No PulseQueue line parser, transcript, action budget, or manual slot replay is involved. |
| Multi-action fallback | Hold a macro with two or more authored action lines whose valid line changes with state | FFXIV evaluates authored lines in normal order on every native press. PulseQueue does not select the line, and different pulses may naturally produce different actions. |
| Arbitrary macro | Hold a safe macro containing non-action commands in addition to action lines | The complete macro repeats, including its native non-action behavior. PulseQueue neither rejects nor strips commands. |
| Zero-action macro | Hold a harmless macro with no action commands | The complete macro still repeats as native input. There is no fake `observedActions=0` PulseQueue pulse path. |
| Macro wait/lock | Hold a macro containing a short wait and observe beyond one repeat interval | Native macro lock/executor behavior remains authoritative. PulseQueue creates no parallel macro runtime or stale-epoch quarantine. Document FFXIV's actual behavior. |
| ReAction Macro Queue off | Disable ReAction Macro Queue | PulseQueue preserves native Macro mode. Complete-slot repetition still works, subject to FFXIV's own `MacroLocked` state. |
| ReAction Macro Queue on | Enable ReAction Macro Queue | PulseQueue still preserves native Macro mode; ReAction may transform it downstream. PulseQueue performs no duplicate mode rewrite. |
| Macro -> defensive takeover | Hold a repeating multi-action macro, then physically press Recuperate/Purify/Guard | The defensive edge preempts the macro repeat owner immediately. The old macro does not run again until release/fresh press. |

## ReAction compatibility and hook order

| Case | Procedure | Required result |
|---|---|---|
| ReAction absent | Unload ReAction and hold standard-hotbar actions/macros | PulseQueue emits injected repeats and remains fully standalone. No missing-plugin compatibility block appears. |
| ReAction present, Turbo off | Load ReAction with Turbo Hotbars off | PulseQueue still emits injected repeats; ReAction presence alone is not a conflict. |
| ReAction present, Turbo on | Enable ReAction Turbo Hotbars and test a supported short-recast slot, a transforming combo slot, and a macro | ReAction pulses pass through. PulseQueue stays silent while current-owner pulses continue and supplies a same-`InputId` fallback exactly one interval after the last pulse if ReAction stops. The smart buffer remains enabled. |
| Early external pulse | With both repeat systems active, produce a ReAction pulse before PulseQueue's next deadline | The external pulse passes through and resets PulseQueue's deadline to that pulse time plus one interval. No PulseQueue press occurs at the superseded deadline. |
| External gap recovery | Stop ReAction pulses while the current owner remains held | PulseQueue emits one fallback after one complete configured interval without an observed pulse, then continues its own cadence until external pulses resume. There is no catch-up burst. |
| Outer-hook delegated slot | Use a hook order where ReAction turns a false pressed result into a pulse and executes the slot outside PulseQueue's pressed detour | Exact slot correlation classifies and coalesces the delegated execution. PulseQueue resets its deadline and does not emit an immediate duplicate. |
| ReAction toggle during hold | Toggle Turbo Hotbars off/on while an input is held, then release and freshly press | No double pulse, crash, stale owner, or burst occurs. PulseQueue fallback remains available under either setting. |
| Plugin load order A | Load PulseQueue before ReAction and run simple hold plus Viper -> heal takeover | External classification, gap-filler timing, and suppression meet the same requirements as standalone. |
| Plugin load order B | Reverse the load order and repeat | Results are semantically identical. If an outer hook creates a pulse after PulseQueue returns false, exact slot correlation still classifies or suppresses it. |
| ReAction unsupported/unreadable | Use a controlled mismatched test build or make config reflection unavailable | PulseQueue reports capability uncertainty. Smart action/queue mutation may fail closed, but action-agnostic native same-`InputId` cadence remains available. |
| Other ReAction features | Exercise Auto Target, Auto Dismount, camera-relative directionals, queue adjustments, requeuing, and action stacks independently | Supported feature state is reported granularly. Auto Target and Action Stacks do not pause or release-gate PulseQueue native cadence. A target change still cancels only the one-shot token. Smart replay may remain conservative where action/queue ownership is uncertain. |

## NoClippy compatibility

| Case | Procedure | Required result |
|---|---|---|
| NoClippy standalone coexistence | Enable NoClippy and hold actions across animation-lock boundaries | PulseQueue input counters continue normally. No duplicate animation-lock owner or lock mismatch warning appears. |
| NoClippy + ReAction | Enable all three plugins with ReAction Turbo/Macro Queue on | NoClippy owns animation-lock correction; ReAction pulses pass through and may transform Macro mode; PulseQueue keeps same-input gap filling plus newest-input control. No capability globally disables native cadence. |
| NoClippy toggle | Toggle NoClippy while an input is held and while a one-shot token is pending | No crash or duplicate input occurs. PulseQueue never writes a replacement animation-lock value. Pending action safety remains conservative. |

## Unsupported input paths

| Case | Procedure | Required result |
|---|---|---|
| Direct mouse click | Click and hold a standard-hotbar slot with the mouse | The click remains vanilla and creates no logical held-input Turbo owner. |
| Mouse-bound hotbar input | Bind a mouse button as a normal standard-hotbar command and hold it | If FFXIV exposes the normal logical standard-hotbar `InputId`, it follows the same repeat path as a keyboard binding. It is distinct from directly clicking the slot. |
| Cross hotbar/controller | Hold actions and macros through controller/cross hotbar | No PulseQueue Turbo repeat is created in 0.3.5. Native controller behavior remains unchanged. |
| Plugin-originated slot/action | Invoke an action or slot directly from another plugin | It cannot establish logical held-input ownership and is not labeled a physical edge. |

## Log acceptance criteria

A candidate passes only if the collected logs prove all of the following:

- standalone holds have at least one physical edge and multiple injected repeats;
- with ReAction Turbo active, every current-owner external pulse postpones the
  fallback by one interval; PulseQueue resumes only after the external gap;
- outer-hook delegated slot execution is coalesced and is not followed by an
  immediate PulseQueue duplicate;
- a Viper -> heal/Purify/Guard test records physical preemption and suppression
  of the older continuously held input;
- injected/external repeats never increment newest-input replacement counters;
- arbitrary and multi-action macro holds execute through native slot resolution;
- no manual Turbo slot/action replay or manual macro transcript event occurs;
- no catch-up burst follows a stall;
- fail-open events remain zero in normal operation; and
- the Dalamud log contains no PulseQueue hook exception, access violation,
  recursive dispatch, or animation-lock write.

Any failure in newest-input authority, duplicate-source prevention, exact queue
ownership, or hook stability blocks publication. A feeling of responsiveness is
useful evidence for tuning, but never substitutes for the counters and outcome
sequence above.
