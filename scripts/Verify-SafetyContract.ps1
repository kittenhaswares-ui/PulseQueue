param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-Source([string]$RelativePath) {
    $path = Join-Path $RepositoryRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required safety-contract source is missing: $RelativePath"
    }

    return Get-Content -LiteralPath $path -Raw
}

function Assert-Contains(
    [string]$Value,
    [string]$Pattern,
    [string]$Message) {
    if ($Value -notmatch $Pattern) { throw $Message }
}

function Assert-NotContains(
    [string]$Value,
    [string]$Pattern,
    [string]$Message) {
    if ($Value -match $Pattern) { throw $Message }
}

function Assert-Count(
    [string]$Value,
    [string]$Pattern,
    [int]$Expected,
    [string]$Message) {
    $count = [regex]::Matches($Value, $Pattern).Count
    if ($count -ne $Expected) {
        throw "$Message Found $count; expected $Expected."
    }
}

function Assert-CountAtLeast(
    [string]$Value,
    [string]$Pattern,
    [int]$Minimum,
    [string]$Message) {
    $count = [regex]::Matches($Value, $Pattern).Count
    if ($count -lt $Minimum) {
        throw "$Message Found $count; expected at least $Minimum."
    }
}

function Get-MethodBlock(
    [string]$Value,
    [string]$DeclarationPattern,
    [string]$Description) {
    $pattern = $DeclarationPattern + '[\s\S]*?(?=\r?\n\s*(?:public|private|internal|protected)\s+(?:static\s+)?(?:unsafe\s+)?[\w<>,\[\]\?\.]+\s+\w+\s*\(|\z)'
    $match = [regex]::Match($Value, $pattern)
    if (-not $match.Success) { throw "Could not isolate $Description." }
    return $match.Value
}

$runtime = Read-Source 'src/PulseQueue.Plugin/Services/ActionBufferService.cs'
$compatibility = Read-Source 'src/PulseQueue.Plugin/Services/PluginCompatibilityService.cs'
$physicalInput = Read-Source 'src/PulseQueue.Plugin/Services/PhysicalHotbarInputSource.cs'
$logicalRepeat = Read-Source 'src/PulseQueue.Core/LogicalHotbarRepeatEngine.cs'
$bufferEngine = Read-Source 'src/PulseQueue.Core/BufferEngine.cs'
$inputGeneration = Read-Source 'src/PulseQueue.Core/InputGenerationGate.cs'
$nativeOutcome = Read-Source 'src/PulseQueue.Core/NativeActionOutcome.cs'
$nativeOwnership = Read-Source 'src/PulseQueue.Core/NativeQueueOwnership.cs'
$repeatOwnership = Read-Source 'src/PulseQueue.Core/RepeatNativeQueueOwnership.cs'
$configuration = Read-Source 'src/PulseQueue.Plugin/Models/PluginConfiguration.cs'
$manifest = Read-Source 'src/PulseQueue.Plugin/PulseQueue.Plugin.json' | ConvertFrom-Json

# ---------------------------------------------------------------------------
# One-shot smart buffer: keep the original hard safety and exact-ownership
# contract. Native held-input repetition is deliberately a separate layer.
# ---------------------------------------------------------------------------

Assert-Contains $bufferEngine 'AbsoluteHoldCapMilliseconds\s*=\s*350\s*;' `
    'The one-shot buffer hard cap is no longer exactly 350 ms.'
Assert-Contains $bufferEngine 'pending\s*=\s*null;[\s\S]*BufferDecisionKind\.Dispatch' `
    'The one-shot buffer no longer consumes ownership before dispatch.'
Assert-Contains $bufferEngine 'ActionFailureKind\.Cooldown' `
    'The one-shot buffer lost short local-cooldown handling.'
Assert-Contains $bufferEngine 'safety\.IsMounted[\s\S]*CancelReason\.Mounted' `
    'Mounted-state cancellation is missing from the one-shot policy.'
Assert-Contains $inputGeneration 'Interlocked\.CompareExchange\(ref current,\s*next,\s*observed\)' `
    'Input-generation advancement is no longer atomic.'
Assert-Contains $inputGeneration 'generation\s*>\s*0\s*&&\s*generation\s*==\s*Current' `
    'Input-generation validity is no longer exact/current-only.'
Assert-Contains $runtime 'public void Cancel\([^\)]*\)[\s\S]*inputGenerations\.Invalidate\(\);[\s\S]*engine\.Cancel' `
    'Cancellation does not invalidate the generation before clearing the one-shot core.'
Assert-CountAtLeast $runtime 'inputGenerations\.IsCurrent\(' 3 `
    'Generation ownership is not rechecked through capture, outcome and dispatch.'

$protectedNativeWrite = '->(?:AnimationLock|QueuedActionType|QueuedActionId|QueuedTargetId|QueuedExtraParam|QueueType|QueuedComboRouteId)\s*='
Assert-NotContains $runtime $protectedNativeWrite `
    'Runtime writes a protected native lock or queue-identity field.'
Assert-NotContains $runtime 'targetManager\.(?:Target|SoftTarget|MouseOverTarget|MouseOverNameplateTarget|FocusTarget|PreviousTarget)\s*=' `
    'Runtime writes a target-manager field.'
Assert-NotContains $runtime 'UseActionLocation\s*\(' `
    'Runtime substitutes a location through UseActionLocation.'

# ActionQueued is the only native queue bit PulseQueue may clear. All three
# writes must consume exact proven ownership: smart newest-input replacement,
# terminal semantic safety cancellation, or repeat-owned newest-input replacement.
Assert-Count $runtime '->ActionQueued\s*=' 3 `
    'Unexpected native ActionQueued mutation count.'
Assert-Contains $runtime 'private bool TryReplaceOwnedNativeQueue\([\s\S]*nativeQueueOwnership\.TryTakeForNewerInput\([\s\S]*SynchronizeOwnedNativeQueueSafetyContext\(\);[\s\S]*actionManager->ActionQueued\s*=\s*false;' `
    'Newest-input queue replacement is not guarded by exact certified ownership.'
Assert-Contains $runtime 'private bool RetryExactOwnedNativeQueueSafetyClear\([\s\S]*nativeQueueOwnership\.HasOwnership[\s\S]*TryTakeExactCurrent\([\s\S]*SynchronizeOwnedNativeQueueSafetyContext\(\);[\s\S]*actionManager->ActionQueued\s*=\s*false;' `
    'Terminal safety cancellation can clear an unproven or newer native queue.'
# Detect-only/faulted/disabled operation may abandon proof, but that helper is
# not permitted to write native queue state. All operational removals still use
# an exact take, drain or reconciliation.
Assert-Count $runtime 'nativeQueueOwnership\.Clear\(\)' 1 `
    'Smart native ownership gained an unexpected non-exact clear site.'
$abandonOwnedQueueProof = Get-MethodBlock $runtime `
    'private void AbandonOwnedQueueProvenanceForDetectOnly\(\)' `
    'AbandonOwnedQueueProvenanceForDetectOnly'
Assert-Contains $abandonOwnedQueueProof 'nativeQueueOwnership\.Clear\(\);[\s\S]*logicalRepeatQueueOwnership\.Clear\(\);' `
    'Detect-only transition does not abandon both stale ownership proofs.'
Assert-NotContains $abandonOwnedQueueProof '->ActionQueued\s*=' `
    'Detect-only proof abandonment mutates the native queue.'

Assert-Contains $nativeOwnership '!after\.Matches\(attempted\)[\s\S]*before\.Matches\(attempted\)[\s\S]*return false' `
    'Native queue ownership can be claimed without a newly created exact queue.'
Assert-Contains $nativeOwnership 'generation\s*<=\s*value\.Generation[\s\S]*return false' `
    'An older or equal input generation can replace a newer owned queue.'
Assert-Contains $nativeOwnership 'sequenceMarker\s*!=\s*value\.SequenceMarker[\s\S]*owned\s*=\s*null' `
    'A changed action sequence does not revoke native queue ownership.'
Assert-Contains $nativeOwnership 'public bool TryBeginExactDrain\([\s\S]*activeDrainLease\s+is\s+not\s+null[\s\S]*current\.Equals\(value\.Snapshot\)[\s\S]*activeDrainLease\s*=\s*new ActiveDrainLease' `
    'Exact native queue drains no longer use a non-reentrant exact-owner lease.'
Assert-Contains $nativeOwnership 'public NativeQueueDrainFinalizeResult CompleteExactDrain\([\s\S]*active\.LeaseId\s*!=\s*lease\.LeaseId[\s\S]*activeDrainLease\s*=\s*null;[\s\S]*OwnershipRetained[\s\S]*OwnershipInvalidated[\s\S]*OwnershipConsumed' `
    'Queue-drain finalization can be forged, duplicated, or lose restored ownership.'

# Repeat-created queues use a separate replacement-only facade. Deliberately
# omitting every drain/replay API makes it impossible for Turbo queue proof to
# become a second action dispatcher.
Assert-Contains $repeatOwnership 'public sealed class RepeatNativeQueueOwnership' `
    'The separate repeat-native queue ownership facade is missing.'
Assert-Contains $runtime 'private readonly RepeatNativeQueueOwnership logicalRepeatQueueOwnership\s*=\s*new\(\);' `
    'Runtime does not keep repeat-created native queue proof separate from smart-buffer ownership.'
Assert-Contains $repeatOwnership 'private readonly NativeQueueOwnership ownership\s*=\s*new\(\);' `
    'Repeat-native queue proof no longer delegates to the exact ownership primitive.'
Assert-Contains $repeatOwnership 'public bool TryClaimFromObservedDelta\([\s\S]*ownership\.TryClaimNewQueue\(generation,\s*sequenceMarker,\s*before,\s*after,\s*attempted\)' `
    'Repeat-native queue proof is not claimed from an observed exact queue delta.'
Assert-Contains $repeatOwnership 'public bool TryTakeForNewerInput\([\s\S]*ownership\.TryTakeForNewerInput\(generation,\s*sequenceMarker,\s*current,\s*out replaceable\)' `
    'A newer physical input cannot take the exact repeat-owned native queue.'
Assert-NotContains $repeatOwnership '\b(?:TryBeginExactDrain|CompleteExactDrain|TryAuthorizeExactDrain|CanDeferExactHiddenDrain|TryTakeExactCurrent|Replay|Dispatch)\b' `
    'Repeat-native queue ownership exposes a drain, replay or dispatch API.'

foreach ($fieldPattern in @(
    'ActionType\s*==\s*attempted\.ActionType',
    'ActionId\s*==\s*attempted\.RequestedActionId',
    'ActionId\s*==\s*attempted\.ResolvedActionId',
    'TargetId\s*==\s*attempted\.TargetId',
    'Param\s*==\s*attempted\.Param',
    'Mode\s*==\s*attempted\.Mode',
    'RouteId\s*==\s*attempted\.RouteId'
)) {
    Assert-Contains $nativeOutcome $fieldPattern `
        "Exact native queue identity is incomplete: $fieldPattern"
}
Assert-Contains $nativeOutcome 'after\.Matches\(attempted\)\s*&&\s*!before\.Matches\(attempted\)[\s\S]*NativeActionOutcome\.MatchingNewQueue' `
    'A newly created exact native queue is not classified distinctly.'
Assert-Contains $nativeOutcome 'if \(before\.IsQueued\s*\|\|\s*after\.IsQueued\)[\s\S]*NativeActionOutcome\.ForeignOrPreexistingQueue' `
    'Foreign or pre-existing native queue state is not fail-closed.'
Assert-Contains $runtime 'useActionHook\.Original\([\s\S]*runtime\.Candidate\.ActionType,[\s\S]*runtime\.Candidate\.RequestedActionId,[\s\S]*runtime\.Candidate\.TargetId,[\s\S]*runtime\.Candidate\.ExtraParam,[\s\S]*ActionManager\.UseActionMode\.Queue,[\s\S]*runtime\.Candidate\.ComboRouteId' `
    'One-shot dispatch no longer preserves the immutable requested action tuple.'
Assert-Count $runtime 'useActionHook\.Original\s*\(' 2 `
    'UseAction must have one native pass-through and one exact one-shot queue dispatch.'

# ---------------------------------------------------------------------------
# Native held-input Turbo: observe the same logical input boundary FFXIV uses.
# It reports pressed; it never replays a remembered slot/action/macro itself.
# ---------------------------------------------------------------------------

Assert-Contains $physicalInput 'CheckHotbarBindingsSignature\s*=\s*"89 54 24 10 53 41 55 41 57"' `
    'The audited CheckHotbarBindings signature scope is missing.'
Assert-Contains $physicalInput 'HookFromSignature<CheckHotbarBindingsDelegate>\([\s\S]*CheckHotbarBindingsSignature,[\s\S]*CheckHotbarBindingsDetour' `
    'CheckHotbarBindings is not hooked as the native binding-scan scope.'
Assert-Contains $physicalInput 'HookFromAddress<InputData\.Delegates\.IsInputIdPressed>\([\s\S]*InputData\.MemberFunctionPointers\.IsInputIdPressed,[\s\S]*IsInputIdPressedDetour' `
    'The native IsInputIdPressed boundary is not hooked.'

$bindingScan = Get-MethodBlock $physicalInput `
    'private void CheckHotbarBindingsDetour\([^\)]*\)' `
    'CheckHotbarBindingsDetour'
Assert-Contains $bindingScan 'activeScanSource\s*=\s*this;[\s\S]*activeScanSettings\s*=\s*ReadSettingsFailOpen\(\);[\s\S]*checkHotbarBindingsHook\.Original\(context,\s*mode\);[\s\S]*finally[\s\S]*activeScanSource\s*=\s*previousSource;[\s\S]*activeScanSettings\s*=\s*previousSettings;' `
    'Logical pressed injection is not strictly scoped to the native binding scan.'
Assert-Count $bindingScan 'checkHotbarBindingsHook\.Original\s*\(' 1 `
    'CheckHotbarBindings must forward its native original exactly once.'

$pressedDetour = Get-MethodBlock $physicalInput `
    'private bool IsInputIdPressedDetour\([^\)]*\)' `
    'IsInputIdPressedDetour'
Assert-Contains $pressedDetour 'var nativePressed\s*=\s*pressedHook\.Original\(inputData,\s*inputId\);[\s\S]*activeScanSource\s*!=\s*this[\s\S]*return nativePressed;' `
    'Native pressed state is not read first and preserved outside the binding scan.'
Assert-Count $pressedDetour 'pressedHook\.Original\s*\(' 1 `
    'IsInputIdPressed must call its native original exactly once.'
Assert-Contains $pressedDetour 'var held\s*=\s*inputData->IsInputIdHeld\(inputId\);' `
    'Logical held state is not read from native IsInputIdHeld.'
Assert-Contains $pressedDetour 'repeatEngine\.Observe\(new LogicalHotbarRepeatObservation\([\s\S]*\(long\)inputId,[\s\S]*nativePressed,[\s\S]*held,[\s\S]*settings\.RepeatEnabled,[\s\S]*settings\.ExternalRepeatOwnerActive' `
    'The logical repeat arbiter is not fed the complete native observation.'
Assert-Contains $pressedDetour 'LogicalHotbarRepeatDecisionKind\.PhysicalPress[\s\S]*HotbarActivationKind\.PhysicalPress' `
    'Physical logical edges are not classified distinctly.'
Assert-Contains $pressedDetour 'LogicalHotbarRepeatDecisionKind\.InjectedRepeat[\s\S]*HotbarActivationKind\.InjectedRepeat' `
    'PulseQueue-owned repeats are not classified distinctly.'
Assert-Contains $pressedDetour 'LogicalHotbarRepeatDecisionKind\.DelegatedRepeat[\s\S]*HotbarActivationKind\.DelegatedRepeat' `
    'External-owner repeats are not classified distinctly.'
Assert-Contains $pressedDetour 'LogicalHotbarRepeatDecisionKind\.SuppressedOlderHold[\s\S]*SuppressedByNewerInput:\s*true' `
    'A superseded held input cannot be marked for exact-slot suppression.'
Assert-Contains $pressedDetour 'onPhysicalPress\(observed\)' `
    'A fresh physical edge is not synchronously published for newest-input preemption.'
Assert-Contains $pressedDetour 'catch[\s\S]*return nativePressed;' `
    'The native input hook no longer fails open to the original pressed result.'

foreach ($kind in @('PhysicalPress', 'InjectedRepeat', 'DelegatedRepeat')) {
    Assert-Contains $physicalInput ("HotbarActivationKind[\s\S]*" + $kind) `
        "Hotbar activation type $kind is missing."
}
Assert-Contains $physicalInput 'record struct HotbarActivation\([\s\S]*HotbarActivationKind Kind,[\s\S]*CertifiedHotbarPress Press,[\s\S]*bool SuppressedByNewerInput' `
    'Hotbar activation provenance does not include kind, press and preemption state.'
Assert-Contains $physicalInput 'MaximumCorrelationAgeMilliseconds\s*=\s*250' `
    'Logical-input-to-slot activation correlation is no longer bounded to 250 ms.'
Assert-Contains $physicalInput 'GetSlotById\([\s\S]*candidate\.Binding\.HotbarId,[\s\S]*candidate\.Binding\.SlotId\)[\s\S]*expected\s*!=\s*slot' `
    'Pointer-based activation is not correlated to the exact native hotbar slot.'
Assert-Contains $physicalInput 'TryFromSlot\(hotbarId,\s*slotId,\s*out var binding\)[\s\S]*pendingActivations\[binding\.Index\]' `
    'ID-based activation is not correlated to the exact native hotbar slot.'

# The input layer must stay action-agnostic. FFXIV resolves the current binding,
# slot, action, target and arbitrary macro content after a pressed result.
Assert-NotContains $physicalInput '\b(?:useActionHook|executeSlot(?:ById)?Hook|MacroSafetyAnalyzer\.|TryReadSafeMacroProfile\s*\(|ActionManager\.|TargetManager\.)' `
    'The logical repeat source selects, parses or dispatches an action/macro/target.'
Assert-NotContains $physicalInput $protectedNativeWrite `
    'The logical repeat source writes protected native action state.'
Assert-NotContains $physicalInput '->ActionQueued\s*=' `
    'The logical repeat source writes the native queue bit.'

# Dependency-free newest-input arbiter: a real edge always passes, the latest
# input owns cadence, the old still-held input stays suppressed until release,
# and external pulses never disable PulseQueue's same-input fallback cadence.
foreach ($kind in @(
    'PhysicalPress',
    'InjectedRepeat',
    'DelegatedRepeat',
    'SuppressedOlderHold',
    'Released'
)) {
    Assert-Contains $logicalRepeat ("LogicalHotbarRepeatDecisionKind\." + $kind) `
        "Logical repeat decision $kind is missing."
}
Assert-Contains $logicalRepeat 'public sealed class LogicalHotbarRepeatEngine' `
    'The dependency-free LogicalHotbarRepeatEngine is missing.'
Assert-Contains $logicalRepeat 'lock \(gate\)[\s\S]*observations\+\+' `
    'Logical input ownership decisions are not serialized.'
Assert-Contains $logicalRepeat 'observation\.NativePressed[\s\S]*ownerLogicalInputId\s*!=\s*observation\.LogicalInputId[\s\S]*ClaimNewestOwner\([\s\S]*PhysicalPress' `
    'A first native press does not immediately become the newest owner.'
Assert-Contains $logicalRepeat 'ownerLogicalInputId\s*>\s*0\s*&&\s*wasHeld[\s\S]*SuppressedOlderHold' `
    'An external/native pulse from an older continuously held input can steal newest-input ownership.'
Assert-Contains $logicalRepeat 'input\.SuppressedUntilRelease[\s\S]*SuppressedOlderHold[\s\S]*shouldReportPressed:\s*false' `
    'A preempted still-held input can be reported pressed again before release.'
Assert-Contains $logicalRepeat 'ownerLogicalInputId\s*>\s*0\s*&&\s*ownerLogicalInputId\s*!=\s*observation\.LogicalInputId[\s\S]*previousOwner\.SuppressedUntilRelease\s*=\s*previousOwner\.Held;[\s\S]*ownerLogicalInputId\s*=\s*observation\.LogicalInputId' `
    'Newest logical input does not atomically preempt and suppress the old owner.'
Assert-Contains $logicalRepeat '!observation\.RepeatEnabled[\s\S]*observation\.NowMilliseconds\s*<\s*nextRepeatAtMilliseconds' `
    'Repeat injection can bypass enablement or cadence.'
Assert-NotContains $logicalRepeat '!observation\.RepeatEnabled\s*\|\|\s*observation\.ExternalRepeatOwnerActive' `
    'An external repeater can still disable PulseQueue fallback cadence.'
Assert-Contains $logicalRepeat 'observation\.ExternalRepeatOwnerActive[\s\S]*delegatedRepeats\+\+;[\s\S]*DelegatedRepeat' `
    'Observed external repeat pulses are not classified distinctly.'
Assert-Contains $logicalRepeat 'if \(observation\.NativePressed\)[\s\S]*lastRepeatSignalAtMilliseconds\s*=\s*observation\.NowMilliseconds;[\s\S]*nextRepeatAtMilliseconds\s*=\s*SaturatingAdd\([\s\S]*observation\.NowMilliseconds,[\s\S]*options\.RepeatIntervalMilliseconds' `
    'A current-owner native/external pulse does not restart the fallback interval.'
Assert-Contains $logicalRepeat 'public bool CoalesceExternalExecution\([\s\S]*ownerLogicalInputId\s*!=\s*logicalInputId[\s\S]*input\.SuppressedUntilRelease[\s\S]*nextRepeatAtMilliseconds\s*=\s*SaturatingAdd\(' `
    'An outer-hook external execution cannot be coalesced without claiming ownership.'
Assert-Contains $logicalRepeat 'injectedRepeats\+\+;[\s\S]*nextRepeatAtMilliseconds\s*=\s*SaturatingAdd\([\s\S]*observation\.NowMilliseconds,[\s\S]*options\.RepeatIntervalMilliseconds\)' `
    'Injected repeat cadence catches up from an old deadline instead of scheduling from now.'
Assert-Contains $logicalRepeat 'input\.ReleaseObserved\s*=\s*true;[\s\S]*input\.SuppressedUntilRelease\s*=\s*false;' `
    'Release does not re-arm a preempted logical input.'

# ---------------------------------------------------------------------------
# Runtime integration: repeat roots remain native pass-throughs and never enter
# BufferEngine candidate/replay machinery. They do, however, observe the exact
# native post-call queue delta in a separate replacement-only ownership path.
# ---------------------------------------------------------------------------

Assert-Contains $runtime 'new PhysicalHotbarInputSource\([\s\S]*GetNativeHotbarRepeatSettings,[\s\S]*OnCertifiedPhysicalPress' `
    'The runtime does not wire settings and physical-preemption into the native input source.'

$executeSlot = Get-MethodBlock $runtime `
    'private byte ExecuteSlotDetour\([^\)]*\)' `
    'ExecuteSlotDetour'
$executeSlotById = Get-MethodBlock $runtime `
    'private byte ExecuteSlotByIdDetour\([^\)]*\)' `
    'ExecuteSlotByIdDetour'
foreach ($entry in @(
    @($executeSlot, 'pointer ExecuteSlot'),
    @($executeSlotById, 'ID ExecuteSlot')
)) {
    $method = $entry[0]
    $description = $entry[1]
    Assert-Contains $method 'TryConsumeActivation\([\s\S]*out var observedActivation' `
        "$description does not explicitly consume HotbarActivation provenance."
    Assert-Contains $method 'activation\s+is\s+\{\s*SuppressedByNewerInput:\s*true\s*\}' `
        "$description does not identify an exact superseded repeat."
    Assert-Contains $method 'Kind:\s*HotbarActivationKind\.InjectedRepeat\s+or\s+HotbarActivationKind\.DelegatedRepeat,[\s\S]*SuppressedByNewerInput:\s*false' `
        "$description does not classify injected/delegated repeat roots."
    Assert-Contains $method 'if \(!suppressPreemptedRepeat\s*&&\s*!repeatRoot\)[\s\S]*BeginHotbarInput\(' `
        "$description can start smart-buffer replacement for a repeat root."
    Assert-Contains $method 'if \(suppressPreemptedRepeat\) return 0;[\s\S]*\.Original\(' `
        "$description forwards a superseded older repeat to the native Original."
    Assert-Contains $method 'repeatExecutionScope\s*=\s*CreateNativeLogicalRepeatExecutionScope\(repeatedActivation\);[\s\S]*TryPrepareNativeMacroRepeatRoot\([\s\S]*repeatExecutionScope' `
        "$description does not bind repeat provenance and a possible async macro tail to the exact root."
    Assert-Contains $method 'if \(repeatRoot\)[\s\S]*logicalRepeatExecutionDepth\+\+;[\s\S]*activeLogicalRepeatExecution\s*=\s*repeatExecutionScope;[\s\S]*\.Original\([\s\S]*finally[\s\S]*CompleteNativeMacroRepeatRoot\([\s\S]*activeLogicalRepeatExecution\s*=\s*previousRepeatExecution;[\s\S]*logicalRepeatExecutionDepth--;' `
        "$description does not scope repeat queue provenance around the authoritative native Original."
}

$useAction = Get-MethodBlock $runtime `
    'private bool UseActionDetour\([^\)]*\)' `
    'UseActionDetour'
Assert-Contains $useAction 'var directLogicalRepeatInput\s*=\s*logicalRepeatExecutionDepth\s*>\s*0;[\s\S]*ClassifyNativeMacroRepeatTail\([\s\S]*out asynchronousLogicalRepeatInput,[\s\S]*out staleLogicalRepeatMacroTail,[\s\S]*out asynchronousRepeatExecutionScope\);[\s\S]*var logicalRepeatInput\s*=\s*directLogicalRepeatInput\s*\|\|\s*asynchronousLogicalRepeatInput;' `
    'Direct and asynchronous logical repeats are not unified under repeat-only provenance.'
Assert-Contains $useAction 'var nativeHotbarInput\s*=\s*hotbarExecutionDepth\s*>\s*0[\s\S]*&&\s*!logicalRepeatInput[\s\S]*&&\s*!replaying[\s\S]*&&\s*!turboDispatching;' `
    'Logical repeats are not separated from physical smart-buffer capture.'
Assert-Contains $useAction 'if \(!suppressSyntheticMacroCall\s*&&\s*logicalRepeatInput\)[\s\S]*createdAttempt\s*=\s*TryCreateLogicalRepeatQueueAttempt\([\s\S]*logicalRepeatExecution[\s\S]*logicalRepeatQueueAttempt\s*=\s*createdAttempt;[\s\S]*logicalRepeatQueueInFlight\s*=\s*createdAttempt;' `
    'A direct or current asynchronous repeat does not create its separate queue-delta observation.'
Assert-Contains $useAction 'if \(!suppressSyntheticMacroCall[\s\S]*&&\s*!logicalRepeatInput[\s\S]*&&\s*!replaying[\s\S]*&&\s*!turboDispatching\)[\s\S]*candidate\s*=\s*TryCreateCandidate' `
    'A logical repeat can enter the BufferEngine candidate/replay path.'
Assert-Contains $useAction 'useActionHook\.Original\([\s\S]*nativeMode[\s\S]*if \(logicalRepeatQueueAttempt\s+is\s+\{\s*\}\s+repeatedAttempt\)[\s\S]*ProcessLogicalRepeatQueueAttempt\([\s\S]*repeatedAttempt,[\s\S]*currentSequence' `
    'Repeat-native queue outcome proof is not processed after the authoritative native call.'

$tryCreateLogicalRepeatQueue = Get-MethodBlock $runtime `
    'private LogicalRepeatQueueAttempt\? TryCreateLogicalRepeatQueueAttempt\([^\)]*\)' `
    'TryCreateLogicalRepeatQueueAttempt'
$processLogicalRepeatQueue = Get-MethodBlock $runtime `
    'private void ProcessLogicalRepeatQueueAttempt\([^\)]*\)' `
    'ProcessLogicalRepeatQueueAttempt'
Assert-NotContains ($tryCreateLogicalRepeatQueue + "`n" + $processLogicalRepeatQueue) '\b(?:engine|pendingRuntimeAction|replaying|TryDispatch)\b|useActionHook\.Original\s*\(' `
    'The repeat-native outcome path can enter BufferEngine replay or dispatch an action.'
Assert-Contains $processLogicalRepeatQueue 'var queueAfter\s*=\s*CaptureNativeQueue\(actionManager\);[\s\S]*var sequenceUnchanged\s*=\s*currentSequence\s*==\s*attempt\.SequenceBefore;[\s\S]*queueAfter\.IsQueued[\s\S]*!queueAfter\.Equals\(attempt\.QueueBefore\)[\s\S]*queueAfter\.Matches\(attempt\.Expected\)[\s\S]*logicalRepeatQueueOwnership\.TryClaimFromObservedDelta\([\s\S]*attempt\.Execution\.Generation,[\s\S]*currentSequence,[\s\S]*attempt\.QueueBefore,[\s\S]*queueAfter,[\s\S]*attempt\.Expected\)' `
    'Repeat queue ownership is not claimed from a post-call delta matching the pre-call expected action tuple.'
Assert-NotContains $processLogicalRepeatQueue 'ExactTupleFromNativeQueue\s*\(' `
    'Repeat queue ownership tautologically derives its authorization tuple from the post-call queue.'

$resolveLogicalRepeatQueue = Get-MethodBlock $runtime `
    'private void ResolveLogicalRepeatQueuePending\([^\)]*\)' `
    'ResolveLogicalRepeatQueuePending'
Assert-Contains $resolveLogicalRepeatQueue 'current\.Matches\(pending\.Expected\)[\s\S]*!pending\.QueueBefore\.Matches\(pending\.Expected\)[\s\S]*logicalRepeatQueueOwnership\.TryClaimFromObservedDelta\([\s\S]*pending\.QueueBefore,[\s\S]*current,[\s\S]*pending\.Expected\)' `
    'Deferred repeat queue correlation can claim a pre-existing identical queue or authorize itself from post-call state.'
Assert-NotContains $resolveLogicalRepeatQueue 'ExactTupleFromNativeQueue\s*\(' `
    'Deferred repeat queue correlation tautologically derives its authorization tuple from the observed queue.'

$completeHotbar = Get-MethodBlock $runtime `
    'private void CompleteHotbarInput\(\)' `
    'CompleteHotbarInput'
$frameworkUpdate = Get-MethodBlock $runtime `
    'private void OnFrameworkUpdate\([^\)]*\)' `
    'OnFrameworkUpdate'
Assert-NotContains $completeHotbar '\bTryStartTurbo\s*\(' `
    'CompleteHotbarInput still starts the obsolete manual slot-replay Turbo.'
Assert-NotContains $frameworkUpdate '\bProcessTurbo\s*\(' `
    'The framework loop still runs the obsolete manual slot-replay Turbo.'
Assert-Count $runtime '\bTryStartTurbo\s*\(' 1 `
    'Obsolete TryStartTurbo regained an active call site.'
Assert-Count $runtime '\bProcessTurbo\s*\(' 1 `
    'Obsolete ProcessTurbo regained an active call site.'

# There is one live Original call in each native detour. Legacy replay helpers
# may remain temporarily, but their only entry points above are statically dead.
Assert-Count $executeSlot 'executeSlotHook\.Original\s*\(' 1 `
    'Pointer ExecuteSlot must forward exactly one native Original.'
Assert-Count $executeSlotById 'executeSlotByIdHook\.Original\s*\(' 1 `
    'ID ExecuteSlot must forward exactly one native Original.'
Assert-NotContains ($completeHotbar + "`n" + $frameworkUpdate) 'executeSlot(?:ById)?Hook\.Original\s*\(' `
    'An active completion/framework path manually replays an ExecuteSlot.'

$physicalPreemption = Get-MethodBlock $runtime `
    'private void OnCertifiedPhysicalPress\([^\)]*\)' `
    'OnCertifiedPhysicalPress'
Assert-Contains $physicalPreemption 'lock \(dispatchGate\)[\s\S]*latestCertifiedPressId,\s*press\.PressId[\s\S]*Cancel\([\s\S]*CancelReason\.Replaced' `
    'A new physical press does not synchronously cancel/replace the older pending owner.'
Assert-Contains $physicalPreemption 'ResolveLogicalRepeatQueuePending\([\s\S]*Cancel\([\s\S]*CancelReason\.Replaced[\s\S]*latestLogicalRepeatQueueReplacementGeneration\s*=\s*inputGenerations\.Current;[\s\S]*TryReplaceLogicalRepeatNativeQueue\([\s\S]*TryReplaceOwnedNativeQueue\(' `
    'A physical edge does not resolve and take repeat-owned queue proof before the smart owner.'

$replaceLogicalRepeatQueue = Get-MethodBlock $runtime `
    'private bool TryReplaceLogicalRepeatNativeQueue\([^\)]*\)' `
    'TryReplaceLogicalRepeatNativeQueue'
Assert-Contains $replaceLogicalRepeatQueue 'var current\s*=\s*CaptureNativeQueue\(actionManager\);[\s\S]*current\.IsQueued[\s\S]*logicalRepeatQueueOwnership\.TryTakeForNewerInput\([\s\S]*replacingGeneration,[\s\S]*actionManager->LastUsedActionSequence,[\s\S]*current,[\s\S]*out var replaced\)[\s\S]*actionManager->ActionQueued\s*=\s*false;' `
    'Repeat-owned native queue clearing is not preceded by an exact newer-generation take.'

# Async native Macro tails inherit the repeat generation. A current tail stays
# in repeat-only outcome processing and therefore cannot execute the generic
# Cancel(Replaced) path; a stale tail is rejected before native Original.
$classifyMacroRepeatTail = Get-MethodBlock $runtime `
    'private void ClassifyNativeMacroRepeatTail\([^\)]*\)' `
    'ClassifyNativeMacroRepeatTail'
Assert-Contains $classifyMacroRepeatTail 'var isCurrent\s*=\s*inputGenerations\.IsCurrent\(tail\.Execution\.Generation\);[\s\S]*if \(isCurrent\)[\s\S]*currentTail\s*=\s*true;[\s\S]*else[\s\S]*staleTail\s*=\s*true;' `
    'Async native Macro tails are not classified by their exact input generation.'
Assert-Contains $useAction 'var suppressSyntheticMacroCall\s*=\s*staleLogicalRepeatMacroTail;[\s\S]*if \(suppressSyntheticMacroCall\)[\s\S]*result\s*=\s*false;[\s\S]*else[\s\S]*useActionHook\.Original\(' `
    'A stale native Macro repeat tail can reach the authoritative native Original.'
Assert-Contains $useAction 'var logicalRepeatInput\s*=\s*directLogicalRepeatInput\s*\|\|\s*asynchronousLogicalRepeatInput;[\s\S]*if \(!suppressSyntheticMacroCall[\s\S]*&&\s*!logicalRepeatInput[\s\S]*&&\s*!replaying[\s\S]*&&\s*!turboDispatching\)[\s\S]*Cancel\(CancelReason\.Replaced,' `
    'A current asynchronous Macro repeat tail can enter generic newer-input cancellation.'

# Detect-only mode may observe, but it may not inject, claim queue ownership, or
# clear ActionQueued. Macro mode is never converted by the repeat path.
$nativeRepeatSettings = Get-MethodBlock $runtime `
    'private NativeHotbarRepeatSettings GetNativeHotbarRepeatSettings\(\)' `
    'GetNativeHotbarRepeatSettings'
$canReplaceOwnedQueues = Get-MethodBlock $runtime `
    'private bool CanReplaceOwnedQueuesForNewestInput\(\)' `
    'CanReplaceOwnedQueuesForNewestInput'
$requiresNativeInputRelease = Get-MethodBlock $runtime `
    'private static bool RequiresNativeInputRelease\([^\)]*\)' `
    'RequiresNativeInputRelease'
Assert-Contains $nativeRepeatSettings 'var featureEnabled\s*=\s*pluginOperational[\s\S]*&&\s*configuration\.TurboEnabled[\s\S]*&&\s*!configuration\.DryRun' `
    'Dry-run can still inject native held-input repeats.'
Assert-Contains $tryCreateLogicalRepeatQueue 'configuration\.DryRun[\s\S]*return null;' `
    'Dry-run can still create repeat-native queue provenance.'
Assert-Contains $processLogicalRepeatQueue 'configuration\.DryRun[\s\S]*logicalRepeatQueueOwnership\.Clear\(\);[\s\S]*return;' `
    'Dry-run can retain or claim repeat-native queue provenance.'
Assert-Contains $replaceLogicalRepeatQueue 'if \(actionManager\s*==\s*null\s*\|\|\s*configuration\.DryRun\)\s*return false;[\s\S]*actionManager->ActionQueued\s*=\s*false;' `
    'Dry-run does not guard the repeat-owned ActionQueued mutation.'
Assert-Contains $canReplaceOwnedQueues 'configuration\.Enabled[\s\S]*&&\s*!configuration\.DryRun[\s\S]*&&\s*!faulted[\s\S]*&&\s*!disposed' `
    'Newest-input native queue replacement is not disabled in dry-run.'
Assert-NotContains $requiresNativeInputRelease 'CancelReason\.TargetChange' `
    'Target changes still release-gate native held-input cadence and can break ReAction Auto Target combos.'
Assert-Contains $physicalPreemption 'if \(configuration\.DryRun\)[\s\S]*AbandonOwnedQueueProvenanceForDetectOnly\(\);[\s\S]*else if \(CanReplaceOwnedQueuesForNewestInput\(\)\s*&&\s*actionManager\s*!=\s*null\)[\s\S]*TryReplaceLogicalRepeatNativeQueue\(' `
    'The physical edge can mutate repeat-owned queue state before the dry-run gate.'

# Macro repeat remains arbitrary native macro execution. No repeat-path parser,
# transcript, action whitelist, mode conversion or target selector may be introduced.
Assert-NotContains ($physicalInput + "`n" + $logicalRepeat) 'MacroSafetyAnalyzer|MacroTurboTranscript|TryReadSafeMacroProfile|ActionCommands|HarmlessMetadataCommands' `
    'The native repeat path parses or whitelists macro content.'
Assert-Contains $useAction 'var nativeMode\s*=\s*mode;' `
    'The native repeat path no longer preserves FFXIV Macro mode exactly.'
Assert-NotContains $useAction 'var nativeMode\s*=\s*mode\s*==\s*ActionManager\.UseActionMode\.Macro' `
    'The native repeat path still converts Macro mode.'

# ---------------------------------------------------------------------------
# ReAction coexistence is feature-granular. Its pulses and Macro Queue mode are
# observed capabilities, while unknown/unreadable ReAction never globally
# suspends the same-input fallback. NoClippy and MOAction keep separate rules.
# ---------------------------------------------------------------------------

Assert-Contains $compatibility 'ReActionTurboHotbarsEnabled\s*\{\s*get;\s*init;\s*\}' `
    'ReAction Turbo capability is absent from the compatibility assessment.'
Assert-Contains $compatibility 'ReActionMacroQueueEnabled\s*\{\s*get;\s*init;\s*\}' `
    'ReAction Macro Queue capability is absent from the compatibility assessment.'
Assert-Contains $compatibility 'TryReadBoolean\([^\)]*[\s\S]*"EnableTurboHotbars"' `
    'ReAction Turbo Hotbars capability is not inspected.'
Assert-Contains $compatibility 'TryReadBoolean\([^\)]*[\s\S]*"EnableMacroQueue"' `
    'ReAction Macro Queue capability is not inspected.'

$assessReActionPlugins = Get-MethodBlock $compatibility `
    'private ReActionCompatibilityState AssessReActionPlugins\([^\)]*\)' `
    'AssessReActionPlugins'
$assessReAction = Get-MethodBlock $compatibility `
    'private ReActionCompatibilityState AssessReAction\([^\)]*\)' `
    'AssessReAction'
Assert-NotContains ($assessReActionPlugins + "`n" + $assessReAction) 'conflicts\.Add\s*\(' `
    'Supported, duplicate, unknown or unreadable ReAction is still a global conflict.'
Assert-Contains $assessReActionPlugins 'matches\.Length\s*>\s*1[\s\S]*integrations\.Add\([\s\S]*PulseQueue remains available[\s\S]*LoadedUnknown' `
    'Duplicate ReAction instances no longer remain available with unknown capabilities.'
Assert-Contains $assessReAction 'plugin\.Version\s*!=\s*SupportedReActionVersion[\s\S]*integrations\.Add\([\s\S]*not audited; feature capabilities unknown, PulseQueue remains available[\s\S]*LoadedUnknown' `
    'An unknown ReAction version can globally suspend PulseQueue.'
Assert-Contains $assessReAction '!TryReadReActionConfiguration\([\s\S]*integrations\.Add\([\s\S]*settings unreadable; feature capabilities unknown, PulseQueue remains available[\s\S]*LoadedUnknown' `
    'Unreadable ReAction settings can globally suspend PulseQueue.'
Assert-Contains $assessReAction 'ReAction features are capabilities, not global compatibility gates[\s\S]*integrations\.Add\([\s\S]*Turbo Hotbars=[\s\S]*Macro Queue=[\s\S]*new ReActionCompatibilityState\([\s\S]*TurboHotbarsEnabled:[\s\S]*MacroQueueEnabled:' `
    'Audited ReAction features are not published as feature capabilities.'
Assert-Contains $compatibility 'WeakReference<object>' `
    'The live ReAction capability guard strongly retains the foreign plugin.'

# Runtime records the audited ReAction capabilities for telemetry and exact
# pulse provenance, but PulseQueue always retains its own fallback cadence.
Assert-Contains $runtime 'reActionTurboHotbarsEnabled\s*=\s*assessment\.ReActionAudited[\s\S]*&&\s*assessment\.ReActionTurboHotbarsEnabled;' `
    'Runtime does not require an audited active ReAction Turbo capability before classifying external pulses.'
Assert-Contains $runtime 'reActionMacroQueueEnabled\s*=\s*assessment\.ReActionAudited[\s\S]*&&\s*assessment\.ReActionMacroQueueEnabled;' `
    'Runtime does not require an audited active ReAction Macro Queue capability before deferring.'
Assert-Contains $runtime 'ExternalRepeatOwnerActive:\s*reActionRepeatActive' `
    'ReAction pulse provenance is not passed to the logical input arbiter.'
Assert-Contains $runtime 'MaximumFrameGapMilliseconds\s*=\s*1_000' `
    'Native-input ownership is still cancelled by an overly narrow frame-gap threshold.'

# Global timing/mutation guardrails. Repetition is driven only by native binding
# scans; there is no background timer and testing builds stay testing-exclusive.
$nativeTurboSources = $logicalRepeat + "`n" + $physicalInput + "`n" + $configuration
Assert-NotContains $nativeTurboSources '\b(?:System\.Threading\.Timer|System\.Timers\.Timer|PeriodicTimer|Thread\.Sleep|Task\.Delay)\b|(?m)\b(?:Sleep|Delay)\s*\(' `
    'Native held-input Turbo contains a timer, sleep or background scheduler.'

if (-not [bool]$manifest.IsTestingExclusive) {
    throw 'The native-input release must remain testing-exclusive.'
}

Write-Host 'PulseQueue native-input and one-shot safety-contract checks passed.'
