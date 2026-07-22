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
$macroTranscriptPath = Join-Path $RepositoryRoot 'src/PulseQueue.Core/MacroTurboTranscript.cs'
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
$macroTranscript = Get-Content -LiteralPath $macroTranscriptPath -Raw
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
    throw "Expected exactly the original pass-through, one exact one-shot queue replay, and one exact direct-Turbo call site; found $originalCalls."
}
Assert-Contains $runtime 'useActionHook\.Original\(\s*actionManager,\s*runtime\.Candidate\.ActionType,\s*runtime\.Candidate\.RequestedActionId,\s*runtime\.Candidate\.TargetId,\s*runtime\.Candidate\.ExtraParam,\s*\(ActionManager\.UseActionMode\)runtime\.Candidate\.ExactTuple\.Mode,\s*runtime\.Candidate\.ComboRouteId,' 'Direct Turbo no longer repeats only the exact captured native action tuple.'

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
Assert-Contains $runtime 'if \(!nativeHotbarInput\)[\s\S]*IsOwnedMacroTurboQueueDrain\([\s\S]*ownedMacroExecution\s*=\s*!ownedMacroQueueDrain[\s\S]*IsOwnedMacroTurboExecutionContinuation\(\s*thisPtr,\s*actionType,\s*actionId,\s*targetId,\s*extraParam,\s*mode,\s*comboRouteId,\s*out continuationEntry,\s*out firstInitialEntry,\s*out suppressSyntheticContinuation\)[\s\S]*suppressSyntheticMacroCall\s*\|=\s*suppressSyntheticContinuation;[\s\S]*!ownedMacroQueueDrain[\s\S]*!ownedMacroExecution[\s\S]*!IsOwnedTurboActionContinuation\([\s\S]*Cancel\(CancelReason\.Replaced' 'Independent native action invocations no longer cancel pending work outside only the exact Macro queue-drain, owned Macro-executor, and direct queue-drain exceptions.'
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
if ($freshMarkerWrites -ne 2) {
    throw "Expected one direct and one Macro Turbo construction of a certified-fresh request; found $freshMarkerWrites."
}
$macroStartMatch = [regex]::Match(
    $runtime,
    'private void StartMacroTurboRuntime\([\s\S]*?(?=\r?\n\s*private void StartTurboRuntime\()')
if (-not $macroStartMatch.Success) {
    throw 'The audited Macro Turbo start method could not be isolated.'
}
$macroStart = $macroStartMatch.Value
$directStartMatch = [regex]::Match(
    $runtime,
    'private void StartTurboRuntime\([\s\S]*?(?=\r?\n\s*private void LogTurboStartRejected\()')
if (-not $directStartMatch.Success) {
    throw 'The audited direct Turbo start method could not be isolated.'
}
$directStart = $directStartMatch.Value
Assert-Contains $macroStart 'new HoldRepeatStartRequest\(\s*press\.PressId,\s*scope\.Generation,\s*slotIdentity\.ControlFingerprint,[\s\S]*?IsCertifiedFreshPress:\s*true\)' 'Macro Turbo can manufacture a fresh hold request without the certified press, generation, and exact control fingerprint.'
Assert-Contains $directStart 'new HoldRepeatStartRequest\(\s*press\.PressId,\s*scope\.Generation,\s*slotIdentity\.ControlFingerprint,[\s\S]*?IsCertifiedFreshPress:\s*true\)' 'Direct Turbo can manufacture a fresh hold request without the certified press, generation, and exact control fingerprint.'

# Only direct Action slots and separately opted-in, statically verified Macro
# slots are eligible. Direct slots must still yield exactly one captured
# Action/PvPAction tuple. Macro slots follow their own action-only slot-replay
# contract. PvPCombo, items, movement, cross-hotbar/controller, mouse, and
# arbitrary side-effect macros remain ordinary vanilla input.
Assert-Contains $runtime 'DirectActionHotbarSlotType\s*=\s*1\s*;' 'The audited direct Action slot type changed.'
Assert-Contains $runtime 'MacroHotbarSlotType\s*=\s*7\s*;' 'The audited Macro slot type changed.'
Assert-Contains $runtime 'commandType\s+is\s+not\s+\(DirectActionHotbarSlotType\s+or\s+MacroHotbarSlotType\)[\s\S]*return null' 'A slot outside the audited direct Action or Macro types can enter Turbo provenance.'
Assert-Contains $runtime 'if \(slotIdentity\.CommandType\s*==\s*MacroHotbarSlotType\)[\s\S]*TryBeginMacroTurbo\(scope,\s*press,\s*slotIdentity,\s*inputSource\);[\s\S]*return;[\s\S]*slotIdentity\.CommandType\s*!=\s*DirectActionHotbarSlotType' 'Direct Action and separately verified Macro starts are no longer routed through distinct gates.'
Assert-Contains $runtime 'candidate\.InputGeneration\s*!=\s*scope\.Generation[\s\S]*slotIdentity\.CommandId\s*!=\s*candidate\.RequestedActionId' 'Direct Action Turbo start no longer proves the exact slot command ID and input generation.'
Assert-Contains $runtime 'actionType\s+is\s+not\s+\(ActionType\.Action\s+or\s+ActionType\.PvPAction\)[\s\S]*?return null' 'A non-Action/PvPAction invocation can become a Turbo candidate.'
Assert-Contains $runtime 'inputScope\.ActionInvocationCount\+\+;[\s\S]*?ActionInvocationCount\s*>\s*1[\s\S]*?TurboCandidate\s*=\s*null;[\s\S]*?TurboDisqualified\s*=\s*true' 'A direct hotbar slot with multiple action invocations is not disqualified from exact-action Turbo.'
Assert-Contains $runtime 'scope\.TurboCandidate\s+is\s+not\s+\{\s*\}\s+candidate[\s\S]*scope\.TurboDisqualified[\s\S]*scope\.ActionInvocationCount\s*!=\s*1' 'Direct Action Turbo start no longer requires exactly one non-disqualified captured action invocation.'
if ($runtime -match 'PvPComboHotbarSlotType') {
    throw 'PvPCombo regained Turbo ownership without an audited end-to-end route proof.'
}

# Macro Turbo is a second explicit permission. It accepts one or more action
# lines, plus only audited metadata, because each pulse replays the same
# certified slot and leaves line/target selection to the native macro executor.
Assert-Contains $runtime 'private void TryBeginMacroTurbo\([\s\S]*if \(!configuration\.TurboMacrosEnabled\)[\s\S]*return;[\s\S]*if \(scope\.MacroWasLockedBeforeExecution\)[\s\S]*return;' 'Macro Turbo can start without its separate opt-in and a free initial macro executor.'
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
Assert-Contains $macroSafety 'ContentFingerprint\.Length\s*==\s*64[\s\S]*ActionCount\s*>\s*0' 'A safe macro profile no longer requires a SHA-256-sized fingerprint and at least one action line.'
$macroAnalyzeMatch = [regex]::Match(
    $macroSafety,
    'public static MacroSafetyAnalysis Analyze\([\s\S]*?(?=\r?\n\s*private static MacroSafetyAnalysis Rejected\()')
if (-not $macroAnalyzeMatch.Success) {
    throw 'The audited action-only macro analyzer could not be isolated.'
}
$macroAnalyze = $macroAnalyzeMatch.Value
Assert-Contains $macroAnalyze 'if \(ActionCommands\.Contains\(command\)\)\s*\{\s*actionCount\+\+;\s*continue;\s*\}' 'The macro analyzer no longer accepts and counts every allowlisted action line.'
Assert-Contains $macroAnalyze 'if \(actionCount\s*==\s*0\)[\s\S]*MacroSafetyFailure\.MissingAction' 'A metadata-only macro can pass without at least one action line.'
Assert-Contains $macroAnalyze 'new SafeActionMacroProfile\(fingerprint,\s*actionCount\)' 'The safe macro profile no longer retains the complete 1..N action count.'
if ($macroAnalyze -match 'actionCount\s*(?:>|>=|==|!=)\s*[1-9]\d*' -or
    $macroAnalyze -match 'actionCount\s+is\s+(?:>|>=)\s*[1-9]\d*') {
    throw 'The macro analyzer imposes an obsolete upper action-count rejection instead of accepting 1..N action lines.'
}
Assert-Contains $macroSafety '!HarmlessMetadataCommands\.Contains\(command\)[\s\S]*MacroSafetyFailure\.UnsupportedCommand' 'Unknown or state-changing macro commands no longer fail closed.'
Assert-Contains $macroSafety 'SHA256\.HashData\(Encoding\.UTF8\.GetBytes\(canonical\.ToString\(\)\)\)' 'Safe macro content no longer receives an exact canonical SHA-256 fingerprint.'

# The statically analyzed ActionCount is enforced by an immutable ordered
# baseline transcript. Later executions may consume only the next exact entry;
# duplicate lines remain distinct and extra/mismatched/incomplete executions
# terminate instead of being treated as an unordered set.
Assert-Contains $macroTranscript 'record struct MacroTurboTranscriptEntry\([\s\S]*uint ActionType,[\s\S]*uint RequestedActionId,[\s\S]*uint ResolvedActionId,[\s\S]*ulong TargetId,[\s\S]*uint ExtraParam,[\s\S]*uint RouteId,[\s\S]*ulong ResolverFingerprint\)' 'The Macro transcript entry omits action, target, parameter, route, or resolver identity.'
Assert-Contains $macroTranscript 'SemanticallyMatches\(MacroTurboTranscriptEntry observed\)[\s\S]*ActionType\s*==\s*observed\.ActionType[\s\S]*RequestedActionId\s*==\s*observed\.RequestedActionId[\s\S]*TargetId\s*==\s*observed\.TargetId[\s\S]*ExtraParam\s*==\s*observed\.ExtraParam[\s\S]*RouteId\s*==\s*observed\.RouteId[\s\S]*ResolverFingerprint\s*==\s*observed\.ResolverFingerprint' 'Ordered Macro transcript matching no longer includes the complete stable action and resolver-target identity.'
Assert-Contains $macroTranscript 'MacroTurboTranscriptBuilder\(int expectedActionCount\)[\s\S]*expectedActionCount\s*<=\s*0[\s\S]*this\.expectedActionCount\s*=\s*expectedActionCount;[\s\S]*new List<MacroTurboTranscriptEntry>\(expectedActionCount\)' 'The baseline transcript builder no longer derives its exact positive capacity from the analyzed ActionCount.'
Assert-Contains $macroTranscript 'Append\(MacroTurboTranscriptEntry entry\)[\s\S]*if \(closed\)[\s\S]*if \(failure\s+is\s+not\s+null\)[\s\S]*!entry\.IsValid[\s\S]*MacroTurboFreezeResult\.InvalidEntry[\s\S]*entries\.Count\s*>=\s*expectedActionCount[\s\S]*MacroTurboFreezeResult\.ExtraEntry[\s\S]*entries\.Add\(entry\)' 'The baseline transcript builder can append after closure/failure or accept an invalid/extra action beyond analyzed ActionCount.'
Assert-Contains $macroTranscript 'Freeze\(out MacroTurboTranscript\? transcript\)[\s\S]*closed\s*=\s*true;[\s\S]*failure\s+is\s+\{\s*\}\s+failed[\s\S]*entries\.Count\s*!=\s*expectedActionCount[\s\S]*MacroTurboFreezeResult\.Incomplete[\s\S]*new MacroTurboTranscript\(expectedActionCount,\s*entries\.ToArray\(\)\)' 'A baseline Macro transcript can freeze without exactly the analyzed ActionCount or after a terminal builder failure.'
Assert-Contains $macroTranscript 'MacroTurboTranscript\([\s\S]*entries\.Length\s*!=\s*expectedActionCount[\s\S]*entries\s*=\s*\(MacroTurboTranscriptEntry\[\]\)entries\.Clone\(\)' 'A frozen Macro transcript is not exact-count and immutable.'
Assert-Contains $macroTranscript 'StartExecution\(\)\s*=>\s*new\(this\)' 'Each Macro pulse no longer creates its own ordered execution cursor.'
Assert-Contains $macroTranscript 'Accept\(MacroTurboTranscriptEntry observed\)[\s\S]*acceptedCount\s*>=\s*transcript\.Count[\s\S]*MacroTurboExecutionResult\.Extra[\s\S]*!transcript\[acceptedCount\]\.SemanticallyMatches\(observed\)[\s\S]*MacroTurboExecutionResult\.Mismatch[\s\S]*acceptedCount\+\+' 'A Macro execution cursor can skip order or accept an extra/mismatched action.'
Assert-Contains $macroTranscript 'Finish\(\)[\s\S]*acceptedCount\s*==\s*transcript\.Count[\s\S]*MacroTurboExecutionResult\.Complete[\s\S]*MacroTurboExecutionResult\.Incomplete' 'Macro execution completion no longer requires every ordered baseline action exactly once.'

Assert-Contains $runtime 'private static bool IsMacroExecutionActive\(\)[\s\S]*shell->MacroLocked' 'Macro capture is no longer guarded by the native MacroLocked state.'
Assert-Contains $runtime 'private void BeginHotbarInput\([\s\S]*MacroWasLockedBeforeExecution\s*=\s*IsMacroExecutionActive\(\)[\s\S]*MacroSnapshotAtPress\s*=\s*slotIdentity\s+is\s+\{\s*CommandType:\s*MacroHotbarSlotType\s*\}[\s\S]*CaptureSnapshot\(0,\s*0,\s*includeResolverTargets:\s*true\)' 'Macro Turbo no longer snapshots the native macro lock and complete target/context state at the certified press.'
$prepareMacroMatch = [regex]::Match(
    $runtime,
    'private void PrepareCertifiedMacroInput\([\s\S]*?(?=\r?\n\s*private void OnCertifiedPhysicalPress\()')
if (-not $prepareMacroMatch.Success) {
    throw 'The audited pre-execution Macro certification method could not be isolated.'
}
$prepareMacro = $prepareMacroMatch.Value
Assert-Contains $runtime 'private byte ExecuteSlotDetour\([\s\S]*BeginHotbarInput\([^;]*\);\s*PrepareCertifiedMacroInput\(\);[\s\S]*executeSlotHook\.Original' 'Pointer-based macro input is not fully certified before native slot execution.'
Assert-Contains $runtime 'private byte ExecuteSlotByIdDetour\([\s\S]*BeginHotbarInput\([^;]*\);\s*PrepareCertifiedMacroInput\(\);[\s\S]*executeSlotByIdHook\.Original' 'ID-based macro input is not fully certified before native slot execution.'
Assert-Contains $prepareMacro 'scope\.CertifiedPress\s+is\s+not\s+\{\s*\}\s+press[\s\S]*scope\.SlotIdentity\s+is\s+not\s+\{\s*CommandType:\s*MacroHotbarSlotType\s*\}\s+slotIdentity[\s\S]*scope\.MacroWasLockedBeforeExecution[\s\S]*scope\.MacroSnapshotAtPress\s+is\s+not\s+\{\s*\}\s+snapshot' 'A Macro can be certified without a physical press, exact Macro slot, free executor, and press-time context.'
Assert-Contains $prepareMacro '!configuration\.Enabled[\s\S]*!configuration\.TurboEnabled[\s\S]*!configuration\.TurboMacrosEnabled[\s\S]*configuration\.DryRun[\s\S]*activeConflicts\.Count\s*>\s*0[\s\S]*compatibilityQuarantineFrames\s*>\s*0[\s\S]*!configuration\.TurboOutOfCombat\s*&&\s*!condition\[ConditionFlag\.InCombat\]' 'Pre-execution Macro certification can bypass opt-in, dry-run, conflict, quarantine, or combat gates.'
Assert-Contains $prepareMacro '!inputGenerations\.IsCurrent\(scope\.Generation\)[\s\S]*latestCertifiedPressId\)\s*!=\s*press\.PressId[\s\S]*physicalHotbarInput\?\.IsStillHeld\(press\)\s*!=\s*true[\s\S]*!TryReadCurrentSlotIdentity\(press,\s*out var currentIdentity\)[\s\S]*currentIdentity\s*!=\s*slotIdentity' 'Pre-execution Macro certification no longer proves the current generation, newest still-held physical press, and exact binding/slot.'
Assert-Contains $prepareMacro '!IsSafeSnapshot\(snapshot\)[\s\S]*!compatibility\.IsLiveReActionProfileCurrent\(\)[\s\S]*!TryReadSafeMacroProfile\(slotIdentity,\s*out var profile,\s*out _\)[\s\S]*scope\.MacroProfileAtPress\s*=\s*profile;\s*scope\.MacroTranscriptBuilder\s*=\s*new MacroTurboTranscriptBuilder\(profile\.ActionCount\);' 'A Macro can be certified without a safe target/context, live plugin profile, and exact analyzed ActionCount baseline builder before execution.'

$tryBeginMacroMatch = [regex]::Match(
    $runtime,
    'private void TryBeginMacroTurbo\([\s\S]*?(?=\r?\n\s*private void StartMacroTurboRuntime\()')
if (-not $tryBeginMacroMatch.Success) {
    throw 'The audited Macro Turbo ownership gate could not be isolated.'
}
$tryBeginMacro = $tryBeginMacroMatch.Value
Assert-Contains $tryBeginMacro 'scope\.MacroProfileAtPress\s+is\s+not\s+\{\s*\}\s+certifiedProfile[\s\S]*TryReadSafeMacroProfile\(slotIdentity,\s*out var profile,\s*out var failure\)[\s\S]*profile\.ContentFingerprint\s*!=\s*certifiedProfile\.ContentFingerprint' 'Macro Turbo can start from content that was not certified before execution or changed during the original slot call.'
Assert-Contains $tryBeginMacro 'scope\.MacroProvenanceDisqualified[\s\S]*scope\.MacroTranscriptBuilder\s+is\s+null[\s\S]*return;' 'Macro Turbo can start from a failed or missing initial ordered transcript builder.'
Assert-Contains $tryBeginMacro 'scope\.MacroSnapshotAtPress\s+is\s+not\s+\{\s*\}\s+macroSnapshot[\s\S]*!IsSafeSnapshot\(macroSnapshot\)[\s\S]*StartMacroTurboRuntime\(' 'Macro Turbo can start without the exact safe target/context snapshot captured at the physical press.'
Assert-Contains $macroStart 'var currentSnapshot\s*=\s*CaptureSnapshot\(0,\s*0,\s*includeResolverTargets:\s*true\);[\s\S]*if \(candidateIdentityChanged\(\)\)[\s\S]*return;' 'Macro Turbo no longer re-captures and validates target/context before taking ownership.'
Assert-Contains $macroStart 'var macroLocked\s*=\s*IsMacroExecutionActive\(\);[\s\S]*initialBuilder\s*=\s*scope\.MacroTranscriptBuilder;[\s\S]*if \(initialBuilder\s+is\s+null\)[\s\S]*return;[\s\S]*if \(!macroLocked\)[\s\S]*initialBuilder\.Freeze\(out transcript\)[\s\S]*freezeResult\s*!=\s*MacroTurboFreezeResult\.Frozen\s*\|\|\s*transcript\s+is\s+null[\s\S]*return;' 'Synchronous Macro startup no longer freezes exactly the initial ActionCount transcript or rejects incomplete/extra/invalid baselines.'
Assert-Contains $macroStart '!inputGenerations\.IsCurrent\(scope\.Generation\)[\s\S]*!inputSource\.IsStillHeld\(press\)[\s\S]*latestCertifiedPressId\)\s*!=\s*press\.PressId[\s\S]*!TryReadCurrentSlotIdentity\(press,\s*out var currentIdentity\)[\s\S]*currentIdentity\s*!=\s*slotIdentity' 'Macro Turbo start no longer proves the current generation, physical hold, newest press, and exact binding/slot identity.'
Assert-Contains $macroStart '!TryReadSafeMacroProfile\(slotIdentity,\s*out var currentMacro,\s*out _\)[\s\S]*currentMacro\.ContentFingerprint\s*!=\s*macroProfile\.ContentFingerprint' 'Macro content is not hash-revalidated before Macro Turbo ownership starts.'
Assert-Contains $macroStart '!macroSnapshot\.Equals\(currentSnapshot\)[\s\S]*!IsSafeSnapshot\(currentSnapshot\)[\s\S]*!compatibility\.IsLiveReActionProfileCurrent\(\)' 'Macro Turbo start no longer fails closed on target/context, safety, or live plugin-profile changes.'
Assert-Contains $macroStart 'slotIdentity\.CommandType,[\s\S]*slotIdentity\.CommandId,[\s\S]*macroSnapshot\.TargetFingerprint,[\s\S]*macroSnapshot\.ContextFingerprint,[\s\S]*Convert\.ToUInt64\(macroProfile\.ContentFingerprint\[\.\.16\],\s*16\)' 'Macro slot, target/context, and content hash are no longer encoded into the immutable hold intent.'
Assert-Contains $macroStart 'turboRuntime\s*=\s*null;[\s\S]*Interlocked\.Exchange\(ref turboAcknowledgement,\s*null\);[\s\S]*macroTurboRuntime\s*=\s*new MacroTurboRuntime\(\s*press,\s*slotIdentity,\s*macroProfile,\s*macroSnapshot,\s*compatibilitySignature,\s*scope\.Generation,\s*request,[\s\S]*macroLocked\s*\?\s*initialBuilder\s*:\s*null,\s*transcript\)' 'Macro Turbo can overlap the direct runtime/acknowledgement owner or omit its stored press, slot, hash, context, plugin signature, generation, hold request, and exact sync/async transcript owner.'
Assert-Contains $macroStart 'InitialMacroLockObserved\s*=\s*scope\.MacroLockObservedDuringExecution\s*\|\|\s*macroLocked,[\s\S]*InitialMacroLockCompleted\s*=\s*!macroLocked,[\s\S]*OwnsMacroExecutor\s*=\s*macroLocked,[\s\S]*OwnedQueueTuple\s*=\s*scope\.OwnedMacroQueueTuple' 'Macro Turbo no longer records the synchronous completion barrier, exact executor ownership, and any exact native queue inherited from the physical macro.'

# Only calls inside a proven synthetic Macro execution scope may be suppressed.
# An exact owned Queue drain is authorized first. Any other action from the
# active synthetic pulse/continuation must pass the ordered transcript before
# native execution; a failed synthetic call returns false before Original.
# The initial physical macro remains an ordinary vanilla execution even when it
# fails Turbo certification.
$useActionDetourMatch = [regex]::Match(
    $runtime,
    'private bool UseActionDetour\([\s\S]*?(?=\r?\n\s*private void BeginHotbarInput\()')
if (-not $useActionDetourMatch.Success) {
    throw 'The audited UseAction authorization and suppression boundary could not be isolated.'
}
$useActionDetour = $useActionDetourMatch.Value
$activeMacroPulseBranchMatch = [regex]::Match(
    $useActionDetour,
    'if \(!suppressSyntheticMacroCall\s*&&\s*turboDispatching\s*&&\s*activeMacroPulseExecution\s+is\s+\{\s*\}\s+pulseExecution\)[\s\S]*?(?=\r?\n\s*if \(!suppressSyntheticMacroCall\s*&&\s*!replaying\s*&&\s*!turboDispatching\))')
if (-not $activeMacroPulseBranchMatch.Success) {
    throw 'The active synthetic Macro pulse authorization branch could not be isolated.'
}
$activeMacroPulseBranch = $activeMacroPulseBranchMatch.Value
Assert-Contains $activeMacroPulseBranch 'if \(mode\s*==\s*ActionManager\.UseActionMode\.Queue\)[\s\S]*IsOwnedMacroTurboQueueDrain\(\s*thisPtr,\s*actionType,\s*actionId,\s*targetId,\s*extraParam,\s*mode,\s*comboRouteId\);[\s\S]*else if \(TryAuthorizeMacroPulseInvocation\([\s\S]*out var pulseEntry\)\)[\s\S]*TryCreateAuthorizedMacroQueueAttempt\([\s\S]*pulseEntry,[\s\S]*pulseExecution\.Token\);[\s\S]*else[\s\S]*suppressSyntheticMacroCall\s*=\s*true;' 'An active Macro pulse can bypass the Queue drain-first branch, capture a queue before transcript authorization, or leak an unauthorized synthetic call to native execution.'
if ([regex]::Matches($activeMacroPulseBranch, 'IsOwnedMacroTurboQueueDrain\s*\(').Count -ne 1 -or
    [regex]::Matches($activeMacroPulseBranch, 'TryAuthorizeMacroPulseInvocation\s*\(').Count -ne 1) {
    throw 'The active Macro pulse no longer has exactly one drain-first gate and one ordered transcript authorization gate.'
}
Assert-Contains $useActionDetour 'if \(mode\s*==\s*ActionManager\.UseActionMode\.Macro\)\s*\{\s*lock \(dispatchGate\)\s*\{\s*ReconcileSyntheticMacroExecutorQuarantine\(NowMilliseconds\);\s*suppressSyntheticMacroCall\s*=\s*ShouldSuppressQuarantinedSyntheticMacroCall\(\);\s*\}\s*\}[\s\S]*if \(!suppressSyntheticMacroCall[\s\S]*if \(!suppressSyntheticMacroCall\s*&&\s*!replaying\s*&&\s*!turboDispatching\)' 'The quarantine can suppress Queue/Normal calls, or a quarantined Macro call can re-enter synthetic authorization before the native Original boundary.'
Assert-Contains $useActionDetour 'var suppressSyntheticContinuation\s*=\s*false;[\s\S]*IsOwnedMacroTurboExecutionContinuation\([\s\S]*out continuationEntry,[\s\S]*out firstInitialEntry,[\s\S]*out suppressSyntheticContinuation\);[\s\S]*suppressSyntheticMacroCall\s*\|=\s*suppressSyntheticContinuation;' 'An unauthorized asynchronous synthetic Macro continuation can escape its suppression result.'

$physicalMacroBranchMatch = [regex]::Match(
    $useActionDetour,
    'if \(nativeHotbarInput\)[\s\S]*?if \(macroScope\s+is\s+not\s+null\)[\s\S]*?(?=\r?\n\s*if \(activeHotbarInput\s+is\s+\{\s*\}\s+inputScope\))')
if (-not $physicalMacroBranchMatch.Success) {
    throw 'The physical initial Macro pass-through branch could not be isolated.'
}
$physicalMacroBranch = $physicalMacroBranchMatch.Value
Assert-Contains $physicalMacroBranch 'TryAuthorizeCertifiedMacroInvocation\([\s\S]*out var originalEntry,[\s\S]*out var firstOriginalEntry\)[\s\S]*TryCreateAuthorizedMacroQueueAttempt\([\s\S]*originalEntry,[\s\S]*pulseToken:\s*null\);[\s\S]*goto CandidateCaptureComplete;' 'The physical initial Macro no longer remains vanilla while optional Turbo transcript/queue provenance is collected.'
if ($physicalMacroBranch -match 'suppressSyntheticMacroCall|suppressSyntheticContinuation') {
    throw 'A failed physical initial Macro certification can suppress the player''s vanilla action call.'
}

Assert-Contains $useActionDetour 'bool result;\s*if \(suppressSyntheticMacroCall\)\s*\{[\s\S]*outOptAreaTargeted\s*!=\s*null\)\s*\*outOptAreaTargeted\s*=\s*false;[\s\S]*result\s*=\s*false;[\s\S]*\}\s*else\s*\{[\s\S]*result\s*=\s*useActionHook\.Original\(\s*thisPtr,\s*actionType,\s*actionId,\s*targetId,\s*extraParam,\s*mode,\s*comboRouteId,\s*outOptAreaTargeted\);\s*\}' 'An unauthorized synthetic Macro call is not suppressed before the sole native Original boundary.'
if ([regex]::Matches($useActionDetour, 'useActionHook\.Original\s*\(').Count -ne 1) {
    throw 'UseActionDetour no longer has exactly one conditional native Original boundary.'
}

# Every original or repeated Macro action must first become an eligible ordered
# transcript entry. The single queue-capture constructor is reachable only
# after that authorization; the former broad nested-call capture path is banned.
$macroEntryMatch = [regex]::Match(
    $runtime,
    'private bool TryCreateMacroTranscriptEntry\([\s\S]*?(?=\r?\n\s*private bool TryAuthorizeCertifiedMacroInvocation\()')
if (-not $macroEntryMatch.Success) {
    throw 'The audited Macro transcript-entry eligibility method could not be isolated.'
}
$macroEntry = $macroEntryMatch.Value
Assert-Contains $macroEntry 'actionManager\s*==\s*null[\s\S]*mode\s*!=\s*ActionManager\.UseActionMode\.Macro[\s\S]*actionType\s+is\s+not\s+\(ActionType\.Action\s+or\s+ActionType\.PvPAction\)[\s\S]*actionId\s*==\s*0' 'A Macro transcript entry can be created outside strict Macro mode or from an invalid action invocation.'
Assert-Contains $macroEntry 'GetAdjustedActionId\(actionId\)[\s\S]*resolvedActionId\s*==\s*0[\s\S]*excludedIntegrationActionIds\.Contains\(actionId\)[\s\S]*excludedIntegrationActionIds\.Contains\(resolvedActionId\)[\s\S]*!compatibility\.IsLiveMOActionUnowned\(actionId,\s*resolvedActionId\)[\s\S]*!TryGetEligibleActionProfile\(' 'Macro transcript entry creation no longer performs current resolution, static eligibility, and both cached/live MOAction exclusion checks.'
Assert-Contains $macroEntry 'TryGetEligibleActionProfile\(\s*actionType,\s*resolvedActionId,\s*targetId,\s*out var includeResolverTargets\)[\s\S]*CaptureSnapshot\(targetId,\s*resolvedActionId,\s*includeResolverTargets\)[\s\S]*!IsSafeSnapshot\(snapshot\)' 'Macro transcript entry creation no longer derives and validates live resolver eligibility and context.'
Assert-Contains $macroEntry 'targetId\s+is\s+not\s+\(0\s+or\s+InvalidObjectId\)[\s\S]*targetId\s*!=\s*snapshot\.LocalGameObjectId[\s\S]*FindTargetAddress\(targetId\)\s*==\s*nint\.Zero' 'A Macro transcript can retain an explicit target whose immutable object identity no longer exists.'
Assert-Contains $macroEntry 'new MacroTurboTranscriptEntry\(\s*\(uint\)actionType,\s*actionId,\s*resolvedActionId,\s*targetId,\s*extraParam,\s*comboRouteId,\s*includeResolverTargets\s*\?\s*snapshot\.TargetFingerprint\s*:\s*0\)' 'Macro transcript entries no longer bind resolver-based calls to the exact live target fingerprint.'

$certifiedMacroInvocationMatch = [regex]::Match(
    $runtime,
    'private bool TryAuthorizeCertifiedMacroInvocation\([\s\S]*?(?=\r?\n\s*private bool TryAuthorizeRuntimeMacroInvocation\()')
if (-not $certifiedMacroInvocationMatch.Success) {
    throw 'The audited physical Macro baseline authorization could not be isolated.'
}
$certifiedMacroInvocation = $certifiedMacroInvocationMatch.Value
Assert-Contains $certifiedMacroInvocation 'scope\.MacroProfileAtPress\s+is\s+null[\s\S]*scope\.MacroTranscriptBuilder\s+is\s+not\s+\{\s*\}\s+builder[\s\S]*scope\.MacroProvenanceDisqualified[\s\S]*return false;' 'A physical Macro baseline call can bypass its exact ActionCount builder or a prior provenance failure.'
Assert-Contains $certifiedMacroInvocation '!TryCreateMacroTranscriptEntry\([\s\S]*scope\.MacroProvenanceDisqualified\s*=\s*true;[\s\S]*return false;' 'An ineligible, non-Macro-mode, resolver-unstable, or MOAction-owned physical Macro call does not permanently disqualify the baseline.'
Assert-Contains $certifiedMacroInvocation 'firstEntry\s*=\s*builder\.ObservedActionCount\s*==\s*0;[\s\S]*builder\.Append\(entry\)[\s\S]*MacroTurboBuildStepResult\.Appended[\s\S]*scope\.MacroProvenanceDisqualified\s*=\s*true;' 'Physical Macro baseline actions are not appended in order or an invalid/extra ActionCount result does not disqualify ownership.'

$runtimeMacroInvocationMatch = [regex]::Match(
    $runtime,
    'private bool TryAuthorizeRuntimeMacroInvocation\([\s\S]*?(?=\r?\n\s*private bool TryAuthorizeMacroPulseInvocation\()')
if (-not $runtimeMacroInvocationMatch.Success) {
    throw 'The audited runtime Macro transcript authorization could not be isolated.'
}
$runtimeMacroInvocation = $runtimeMacroInvocationMatch.Value
Assert-Contains $runtimeMacroInvocation '!TryCreateMacroTranscriptEntry\([\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.PluginChange,[\s\S]*return false;' 'A runtime Macro call can survive failed Macro-mode, eligibility, resolver, or MOAction validation.'
Assert-Contains $runtimeMacroInvocation 'runtime\.Transcript\s+is\s+null[\s\S]*runtime\.InitialTranscriptBuilder\s+is\s+not\s+\{\s*\}\s+builder[\s\S]*runtime\.InitialMacroLockCompleted[\s\S]*CancelTurboUnsafe\([\s\S]*HoldRepeatCancelReason\.Fault' 'Asynchronous initial Macro calls can append without the unique pre-execution builder or after its completion boundary.'
Assert-Contains $runtimeMacroInvocation 'firstInitialEntry\s*=\s*builder\.ObservedActionCount\s*==\s*0;[\s\S]*builder\.Append\(entry\)[\s\S]*MacroTurboBuildStepResult\.Appended[\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.ResolvedActionChange' 'Asynchronous initial Macro extra/invalid transcript entries do not terminate the hold.'
Assert-Contains $runtimeMacroInvocation 'runtime\.ActiveExecutionCursor\s+is\s+not\s+\{\s*\}\s+cursor[\s\S]*runtime\.ActiveExecutionEpoch\s*<=\s*0[\s\S]*CancelTurboUnsafe\([\s\S]*cursor\.Accept\(entry\)[\s\S]*MacroTurboExecutionAcceptResult\.Accepted[\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.ResolvedActionChange' 'A repeated Macro call can bypass its active ordered cursor, or mismatch/extra results do not terminate the hold.'

$pulseMacroInvocationMatch = [regex]::Match(
    $runtime,
    'private bool TryAuthorizeMacroPulseInvocation\([\s\S]*?(?=\r?\n\s*private MacroQueueAttempt\? TryCreateAuthorizedMacroQueueAttempt\()')
if (-not $pulseMacroInvocationMatch.Success) {
    throw 'The audited synchronous Macro pulse provenance gate could not be isolated.'
}
$pulseMacroInvocation = $pulseMacroInvocationMatch.Value
Assert-Contains $pulseMacroInvocation '!ReferenceEquals\(macroTurboRuntime,\s*runtime\)[\s\S]*!turboEngine\.IsTokenCurrent\(pulseExecution\.Token\)[\s\S]*runtime\.ActiveExecutionEpoch\s*!=\s*pulseExecution\.ExecutionEpoch[\s\S]*runtime\.Transcript\s+is\s+null[\s\S]*CancelTurboUnsafe\(' 'A stale synchronous Macro call-chain can reach the ordered transcript cursor.'
Assert-Contains $pulseMacroInvocation 'return TryAuthorizeRuntimeMacroInvocation\(' 'A synchronous Macro pulse call can bypass live entry eligibility and ordered cursor authorization.'

$macroQueueAttemptMatch = [regex]::Match(
    $runtime,
    'private MacroQueueAttempt\? TryCreateAuthorizedMacroQueueAttempt\([\s\S]*?(?=\r?\n\s*private void ProcessMacroQueueAttempt\()')
if (-not $macroQueueAttemptMatch.Success) {
    throw 'The audited transcript-authorized Macro native-queue capture could not be isolated.'
}
$macroQueueAttempt = $macroQueueAttemptMatch.Value
Assert-Contains $macroQueueAttempt 'actionManager\s*==\s*null[\s\S]*generation\s*<=\s*0[\s\S]*!inputGenerations\.IsCurrent\(generation\)[\s\S]*mode\s*!=\s*ActionManager\.UseActionMode\.Macro[\s\S]*!entry\.IsValid' 'Macro native-queue capture can accept invalid generation, non-Macro mode, or an unauthorized transcript entry.'
Assert-Contains $macroQueueAttempt 'runtime\s+is\s+not\s+null[\s\S]*!ReferenceEquals\(macroTurboRuntime,\s*runtime\)[\s\S]*!turboEngine\.Snapshot\.HasActiveHold[\s\S]*latestCertifiedPressId\)\s*!=\s*runtime\.Press\.PressId[\s\S]*physicalHotbarInput\?\.IsStillHeld\(runtime\.Press\)\s*!=\s*true' 'A stale Macro runtime or released/superseded physical press can capture native queue ownership.'
Assert-Contains $macroQueueAttempt 'pulseToken\s+is\s+\{\s*\}\s+token[\s\S]*!turboEngine\.IsTokenCurrent\(token\)[\s\S]*activeMacroPulseExecution\s+is\s+not\s+\{\s*\}\s+pulseExecution[\s\S]*pulseExecution\.Token\s*!=\s*token[\s\S]*pulseExecution\.ExecutionEpoch\s*!=\s*runtime\.ActiveExecutionEpoch' 'A synthetic Macro action can capture queue ownership without the exact active pulse/runtime/epoch token.'
Assert-Contains $macroQueueAttempt 'new ExactActionTuple\(\s*entry\.ActionType,\s*entry\.RequestedActionId,\s*entry\.ResolvedActionId,\s*entry\.TargetId,\s*entry\.ExtraParam,\s*\(uint\)mode,\s*entry\.RouteId\),\s*CaptureNativeQueue\(actionManager\),\s*actionManager->LastUsedActionSequence' 'Macro queue capture no longer derives its complete tuple exclusively from the authorized transcript entry.'

if ($runtime -match '\bTryCreateMacroQueueAttempt\s*\(') {
    throw 'Runtime restored the stale broad Macro nested-call queue-capture path.'
}
$authorizedQueueCaptureCalls = [regex]::Matches($runtime, 'TryCreateAuthorizedMacroQueueAttempt\s*\(').Count
$macroQueueConstructions = [regex]::Matches($runtime, 'new MacroQueueAttempt\s*\(').Count
if ($authorizedQueueCaptureCalls -ne 4 -or $macroQueueConstructions -ne 1) {
    throw "Macro queue capture must have exactly three transcript-authorized callers and one constructor; found $authorizedQueueCaptureCalls method/call occurrences and $macroQueueConstructions constructors."
}
Assert-Contains $runtime 'TryAuthorizeMacroPulseInvocation\([\s\S]*out var pulseEntry\)\)[\s\S]*TryCreateAuthorizedMacroQueueAttempt\([\s\S]*pulseEntry,[\s\S]*pulseExecution\.Token\)' 'Synchronous Macro pulse queue capture can occur before ordered transcript authorization.'
Assert-Contains $runtime 'ownedMacroExecution\s*=\s*!ownedMacroQueueDrain[\s\S]*IsOwnedMacroTurboExecutionContinuation\([\s\S]*out continuationEntry,[\s\S]*out firstInitialEntry,[\s\S]*out suppressSyntheticContinuation\);[\s\S]*if \(ownedMacroExecution\s*&&\s*macroTurboRuntime\s+is\s+\{\s*\}\s+ownedRuntime\)[\s\S]*TryCreateAuthorizedMacroQueueAttempt\([\s\S]*continuationEntry,[\s\S]*pulseToken:\s*null\)' 'Asynchronous Macro queue capture can occur before the executor helper returns successful live eligibility and ordered transcript authorization.'
Assert-Contains $runtime 'TryAuthorizeCertifiedMacroInvocation\([\s\S]*out var originalEntry,[\s\S]*out var firstOriginalEntry\)[\s\S]*TryCreateAuthorizedMacroQueueAttempt\([\s\S]*originalEntry,[\s\S]*pulseToken:\s*null\)[\s\S]*goto CandidateCaptureComplete;' 'Physical Macro queue capture can occur before the exact ActionCount baseline accepts the action.'
Assert-Contains $runtime 'macroQueueAttempt\s+is\s+\{\s*\}\s+attemptedMacroQueue[\s\S]*lock \(dispatchGate\)[\s\S]*ProcessMacroQueueAttempt\(\s*thisPtr,\s*attemptedMacroQueue,\s*currentSequence\)' 'Macro queue ownership is not reconciled under the dispatch gate after the one authoritative native UseAction result.'

$macroQueueOutcomeMatch = [regex]::Match(
    $runtime,
    'private void ProcessMacroQueueAttempt\([\s\S]*?(?=\r?\n\s*private bool IsOwnedMacroTurboQueueDrain\()')
if (-not $macroQueueOutcomeMatch.Success) {
    throw 'The audited Macro native-queue ownership classifier could not be isolated.'
}
$macroQueueOutcome = $macroQueueOutcomeMatch.Value
Assert-Contains $macroQueueOutcome 'actionManager\s*==\s*null\s*\|\|\s*!inputGenerations\.IsCurrent\(attempt\.Generation\)[\s\S]*ReferenceEquals\(macroTurboRuntime,\s*runtime\)[\s\S]*ReferenceEquals\(activeHotbarInput,\s*inputScope\)[\s\S]*inputScope\.MacroProfileAtPress\s+is\s+null' 'Macro native-queue outcome can attach to a stale runtime, generation, or uncertified physical input scope.'
Assert-Contains $macroQueueOutcome 'nativeQueueOwnership\.Reconcile\(currentSequence,\s*queueAfter\);[\s\S]*queueTuple\s*=\s*attempt\.Attempted\s+with\s*\{[\s\S]*Mode\s*=\s*queueAfter\.Mode' 'Macro native-queue outcome no longer reconciles prior ownership or uses the exact stored QueueType.'
Assert-Contains $macroQueueOutcome 'currentSequence\s*==\s*attempt\.SequenceBefore[\s\S]*queueAfter\.Matches\(queueTuple\)[\s\S]*!attempt\.QueueBefore\.Matches\(queueTuple\)[\s\S]*nativeQueueOwnership\.TryClaimNewQueue\(\s*attempt\.Generation,\s*currentSequence,\s*attempt\.QueueBefore,\s*queueAfter,\s*queueTuple\)' 'Macro queue ownership can be claimed after a sequence transition or without a newly created exact queue tuple.'
Assert-Contains $macroQueueOutcome 'owningRuntime\.OwnedQueueTuple\s*=\s*queueTuple;[\s\S]*owningScope\.OwnedMacroQueueTuple\s*=\s*queueTuple;' 'Exact Macro queue ownership is not stored on the correct runtime or physical input scope.'
Assert-Contains $macroQueueOutcome 'OwnedQueueTuple:\s*\{\s*\}\s+runtimeTuple[\s\S]*!queueAfter\.Matches\(runtimeTuple\)[\s\S]*OwnedQueueTuple\s*=\s*null;[\s\S]*OwnedMacroQueueTuple:\s*\{\s*\}\s+scopeTuple[\s\S]*!queueAfter\.Matches\(scopeTuple\)[\s\S]*OwnedMacroQueueTuple\s*=\s*null;' 'Changed Macro queue state does not revoke exact runtime/input-scope ownership.'

$macroQueueDrainMatch = [regex]::Match(
    $runtime,
    'private bool IsOwnedMacroTurboQueueDrain\([\s\S]*?(?=\r?\n\s*private bool IsOwnedMacroTurboExecutionContinuation\()')
if (-not $macroQueueDrainMatch.Success) {
    throw 'The audited Macro native-queue drain authorization could not be isolated.'
}
$macroQueueDrain = $macroQueueDrainMatch.Value
Assert-Contains $macroQueueDrain 'mode\s*!=\s*ActionManager\.UseActionMode\.Queue[\s\S]*runtime\.OwnedQueueTuple\s+is\s+not\s+\{\s*\}\s+ownedTuple[\s\S]*!turboEngine\.Snapshot\.HasActiveHold[\s\S]*!inputGenerations\.IsCurrent\(runtime\.Generation\)' 'A non-Queue invocation or unowned/inactive/stale Macro runtime can authorize a native queue drain.'
Assert-Contains $macroQueueDrain 'latestCertifiedPressId\)\s*!=\s*runtime\.Press\.PressId[\s\S]*physicalHotbarInput\?\.IsStillHeld\(runtime\.Press\)\s*!=\s*true[\s\S]*actionType\s*!=\s*\(ActionType\)ownedTuple\.ActionType[\s\S]*actionId\s*==\s*0[\s\S]*actionId\s*!=\s*ownedTuple\.RequestedActionId\s*&&\s*actionId\s*!=\s*ownedTuple\.ResolvedActionId[\s\S]*targetId\s*!=\s*ownedTuple\.TargetId[\s\S]*extraParam\s*!=\s*ownedTuple\.Param[\s\S]*comboRouteId\s*!=\s*ownedTuple\.RouteId' 'Macro queue drain no longer requires the newest still-held press and complete exact stored action tuple.'
Assert-Contains $macroQueueDrain 'excludedIntegrationActionIds\.Contains\(ownedTuple\.RequestedActionId\)[\s\S]*excludedIntegrationActionIds\.Contains\(ownedTuple\.ResolvedActionId\)[\s\S]*!compatibility\.IsLiveMOActionUnowned\(\s*ownedTuple\.RequestedActionId,\s*ownedTuple\.ResolvedActionId\)[\s\S]*nativeQueueOwnership\.TryAuthorizeExactDrain\(' 'An exact Macro queue drain can bypass cached or live MOAction ownership validation immediately before consumption.'
Assert-Contains $macroQueueDrain '!IsTurboSafetySafe\(ObserveMacroTurbo\(runtime,\s*checkMacroHash:\s*true\)\.Safety\)[\s\S]*TryAuthorizeExactDrain\(\s*runtime\.Generation,\s*actionManager->LastUsedActionSequence,\s*currentQueue,\s*ownedTuple\)[\s\S]*runtime\.OwnedQueueTuple\s*=\s*null;[\s\S]*return true;' 'Macro queue drain can bypass final hash/context safety, exact ownership, or single-consumption clearing.'

$macroContinuationMatch = [regex]::Match(
    $runtime,
    'private bool IsOwnedMacroTurboExecutionContinuation\([\s\S]*?(?=\r?\n\s*private bool IsOwnedTurboActionContinuation\()')
if (-not $macroContinuationMatch.Success) {
    throw 'The audited Macro Turbo executor-continuation gate could not be isolated.'
}
$macroContinuation = $macroContinuationMatch.Value
Assert-Contains $macroContinuation 'out bool suppressCurrentCall\)[\s\S]*suppressCurrentCall\s*=\s*false;[\s\S]*var macroLocked\s*=\s*IsMacroExecutionActive\(\);' 'Macro executor continuation no longer exposes a fail-closed synthetic-call suppression result tied to the live macro lock.'
Assert-Contains $macroContinuation 'var bindingMatches\s*=\s*runtime\s+is\s+not\s+null[\s\S]*TryReadCurrentSlotIdentity\(runtime\.Press,\s*out var currentIdentity\)[\s\S]*currentIdentity\s*==\s*runtime\.SlotIdentity;[\s\S]*var ownedExecutorContext\s*=\s*runtime\s+is\s+not\s+null[\s\S]*runtime\.OwnsMacroExecutor[\s\S]*macroLocked[\s\S]*turboEngine\.Snapshot\.HasActiveHold[\s\S]*inputGenerations\.IsCurrent\(runtime\.Generation\)[\s\S]*latestCertifiedPressId\)\s*==\s*runtime\.Press\.PressId[\s\S]*physicalHotbarInput\?\.IsStillHeld\(runtime\.Press\)\s*==\s*true[\s\S]*bindingMatches;' 'Macro executor continuation no longer requires its exact owned executor, native lock, active hold, current generation, newest still-held press, and unchanged slot.'
Assert-Contains $macroContinuation 'runtime\s+is\s+not\s+null[\s\S]*runtime\.OwnsMacroExecutor[\s\S]*macroLocked[\s\S]*runtime\.Transcript\s+is\s+not\s+null[\s\S]*runtime\.ActiveExecutionCursor\s+is\s+not\s+null[\s\S]*runtime\.ActiveExecutionEpoch\s*>\s*0[\s\S]*suppressCurrentCall\s*=\s*true;' 'A denied action inside a proven repeated Macro execution epoch can escape native suppression.'
Assert-Contains $macroContinuation 'runtime\s+is\s+null[\s\S]*!ownedExecutorContext[\s\S]*mode\s*!=\s*ActionManager\.UseActionMode\.Macro[\s\S]*!IsTurboSafetySafe\(ObserveMacroTurbo\(runtime,\s*checkMacroHash:\s*true\)\.Safety\)[\s\S]*return false;' 'A non-Macro, unsafe, stale, or unowned Macro executor call can continue ordered authorization.'
Assert-Contains $macroContinuation 'runtime\.InitialMacroLockObserved\s*=\s*true;[\s\S]*var authorized\s*=\s*TryAuthorizeRuntimeMacroInvocation\([\s\S]*if \(authorized\)\s*suppressCurrentCall\s*=\s*false;[\s\S]*return authorized;' 'An asynchronous Macro continuation can bypass live entry/builder/cursor authorization or remain suppressed after successful authorization.'

# Cancellation can remove the Macro runtime while the native executor is still
# locked and capable of emitting later lines. A bounded frozen-epoch tombstone
# must therefore survive ordinary cancellation and suppress only later
# Macro-mode calls until native unlock. Its only other clear boundaries are
# disposal or a strictly newer certified, unlocked root Macro press.
$quarantineArmMatch = [regex]::Match(
    $runtime,
    'private void QuarantineSyntheticMacroExecutor\([\s\S]*?(?=\r?\n\s*private bool ShouldSuppressQuarantinedSyntheticMacroCall\()')
if (-not $quarantineArmMatch.Success) {
    throw 'The synthetic Macro executor quarantine arm method could not be isolated.'
}
$quarantineArm = $quarantineArmMatch.Value
Assert-Contains $quarantineArm 'runtime\.Transcript\s+is\s+null\s*\|\|\s*runtime\.ActiveExecutionEpoch\s*<=\s*0\)\s*return;[\s\S]*existing\s+is\s+null[\s\S]*existing\.Generation\s*!=\s*runtime\.Generation[\s\S]*existing\.ExecutionEpoch\s*!=\s*runtime\.ActiveExecutionEpoch[\s\S]*new SyntheticMacroExecutorQuarantine\(\s*runtime\.Generation,\s*runtime\.Press\.PressId,\s*runtime\.ActiveExecutionEpoch,\s*now,\s*SaturatingAdd\(now,\s*MaximumMacroCaptureMilliseconds\)\)' 'The quarantine can arm without a frozen execution epoch, lose its exact generation/press/epoch identity, or extend an existing same-epoch timeout.'

$quarantineSuppressMatch = [regex]::Match(
    $runtime,
    'private bool ShouldSuppressQuarantinedSyntheticMacroCall\([\s\S]*?(?=\r?\n\s*private void ReconcileSyntheticMacroExecutorQuarantine\()')
if (-not $quarantineSuppressMatch.Success) {
    throw 'The quarantined synthetic Macro suppression method could not be isolated.'
}
$quarantineSuppress = $quarantineSuppressMatch.Value
Assert-Contains $quarantineSuppress 'syntheticMacroExecutorQuarantine\s+is\s+not\s+\{\s*\}\s+quarantine\s*\|\|\s*!IsMacroExecutionActive\(\)[\s\S]*return false;[\s\S]*syntheticMacroSuppressedCallCount\+\+;[\s\S]*return true;' 'A tombstone can suppress without native MacroLocked, or can fail to suppress and record a later quarantined Macro line while the lock remains active.'

$quarantineReconcileMatch = [regex]::Match(
    $runtime,
    'private void ReconcileSyntheticMacroExecutorQuarantine\([\s\S]*?(?=\r?\n\s*private void TryClearSyntheticMacroQuarantineForCertifiedRoot\()')
if (-not $quarantineReconcileMatch.Success) {
    throw 'The synthetic Macro quarantine unlock/timeout reconciliation method could not be isolated.'
}
$quarantineReconcile = $quarantineReconcileMatch.Value
Assert-Contains $quarantineReconcile 'var quarantine\s*=\s*syntheticMacroExecutorQuarantine;\s*if \(quarantine\s+is\s+null\)\s*return;[\s\S]*if \(IsMacroExecutionActive\(\)[\s\S]*now\s*>=\s*0[\s\S]*now\s*<=\s*quarantine\.ExpiresAtMilliseconds\)[\s\S]*return;[\s\S]*syntheticMacroExecutorQuarantine\s*=\s*null;' 'The tombstone does not survive while MacroLocked remains active inside its bound, or is not cleared after observed unlock/bounded expiry.'
Assert-Contains $runtime 'private void OnFrameworkUpdate\([\s\S]*lock \(dispatchGate\)[\s\S]*ReconcileSyntheticMacroExecutorQuarantine\(now\);' 'Framework observation no longer clears the quarantine only after native unlock or bounded expiry under the dispatch gate.'

$quarantineRootClearMatch = [regex]::Match(
    $runtime,
    'private void TryClearSyntheticMacroQuarantineForCertifiedRoot\([\s\S]*?(?=\r?\n\s*private void CompleteHotbarInput\()')
if (-not $quarantineRootClearMatch.Success) {
    throw 'The newer certified root Macro quarantine-clear method could not be isolated.'
}
$quarantineRootClear = $quarantineRootClearMatch.Value
Assert-Contains $quarantineRootClear 'scope\.CertifiedPress\s+is\s+not\s+\{\s*\}\s+press[\s\S]*scope\.SlotIdentity\s+is\s+not\s+\{\s*CommandType:\s*MacroHotbarSlotType\s*\}[\s\S]*scope\.MacroWasLockedBeforeExecution[\s\S]*IsMacroExecutionActive\(\)[\s\S]*scope\.Generation\s*<=\s*quarantine\.Generation[\s\S]*press\.PressId\s*<=\s*quarantine\.PressId[\s\S]*return;[\s\S]*syntheticMacroExecutorQuarantine\s*=\s*null;' 'A non-Macro, uncertified, locked, stale, or non-newer hotbar root can clear the synthetic executor tombstone.'
Assert-Contains $runtime 'private void BeginHotbarInput\([\s\S]*Cancel\(CancelReason\.Replaced,[\s\S]*activeHotbarInput\s*=\s*new HotbarInputScope\([\s\S]*MacroWasLockedBeforeExecution\s*=\s*IsMacroExecutionActive\(\)[\s\S]*TryClearSyntheticMacroQuarantineForCertifiedRoot\(activeHotbarInput\);' 'A replacement input can clear quarantine before its exact certified root, pre-existing lock, generation, and press identity are captured.'

$cancelTurboMatch = [regex]::Match(
    $runtime,
    'private void CancelTurboUnsafe\([\s\S]*?(?=\r?\n\s*private static HoldRepeatCancelReason ToTurboCancelReason\()')
if (-not $cancelTurboMatch.Success) {
    throw 'The central Turbo cancellation boundary could not be isolated for quarantine validation.'
}
$cancelTurbo = $cancelTurboMatch.Value
Assert-Contains $cancelTurbo 'macroTurboRuntime\s+is\s+\{\s*\}\s+macroRuntime[\s\S]*macroRuntime\.Transcript\s+is\s+not\s+null[\s\S]*macroRuntime\.ActiveExecutionCursor\s+is\s+not\s+null[\s\S]*macroRuntime\.ActiveExecutionEpoch\s*>\s*0[\s\S]*macroRuntime\.OwnsMacroExecutor[\s\S]*IsMacroExecutionActive\(\)[\s\S]*QuarantineSyntheticMacroExecutor\([\s\S]*turboEngine\.Cancel\(reason\);[\s\S]*macroTurboRuntime\s*=\s*null;' 'Central cancellation can discard a frozen active cursor owned under MacroLocked before arming its synthetic-executor tombstone.'
if ($cancelTurbo -match 'syntheticMacroExecutorQuarantine\s*=\s*null') {
    throw 'Ordinary Turbo cancellation clears the synthetic Macro executor tombstone.'
}

$ordinaryCancelMatch = [regex]::Match(
    $runtime,
    'public void Cancel\([\s\S]*?(?=\r?\n\s*public void ClearFaultForReload\()')
if (-not $ordinaryCancelMatch.Success) {
    throw 'The ordinary public cancellation boundary could not be isolated for quarantine retention.'
}
$ordinaryCancel = $ordinaryCancelMatch.Value
Assert-Contains $ordinaryCancel 'lock \(dispatchGate\)[\s\S]*CancelTurboUnsafe\(ToTurboCancelReason\(reason\),\s*detail\)' 'Ordinary cancellation bypasses the central tombstone-aware Turbo cancellation boundary.'
if ($ordinaryCancel -match 'syntheticMacroExecutorQuarantine\s*=\s*null') {
    throw 'An ordinary public Cancel clears the synthetic Macro executor tombstone.'
}

$disposeMatch = [regex]::Match(
    $runtime,
    'public void Dispose\(\)[\s\S]*?(?=\r?\n\s*private byte ExecuteSlotDetour\()')
if (-not $disposeMatch.Success) {
    throw 'The disposal-only quarantine clear boundary could not be isolated.'
}
$disposeMethod = $disposeMatch.Value
Assert-Contains $disposeMethod 'Cancel\(CancelReason\.Disabled,[\s\S]*lock \(dispatchGate\)[\s\S]*nativeQueueOwnership\.Clear\(\);\s*syntheticMacroExecutorQuarantine\s*=\s*null;' 'Disposal no longer clears the retained synthetic Macro executor tombstone under the dispatch gate after cancellation.'

$quarantineNullWrites = [regex]::Matches($runtime, 'syntheticMacroExecutorQuarantine\s*=\s*null;').Count
$quarantineArmWrites = [regex]::Matches($runtime, 'syntheticMacroExecutorQuarantine\s*=\s*new SyntheticMacroExecutorQuarantine\s*\(').Count
if ($quarantineNullWrites -ne 3 -or $quarantineArmWrites -ne 1) {
    throw "Synthetic Macro quarantine authority must have one arm and only three clears (unlock/timeout, newer certified unlocked root, dispose); found $quarantineArmWrites arm and $quarantineNullWrites clear writes."
}
Assert-Contains $runtime 'private sealed record SyntheticMacroExecutorQuarantine\(\s*long Generation,\s*long PressId,\s*long ExecutionEpoch,\s*long StartedAtMilliseconds,\s*long ExpiresAtMilliseconds\);' 'The quarantine tombstone no longer retains exact generation, press, epoch, start, and bounded expiry identity.'

# Provenance failures inside a frozen synthetic epoch must arm immediately,
# before central cancellation destroys the runtime needed to identify later
# native Macro lines.
Assert-Contains $runtimeMacroInvocation '!TryCreateMacroTranscriptEntry\([\s\S]*runtime\.Transcript\s+is\s+not\s+null[\s\S]*QuarantineSyntheticMacroExecutor\([\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.PluginChange' 'A frozen runtime eligibility/resolver/MOAction provenance failure can cancel without arming quarantine first.'
Assert-Contains $runtimeMacroInvocation 'runtime\.ActiveExecutionCursor\s+is\s+not\s+\{\s*\}\s+cursor[\s\S]*runtime\.ActiveExecutionEpoch\s*<=\s*0[\s\S]*QuarantineSyntheticMacroExecutor\(runtime,[\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.Fault' 'A missing ordered cursor/epoch provenance failure can cancel without arming quarantine first.'
Assert-Contains $runtimeMacroInvocation 'cursor\.Accept\(entry\)[\s\S]*MacroTurboExecutionAcceptResult\.Accepted\)\s*return true;[\s\S]*QuarantineSyntheticMacroExecutor\(runtime,[\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.ResolvedActionChange' 'An ordered Macro mismatch or extra call can cancel without arming quarantine first.'
Assert-Contains $pulseMacroInvocation '!ReferenceEquals\(macroTurboRuntime,\s*runtime\)[\s\S]*runtime\.Transcript\s+is\s+null[\s\S]*QuarantineSyntheticMacroExecutor\([\s\S]*CancelTurboUnsafe\(' 'A stale synchronous pulse provenance failure can cancel without arming quarantine first.'
Assert-Contains $macroContinuation 'runtime\s+is\s+not\s+null\s*&&\s*suppressCurrentCall[\s\S]*QuarantineSyntheticMacroExecutor\([\s\S]*return false;' 'A frozen asynchronous continuation safety/mode provenance failure can return without retaining its quarantine tombstone.'

$macroProcessMatch = [regex]::Match(
    $runtime,
    'private void ProcessMacroTurboUnsafe\([\s\S]*?(?=\r?\n\s*private bool TryFreezeInitialMacroTranscript\()')
if (-not $macroProcessMatch.Success) {
    throw 'The audited Macro Turbo framework processor could not be isolated.'
}
$macroProcess = $macroProcessMatch.Value
Assert-Contains $macroProcess 'if \(macroLocked\)[\s\S]*if \(!runtime\.OwnsMacroExecutor\)[\s\S]*HoldRepeatCancelReason\.PluginChange[\s\S]*return;[\s\S]*InitialMacroLockObserved\s*=\s*true;[\s\S]*else if \(runtime\.OwnsMacroExecutor\)[\s\S]*runtime\.OwnsMacroExecutor\s*=\s*false;[\s\S]*runtime\.Transcript\s+is\s+null[\s\S]*TryFreezeInitialMacroTranscript\(runtime\)[\s\S]*runtime\.ActiveExecutionCursor\s+is\s+not\s+null[\s\S]*TryCompleteMacroExecutionEpoch\(' 'Macro Turbo no longer rejects foreign MacroLock or completes the exact initial/repeated ordered transcript when its owned executor releases.'
Assert-Contains $macroProcess '!runtime\.InitialMacroLockCompleted[\s\S]*InitialMacroLockDeadlineMilliseconds[\s\S]*CancelTurboUnsafe\([\s\S]*HoldRepeatCancelReason\.InputLost' 'Macro Turbo can wait indefinitely without proving that the original macro executor completed.'
Assert-Contains $macroProcess 'var decision\s*=\s*turboEngine\.Tick\(now,\s*observation\.Safety,\s*observation\.ActionReady\);[\s\S]*decision\.Kind\s*==\s*HoldRepeatDecisionKind\.Pulse[\s\S]*DispatchMacroTurboPulse\(runtime,\s*decision\.Pulse\)' 'Macro Turbo no longer emits only a current framework-driven hold-engine pulse.'

$macroFreezeMatch = [regex]::Match(
    $runtime,
    'private bool TryFreezeInitialMacroTranscript\([\s\S]*?(?=\r?\n\s*private bool TryCompleteMacroExecutionEpoch\()')
if (-not $macroFreezeMatch.Success) {
    throw 'The audited asynchronous initial Macro transcript freeze could not be isolated.'
}
$macroFreeze = $macroFreezeMatch.Value
Assert-Contains $macroFreeze 'runtime\.InitialTranscriptBuilder\s+is\s+not\s+\{\s*\}\s+builder[\s\S]*CancelTurboUnsafe\([\s\S]*builder\.Freeze\(out var transcript\)[\s\S]*runtime\.InitialTranscriptBuilder\s*=\s*null;[\s\S]*freezeResult\s*!=\s*MacroTurboFreezeResult\.Frozen\s*\|\|\s*transcript\s+is\s+null[\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.ResolvedActionChange[\s\S]*runtime\.Transcript\s*=\s*transcript;[\s\S]*runtime\.InitialMacroLockCompleted\s*=\s*true;' 'Asynchronous initial Macro completion can retain its builder or accept an incomplete/extra/invalid ActionCount transcript.'

$macroEpochCompleteMatch = [regex]::Match(
    $runtime,
    'private bool TryCompleteMacroExecutionEpoch\([\s\S]*?(?=\r?\n\s*private void DispatchMacroTurboPulse\()')
if (-not $macroEpochCompleteMatch.Success) {
    throw 'The audited repeated Macro execution completion gate could not be isolated.'
}
$macroEpochComplete = $macroEpochCompleteMatch.Value
Assert-Contains $macroEpochComplete 'epoch\s*<=\s*0[\s\S]*runtime\.ActiveExecutionEpoch\s*!=\s*epoch[\s\S]*runtime\.ActiveExecutionCursor\s+is\s+not\s+\{\s*\}\s+cursor[\s\S]*CancelTurboUnsafe\(' 'A missing or stale ordered Macro execution epoch can complete successfully.'
Assert-Contains $macroEpochComplete 'cursor\.Finish\(\);[\s\S]*runtime\.ActiveExecutionCursor\s*=\s*null;[\s\S]*runtime\.ActiveExecutionEpoch\s*=\s*0;[\s\S]*completion\s*==\s*MacroTurboExecutionResult\.Complete[\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.ResolvedActionChange' 'Repeated Macro execution no longer clears its cursor/epoch and cancels on mismatch, extra, or incomplete Finish results.'

$macroObserveMatch = [regex]::Match(
    $runtime,
    'private MacroTurboObservation ObserveMacroTurbo\([\s\S]*?(?=\r?\n\s*private void DispatchTurboPulse\()')
if (-not $macroObserveMatch.Success) {
    throw 'The audited Macro Turbo observation method could not be isolated.'
}
$macroObserve = $macroObserveMatch.Value
Assert-Contains $macroObserve 'inputSource\?\.IsStillHeld\(runtime\.Press\)\s*==\s*true' 'Macro Turbo final observation no longer requires the exact physical control to remain held.'
Assert-Contains $macroObserve 'TryReadSafeMacroProfile\(runtime\.SlotIdentity,\s*out var currentMacroProfile,\s*out _\)[\s\S]*currentMacroProfile\.ContentFingerprint\s*==\s*runtime\.MacroProfile\.ContentFingerprint' 'Macro Turbo final observation no longer revalidates the complete macro-content hash.'
Assert-Contains $macroObserve 'runtime\.Transcript\s+is\s+null[\s\S]*!runtime\.InitialMacroLockCompleted[\s\S]*!checkMacroHash[\s\S]*actionManager\s*!=\s*null\s*&&\s*IsFrozenMacroTranscriptLive\(runtime\.Transcript,\s*actionManager\)' 'Macro Turbo final observation can treat a missing completed transcript as valid or skip live ordered-entry eligibility before a due pulse.'
Assert-Contains $macroObserve 'latestCertifiedPressId\)\s*==\s*runtime\.Press\.PressId[\s\S]*TryReadCurrentSlotIdentity\(runtime\.Press,\s*out var currentIdentity\)[\s\S]*currentIdentity\s*==\s*runtime\.SlotIdentity' 'Macro Turbo final observation no longer proves the newest certified press and unchanged exact binding/slot identity.'
Assert-Contains $macroObserve 'TargetFingerprint\s*==\s*runtime\.Snapshot\.TargetFingerprint[\s\S]*TerritoryId\s*==\s*runtime\.Snapshot\.TerritoryId[\s\S]*ContextFingerprint\s*==\s*runtime\.Snapshot\.ContextFingerprint[\s\S]*LocalGameObjectId\s*==\s*runtime\.Snapshot\.LocalGameObjectId[\s\S]*LocalAddress\s*==\s*runtime\.Snapshot\.LocalAddress' 'Macro Turbo final observation no longer requires the exact target, territory, instance/context, and local-player identity.'
Assert-Contains $macroObserve 'string\.Equals\(\s*compatibilitySignature,\s*runtime\.CompatibilitySignature,\s*StringComparison\.Ordinal\)[\s\S]*compatibility\.IsLiveReActionProfileCurrent\(\)' 'Macro Turbo final observation no longer requires the captured plugin topology and live audited ReAction profile.'
Assert-Contains $macroObserve 'configuration\.Enabled[\s\S]*configuration\.TurboEnabled[\s\S]*configuration\.TurboMacrosEnabled[\s\S]*!configuration\.DryRun[\s\S]*configuration\.TurboOutOfCombat\s*\|\|\s*condition\[ConditionFlag\.InCombat\]' 'Macro Turbo enablement, opt-in, dry-run, or combat gates are incomplete.'
Assert-Contains $macroObserve 'ConflictDetected:\s*activeConflicts\.Count\s*>\s*0\s*\|\|\s*compatibilityQuarantineFrames\s*>\s*0[\s\S]*LoggedIn:[\s\S]*IsAlive:[\s\S]*IsMounted:[\s\S]*IsStunned:[\s\S]*IsKnockbackActive:[\s\S]*PhysicalControlDown:[\s\S]*ReleaseObserved:[\s\S]*TerritoryMatches:[\s\S]*InstanceMatches:[\s\S]*TargetMatches:[\s\S]*ResolvedActionMatches:[\s\S]*BindingMatches:[\s\S]*PluginStateMatches:[\s\S]*Faulted:' 'Macro Turbo no longer constructs the complete fail-closed hold safety state.'
Assert-Contains $macroObserve 'ResolvedActionMatches:\s*macroProfileMatches\s*&&\s*transcriptMatches' 'Macro Turbo safety no longer combines the content hash with live per-entry transcript eligibility.'
Assert-Contains $macroObserve 'runtime\.InitialMacroLockCompleted[\s\S]*runtime\.Transcript\s+is\s+not\s+null[\s\S]*runtime\.ActiveExecutionCursor\s+is\s+null[\s\S]*runtime\.ActiveExecutionEpoch\s*==\s*0[\s\S]*engine\.Pending\s+is\s+null[\s\S]*actionManager\s*!=\s*null[\s\S]*hotbarModule\s*!=\s*null[\s\S]*!IsMacroExecutionActive\(\)[\s\S]*!actionManager->ActionQueued[\s\S]*actionManager->AnimationLock\s*<=\s*AnimationLockEpsilonSeconds' 'Macro Turbo readiness no longer requires a frozen complete transcript, no active execution epoch, free macro executor, no one-shot/native queue, and a clear animation lock.'

$macroLiveMatch = [regex]::Match(
    $runtime,
    'private bool IsFrozenMacroTranscriptLive\([\s\S]*?(?=\r?\n\s*private void DispatchTurboPulse\()')
if (-not $macroLiveMatch.Success) {
    throw 'The audited pre-dispatch Macro transcript live check could not be isolated.'
}
$macroLive = $macroLiveMatch.Value
Assert-Contains $macroLive 'transcript\.Count\s*!=\s*transcript\.ExpectedActionCount[\s\S]*for \(var index\s*=\s*0;\s*index\s*<\s*transcript\.Count;\s*index\+\+\)[\s\S]*expected\s*=\s*transcript\[index\]' 'Pre-dispatch Macro validation no longer checks every exact ActionCount entry in baseline order.'
Assert-Contains $macroLive 'TryCreateMacroTranscriptEntry\(\s*actionManager,\s*\(ActionType\)expected\.ActionType,\s*expected\.RequestedActionId,\s*expected\.TargetId,\s*expected\.ExtraParam,\s*ActionManager\.UseActionMode\.Macro,\s*expected\.RouteId,\s*out var observed\)[\s\S]*!expected\.SemanticallyMatches\(observed\)[\s\S]*return false;' 'Immediately before dispatch, a baseline action can bypass strict Macro mode, live resolution/eligibility/MOAction checks, or resolver-target semantic identity.'
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

# The original physical macro remains vanilla and cannot enter the one-shot
# exact-action buffer; only the separately certified hold may own slot replay.
Assert-Contains $runtime 'if \(nativeHotbarInput\)[\s\S]*activeHotbarInput\s+is\s+\{\s*SlotIdentity\.CommandType:\s*MacroHotbarSlotType\s*\}[\s\S]*macroScope[\s\S]*goto CandidateCaptureComplete;' 'The original physical macro action can incorrectly enter one-shot buffering.'

# Every direct action that can precede another exact-action pulse establishes an
# exact acknowledgement barrier: the original physical action, a one-shot
# buffered dispatch, and each direct Turbo pulse. Macro slot replay instead uses
# native macro/queue/animation readiness and never borrows this action identity.
# Early direct effects are retained briefly so a synchronous effect cannot race
# direct Turbo runtime construction.
Assert-Contains $runtime 'private void ProcessOriginalOutcome\([\s\S]*exactQueueClaimed\s*=\s*nativeOutcome\s*==\s*NativeActionOutcome\.MatchingNewQueue[\s\S]*TryClaimNewQueue\([\s\S]*RecordInitialTurboOutcome\([\s\S]*exactQueueClaimed,[\s\S]*allowQueuedOutcome:\s*true\)' 'The original physical action no longer records only exact immediate/owned-queue acknowledgement provenance.'
Assert-Contains $runtime 'private static TurboAcknowledgementSeed CreateTurboAcknowledgementSeed\([\s\S]*new TurboActionEffectExpectation\([\s\S]*candidate\.ActionType,[\s\S]*candidate\.RequestedActionId,[\s\S]*candidate\.ResolvedActionId,[\s\S]*sequenceMode,[\s\S]*sequenceMarker\)[\s\S]*NowMilliseconds' 'Initial acknowledgement seeds no longer retain the exact action identity, sequence mode/marker, and start time.'
Assert-Contains $runtime 'private void ApplyTurboCaptureOutcome\([\s\S]*scope\.Generation\s*==\s*candidate\.InputGeneration[\s\S]*scope\.TurboCandidate\?\.ExactTuple\s*==\s*candidate\.ExactTuple[\s\S]*scope\.InitialAcknowledgement\s*=\s*seed' 'A direct original acknowledgement seed can attach to a different generation or action tuple.'
Assert-Contains $directStart 'macroTurboRuntime\s*=\s*null;[\s\S]*turboRuntime\s*=\s*runtime;[\s\S]*scope\.InitialAcknowledgement\s+is\s+\{\s*\}\s+initialAcknowledgement[\s\S]*!BeginInitialTurboAcknowledgement\(runtime,\s*initialAcknowledgement\)[\s\S]*CancelTurboUnsafe\([\s\S]*PulseRejected' 'Direct Turbo can overlap Macro Turbo or start without installing/proving its original-action acknowledgement barrier.'

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
# token. Direct Turbo emits one immutable UseAction tuple. Macro Turbo has a
# distinct path that invokes the original ExecuteSlotById exactly once for the
# still-certified stored hotbar/slot.
Assert-Contains $runtime 'private void DispatchTurboPulse\([\s\S]*?lock \(dispatchGate\)[\s\S]*?turboEngine\.IsTokenCurrent\(token\)[\s\S]*?ObserveTurbo\(runtime,\s*checkLiveMOAction:\s*true\)' 'Direct Turbo final token and safety validation are not serialized under the dispatch gate.'
$turboDispatchMatch = [regex]::Match(
    $runtime,
    'private void DispatchTurboPulse\([\s\S]*?(?=\r?\n\s*private TurboObservation ObserveTurbo\()')
if (-not $turboDispatchMatch.Success) {
    throw 'The audited direct Turbo dispatch method could not be isolated.'
}
$turboDispatch = $turboDispatchMatch.Value
$turboTupleCalls = [regex]::Matches(
    $turboDispatch,
    'useActionHook\.Original\(\s*actionManager,\s*runtime\.Candidate\.ActionType,\s*runtime\.Candidate\.RequestedActionId,\s*runtime\.Candidate\.TargetId,\s*runtime\.Candidate\.ExtraParam,\s*\(ActionManager\.UseActionMode\)runtime\.Candidate\.ExactTuple\.Mode,\s*runtime\.Candidate\.ComboRouteId,').Count
$turboUseActionCalls = [regex]::Matches($turboDispatch, 'useActionHook\.Original\s*\(').Count
if ($turboTupleCalls -ne 1 -or $turboUseActionCalls -ne 1) {
    throw "Direct Turbo must contain exactly one captured-tuple UseAction call; found $turboTupleCalls exact out of $turboUseActionCalls total."
}
if ($turboDispatch -match 'executeSlot(?:ById)?Hook\.Original\s*\(' -or
    $turboDispatch -match 'DispatchMacroTurboPulse\s*\(') {
    throw 'Direct Turbo dispatch can diverge into Macro slot replay.'
}
Assert-Contains $runtime 'var resolvedActionId\s*=[\s\S]*GetAdjustedActionId\(runtime\.Candidate\.RequestedActionId\);[\s\S]*resolvedActionId\s*!=\s*0[\s\S]*resolvedActionId\s*==\s*runtime\.Candidate\.ResolvedActionId[\s\S]*TryGetEligibleActionProfile' 'Direct Turbo no longer cancels when the currently resolved action ID differs from the captured resolved ID.'
Assert-Contains $turboDispatch 'var exactTuple\s*=\s*runtime\.Candidate\.ExactTuple;' 'Direct Turbo dispatch no longer uses the captured ExactTuple unchanged.'
$exactTupleAssignments = [regex]::Matches($turboDispatch, '\bexactTuple\s*=').Count
if ($exactTupleAssignments -ne 1 -or
    $turboDispatch -match 'runtime\.Candidate\.ExactTuple\s+with\s*\{') {
    throw 'Direct Turbo dispatch rewrites the captured ExactTuple before outcome classification or acknowledgement.'
}

$macroDispatchMatch = [regex]::Match(
    $runtime,
    'private void DispatchMacroTurboPulse\([\s\S]*?(?=\r?\n\s*private MacroTurboObservation ObserveMacroTurbo\()')
if (-not $macroDispatchMatch.Success) {
    throw 'The audited Macro Turbo slot dispatch method could not be isolated.'
}
$macroDispatch = $macroDispatchMatch.Value
Assert-Contains $macroDispatch 'lock \(dispatchGate\)[\s\S]*turboEngine\.IsTokenCurrent\(token\)[\s\S]*ReferenceEquals\(macroTurboRuntime,\s*runtime\)[\s\S]*activeMacroPulseExecution\s+is\s+not\s+null[\s\S]*engine\.Pending\s+is\s+not\s+null' 'Macro Turbo final dispatch is not serialized with the exact current pulse/runtime, single execution scope, and one-shot exclusion.'
Assert-Contains $macroDispatch 'ObserveMacroTurbo\(runtime,\s*checkMacroHash:\s*true\)[\s\S]*IsTurboSafetySafe\(observation\.Safety\)[\s\S]*observation\.ActionReady[\s\S]*observation\.HotbarModule\s*==\s*null[\s\S]*runtime\.Transcript\s+is\s+not\s+\{\s*\}\s+transcript' 'Macro Turbo does not repeat complete hash/transcript live eligibility, safety, and readiness validation immediately before slot replay.'
Assert-Contains $macroDispatch 'runtime\.NextExecutionEpoch\s*==\s*long\.MaxValue[\s\S]*CancelTurboUnsafe\([\s\S]*executionEpoch\s*=\s*\+\+runtime\.NextExecutionEpoch;[\s\S]*runtime\.ActiveExecutionEpoch\s*=\s*executionEpoch;[\s\S]*runtime\.ActiveExecutionCursor\s*=\s*transcript\.StartExecution\(\);' 'A Macro pulse can begin without a unique ordered execution epoch and fresh baseline cursor.'
$macroExactSlotCalls = [regex]::Matches(
    $macroDispatch,
    'executeSlotByIdHook\.Original\(\s*observation\.HotbarModule,\s*runtime\.SlotIdentity\.Binding\.HotbarId,\s*runtime\.SlotIdentity\.Binding\.SlotId\)').Count
$macroSlotCalls = [regex]::Matches(
    $macroDispatch,
    'executeSlot(?:ById)?Hook\.Original\s*\(').Count
if ($macroExactSlotCalls -ne 1 -or $macroSlotCalls -ne 1) {
    throw "Macro Turbo must contain exactly one original replay of the stored certified hotbar/slot; found $macroExactSlotCalls exact out of $macroSlotCalls total."
}
if ($macroDispatch -match 'useActionHook\.Original\s*\(' -or
    $macroDispatch -match '(?m)\b(?:while|for)\s*\(' -or
    [regex]::Matches($macroDispatch, 'DispatchMacroTurboPulse\s*\(').Count -ne 1) {
    throw 'Macro Turbo can select an action tuple, loop, recurse, or otherwise burst more than one slot replay per pulse.'
}
Assert-Contains $macroDispatch 'activeMacroPulseExecution\s*=\s*new MacroPulseExecutionScope\(runtime,\s*token,\s*executionEpoch\);[\s\S]*runtime\.OwnsMacroExecutor\s*=\s*true;[\s\S]*turboDispatching\s*=\s*true;[\s\S]*hotbarExecutionDepth\+\+;[\s\S]*try[\s\S]*executeSlotByIdHook\.Original[\s\S]*finally[\s\S]*hotbarExecutionDepth--;[\s\S]*turboDispatching\s*=\s*false;[\s\S]*activeMacroPulseExecution\s*=\s*null;[\s\S]*runtime\.OwnsMacroExecutor\s*=\s*ReferenceEquals\(macroTurboRuntime,\s*runtime\)[\s\S]*IsMacroExecutionActive\(\)[\s\S]*ReferenceEquals\(macroTurboRuntime,\s*runtime\)[\s\S]*!runtime\.OwnsMacroExecutor[\s\S]*TryCompleteMacroExecutionEpoch\(\s*runtime,\s*executionEpoch,\s*"synchronous slot return"\)' 'Macro slot replay is not bound to one ordered execution epoch/executor owner, can be re-certified as physical input, or can skip synchronous Finish validation.'
Assert-Contains $macroDispatch 'turboPulseCount\+\+;[\s\S]*!ReferenceEquals\(macroTurboRuntime,\s*runtime\)\) return;[\s\S]*if \(result\s*!=\s*0\)' 'A mismatch/extra/incomplete cancellation during Macro slot execution can still be counted as an accepted pulse.'
Assert-Contains $macroDispatch 'turboPulseCount\+\+;[\s\S]*if \(result\s*!=\s*0\)[\s\S]*turboAcceptedCount\+\+;[\s\S]*else[\s\S]*turboRejectedCount\+\+;' 'Macro slot replay no longer records one terminal outcome without an immediate retry path.'

$macroTickCalls = [regex]::Matches($macroProcess, 'turboEngine\.Tick\s*\(').Count
$macroPulseDispatchCalls = [regex]::Matches($macroProcess, 'DispatchMacroTurboPulse\s*\(').Count
if ($macroTickCalls -ne 1 -or $macroPulseDispatchCalls -ne 1 -or
    $macroProcess -match '(?m)\b(?:while|for)\s*\(') {
    throw "Macro Turbo must make one hold-engine decision and at most one slot dispatch per framework pass; found $macroTickCalls ticks and $macroPulseDispatchCalls dispatches."
}

if ($runtime -match '\bExecuteMacro\w*\s*\(') {
    throw 'Runtime bypasses the audited same-slot ExecuteSlotById path with a direct macro-execution call.'
}
$executeSlotByIdDetourMatch = [regex]::Match(
    $runtime,
    'private byte ExecuteSlotByIdDetour\([\s\S]*?(?=\r?\n\s*private bool UseActionDetour\()')
if (-not $executeSlotByIdDetourMatch.Success) {
    throw 'The original manual ExecuteSlotById pass-through could not be isolated.'
}
$executeSlotByIdDetourCalls = [regex]::Matches(
    $executeSlotByIdDetourMatch.Value,
    'executeSlotByIdHook\.Original\(thisPtr,\s*hotbarId,\s*slotId\)').Count
$executeSlotByIdOriginalCalls = [regex]::Matches($runtime, 'executeSlotByIdHook\.Original\s*\(').Count
if ($executeSlotByIdDetourCalls -ne 1 -or $executeSlotByIdOriginalCalls -ne 2) {
    throw "Expected only the manual ExecuteSlotById pass-through and one Macro Turbo same-slot replay; found $executeSlotByIdDetourCalls manual and $executeSlotByIdOriginalCalls total call sites."
}
Assert-Contains $turboDispatch 'NativeActionOutcomeClassifier\.Classify\([\s\S]*?result\s*!=\s*0\s*\|\|\s*sequenceAdvanced,[\s\S]*?queueBefore,[\s\S]*?queueAfter,[\s\S]*?exactTuple\)' 'Direct Turbo pulse outcome is not classified against the complete exact queue tuple.'
Assert-Contains $turboDispatch 'nativeOutcome\s*==\s*NativeActionOutcome\.ImmediateAcceptance\s*&&\s*sequenceAdvanced[\s\S]*?TurboAcknowledgementSequenceMode\.ImmediateExact[\s\S]*?sequenceAfter' 'Immediate direct-Turbo acceptance is not gated by an exact advanced sequence.'
Assert-Contains $turboDispatch 'nativeOutcome\s*==\s*NativeActionOutcome\.MatchingNewQueue[\s\S]*&&\s*!sequenceAdvanced[\s\S]*&&\s*!runtime\.IsMacro[\s\S]*TryClaimNewQueue\([\s\S]*if \(!claimed[\s\S]*TurboAcknowledgementSequenceMode\.QueuedAfterBaseline[\s\S]*sequenceBefore' 'Queued direct-Turbo acceptance is not restricted to a newly owned queue without a simultaneous sequence transition.'
Assert-Contains $runtime 'private void RejectTurboPulseUnsafe\([\s\S]*?HoldRepeatCancelReason\.PulseRejected,[\s\S]*?hold ended without retry' 'An unproven direct-Turbo pulse is not terminal without retry.'

$processTurboMatch = [regex]::Match(
    $runtime,
    'private void ProcessTurbo\([\s\S]*?(?=\r?\n\s*private void ProcessMacroTurboUnsafe\()')
if (-not $processTurboMatch.Success) {
    throw 'The audited direct/Macro Turbo runtime router could not be isolated.'
}
$processTurbo = $processTurboMatch.Value
Assert-Contains $runtime '\[ThreadStatic\][\s\S]*private static MacroPulseExecutionScope\? activeMacroPulseExecution;[\s\S]*private TurboRuntime\? turboRuntime;[\s\S]*private MacroTurboRuntime\? macroTurboRuntime;' 'Macro pulse execution, direct Turbo, and Macro Turbo no longer have distinct runtime ownership state.'
Assert-Contains $runtime 'private sealed class MacroTurboRuntime\([\s\S]*MacroTurboTranscriptBuilder\? initialTranscriptBuilder,[\s\S]*MacroTurboTranscript\? transcript\)[\s\S]*public MacroTurboTranscriptBuilder\? InitialTranscriptBuilder\s*\{\s*get;\s*set;\s*\}\s*=\s*initialTranscriptBuilder;[\s\S]*public MacroTurboTranscript\? Transcript\s*\{\s*get;\s*set;\s*\}\s*=\s*transcript;[\s\S]*public bool OwnsMacroExecutor\s*\{\s*get;\s*set;\s*\}[\s\S]*public ExactActionTuple\? OwnedQueueTuple\s*\{\s*get;\s*set;\s*\}[\s\S]*public long NextExecutionEpoch\s*\{\s*get;\s*set;\s*\}[\s\S]*public long ActiveExecutionEpoch\s*\{\s*get;\s*set;\s*\}[\s\S]*public MacroTurboExecutionCursor\? ActiveExecutionCursor\s*\{\s*get;\s*set;\s*\}[\s\S]*private sealed record MacroPulseExecutionScope\(\s*MacroTurboRuntime Runtime,\s*HoldRepeatPulseToken Token,\s*long ExecutionEpoch\);' 'Macro Turbo runtime no longer retains the exact initial/frozen transcript, executor/queue owner, ordered execution epoch/cursor, and pulse-token identity.'
Assert-Contains $processTurbo '!snapshot\.HasActiveHold[\s\S]*turboRuntime\s*=\s*null;[\s\S]*macroTurboRuntime\s*=\s*null;[\s\S]*turboAcknowledgement,\s*null' 'An inactive hold does not clear both runtime owners and the direct acknowledgement.'
Assert-Contains $processTurbo 'if \(macroTurboRuntime\s+is\s+\{\s*\}\s+macroRuntime\)[\s\S]*ProcessMacroTurboUnsafe\(macroRuntime,\s*snapshot,\s*now\);[\s\S]*return;[\s\S]*if \(turboRuntime\s+is\s+not\s+\{\s*\}\s+runtime\)' 'Macro and direct Turbo are no longer routed as mutually exclusive runtime owners.'
Assert-Contains $processTurbo 'observation\.ActionReady\s*&&\s*acknowledgement\s+is\s+null[\s\S]*if \(acknowledgement\s+is\s+not\s+null\)[\s\S]*MaximumTurboAcknowledgementMilliseconds[\s\S]*PulseRejected[\s\S]*return;[\s\S]*DispatchTurboPulse' 'Direct Turbo can issue another pulse while an original, one-shot, or prior pulse acknowledgement is pending or timed out.'
if (($macroProcess + "`n" + $macroDispatch + "`n" + $macroObserve) -match 'turboAcknowledgement|BeginTurboAcknowledgement|useActionHook\.Original') {
    throw 'Macro Turbo incorrectly borrows the direct exact-action acknowledgement or UseAction path.'
}
Assert-Contains $runtime 'private void CancelTurboUnsafe\([\s\S]*?turboEngine\.Cancel\(reason\);[\s\S]*?turboRuntime\s*=\s*null;[\s\S]*?macroTurboRuntime\s*=\s*null;[\s\S]*?turboAcknowledgement,\s*null' 'Turbo cancellation does not invalidate the core hold, direct runtime, Macro runtime, and acknowledgement together.'
Assert-Contains $runtime 'MaximumTurboAcknowledgementMilliseconds\s*=\s*2_000\s*;' 'Turbo acknowledgement timeout changed from the audited bound.'
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
