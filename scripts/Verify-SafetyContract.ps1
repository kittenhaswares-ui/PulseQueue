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
$manifestPath = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/PulseQueue.Plugin.json'

$runtime = Get-Content -LiteralPath $runtimePath -Raw
$compatibility = Get-Content -LiteralPath $compatibilityPath -Raw
$engine = Get-Content -LiteralPath $enginePath -Raw
$generation = Get-Content -LiteralPath $generationPath -Raw
$nativeOutcome = Get-Content -LiteralPath $nativeOutcomePath -Raw
$nativeOwnership = Get-Content -LiteralPath $nativeOwnershipPath -Raw
$cooldownTiming = Get-Content -LiteralPath $cooldownTimingPath -Raw
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
Assert-Contains $runtime 'OnActivePluginsChanged\([^\)]*\)[\s\S]*?inputGenerations\.Invalidate\(\);[\s\S]*?pluginTopologyDirty' 'A topology change does not immediately invalidate the input generation.'
Assert-Contains $runtime 'OnActivePluginsChanged\([^\)]*\)[\s\S]*?lock \(dispatchGate\)[\s\S]*?inputGenerations\.Invalidate\(\)' 'Topology invalidation is not serialized with final native dispatch.'
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
Assert-Contains $runtime 'ExecuteSlotDetour\([^\)]*\)[\s\S]*?BeginHotbarInput\(\);[\s\S]*?executeSlotHook\.Original' 'ExecuteSlot does not invalidate the older hotbar generation first.'
Assert-Contains $runtime 'ExecuteSlotByIdDetour\([^\)]*\)[\s\S]*?BeginHotbarInput\(\);[\s\S]*?executeSlotByIdHook\.Original' 'ExecuteSlotById does not invalidate the older hotbar generation first.'
Assert-Contains $runtime 'private void BeginHotbarInput\(\)[\s\S]*?Cancel\(CancelReason\.Replaced' 'Newest-hotbar-input replacement is missing.'
Assert-Contains $runtime 'if \(!certifiedHotbarInput\)[\s\S]*?Cancel\(CancelReason\.Replaced' 'Independent native action invocations do not invalidate pending work.'
Assert-Contains $runtime 'public void Cancel\([^\)]*\)[\s\S]*?inputGenerations\.Invalidate\(\);[\s\S]*?engine\.Cancel' 'Cancellation does not invalidate the generation before clearing the core.'
Assert-Contains $runtime 'inputGenerations\.Current[\s\S]*?new ExactActionTuple' 'Candidates do not capture the current physical-input generation.'
Assert-CountAtLeast $runtime 'inputGenerations\.IsCurrent\(' 3 'Generation validity is not checked throughout outcome and final dispatch.'
Assert-Contains $runtime 'NowMilliseconds\s*>=\s*runtime\.ExpiresAtMilliseconds' 'Final monotonic deadline check is missing.'
Assert-Contains $runtime 'inputGenerations\.Invalidate\(\);[\s\S]*?Interlocked\.Exchange\(ref forcedMovementObserved,\s*1\)' 'Immediate knockback invalidation is missing.'
Assert-Contains $runtime 'lock \(dispatchGate\)[\s\S]*?engine\.Cancel\(CancelReason\.Knockback\)[\s\S]*?forcedMovementObserved' 'Knockback cancellation is not serialized with final native dispatch.'

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

if (-not [bool]$manifest.IsTestingExclusive) {
    throw 'The initial native-hook release must remain testing-exclusive.'
}

Write-Host 'PulseQueue static safety-contract checks passed.'
