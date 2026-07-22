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
$macroSafetyPath = Join-Path $RepositoryRoot 'src/PulseQueue.Core/MacroSafetyAnalyzer.cs'
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
$macroSafety = Get-Content -LiteralPath $macroSafetyPath -Raw
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

function Assert-ExactQuotedStringSet(
    [string]$Value,
    [string]$Pattern,
    [string[]]$Expected,
    [string]$Message) {
    $match = [regex]::Match($Value, $Pattern)
    if (-not $match.Success) { throw $Message }
    $actual = @(
        [regex]::Matches($match.Groups['Body'].Value, '"(?<Item>[^"\r\n]+)"') |
            ForEach-Object { $_.Groups['Item'].Value }
    )
    $actualCanonical = ($actual | Sort-Object -Unique) -join "`n"
    $expectedCanonical = ($Expected | Sort-Object -Unique) -join "`n"
    if ($actual.Count -ne $Expected.Count -or $actualCanonical -ne $expectedCanonical) {
        throw "$Message Found: $($actual -join ', ')."
    }
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

$queueReplayCalls = [regex]::Matches(
    $runtime,
    'useActionHook\.Original\(\s*actionManager,[\s\S]{0,500}?ActionManager\.UseActionMode\.Queue,').Count
$queueIdentityModes = [regex]::Matches(
    $runtime,
    'Mode\s*=\s*\(uint\)ActionManager\.UseActionMode\.Queue').Count
if ($queueReplayCalls -ne 1 -or $queueIdentityModes -ne 1) {
    throw "Expected one queue-mode replay call and one exact replay-queue identity mode; found $queueReplayCalls/$queueIdentityModes."
}

$originalCalls = [regex]::Matches($runtime, 'useActionHook\.Original\s*\(').Count
if ($originalCalls -ne 3) {
    throw "Expected exactly the original pass-through, one queue replay, and one shared exact-tuple Turbo call site; found $originalCalls."
}
Assert-Contains $runtime 'useActionHook\.Original\(\s*actionManager,\s*runtime\.Candidate\.ActionType,\s*runtime\.Candidate\.RequestedActionId,\s*runtime\.Candidate\.TargetId,\s*runtime\.Candidate\.ExtraParam,\s*\(ActionManager\.UseActionMode\)runtime\.Candidate\.ExactTuple\.Mode,\s*runtime\.Candidate\.ComboRouteId,' 'Turbo no longer repeats only the exact captured native action tuple.'

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
Assert-Contains $compatibility 'TryReadBoolean\([^\)]*[\s\S]*?"EnableMacroQueue"' 'ReAction Macro Queue is not inspected.'
Assert-Contains $compatibility 'TryReadBoolean\([^\)]*[\s\S]*?"EnableAutoDismount"' 'ReAction Auto Dismount is not inspected.'
Assert-Contains $compatibility 'TryReadBoolean\([^\)]*[\s\S]*?"EnableCameraRelativeDirectionals"' 'ReAction Camera Relative Directionals are not inspected.'
Assert-Contains $compatibility 'configuration\.ActionStackCount\s*!=\s*0[\s\S]*conflicts\.Add' 'Non-empty ReAction Action Stacks no longer suspend buffering.'
Assert-Contains $compatibility 'configuration\.AutoTargetEnabled[\s\S]*conflicts\.Add' 'ReAction Auto Target no longer suspends buffering.'
Assert-Contains $compatibility 'configuration\.TurboHotbarsEnabled[\s\S]*conflicts\.Add' 'ReAction Turbo Hotbars no longer suspend buffering.'
Assert-Contains $compatibility 'configuration\.MacroQueueEnabled[\s\S]*conflicts\.Add' 'ReAction Macro Queue no longer suspends buffering.'
Assert-Contains $compatibility 'record struct ReActionConfigurationSnapshot\([\s\S]*bool MacroQueueEnabled' 'ReAction Macro Queue is missing from the live configuration snapshot.'
Assert-Contains $compatibility 'TryReadReActionConfigurationObject\(configuration,\s*out var current\)[\s\S]*current\s*==\s*expected\.Value' 'The live ReAction guard no longer compares the complete audited snapshot.'
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
Assert-Contains $runtime 'if \(!nativeHotbarInput\)[\s\S]*?!capturedPendingMacro[\s\S]*?!IsOwnedTurboActionContinuation\([\s\S]*?Cancel\(CancelReason\.Replaced' 'Independent native action invocations do not invalidate pending work outside the exact macro/owned-continuation exceptions.'
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
Assert-Contains $runtime 'private void DispatchOnce\([\s\S]*nativeOutcome\s*==\s*NativeActionOutcome\.ImmediateAcceptance\s*&&\s*sequenceAdvanced[\s\S]*RecordSentSequence\(sequenceAfter,\s*NowMilliseconds\)' 'An exact immediate one-shot send no longer records its authoritative advanced sequence.'
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
Assert-Contains $nativeOwnership 'public bool TryAuthorizeExactDrain\([\s\S]*!current\.Equals\(value\.Snapshot\)\s*\|\|\s*sequenceMarker\s*!=\s*value\.SequenceMarker[\s\S]*owned\s*=\s*null;[\s\S]*generation\s*!=\s*value\.Generation[\s\S]*attempted\s*!=\s*value\.Attempted[\s\S]*!current\.Matches\(attempted\)[\s\S]*owned\s*=\s*null;[\s\S]*return true;' 'Exact native queue drain no longer validates and consumes the complete ownership token exactly once.'
$exactDrainRuntimeMatch = [regex]::Match(
    $runtime,
    'private bool IsOwnedTurboActionContinuation\([\s\S]*?(?=\r?\n\s*private static bool IsMacroExecutionActive\()')
if (-not $exactDrainRuntimeMatch.Success) {
    throw 'The audited exact native queue-drain authorization method could not be isolated.'
}
$exactDrainRuntime = $exactDrainRuntimeMatch.Value
Assert-Contains $exactDrainRuntime 'var currentQueue\s*=\s*CaptureNativeQueue\(actionManager\);[\s\S]*var ownedQueueTuple\s*=\s*candidate\.ExactTuple\s+with\s*\{[\s\S]*Mode\s*=\s*currentQueue\.Mode,[\s\S]*\};' 'Queue-drain ownership no longer uses the stored native QueueType as its exact tuple mode.'
Assert-Contains $exactDrainRuntime 'return mode\s*==\s*ActionManager\.UseActionMode\.Queue\s*&&\s*exactInvocation\s*&&\s*nativeQueueOwnership\.TryAuthorizeExactDrain\(\s*candidate\.InputGeneration,\s*actionManager->LastUsedActionSequence,\s*currentQueue,\s*ownedQueueTuple\)' 'A non-Queue invocation or a nonmatching generation/sequence/snapshot/tuple can authorize native queue drain.'
if ($exactDrainRuntime -match 'Mode\s*=\s*\(uint\)mode') {
    throw 'Queue-drain ownership incorrectly substitutes the observed invocation mode for the stored native QueueType.'
}

Assert-Contains $runtime 'GetSpellIdForAction\([\s\S]*GetMaxCharges\([\s\S]*CooldownTiming\.GetNextChargeRemainingMilliseconds' 'Multi-charge actions do not use the next charge boundary.'
Assert-Contains $cooldownTiming 'totalSeconds\s*/\s*maximumCharges[\s\S]*elapsedSeconds' 'The tested next-charge timing calculation is missing.'

# Native Turbo is a physical keyboard-hold source, not a timer or a generic
# ExecuteSlot repeater. A fresh pressed+held InputId edge must correlate to the
# exact standard-hotbar slot before the core can receive a certified request.
Assert-Contains $physicalInput 'FirstInputId\s*=\s*\(int\)InputId\.HOTBAR_1_1' 'Turbo input range no longer starts at standard hotbar 1 slot 1.'
Assert-Contains $physicalInput 'LastInputId\s*=\s*\(int\)InputId\.HOTBAR_10_B' 'Turbo input range no longer ends at standard hotbar 10 slot 12.'
Assert-Contains $physicalInput 'raw\s*<\s*FirstInputId\s*\|\|\s*raw\s*>\s*LastInputId[\s\S]*return false' 'Out-of-range InputIds can enter keyboard Turbo provenance.'
Assert-Contains $physicalInput 'pressed\s*=\s*pressedHook\.Original\(inputData,\s*inputId\)' 'Turbo does not preserve the native pressed result.'
if ($physicalInput -match 'IsInputIdDown\(') {
    throw 'Physical ownership again relies on the flickering logical Down state instead of the exact raw chord.'
}
Assert-Contains $physicalInput 'Array\.Fill\(needsRawRelease,\s*true\);[\s\S]*HookFromAddress' 'Physical input startup no longer requires every binding to prove a raw release before certification.'
Assert-Contains $physicalInput 'rawHoldOwners\[index\]\s+is\s+\{\s*\}\s+existingOwner[\s\S]*if \(IsPhysicalKeyDown\(inputData,\s*existingOwner\.PhysicalKey\)\)[\s\S]*if \(pressed\) heldRepeat\s*=\s*existingOwner;[\s\S]*goto ObservationComplete;[\s\S]*rawHoldOwners\[index\]\s*=\s*null' 'The raw hold latch no longer survives modifier/binding changes until a genuine physical key-up.'
Assert-Contains $physicalInput 'private static bool IsPhysicalKeyDown\([\s\S]*KeyboardInputs\.KeyState[\s\S]*KeyStateFlags\.Down' 'Raw latch release is no longer based on the original hardware key Down flag.'
Assert-Contains $physicalInput 'if \(!pressed\)\s*\{[\s\S]*needsRawRelease\[index\]\s*=\s*IsAnyBoundKeyboardKeyDown\(inputData,\s*inputId\);[\s\S]*goto ObservationComplete;[\s\S]*\}[\s\S]*TryFindFreshKeyboardChord[\s\S]*new CertifiedHotbarPress' 'A certified Turbo press is not gated by a fresh native pressed edge and an exact raw keyboard chord.'
Assert-Contains $physicalInput 'if \(needsRawRelease\[index\]\)[\s\S]*IsAnyBoundKeyboardKeyDown\(inputData,\s*inputId\)[\s\S]*goto ObservationComplete;[\s\S]*needsRawRelease\[index\]\s*=\s*false' 'A key already held when observation starts can be certified before a complete raw release.'
Assert-Contains $physicalInput 'activePhysicalKeyOwners\.ContainsKey\(physicalKey\)[\s\S]*certifiedPress\s*=\s*null' 'One held physical key can ambiguously certify more than one logical hotbar binding.'
Assert-Contains $physicalInput 'activePhysicalKeyOwners\.TryGetValue\(existingOwner\.PhysicalKey,\s*out var ownerPressId\)[\s\S]*ownerPressId\s*==\s*existingOwner\.PressId[\s\S]*activePhysicalKeyOwners\.Remove\(existingOwner\.PhysicalKey\)' 'Physical-key ownership is not released only for the exact latched press after key-up.'
Assert-Contains $physicalInput 'shouldSuppressHeldRepeat\(repeated\)[\s\S]*return false' 'An owned held-key typematic repeat is no longer suppressible.'
$rawOwnerReleaseWrites = [regex]::Matches($physicalInput, 'rawHoldOwners\[index\]\s*=\s*null').Count
$startupReleaseWrites = [regex]::Matches($physicalInput, 'needsRawRelease\[index\]\s*=\s*false').Count
$physicalKeyOwnerRemovals = [regex]::Matches($physicalInput, 'activePhysicalKeyOwners\.Remove\(').Count
if ($rawOwnerReleaseWrites -ne 1 -or $startupReleaseWrites -ne 1 -or $physicalKeyOwnerRemovals -ne 1) {
    throw "A physical hold can be re-certified through a path other than its audited raw key-up gates; found owner/startup/global-key release writes $rawOwnerReleaseWrites/$startupReleaseWrites/$physicalKeyOwnerRemovals."
}
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

# Only direct Action slots and separately opted-in, statically verified Macro
# slots are eligible. Both must yield exactly one captured Action/PvPAction.
# PvPCombo, items, movement, cross-hotbar/controller, mouse, and arbitrary
# macros remain ordinary vanilla input.
Assert-Contains $runtime 'DirectActionHotbarSlotType\s*=\s*1\s*;' 'The audited direct Action slot type changed.'
Assert-Contains $runtime 'MacroHotbarSlotType\s*=\s*7\s*;' 'The audited Macro slot type changed.'
Assert-Contains $runtime 'commandType\s+is\s+not\s+\(DirectActionHotbarSlotType\s+or\s+MacroHotbarSlotType\)[\s\S]*return null' 'A slot outside the audited direct Action or Macro types can enter Turbo provenance.'
Assert-Contains $runtime 'if \(slotIdentity\.CommandType\s*==\s*MacroHotbarSlotType\)[\s\S]*TryBeginMacroTurbo\(scope,\s*press,\s*slotIdentity,\s*inputSource\);[\s\S]*return;[\s\S]*slotIdentity\.CommandType\s*!=\s*DirectActionHotbarSlotType' 'Direct Action and separately verified Macro starts are no longer routed through distinct gates.'
Assert-Contains $runtime 'candidate\.InputGeneration\s*!=\s*scope\.Generation[\s\S]*slotIdentity\.CommandId\s*!=\s*candidate\.RequestedActionId' 'Direct Action Turbo start no longer proves the exact slot command ID and input generation.'
Assert-Contains $runtime 'actionType\s+is\s+not\s+\(ActionType\.Action\s+or\s+ActionType\.PvPAction\)[\s\S]*?return null' 'A non-Action/PvPAction invocation can become a Turbo candidate.'
Assert-Contains $runtime 'inputScope\.ActionInvocationCount\+\+;[\s\S]*?ActionInvocationCount\s*>\s*1[\s\S]*?TurboCandidate\s*=\s*null;[\s\S]*?TurboDisqualified\s*=\s*true' 'A hotbar slot with multiple action invocations is not disqualified from Turbo.'
Assert-Contains $runtime 'scope\.TurboCandidate\s+is\s+not\s+\{\s*\}\s+candidate[\s\S]*scope\.TurboDisqualified[\s\S]*scope\.ActionInvocationCount\s*!=\s*1' 'Direct Action Turbo start no longer requires exactly one non-disqualified captured action invocation.'
if ($runtime -match 'PvPComboHotbarSlotType') {
    throw 'PvPCombo regained Turbo ownership without an audited end-to-end route proof.'
}

# Macro Turbo is a second explicit permission. A macro must pass the fail-closed
# analyzer before its one native action can be captured; the complete macro is
# never replayed by a synthetic pulse.
Assert-Contains $runtime 'private void TryBeginMacroTurbo\([\s\S]*if \(!configuration\.TurboMacrosEnabled\)[\s\S]*return;[\s\S]*TryReadSafeMacroProfile\(slotIdentity,\s*out var profile,\s*out var failure\)' 'Macro Turbo can start without its separate opt-in and verified safe profile.'
Assert-Contains $runtime 'private static bool TryReadSafeMacroProfile\([\s\S]*MacroSafetyAnalyzer\.Analyze\(text\)[\s\S]*return analysis\.IsSafe' 'Runtime macro eligibility no longer delegates to the fail-closed MacroSafetyAnalyzer.'
Assert-ExactQuotedStringSet `
    -Value $macroSafety `
    -Pattern 'ActionCommands\s*=\s*new\([^\)]*\)\s*\{(?<Body>[\s\S]*?)\};' `
    -Expected @('/ac', '/action', '/pvpac', '/pvpaction') `
    -Message 'MacroSafetyAnalyzer action commands differ from the exact audited allowlist.'
Assert-ExactQuotedStringSet `
    -Value $macroSafety `
    -Pattern 'HarmlessMetadataCommands\s*=\s*new\([^\)]*\)\s*\{(?<Body>[\s\S]*?)\};' `
    -Expected @('/micon', '/macroicon', '/merror', '/macroerror') `
    -Message 'MacroSafetyAnalyzer metadata commands differ from the exact audited allowlist.'
Assert-Contains $macroSafety 'ContainsWaitDirective\(line\)[\s\S]*MacroSafetyFailure\.WaitDirective' 'Macro wait directives are no longer rejected.'
Assert-Contains $macroSafety 'ActionCommands\.Contains\(command\)[\s\S]*actionLine\s+is\s+not\s+null[\s\S]*MacroSafetyFailure\.MultipleActions' 'Macros with more than one action command are no longer rejected.'
Assert-Contains $macroSafety 'command\.Equals\("/assist"[\s\S]*actionLine\s+is\s+null[\s\S]*!hasResolver' 'The single original-only /assist resolver is no longer constrained to precede the action.'
Assert-Contains $macroSafety '!HarmlessMetadataCommands\.Contains\(command\)[\s\S]*MacroSafetyFailure\.UnsupportedCommand' 'Unknown or state-changing macro commands no longer fail closed.'
Assert-Contains $macroSafety 'ContentFingerprint\.Length\s*==\s*64[\s\S]*!string\.IsNullOrWhiteSpace\(ActionCommand\)' 'A safe macro profile no longer requires a SHA-256-sized fingerprint and captured action command.'
Assert-Contains $macroSafety 'SHA256\.HashData\(Encoding\.UTF8\.GetBytes\(canonical\.ToString\(\)\)\)' 'Safe macro content no longer receives an exact canonical SHA-256 fingerprint.'

Assert-Contains $runtime 'private static bool IsMacroExecutionActive\(\)[\s\S]*shell->MacroLocked' 'Macro capture is no longer guarded by the native MacroLocked state.'
Assert-Contains $runtime 'private void BeginHotbarInput\([\s\S]*MacroWasLockedBeforeExecution\s*=\s*IsMacroExecutionActive\(\)' 'The native MacroLocked state is no longer snapshotted before the physical slot executes.'
Assert-Contains $runtime 'macroScope\.MacroWasLockedBeforeExecution[\s\S]*macroScope\.TurboCandidate\s*=\s*null;[\s\S]*macroScope\.TurboDisqualified\s*=\s*true' 'A macro action can be captured when the macro executor was already locked before this physical press.'
Assert-Contains $runtime 'private void TryBeginMacroTurbo\([\s\S]*if \(scope\.MacroWasLockedBeforeExecution\)[\s\S]*return;' 'Macro Turbo no longer rejects a pre-existing macro-executor owner.'
Assert-Contains $runtime 'var macroExecutionActive\s*=\s*IsMacroExecutionActive\(\);[\s\S]*scope\.TurboCandidate\s+is\s+null\s*&&\s*!macroExecutionActive[\s\S]*return;[\s\S]*if \(macroExecutionActive\)[\s\S]*new PendingMacroCapture\([\s\S]*MaximumMacroCaptureMilliseconds' 'A pending macro capture can start without a newly locked native macro executor or a synchronous captured action.'
Assert-Contains $runtime 'private bool TryCapturePendingMacroInvocation\([\s\S]*mode\s*!=\s*ActionManager\.UseActionMode\.Macro[\s\S]*!IsMacroExecutionActive\(\)[\s\S]*pending\.ActionInvocationCount\+\+[\s\S]*pending\.ActionInvocationCount\s*>\s*1[\s\S]*pending\.Disqualified\s*=\s*true' 'Pending macro capture no longer requires Macro mode/lock or reject multiple native action invocations.'
Assert-Contains $runtime 'private void TryCompletePendingMacroCapture\([\s\S]*if \(IsMacroExecutionActive\(\)\) return;[\s\S]*pending\.ActionInvocationCount\s*!=\s*1[\s\S]*pending\.Candidate\s+is\s+not\s+\{\s*\}\s+candidate' 'Macro Turbo can start before MacroLocked clears or without exactly one captured candidate.'
Assert-Contains $runtime 'private static bool IsMacroTargetProven\(Candidate candidate\)\s*=>\s*!candidate\.IncludeResolverTargets\s*\|\|\s*candidate\.TargetId\s+is\s+not\s+\(0\s+or\s+InvalidObjectId\)' 'A resolver-based macro can own Turbo without a proven immutable explicit target ID.'
Assert-CountAtLeast $runtime 'IsMacroTargetProven\(' 4 'Macro target identity is not proven at synchronous capture, pending capture, and runtime start.'
Assert-Contains $runtime 'macroProfile\s+is\s+\{\s*\}\s+expectedMacro[\s\S]*currentMacro\.ContentFingerprint\s*!=\s*expectedMacro\.ContentFingerprint[\s\S]*return;' 'Macro content is not hash-revalidated before Turbo ownership starts.'
Assert-Contains $runtime 'macroProfileMatches\s*=\s*runtime\.MacroProfile\s+is\s+null[\s\S]*currentMacroProfile\.ContentFingerprint\s*==\s*runtime\.MacroProfile\.Value\.ContentFingerprint[\s\S]*bindingMatches' 'Macro content is not hash-revalidated during live Turbo safety checks.'
Assert-Contains $runtime 'macroProfile\s+is\s+\{\s*\}\s+macro[\s\S]*Convert\.ToUInt64\(macro\.ContentFingerprint\[\.\.16\],\s*16\)' 'Macro content hash is no longer encoded into the immutable Turbo intent fingerprint.'
Assert-Contains $runtime 'commandType\s+is\s+not\s+\(DirectActionHotbarSlotType\s+or\s+MacroHotbarSlotType\)[\s\S]*commandType\s*==\s*DirectActionHotbarSlotType\s*&&\s*commandId\s*==\s*0[\s\S]*return null;' 'Macro ID zero is no longer distinguished from an invalid direct-action ID.'
$macroIdDecode = [regex]::Match(
    $runtime,
    'private static bool TryDecodeMacroCommandId\([\s\S]*?(?=\r?\n\s*private HoldRepeatOptions CreateTurboOptions\()')
if (-not $macroIdDecode.Success) {
    throw 'The audited macro command-ID decoder could not be isolated.'
}
Assert-Contains $macroIdDecode.Value 'commandId\s*<\s*100[\s\S]*macroIndex\s*=\s*commandId;[\s\S]*return true;' 'Personal macro IDs are no longer decoded exactly as 0-99.'
Assert-Contains $macroIdDecode.Value 'commandId\s+is\s+>=\s*256\s+and\s+<\s*356[\s\S]*macroSet\s*=\s*1;[\s\S]*macroIndex\s*=\s*commandId\s*-\s*256;[\s\S]*return true;' 'Shared macro IDs are no longer decoded exactly as 256-355.'
$macroIdSuccessPaths = [regex]::Matches($macroIdDecode.Value, 'return true;').Count
if ($macroIdSuccessPaths -ne 2 -or $macroIdDecode.Value -match '>=\s*100\s+and\s+<\s*200') {
    throw 'Ambiguous or unaudited macro command-ID encodings can reach Macro Turbo.'
}

# Macro outcomes are classified separately. Only an exact immediate send may
# seed an acknowledgement; native queues are never accepted for Macro Turbo.
Assert-Contains $runtime 'private void ProcessMacroTurboOriginalOutcome\([\s\S]*NativeActionOutcomeClassifier\.Classify\([\s\S]*candidate\.QueueAtCapture,[\s\S]*queueAfter,[\s\S]*candidate\.ExactTuple\)[\s\S]*RecordInitialTurboOutcome\([\s\S]*exactQueueClaimed:\s*false,[\s\S]*allowQueuedOutcome:\s*false\)' 'Macro original outcomes no longer use strict exact classification with queued outcomes disabled.'
Assert-Contains $runtime 'NativeActionOutcome\.MatchingNewQueue[\s\S]*when allowQueuedOutcome[\s\S]*&&\s*!sequenceAdvanced[\s\S]*&&\s*exactQueueClaimed[\s\S]*CreateTurboAcknowledgementSeed' 'Initial queued acknowledgements can be seeded without explicit queue permission and exact ownership.'
Assert-Contains $runtime 'var disqualified\s*=\s*outcome\s+is\s+NativeActionOutcome\.ForeignOrPreexistingQueue[\s\S]*outcome\s*==\s*NativeActionOutcome\.ImmediateAcceptance\s*&&\s*seed\s+is\s+null[\s\S]*outcome\s*==\s*NativeActionOutcome\.MatchingNewQueue\s*&&\s*seed\s+is\s+null[\s\S]*ApplyTurboCaptureOutcome\(candidate,\s*seed,\s*disqualified\)' 'Foreign, unacknowledgeable immediate, or unowned queued initial outcomes no longer disqualify Turbo capture.'
Assert-Contains $runtime 'macroScope[\s\S]*CandidateCaptureComplete' 'The original physical macro action can incorrectly enter one-shot buffering.'

# Every action that can precede another held-input pulse establishes an exact
# acknowledgement barrier: the original physical action, a one-shot buffered
# dispatch, and each Turbo pulse. Early original effects are retained briefly so
# a synchronous effect cannot race Turbo runtime construction.
Assert-Contains $runtime 'private void ProcessOriginalOutcome\([\s\S]*exactQueueClaimed\s*=\s*nativeOutcome\s*==\s*NativeActionOutcome\.MatchingNewQueue[\s\S]*TryClaimNewQueue\([\s\S]*RecordInitialTurboOutcome\([\s\S]*exactQueueClaimed,[\s\S]*allowQueuedOutcome:\s*true\)' 'The original physical action no longer records only exact immediate/owned-queue acknowledgement provenance.'
Assert-Contains $runtime 'private static TurboAcknowledgementSeed CreateTurboAcknowledgementSeed\([\s\S]*new TurboActionEffectExpectation\([\s\S]*candidate\.ActionType,[\s\S]*candidate\.RequestedActionId,[\s\S]*candidate\.ResolvedActionId,[\s\S]*sequenceMode,[\s\S]*sequenceMarker\)[\s\S]*NowMilliseconds' 'Initial acknowledgement seeds no longer retain the exact action identity, sequence mode/marker, and start time.'
Assert-Contains $runtime 'private void ApplyTurboCaptureOutcome\([\s\S]*scope\.Generation\s*==\s*candidate\.InputGeneration[\s\S]*scope\.TurboCandidate\?\.ExactTuple\s*==\s*candidate\.ExactTuple[\s\S]*scope\.InitialAcknowledgement\s*=\s*seed[\s\S]*pending\.Generation\s*==\s*candidate\.InputGeneration[\s\S]*pending\.Candidate\?\.ExactTuple\s*==\s*candidate\.ExactTuple[\s\S]*pending\.InitialAcknowledgement\s*=\s*seed' 'An original acknowledgement seed can attach to a different generation or action tuple.'
Assert-Contains $runtime 'turboRuntime\s*=\s*runtime;[\s\S]*scope\.InitialAcknowledgement\s+is\s+\{\s*\}\s+initialAcknowledgement[\s\S]*!BeginInitialTurboAcknowledgement\(runtime,\s*initialAcknowledgement\)[\s\S]*CancelTurboUnsafe\([\s\S]*PulseRejected' 'Turbo ownership can start without installing or proving the original-action acknowledgement barrier.'

Assert-Contains $runtime 'private void DispatchOnce\([\s\S]*NativeActionOutcome\.ImmediateAcceptance\s*&&\s*sequenceAdvanced[\s\S]*BeginOneShotTurboAcknowledgement\([\s\S]*ImmediateExact,[\s\S]*sequenceAfter,[\s\S]*replayTuple[\s\S]*CancelMatchingTurboAfterOneShot' 'An immediate one-shot send can leave the matching Turbo hold active without an exact acknowledgement barrier.'
Assert-Contains $runtime 'private void DispatchOnce\([\s\S]*NativeActionOutcome\.MatchingNewQueue\s*&&\s*!sequenceAdvanced[\s\S]*TryClaimNewQueue\([\s\S]*BeginOneShotTurboAcknowledgement\([\s\S]*QueuedAfterBaseline,[\s\S]*sequenceBefore,[\s\S]*replayTuple[\s\S]*CancelMatchingTurboAfterOneShot' 'A queued one-shot can leave the matching Turbo hold active without exact ownership and an acknowledgement barrier.'
Assert-Contains $runtime 'private bool BeginOneShotTurboAcknowledgement\([\s\S]*runtime\.Candidate\.InputGeneration\s*!=\s*candidate\.InputGeneration[\s\S]*return true;[\s\S]*new TurboActionEffectExpectation\([\s\S]*exactTuple\.ActionType,[\s\S]*exactTuple\.RequestedActionId,[\s\S]*exactTuple\.ResolvedActionId[\s\S]*BeginTurboAcknowledgement\(' 'A one-shot acknowledgement can block or attach to a different Turbo generation/action identity.'
Assert-Contains $runtime 'NativeActionOutcome\.Rejected[\s\S]*CancelMatchingTurboAfterOneShot\([\s\S]*else[\s\S]*CancelMatchingTurboAfterOneShot\(' 'Rejected or unproven one-shot outcomes no longer terminate the matching held input without retry.'

Assert-Contains $runtime 'MaximumRecentActionEffectAgeMilliseconds\s*=\s*2_000\s*;' 'The early acknowledgement cache age changed from the audited two-second bound.'
Assert-Contains $runtime 'header->SourceSequence\s*!=\s*0\s*&&\s*casterEntityId\s*==\s*currentLocalEntityId[\s\S]*recentLocalActionEffects\.Enqueue\([\s\S]*TurboActionEffectObservation\([\s\S]*header->ActionType,[\s\S]*header->ActionId,[\s\S]*header->SourceSequence[\s\S]*TryCompleteTurboAcknowledgement\(header\)' 'Local nonzero-sequence effects are not cached before live acknowledgement completion.'
Assert-Contains $runtime 'private void RemoveStaleActionEffects\([\s\S]*MaximumRecentActionEffectAgeMilliseconds[\s\S]*recentLocalActionEffects\.TryDequeue' 'Stale early acknowledgements are no longer removed at the audited bound.'
Assert-Contains $runtime 'private void OnFrameworkUpdate\([\s\S]*RemoveStaleActionEffects\(now\)' 'The recent early-acknowledgement cache is no longer pruned from the framework boundary.'
Assert-Contains $runtime 'private bool WasRecentlyAcknowledged\([\s\S]*observed\.ObservedAtMilliseconds\s*<\s*seed\.StartedAtMilliseconds[\s\S]*TurboActionEffectAcknowledgementMatcher\.Matches\([\s\S]*seed\.Expectation,[\s\S]*observed\.Observation' 'The early acknowledgement cache can satisfy a seed with an older or nonmatching action effect.'
Assert-Contains $runtime 'private bool BeginInitialTurboAcknowledgement\([\s\S]*WasRecentlyAcknowledged\(seed\)[\s\S]*BeginTurboAcknowledgement\([\s\S]*pulse:\s*null,[\s\S]*seed\.Expectation,[\s\S]*seed\.StartedAtMilliseconds' 'Original-action acknowledgement no longer consumes the early cache before installing its exact barrier.'
Assert-Contains $runtime 'public void Cancel\([\s\S]*recentLocalActionEffects\.Clear\(\)[\s\S]*CancelTurboUnsafe' 'Cancellation can retain an early acknowledgement across ownership generations.'

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

# Final dispatch is serialized with cancellation and revalidates the newest
# token. Direct Action and Macro owners share exactly one immutable native
# action-tuple path; no synthetic pulse re-executes a hotbar slot.
Assert-Contains $runtime 'private void DispatchTurboPulse\([\s\S]*?lock \(dispatchGate\)[\s\S]*?turboEngine\.IsTokenCurrent\(token\)[\s\S]*?ObserveTurbo\(runtime,\s*checkLiveMOAction:\s*true\)' 'Turbo final token and safety validation are not serialized under the dispatch gate.'
$turboDispatchMatch = [regex]::Match(
    $runtime,
    'private void DispatchTurboPulse\([\s\S]*?(?=\r?\n\s*private TurboObservation ObserveTurbo\()')
if (-not $turboDispatchMatch.Success) {
    throw 'The audited Turbo dispatch method could not be isolated.'
}
$turboDispatch = $turboDispatchMatch.Value
$turboTupleCalls = [regex]::Matches(
    $turboDispatch,
    'useActionHook\.Original\(\s*actionManager,\s*runtime\.Candidate\.ActionType,\s*runtime\.Candidate\.RequestedActionId,\s*runtime\.Candidate\.TargetId,\s*runtime\.Candidate\.ExtraParam,\s*\(ActionManager\.UseActionMode\)runtime\.Candidate\.ExactTuple\.Mode,\s*runtime\.Candidate\.ComboRouteId,').Count
$turboUseActionCalls = [regex]::Matches($turboDispatch, 'useActionHook\.Original\s*\(').Count
if ($turboTupleCalls -ne 1 -or $turboUseActionCalls -ne 1) {
    throw "Direct and Macro Turbo must share exactly one captured-tuple UseAction call; found $turboTupleCalls exact out of $turboUseActionCalls total."
}
if ($turboDispatch -match 'executeSlot(?:ById)?Hook\.Original\s*\(' -or
    $turboDispatch -match 'if \(runtime\.IsMacro\)') {
    throw 'Turbo dispatch can diverge into a slot replay or a separate Macro/Direct action path.'
}
Assert-Contains $runtime 'var resolvedActionId\s*=[\s\S]*GetAdjustedActionId\(runtime\.Candidate\.RequestedActionId\);[\s\S]*resolvedActionId\s*!=\s*0[\s\S]*resolvedActionId\s*==\s*runtime\.Candidate\.ResolvedActionId[\s\S]*TryGetEligibleActionProfile' 'Turbo no longer cancels when the currently resolved action ID differs from the captured resolved ID.'
Assert-Contains $turboDispatch 'var exactTuple\s*=\s*runtime\.Candidate\.ExactTuple;' 'Turbo dispatch no longer uses the captured ExactTuple unchanged.'
$exactTupleAssignments = [regex]::Matches($turboDispatch, '\bexactTuple\s*=').Count
if ($exactTupleAssignments -ne 1 -or
    $turboDispatch -match 'runtime\.Candidate\.ExactTuple\s+with\s*\{') {
    throw 'Turbo dispatch rewrites the captured ExactTuple before outcome classification or acknowledgement.'
}
if ($runtime -match '\bExecuteMacro\w*\s*\(') {
    throw 'Runtime contains a full-macro execution path; Macro Turbo may replay only the captured native action tuple.'
}
$executeSlotByIdOriginalCalls = [regex]::Matches($runtime, 'executeSlotByIdHook\.Original\s*\(').Count
if ($executeSlotByIdOriginalCalls -ne 1) {
    throw "ExecuteSlotById may exist only as the original manual pass-through; found $executeSlotByIdOriginalCalls call sites."
}
Assert-Contains $runtime 'NativeActionOutcomeClassifier\.Classify\([\s\S]*?result\s*!=\s*0\s*\|\|\s*sequenceAdvanced,[\s\S]*?queueBefore,[\s\S]*?queueAfter,[\s\S]*?exactTuple\)' 'Turbo pulse outcome is not classified against the complete exact queue tuple.'
Assert-Contains $runtime 'nativeOutcome\s*==\s*NativeActionOutcome\.ImmediateAcceptance\s*&&\s*sequenceAdvanced[\s\S]*?TurboAcknowledgementSequenceMode\.ImmediateExact[\s\S]*?sequenceAfter' 'Immediate Turbo acceptance is not gated by an exact advanced sequence.'
Assert-Contains $runtime 'nativeOutcome\s*==\s*NativeActionOutcome\.MatchingNewQueue[\s\S]*&&\s*!sequenceAdvanced[\s\S]*&&\s*!runtime\.IsMacro[\s\S]*TryClaimNewQueue\([\s\S]*if \(!claimed[\s\S]*TurboAcknowledgementSequenceMode\.QueuedAfterBaseline[\s\S]*sequenceBefore' 'Queued Turbo acceptance is not restricted to a newly owned non-Macro queue without a simultaneous sequence transition.'
Assert-Contains $runtime 'private void RejectTurboPulseUnsafe\([\s\S]*?HoldRepeatCancelReason\.PulseRejected,[\s\S]*?hold ended without retry' 'An unproven Turbo pulse is not terminal without retry.'
Assert-Contains $runtime 'private void CancelTurboUnsafe\([\s\S]*?turboEngine\.Cancel\(reason\);[\s\S]*?turboRuntime\s*=\s*null' 'Turbo cancellation does not invalidate the core hold and runtime token together.'
Assert-Contains $runtime 'MaximumTurboAcknowledgementMilliseconds\s*=\s*2_000\s*;' 'Turbo acknowledgement timeout changed from the audited bound.'
Assert-Contains $runtime 'private void ProcessTurbo\([\s\S]*observation\.ActionReady\s*&&\s*acknowledgement\s+is\s+null[\s\S]*if \(acknowledgement\s+is\s+not\s+null\)[\s\S]*MaximumTurboAcknowledgementMilliseconds[\s\S]*PulseRejected[\s\S]*return;[\s\S]*DispatchTurboPulse' 'Turbo can issue another pulse while an original, one-shot, or prior pulse acknowledgement is pending or timed out.'
Assert-Contains $runtime 'new TurboActionEffectExpectation\([\s\S]*?exactTuple\.ActionType,[\s\S]*?exactTuple\.RequestedActionId,[\s\S]*?exactTuple\.ResolvedActionId,[\s\S]*?sequenceMode,[\s\S]*?sequenceMarker' 'Turbo acknowledgement does not retain exact type, requested ID, resolved ID, and sequence identity.'
Assert-Contains $runtime 'TryCompleteTurboAcknowledgement\(header\)[\s\S]*?TurboActionEffectAcknowledgementMatcher\.Matches\([\s\S]*?acknowledgement\.Expectation,[\s\S]*?observation\)' 'Local-player action effects are not matched against the exact Turbo acknowledgement identity.'
Assert-Contains $runtime 'header->SourceSequence\s*!=\s*0\s*&&\s*casterEntityId\s*==\s*currentLocalEntityId[\s\S]*?TryCompleteTurboAcknowledgement\(header\)' 'A zero-sequence or foreign-caster action effect can reach Turbo acknowledgement matching.'
Assert-Contains $runtime 'private bool BeginTurboAcknowledgement\([\s\S]*pulse\s+is\s+\{\s*\}\s+pulseToken[\s\S]*!pulseToken\.IsValid\s*\|\|\s*!turboEngine\.IsTokenCurrent\(pulseToken\)[\s\S]*new TurboAcknowledgement\([\s\S]*snapshot\.HoldId,[\s\S]*snapshot\.PressId' 'A pulse acknowledgement can be installed without the exact current pulse, hold, and press token.'
Assert-Contains $runtime 'private bool IsTurboAcknowledgementCurrent\([\s\S]*snapshot\.HoldId\s*==\s*acknowledgement\.HoldId[\s\S]*snapshot\.PressId\s*==\s*acknowledgement\.PressId[\s\S]*latestCertifiedPressId[\s\S]*acknowledgement\.Pulse\s+is\s+not\s+\{\s*\}\s+pulse[\s\S]*turboEngine\.IsTokenCurrent\(pulse\)' 'A stale hold, press, or pulse token can complete a newer Turbo acknowledgement.'
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
Assert-Contains $configuration 'CurrentVersion\s*=\s*3\s*;' 'Turbo configuration migration version changed unexpectedly.'
Assert-Contains $configuration 'if \(Version\s*<=\s*1\)[\s\S]*TurboEnabled\s*=\s*false' 'Existing configurations can silently opt into native Turbo.'
Assert-Contains $configuration 'if \(Version\s*<=\s*2\)[\s\S]*TurboMacrosEnabled\s*=\s*false' 'Existing configurations can silently opt into Macro Turbo.'
Assert-Contains $configuration 'ResetToDefaults\(\)[\s\S]*TurboEnabled\s*=\s*false' 'Reset defaults no longer keep native Turbo opt-in.'
Assert-Contains $configuration 'ResetToDefaults\(\)[\s\S]*TurboMacrosEnabled\s*=\s*false' 'Reset defaults no longer keep Macro Turbo opt-in.'
Assert-Contains $compatibility 'if \(configuration\.TurboHotbarsEnabled\)\s*\{[\s\S]*?conflicts\.Add\(\s*"Disable ReAction''s Turbo Hotbars;' 'ReAction Turbo no longer creates an actionable hard conflict.'
Assert-Contains $compatibility 'if \(configuration\.MacroQueueEnabled\)\s*\{[\s\S]*?conflicts\.Add\(\s*"Disable ReAction''s Macro Queue;' 'ReAction Macro Queue no longer creates an actionable hard conflict.'

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
