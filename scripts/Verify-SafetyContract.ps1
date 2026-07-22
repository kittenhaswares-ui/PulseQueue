param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$runtimePath = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/Services/ActionBufferService.cs'
$compatibilityPath = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/Services/PluginCompatibilityService.cs'
$enginePath = Join-Path $RepositoryRoot 'src/PulseQueue.Core/BufferEngine.cs'
$generationPath = Join-Path $RepositoryRoot 'src/PulseQueue.Core/InputGenerationGate.cs'
$nativeOutcomePath = Join-Path $RepositoryRoot 'src/PulseQueue.Core/NativeActionOutcome.cs'
$nativeOwnershipPath = Join-Path $RepositoryRoot 'src/PulseQueue.Core/NativeQueueOwnership.cs'
$cooldownTimingPath = Join-Path $RepositoryRoot 'src/PulseQueue.Core/CooldownTiming.cs'
$holdRepeatPath = Join-Path $RepositoryRoot 'src/PulseQueue.Core/HoldRepeatEngine.cs'
$turboAcknowledgementPath = Join-Path $RepositoryRoot 'src/PulseQueue.Core/TurboActionEffectAcknowledgement.cs'
$physicalInputPath = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/Services/PhysicalHotbarInputSource.cs'
$configurationPath = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/Models/PluginConfiguration.cs'
$manifestPath = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/PulseQueue.Plugin.json'

$runtime = Get-Content -LiteralPath $runtimePath -Raw
$compatibility = Get-Content -LiteralPath $compatibilityPath -Raw
$engine = Get-Content -LiteralPath $enginePath -Raw
$generation = Get-Content -LiteralPath $generationPath -Raw
$nativeOutcome = Get-Content -LiteralPath $nativeOutcomePath -Raw
$nativeOwnership = Get-Content -LiteralPath $nativeOwnershipPath -Raw
$cooldownTiming = Get-Content -LiteralPath $cooldownTimingPath -Raw
$holdRepeat = Get-Content -LiteralPath $holdRepeatPath -Raw
$turboAcknowledgement = Get-Content -LiteralPath $turboAcknowledgementPath -Raw
$physicalInput = Get-Content -LiteralPath $physicalInputPath -Raw
$configuration = Get-Content -LiteralPath $configurationPath -Raw
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

function Assert-Contains([string]$Value, [string]$Pattern, [string]$Message) {
    if ($Value -notmatch $Pattern) { throw $Message }
}

function Assert-CountAtLeast([string]$Value, [string]$Pattern, [int]$Minimum, [string]$Message) {
    $count = [regex]::Matches($Value, $Pattern).Count
    if ($count -lt $Minimum) { throw "$Message Found $count; expected at least $Minimum." }
}

Assert-Contains $engine 'AbsoluteHoldCapMilliseconds\s*=\s*180\s*;' 'The hard hold cap is no longer exactly 180 ms.'
Assert-Contains $engine 'pending\s*=\s*null;[\s\S]*BufferDecisionKind\.Dispatch' 'The core no longer consumes before dispatch.'
Assert-Contains $engine 'ActionFailureKind\.Cooldown' 'Short local cooldown handling is missing from the core policy.'
Assert-Contains $engine 'safety\.IsMounted[\s\S]*CancelReason\.Mounted' 'Mounted-state cancellation is missing from the core policy.'

$nativeWritePattern = '->(?:AnimationLock|QueuedActionType|QueuedActionId|QueuedTargetId|QueuedExtraParam|QueueType|QueuedComboRouteId)\s*='
if ($runtime -match $nativeWritePattern) {
    throw 'Runtime source writes a protected native lock/queue field.'
}

$actionQueuedWrites = [regex]::Matches($runtime, '->ActionQueued\s*=').Count
if ($actionQueuedWrites -ne 1) {
    throw "Expected exactly one ownership-guarded ActionQueued write; found $actionQueuedWrites."
}
Assert-Contains $runtime 'private bool TryReplaceOwnedNativeQueue\([\s\S]*?TryTakeForNewerInput\([\s\S]*?actionManager->ActionQueued\s*=\s*false;' 'The sole native queue clear is not protected by exact certified ownership.'
Assert-Contains $runtime 'GetTemporalRemainingMilliseconds\([\s\S]*?supersedingRemainder\s*<\s*CurrentHoldWindowMilliseconds[\s\S]*?TryReplaceOwnedNativeQueue' 'An unavailable or far-future newest action can clear an older owned native queue.'

if ($runtime -match 'targetManager\.(?:Target|SoftTarget|MouseOverTarget|MouseOverNameplateTarget|FocusTarget|PreviousTarget)\s*=') {
    throw 'Runtime source writes a target-manager field.'
}

if ($runtime -match 'UseActionLocation\s*\(') {
    throw 'Runtime source calls UseActionLocation; location/target substitution is forbidden.'
}

$queueModeCalls = [regex]::Matches($runtime, 'ActionManager\.UseActionMode\.Queue').Count
if ($queueModeCalls -ne 2) {
    throw "Expected one replay call and one exact replay-queue identity mode; found $queueModeCalls references."
}

$originalCalls = [regex]::Matches($runtime, 'useActionHook\.Original\s*\(').Count
if ($originalCalls -ne 2) {
    throw "Expected exactly the original pass-through and one replay call site; found $originalCalls."
}

# Compatibility is opt-in only for exact audited versions. Unknown versions and
# unverified configuration must produce conflicts rather than optimistic support.
$auditedVersions = @(
    @('NoClippy', '0,\s*5,\s*0,\s*24'),
    @('ReAction', '1,\s*3,\s*5,\s*1'),
    @('MOAction', '4,\s*10,\s*1,\s*0')
)
foreach ($entry in $auditedVersions) {
    $name = $entry[0]
    $version = $entry[1]
    Assert-Contains $compatibility ("Supported${name}Version\s*=\s*new\($version\)") "The exact audited $name version gate is missing."
    Assert-Contains $compatibility ("plugin\.Version\s*!=\s*Supported${name}Version") "Unknown $name versions no longer fail closed."
}

foreach ($name in @('NoClippyUnchained', 'ReActionEx')) {
    Assert-Contains $compatibility ('"' + [regex]::Escape($name) + '"') "Hard conflict gate $name is missing."
}

Assert-Contains $compatibility 'matches\.Length\s*>\s*1[\s\S]*conflicts\.Add' 'Duplicate plugin instances no longer fail closed.'
Assert-Contains $compatibility 'active plugin list could not be read' 'Unreadable plugin topology no longer fails closed.'
Assert-Contains $compatibility 'Plugin compatibility data could not be verified' 'Unexpected compatibility failures no longer fail closed.'

# ReAction is supported only in an audited guarded mode. Synthetic Turbo input cannot
# be distinguished from a newer physical press at the UseAction boundary.
Assert-Contains $compatibility 'TryReadCollectionCount\([^\)]*[\s\S]*?"ActionStacks"' 'ReAction Action Stacks are not inspected.'
Assert-Contains $compatibility 'TryReadBoolean\([^\)]*[\s\S]*?"EnableAutoTarget"' 'ReAction Auto Target is not inspected.'
Assert-Contains $compatibility 'TryReadBoolean\([^\)]*[\s\S]*?"EnableTurboHotbars"' 'ReAction Turbo Hotbars are not inspected.'
Assert-Contains $compatibility 'TryReadBoolean\([^\)]*[\s\S]*?"EnableAutoDismount"' 'ReAction Auto Dismount is not inspected.'
Assert-Contains $compatibility 'TryReadBoolean\([^\)]*[\s\S]*?"EnableCameraRelativeDirectionals"' 'ReAction Camera Relative Directionals are not inspected.'
Assert-Contains $compatibility 'configuration\.ActionStackCount\s*!=\s*0[\s\S]*conflicts\.Add' 'Non-empty ReAction Action Stacks no longer suspend buffering.'
Assert-Contains $compatibility 'configuration\.AutoTargetEnabled[\s\S]*conflicts\.Add' 'ReAction Auto Target no longer suspends buffering.'
Assert-Contains $compatibility 'configuration\.TurboHotbarsEnabled[\s\S]*conflicts\.Add' 'ReAction Turbo Hotbars no longer suspend buffering.'
Assert-Contains $compatibility 'configuration\.AutoDismountEnabled[\s\S]*conflicts\.Add' 'ReAction Auto Dismount no longer suspends buffering.'
Assert-Contains $compatibility 'configuration\.CameraRelativeDirectionalsEnabled[\s\S]*conflicts\.Add' 'ReAction Camera Relative Directionals no longer suspend buffering.'
Assert-Contains $compatibility 'WeakReference<object>' 'The lightweight ReAction guard must not retain the foreign plugin.'
Assert-CountAtLeast $runtime 'compatibility\.IsLiveReActionProfileCurrent\(\)' 3 'Live ReAction safety fields are not checked at capture, outcome, and final dispatch.'

# MOAction publishes the actions whose target identity it owns. Those actions
# must pass through normally and must never become replay candidates.
Assert-Contains $compatibility 'MOAction\.RetargetedActions' 'The MOAction retargeted-actions IPC contract is missing.'
Assert-Contains $compatibility 'GetIpcSubscriber<uint\[\]>' 'MOAction IPC is not read with the audited uint[] contract.'
Assert-Contains $compatibility 'excludedActionIds\.Add\(actionId\)' 'MOAction action IDs are not added to the exclusion set.'
Assert-Contains $runtime 'excludedIntegrationActionIds\.Contains\(actionId\)[\s\S]*excludedIntegrationActionIds\.Contains\(resolvedId\)[\s\S]*return null;' 'MOAction-owned base/resolved actions can enter the buffer.'
Assert-Contains $compatibility 'public bool IsLiveMOActionUnowned\([\s\S]*subscriber\.InvokeFunc\(\)[\s\S]*!actionIds\.Contains\(requestedActionId\)[\s\S]*!actionIds\.Contains\(resolvedActionId\)' 'MOAction ownership is not rechecked before arming/final dispatch.'
Assert-CountAtLeast $runtime 'compatibility\.IsLiveMOActionUnowned\(' 3 'MOAction live ownership is not checked before native replacement, arming, and final dispatch.'

# Plugin load order and reloads invalidate pending work immediately. Foreign
# settings are polled outside the latency-sensitive per-press path; a detected
# profile change cancels and requires one clean framework frame.
Assert-Contains $runtime 'ActivePluginsChanged\s*\+=\s*OnActivePluginsChanged' 'Active-plugin topology changes are not observed.'
Assert-Contains $runtime 'OnActivePluginsChanged\([^\)]*\)[\s\S]*?Cancel\(CancelReason\.Conflict,[\s\S]*?pluginTopologyDirty' 'A topology change does not immediately invalidate the input generation through the audited Cancel path.'
Assert-Contains $runtime 'OnActivePluginsChanged\([^\)]*\)[\s\S]*?lock \(dispatchGate\)[\s\S]*?Cancel\(CancelReason\.Conflict' 'Topology invalidation is not serialized with final native dispatch.'
Assert-Contains $runtime 'Interlocked\.Exchange\(ref pluginTopologyDirty,\s*0\)[\s\S]*?Cancel\(CancelReason\.Conflict[\s\S]*?compatibilityQuarantineFrames' 'Topology changes do not cancel and quarantine pending work.'
Assert-Contains $runtime 'compatibilitySignature[\s\S]*assessment\.Signature[\s\S]*if \(changed\)[\s\S]*compatibilityQuarantineFrames[\s\S]*Cancel\(CancelReason\.Conflict' 'Live compatibility-profile changes do not cancel and quarantine pending work.'
Assert-Contains $runtime 'CompatibilityPollIntervalMilliseconds\s*=\s*500' 'Compatibility polling is no longer bounded to the audited interval.'
Assert-Contains $runtime 'private Candidate\? TryCreateCandidate\([\s\S]*?RefreshConflicts\(\);[\s\S]*?activeConflicts' 'Capture does not consult the current compatibility snapshot.'
Assert-Contains $runtime 'private bool IsStrictlyReady\([\s\S]*?RefreshConflicts\(\);[\s\S]*?activeConflicts' 'Final readiness does not consult the current compatibility snapshot.'
Assert-Contains $runtime 'OnFrameworkUpdate\([\s\S]*?RefreshConflicts\(\);' 'Compatibility is not refreshed from the framework loop.'

# Every newest hotbar press advances one generation. Direct native invocations,
# cancellation, topology changes, and knockback also invalidate older work.
Assert-Contains $generation 'Interlocked\.CompareExchange\(ref current,\s*next,\s*observed\)' 'Input generation advancement is no longer atomic.'
Assert-Contains $generation 'generation\s*>\s*0\s*&&\s*generation\s*==\s*Current' 'Input generation validity is not exact/current-only.'
Assert-Contains $runtime 'ExecuteSlotDetour\([^\)]*\)[\s\S]*?BeginHotbarInput\([^;]*\);[\s\S]*?executeSlotHook\.Original' 'ExecuteSlot does not invalidate the older hotbar generation first.'
Assert-Contains $runtime 'ExecuteSlotByIdDetour\([^\)]*\)[\s\S]*?BeginHotbarInput\([^;]*\);[\s\S]*?executeSlotByIdHook\.Original' 'ExecuteSlotById does not invalidate the older hotbar generation first.'
Assert-Contains $runtime 'private void BeginHotbarInput\([\s\S]*?\)[\s\S]*?Cancel\(CancelReason\.Replaced' 'Newest-hotbar-input replacement is missing.'
Assert-Contains $runtime 'if \(!certifiedHotbarInput\)[\s\S]*?Cancel\(CancelReason\.Replaced' 'Independent native action invocations do not invalidate pending work.'
Assert-Contains $runtime 'public void Cancel\([^\)]*\)[\s\S]*?inputGenerations\.Invalidate\(\);[\s\S]*?engine\.Cancel' 'Cancellation does not invalidate the generation before clearing the core.'
Assert-Contains $runtime 'inputGenerations\.Current[\s\S]*?new ExactActionTuple' 'Candidates do not capture the current physical-input generation.'
Assert-CountAtLeast $runtime 'inputGenerations\.IsCurrent\(' 3 'Generation validity is not checked throughout outcome and final dispatch.'
Assert-Contains $runtime 'NowMilliseconds\s*>=\s*runtime\.ExpiresAtMilliseconds' 'Final monotonic deadline check is missing.'
Assert-Contains $runtime 'inputGenerations\.Invalidate\(\);[\s\S]*?Interlocked\.Exchange\(ref forcedMovementObserved,\s*1\)' 'Immediate knockback invalidation is missing.'
Assert-Contains $runtime 'lock \(dispatchGate\)[\s\S]*?Cancel\(CancelReason\.Knockback,[\s\S]*?forcedMovementObserved' 'Knockback cancellation is not serialized with final native dispatch.'

# Native queue acceptance must be credited only when the complete immutable
# action tuple matches and that queue did not already exist before the press.
foreach ($pattern in @(
    'ActionType\s*==\s*attempted\.ActionType',
    'ActionId\s*==\s*attempted\.RequestedActionId',
    'ActionId\s*==\s*attempted\.ResolvedActionId',
    'TargetId\s*==\s*attempted\.TargetId',
    'Param\s*==\s*attempted\.Param',
    'Mode\s*==\s*attempted\.Mode',
    'RouteId\s*==\s*attempted\.RouteId'
)) {
    Assert-Contains $nativeOutcome $pattern "Exact native queue identity check is incomplete: $pattern"
}
Assert-Contains $nativeOutcome 'after\.Matches\(attempted\)\s*&&\s*!before\.Matches\(attempted\)[\s\S]*NativeActionOutcome\.MatchingNewQueue' 'A newly created exact native queue is not classified distinctly.'
Assert-Contains $nativeOutcome 'if \(before\.IsQueued\s*\|\|\s*after\.IsQueued\)[\s\S]*NativeActionOutcome\.ForeignOrPreexistingQueue' 'Foreign or pre-existing native queue state is not fail-closed.'
foreach ($field in @(
    'ActionQueued',
    'QueuedActionType',
    'QueuedActionId',
    'QueuedTargetId',
    'QueuedExtraParam',
    'QueueType',
    'QueuedComboRouteId'
)) {
    Assert-Contains $runtime ("actionManager->${field}") "Native queue snapshot omits $field."
}
Assert-Contains $runtime 'new ExactActionTuple\([\s\S]*?\(uint\)actionType,[\s\S]*?actionId,[\s\S]*?resolvedId,[\s\S]*?targetId,[\s\S]*?extraParam,[\s\S]*?\(uint\)mode,[\s\S]*?comboRouteId\)' 'The captured action identity is not the complete original tuple.'
Assert-Contains $runtime 'NativeActionOutcomeClassifier\.Classify\([\s\S]*?candidate\.QueueAtCapture,[\s\S]*?queueAfter,[\s\S]*?candidate\.ExactTuple\)' 'Original native outcome is not classified from before/after exact queue snapshots.'
Assert-Contains $runtime 'NativeActionOutcome\.ImmediateAcceptance\s+or\s+NativeActionOutcome\.MatchingNewQueue[\s\S]*?return;[\s\S]*?NativeActionOutcome\.ForeignOrPreexistingQueue[\s\S]*?return;[\s\S]*?engine\.Arm' 'Accepted or foreign native queues can incorrectly fall through into plugin buffering.'

# Replay preserves the exact requested action, target, parameter, and route; the
# core consumes before this single call, so a rejection cannot be retried.
Assert-Contains $runtime 'useActionHook\.Original\([\s\S]*?runtime\.Candidate\.ActionType,[\s\S]*?runtime\.Candidate\.RequestedActionId,[\s\S]*?runtime\.Candidate\.TargetId,[\s\S]*?runtime\.Candidate\.ExtraParam,[\s\S]*?ActionManager\.UseActionMode\.Queue,[\s\S]*?runtime\.Candidate\.ComboRouteId' 'Replay no longer preserves the original immutable action tuple.'
Assert-Contains $runtime 'if \(sequenceAfter\s*!=\s*sequenceBefore\)[\s\S]*?RecordSentSequence' 'A native sequence advance is no longer authoritative for replay send diagnostics.'
Assert-Contains $runtime 'actionManager->ActionQueued[\s\S]*?Cancel\(CancelReason\.Replaced' 'A newly occupied native queue no longer cancels plugin replay.'
Assert-Contains $runtime 'action\.AffectsPosition' 'Movement-affecting actions are no longer excluded from buffering.'
Assert-Contains $runtime 'ReActionCameraRelativeMovementException\s*=\s*29494' 'The audited ReAction camera-relative movement exception is not excluded.'
Assert-Contains $runtime '!snapshot\.IsMounted' 'Mounted snapshots are no longer rejected at final readiness.'

# Newest-input queue replacement is allowed only for an exact queue entry
# previously claimed by an older certified hotbar generation and unchanged
# action sequence. Foreign or coincidentally similar state must lose ownership.
Assert-Contains $nativeOwnership '!after\.Matches\(attempted\)[\s\S]*before\.Matches\(attempted\)[\s\S]*return false' 'Native queue ownership can be claimed without a new exact matching queue.'
Assert-Contains $nativeOwnership 'generation\s*<=\s*value\.Generation[\s\S]*return false' 'The same or an older generation can replace an owned native queue.'
Assert-Contains $nativeOwnership 'sequenceMarker\s*!=\s*value\.SequenceMarker[\s\S]*owned\s*=\s*null' 'A sent or changed sequence does not revoke native queue ownership.'
Assert-Contains $nativeOwnership 'if \(!current\.IsQueued\)[\s\S]*return false' 'Temporary outer-hook queue hiding incorrectly destroys certified ownership.'
Assert-Contains $runtime 'CompleteHotbarInput\(\)[\s\S]*TryReplaceOwnedNativeQueue' 'ReAction-restored owned queues are not checked after the complete hotbar call.'

Assert-Contains $runtime 'GetSpellIdForAction\([\s\S]*GetMaxCharges\([\s\S]*CooldownTiming\.GetNextChargeRemainingMilliseconds' 'Multi-charge actions do not use the next charge boundary.'
Assert-Contains $cooldownTiming 'totalSeconds\s*/\s*maximumCharges[\s\S]*elapsedSeconds' 'The tested next-charge timing calculation is missing.'

# Native Turbo is a physical keyboard-hold source, not a timer or a generic
# ExecuteSlot repeater. A fresh pressed+held InputId edge must correlate to the
# exact standard-hotbar slot before the core can receive a certified request.
Assert-Contains $physicalInput 'FirstInputId\s*=\s*\(int\)InputId\.HOTBAR_1_1' 'Turbo input range no longer starts at standard hotbar 1 slot 1.'
Assert-Contains $physicalInput 'LastInputId\s*=\s*\(int\)InputId\.HOTBAR_10_B' 'Turbo input range no longer ends at standard hotbar 10 slot 12.'
Assert-Contains $physicalInput 'raw\s*<\s*FirstInputId\s*\|\|\s*raw\s*>\s*LastInputId[\s\S]*return false' 'Out-of-range InputIds can enter keyboard Turbo provenance.'
Assert-Contains $physicalInput 'pressed\s*=\s*pressedHook\.Original\(inputData,\s*inputId\)' 'Turbo does not preserve the native pressed result.'
Assert-Contains $physicalInput 'down\s*=\s*inputData->IsInputIdDown\(inputId\)[\s\S]*if \(!down\)[\s\S]*return pressed;[\s\S]*downLatches\[index\]\s*=\s*true;[\s\S]*if \(!pressed\) return pressed;[\s\S]*TryFindFreshKeyboardChord[\s\S]*new CertifiedHotbarPress' 'A certified Turbo press is not gated by a fresh native press, logical down state, and an exact raw keyboard chord.'
Assert-Contains $physicalInput 'if \(downLatches\[index\]\) return pressed;' 'An already-down logical bind can be certified more than once.'
Assert-Contains $physicalInput 'keybind->KeySettings[\s\S]*KeyboardInputs\.KeyState[\s\S]*KeyStateFlags\.Pressed[\s\S]*KeyStateFlags\.Down' 'Turbo provenance no longer requires a raw keyboard key with both Pressed and Down flags.'
Assert-Contains $physicalInput 'currentModifiers\s*&\s*setting\.KeyModifier\)\s*!=\s*setting\.KeyModifier' 'Turbo provenance no longer validates the configured keyboard modifiers.'
Assert-Contains $physicalInput 'raw\s*>=\s*\(int\)SeVirtualKey\.BACK\s*&&\s*raw\s*<\s*\(int\)SeVirtualKey\.PAD_LMB' 'Mouse or gamepad virtual keys can enter keyboard Turbo provenance.'
Assert-Contains $physicalInput 'MaximumCorrelationAgeMilliseconds\s*=\s*50' 'Physical press-to-slot correlation is no longer capped at 50 ms.'
Assert-Contains $physicalInput 'age\s+is\s+<\s*0\s+or\s+>\s*MaximumCorrelationAgeMilliseconds[\s\S]*return false' 'Stale or future physical presses can correlate to a hotbar invocation.'
Assert-Contains $physicalInput 'GetSlotById\(candidate\.Binding\.HotbarId,\s*candidate\.Binding\.SlotId\)[\s\S]*expected\s*!=\s*slot[\s\S]*return false' 'Pointer-based hotbar execution is not correlated to the exact certified slot.'
Assert-Contains $physicalInput 'candidate\.Binding\.HotbarId\s*!=\s*hotbarId[\s\S]*candidate\.Binding\.SlotId\s*!=\s*slotId[\s\S]*return false' 'ID-based hotbar execution is not correlated to the exact certified slot.'
Assert-Contains $physicalInput 'public bool IsStillHeld\([\s\S]*press\.KeySettingIndex[\s\S]*currentSetting\.Key\s*!=\s*press\.PhysicalKey[\s\S]*currentSetting\.KeyModifier\s*!=\s*press\.RequiredModifiers[\s\S]*CurrentKeyModifier\s*!=\s*press\.ActiveModifiers[\s\S]*KeyStateFlags\.Down' 'Final Turbo liveness no longer checks the exact keybind setting, physical key, modifiers, and raw Down flag.'
Assert-Contains $physicalInput 'onCertifiedPress\(certifiedPress\.Value\)' 'Certified physical edges no longer preempt the existing owner synchronously.'
Assert-Contains $runtime 'private void OnCertifiedPhysicalPress\([\s\S]*lock \(dispatchGate\)[\s\S]*latestCertifiedPressId[\s\S]*Cancel\(CancelReason\.Replaced' 'A newer certified physical edge cannot atomically preempt older pending or Turbo work.'
Assert-Contains $runtime 'Volatile\.Read\(ref latestCertifiedPressId\)\s*!=\s*press\.PressId' 'Turbo start no longer rejects a superseded certified press.'
Assert-Contains $runtime 'bindingMatches\s*=\s*Volatile\.Read\(ref latestCertifiedPressId\)\s*==\s*runtime\.Press\.PressId' 'Turbo final observation no longer rejects a superseded certified press.'

# The core must remain inert until a real release has been observed and must
# reject any runtime request that lacks the certified-fresh provenance marker.
Assert-Contains $holdRepeat 'state\s*=\s*HoldRepeatState\.NeedsRelease' 'Hold-repeat no longer starts fail-closed in NeedsRelease.'
Assert-Contains $holdRepeat 'if \(!request\.IsCertifiedFreshPress\)[\s\S]*HoldRepeatStartResult\.RejectedUncertified' 'Uncertified presses can start hold-repeat.'
Assert-Contains $holdRepeat 'if \(state\s*==\s*HoldRepeatState\.NeedsRelease\)[\s\S]*HoldRepeatStartResult\.RejectedNeedsRelease' 'A fresh-release observation is no longer mandatory before starting hold-repeat.'
$freshMarkerWrites = [regex]::Matches($runtime, 'IsCertifiedFreshPress:\s*true').Count
if ($freshMarkerWrites -ne 1) {
    throw "Expected one audited runtime construction of a certified-fresh request; found $freshMarkerWrites."
}
Assert-Contains $runtime 'scope\.CertifiedPress\s+is\s+not\s*\{\s*\}\s*press[\s\S]*new HoldRepeatStartRequest\([\s\S]*press\.PressId,[\s\S]*scope\.Generation,[\s\S]*IsCertifiedFreshPress:\s*true' 'Runtime can manufacture a fresh hold request without a consumed certified press.'

# Only direct Action slots with a single Action/PvPAction invocation are eligible.
# PvPCombo, macros, items, movement, cross-hotbar/controller, and mouse input
# remain ordinary one-shot input until exact end-to-end identity is proven.
Assert-Contains $runtime 'DirectActionHotbarSlotType\s*=\s*1\s*;' 'The audited direct Action slot type changed.'
Assert-Contains $runtime 'commandType\s*!=\s*DirectActionHotbarSlotType[\s\S]*return null' 'Turbo can start from a slot type other than direct Action.'
Assert-Contains $runtime 'slotIdentity\.CommandType\s*!=\s*DirectActionHotbarSlotType[\s\S]*slotIdentity\.CommandId\s*!=\s*candidate\.RequestedActionId' 'Turbo start no longer proves the exact direct Action slot command ID.'
Assert-Contains $runtime 'actionType\s+is\s+not\s+\(ActionType\.Action\s+or\s+ActionType\.PvPAction\)[\s\S]*?return null' 'A non-Action/PvPAction invocation can become a Turbo candidate.'
Assert-Contains $runtime 'inputScope\.ActionInvocationCount\+\+;[\s\S]*?ActionInvocationCount\s*>\s*1[\s\S]*?TurboCandidate\s*=\s*null;[\s\S]*?TurboDisqualified\s*=\s*true' 'A hotbar slot with multiple action invocations is not disqualified from Turbo.'
Assert-Contains $runtime 'scope\.TurboDisqualified[\s\S]*?scope\.ActionInvocationCount\s*!=\s*1' 'Turbo start no longer requires exactly one non-disqualified action invocation.'
if ($runtime -match 'PvPComboHotbarSlotType') {
    throw 'PvPCombo regained Turbo ownership without an audited end-to-end route proof.'
}

# Timing is framework-driven with a hard cadence floor. A missed interval emits
# one pulse now and schedules from now; it must never accumulate catch-up work.
Assert-Contains $holdRepeat 'MinimumTimingMilliseconds\s*=\s*60\s*;' 'Hold-repeat cadence can fall below the hard 60 ms minimum.'
Assert-Contains $holdRepeat 'Math\.Clamp\(IntervalMilliseconds,\s*MinimumTimingMilliseconds,\s*MaximumIntervalMilliseconds\)' 'Hold-repeat interval normalization no longer enforces the hard cadence bounds.'
Assert-Contains $configuration 'MinimumTurboRepeatIntervalMilliseconds\s*=\s*60\s*;' 'Persisted Turbo configuration can request a cadence below 60 ms.'
Assert-Contains $configuration 'Math\.Clamp\([\s\S]*TurboRepeatIntervalMs,[\s\S]*MinimumTurboRepeatIntervalMilliseconds,[\s\S]*MaximumTurboRepeatIntervalMilliseconds\)' 'Turbo configuration is not normalized before use/save.'
Assert-Contains $holdRepeat 'current\.NextPulseAtMilliseconds\s*=\s*SaturatingAdd\(nowMilliseconds,\s*options\.IntervalMilliseconds\)' 'Hold-repeat no longer schedules from now; catch-up bursts may be possible.'
if ($holdRepeat -match 'NextPulseAtMilliseconds\s*\+=' -or
    $holdRepeat -match '(?m)\b(?:while|for)\s*\([^\r\n]*NextPulseAtMilliseconds') {
    throw 'Hold-repeat contains a catch-up loop or accumulated due-time increment.'
}

# Final dispatch is serialized with cancellation, revalidates the newest token,
# and invokes exactly the original certified hotbar/slot once.
Assert-Contains $runtime 'private void DispatchTurboPulse\([\s\S]*?lock \(dispatchGate\)[\s\S]*?turboEngine\.IsTokenCurrent\(token\)[\s\S]*?ObserveTurbo\(runtime,\s*checkLiveMOAction:\s*true\)[\s\S]*?executeSlotByIdHook\.Original\([\s\S]*?runtime\.SlotIdentity\.Binding\.HotbarId,[\s\S]*?runtime\.SlotIdentity\.Binding\.SlotId\)' 'Turbo final token/safety/same-slot dispatch is not serialized under the dispatch gate.'
$executeSlotByIdOriginalCalls = [regex]::Matches($runtime, 'executeSlotByIdHook\.Original\s*\(').Count
if ($executeSlotByIdOriginalCalls -ne 2) {
    throw "Expected one manual pass-through and one audited Turbo same-slot call; found $executeSlotByIdOriginalCalls."
}
Assert-Contains $runtime 'NativeActionOutcomeClassifier\.Classify\([\s\S]*?result\s*!=\s*0\s*\|\|\s*sequenceAdvanced,[\s\S]*?queueBefore,[\s\S]*?queueAfter,[\s\S]*?exactTuple\)' 'Turbo pulse outcome is not classified against the complete exact queue tuple.'
Assert-Contains $runtime 'nativeOutcome\s*==\s*NativeActionOutcome\.ImmediateAcceptance\s*&&\s*sequenceAdvanced[\s\S]*?TurboAcknowledgementSequenceMode\.ImmediateExact[\s\S]*?sequenceAfter' 'Immediate Turbo acceptance is not gated by an exact advanced sequence.'
Assert-Contains $runtime 'nativeOutcome\s*==\s*NativeActionOutcome\.MatchingNewQueue\s*&&\s*!sequenceAdvanced[\s\S]*?TryClaimNewQueue\([\s\S]*?if \(!claimed[\s\S]*?TurboAcknowledgementSequenceMode\.QueuedAfterBaseline[\s\S]*?sequenceBefore' 'Queued Turbo acceptance is not gated by an exact newly-owned queue without a simultaneous sequence transition.'
Assert-Contains $runtime 'private void RejectTurboPulseUnsafe\([\s\S]*?HoldRepeatCancelReason\.PulseRejected,[\s\S]*?hold ended without retry' 'An unproven Turbo pulse is not terminal without retry.'
Assert-Contains $runtime 'private void CancelTurboUnsafe\([\s\S]*?turboEngine\.Cancel\(reason\);[\s\S]*?turboRuntime\s*=\s*null' 'Turbo cancellation does not invalidate the core hold and runtime token together.'
Assert-Contains $runtime 'MaximumTurboAcknowledgementMilliseconds\s*=\s*2_000\s*;' 'Turbo acknowledgement timeout changed from the audited bound.'
Assert-Contains $runtime 'observation\.ActionReady\s*&&\s*acknowledgement\s+is\s+null' 'Turbo can issue another pulse while the preceding action is unacknowledged.'
Assert-Contains $runtime 'new TurboActionEffectExpectation\([\s\S]*?exactTuple\.ActionType,[\s\S]*?exactTuple\.RequestedActionId,[\s\S]*?exactTuple\.ResolvedActionId,[\s\S]*?sequenceMode,[\s\S]*?sequenceMarker' 'Turbo acknowledgement does not retain exact type, requested ID, resolved ID, and sequence identity.'
Assert-Contains $runtime 'TryCompleteTurboAcknowledgement\(header\)[\s\S]*?TurboActionEffectAcknowledgementMatcher\.Matches\([\s\S]*?acknowledgement\.Expectation,[\s\S]*?observation\)' 'Local-player action effects are not matched against the exact Turbo acknowledgement identity.'
Assert-Contains $runtime 'header->SourceSequence\s*!=\s*0\s*&&\s*casterEntityId\s*==\s*currentLocalEntityId[\s\S]*?TryCompleteTurboAcknowledgement\(header\)' 'A zero-sequence or foreign-caster action effect can reach Turbo acknowledgement matching.'
Assert-Contains $runtime 'turboEngine\.IsTokenCurrent\(acknowledgement\.Pulse\)[\s\S]*?latestCertifiedPressId[\s\S]*?acknowledgement\.Pulse\.PressId' 'A stale hold or press can complete a newer Turbo acknowledgement.'
Assert-Contains $runtime 'now\s*-\s*acknowledgement\.StartedAtMilliseconds\s*>\s*MaximumTurboAcknowledgementMilliseconds[\s\S]*?HoldRepeatCancelReason\.PulseRejected' 'A missing Turbo acknowledgement is not terminal without retry.'
Assert-Contains $turboAcknowledgement 'observed\.ActionType\s*!=\s*expected\.ActionType[\s\S]*?observed\.ActionId\s*!=\s*expected\.RequestedActionId[\s\S]*?observed\.ActionId\s*!=\s*expected\.ResolvedActionId' 'Turbo action-effect matching no longer requires exact action type and requested/resolved action identity.'
Assert-Contains $turboAcknowledgement 'ImmediateExact\s*=>[\s\S]*?observed\.SourceSequence\s*==\s*expected\.SequenceMarker' 'Immediate Turbo acknowledgement no longer requires the exact source sequence.'
Assert-Contains $turboAcknowledgement 'QueuedAfterBaseline\s*=>[\s\S]*?IsWrapSafeNewer\(observed\.SourceSequence,\s*expected\.SequenceMarker\)' 'Queued Turbo acknowledgement no longer requires a wrap-safe newer source sequence.'
$actionEffectOriginalCalls = [regex]::Matches($runtime, 'receiveActionEffectHook\.Original\s*\(').Count
if ($actionEffectOriginalCalls -ne 1) {
    throw "Expected exactly one ActionEffect original forward; found $actionEffectOriginalCalls."
}
Assert-Contains $runtime 'private void ReceiveActionEffectDetour\([\s\S]*?finally\s*\{[\s\S]*?receiveActionEffectHook\.Original\(' 'ActionEffect observation no longer forwards the native original exactly from finally.'

# Turbo remains opt-in after migration, and ReAction Turbo must never become a
# second repeat owner beside PulseQueue native Turbo.
Assert-Contains $configuration 'CurrentVersion\s*=\s*2\s*;' 'Turbo configuration migration version changed unexpectedly.'
Assert-Contains $configuration 'if \(Version\s*<=\s*1\)[\s\S]*TurboEnabled\s*=\s*false' 'Existing configurations can silently opt into native Turbo.'
Assert-Contains $configuration 'ResetToDefaults\(\)[\s\S]*TurboEnabled\s*=\s*false' 'Reset defaults no longer keep native Turbo opt-in.'
Assert-Contains $compatibility 'if \(configuration\.TurboHotbarsEnabled\)\s*\{[\s\S]*?conflicts\.Add\(\s*"Disable ReAction''s Turbo Hotbars;' 'ReAction Turbo no longer creates an actionable hard conflict.'

# No background scheduler and no new mutation authority: framework ticks are
# the only clock, the existing exact-owned ActionQueued clear is the only native
# queue write, and neither input/core/config code may write target or lock state.
$turboSources = $holdRepeat + "`n" + $physicalInput + "`n" + $configuration + "`n" + $runtime
if ($turboSources -match '\b(?:System\.Threading\.Timer|System\.Timers\.Timer|PeriodicTimer|Thread\.Sleep|Task\.Delay)\b' -or
    $turboSources -match '(?m)\b(?:Sleep|Delay)\s*\(') {
    throw 'Turbo contains a timer, sleep, or delayed background scheduler.'
}
if ($physicalInput -match $nativeWritePattern -or $physicalInput -match '->ActionQueued\s*=') {
    throw 'The physical-input observer writes protected native action state.'
}
if (($holdRepeat + "`n" + $configuration + "`n" + $physicalInput) -match
    'targetManager\.(?:Target|SoftTarget|MouseOverTarget|MouseOverNameplateTarget|FocusTarget|PreviousTarget)\s*=') {
    throw 'Turbo support writes a target-manager field outside the audited runtime contract.'
}

if (-not [bool]$manifest.IsTestingExclusive) {
    throw 'The initial native-hook release must remain testing-exclusive.'
}

Write-Host 'PulseQueue static safety-contract checks passed.'
