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
$macroBudgetPath = Join-Path $RepositoryRoot 'src/PulseQueue.Core/MacroTurboTranscript.cs'
$physicalInputPath = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/Services/PhysicalHotbarInputSource.cs'
$configurationPath = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/Models/PluginConfiguration.cs'
$pluginPath = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/Plugin.cs'
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
$macroBudget = Get-Content -LiteralPath $macroBudgetPath -Raw
$physicalInput = Get-Content -LiteralPath $physicalInputPath -Raw
$configuration = Get-Content -LiteralPath $configurationPath -Raw
$plugin = Get-Content -LiteralPath $pluginPath -Raw
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
if ($actionQueuedWrites -ne 2) {
    throw "Expected exactly the newer-input preemption and terminal-safety exact-owned ActionQueued clears; found $actionQueuedWrites writes."
}
Assert-Contains $runtime 'private bool TryReplaceOwnedNativeQueue\([\s\S]*?TryTakeForNewerInput\([\s\S]*?SynchronizeOwnedNativeQueueSafetyContext\(\);[\s\S]*?actionManager->ActionQueued\s*=\s*false;' 'Newest-input native queue preemption is not protected by exact certified ownership or synchronized semantic provenance.'
Assert-Contains $runtime 'private bool RetryExactOwnedNativeQueueSafetyClear\([\s\S]*ownedNativeQueueSafetyClearThroughGeneration\s*<=\s*0[\s\S]*nativeQueueOwnership\.HasOwnership[\s\S]*current\.IsQueued[\s\S]*TryTakeExactCurrent\(\s*ownedNativeQueueSafetyClearThroughGeneration,\s*actionManager->LastUsedActionSequence,\s*current,\s*out var cleared\)[\s\S]*SynchronizeOwnedNativeQueueSafetyContext\(\);[\s\S]*actionManager->ActionQueued\s*=\s*false;' 'Terminal safety cancellation can clear a foreign/changed/newer-generation native queue without consuming exact generation-bounded ownership and its sidecar.'
if ($runtime -match 'nativeQueueOwnership\.Clear\(\)') {
    throw 'Runtime drops native queue ownership proof without exact take, drain authorization, or stable reconciliation.'
}
Assert-Contains $nativeOwnership 'public bool TryTakeExactCurrent\(\s*long maximumGeneration,[\s\S]*maximumGeneration\s*<=\s*0[\s\S]*!current\.IsQueued[\s\S]*value\.Generation\s*>\s*maximumGeneration[\s\S]*return false;[\s\S]*replaceable\s*=\s*value\.Snapshot;[\s\S]*owned\s*=\s*null;' 'Terminal exact-take does not preserve a newer-generation owner or exact hidden queue.'
Assert-Contains $runtime 'private void RequestExactOwnedNativeQueueSafetyClear\([\s\S]*RequestExactOwnedNativeQueueSafetyClearThrough\(\s*inputGenerations\.Current,\s*phase\);' 'Global terminal cancellation no longer snapshots its current generation cutoff.'
Assert-Contains $runtime 'private void RequestExactOwnedNativeQueueSafetyClearThrough\([\s\S]*maximumGeneration\s*<=\s*0[\s\S]*ownedNativeQueueSafetyClearPending\s*=\s*true;[\s\S]*ownedNativeQueueSafetyClearThroughGeneration\s*=\s*Math\.Max\(\s*ownedNativeQueueSafetyClearThroughGeneration,\s*maximumGeneration\)' 'Terminal queue-clear intent is not retained with an explicit monotonic generation cutoff across in-flight/asynchronous outcomes.'
if ([regex]::Matches($runtime, 'ownedNativeQueueSafetyClearPending\s*=\s*false').Count -ne 0) {
    throw 'Terminal exact-clear intent is retired without a generation-bound proof that later stale outcomes cannot appear.'
}
Assert-Contains $runtime 'private void PrepareCertifiedDirectQueueReplacement\([\s\S]*scope\.CertifiedPress\s+is\s+not\s+\{\s*\}\s+press[\s\S]*CommandType:\s*DirectActionHotbarSlotType[\s\S]*!configuration\.Enabled[\s\S]*configuration\.DryRun[\s\S]*!compatibility\.IsLiveReActionProfileCurrent\(\)[\s\S]*ArmCertifiedOwnedQueueReplacement\(' 'A newer direct root can mutate the queue while uncertified, disabled, dry-run, or compatibility-stale.'
$directReplacementMatch = [regex]::Match($runtime, 'private void PrepareCertifiedDirectQueueReplacement\([\s\S]*?(?=\r?\n\s*private void PrepareCertifiedMacroInput\()')
if (-not $directReplacementMatch.Success -or $directReplacementMatch.Value -match 'GetActionStatus|GetTemporalRemainingMilliseconds|TryCreateCandidate|UseActionDetour|IsStillHeld') {
    throw 'Certified direct newest-input replacement incorrectly depends on readiness, continuing hold, or UseAction candidate capture.'
}
if ($directReplacementMatch.Value -match 'IsSafeSnapshot') {
    throw 'Certified direct newest-input replacement is incorrectly blocked by Stunned/BeingMoved safety before Purify or Guard can replace an older exact owned queue.'
}
Assert-Contains $runtime 'private void PrepareCertifiedMacroInput\([\s\S]*TryReadSafeMacroProfile\(slotIdentity,\s*out var profile[\s\S]*ArmCertifiedOwnedQueueReplacement\(\s*scope,[\s\S]*if \(!IsSafeSnapshot\(snapshot\)\)[\s\S]*return;[\s\S]*scope\.MacroProfileAtPress\s*=\s*profile;[\s\S]*scope\.MacroExecutionBudget\s*=\s*new MacroTurboExecutionBudget\(profile\.ActionCount\);' 'A Macro root does not separate action-only queue priority from safe physical-outcome ownership and Turbo certification.'
$macroReplacementMatch = [regex]::Match($runtime, 'private void PrepareCertifiedMacroInput\([\s\S]*?(?=\r?\n\s*private void ArmCertifiedOwnedQueueReplacement\()')
if (-not $macroReplacementMatch.Success -or $macroReplacementMatch.Value -match 'TurboEnabled|TurboMacrosEnabled|TurboOutOfCombat|IsStillHeld') {
    throw 'Certified action-only Macro newest-input replacement incorrectly depends on Turbo opt-in, combat policy, or continuing hold.'
}
Assert-Contains $runtime 'private void ArmCertifiedOwnedQueueReplacement\([\s\S]*scope\.MaySupersedeOwnedQueue\s*=\s*true;[\s\S]*latestCertifiedQueueReplacementGeneration\s*=\s*scope\.Generation;[\s\S]*TryReplaceOwnedNativeQueue\(actionManager,\s*scope\.Generation,\s*phase\)' 'Certified direct/action-only Macro replacement does not retain a generation tombstone and attempt exact pre-execution takeover.'
Assert-Contains $runtime 'private bool TryReplaceOwnedNativeQueue\([\s\S]*current\.IsQueued[\s\S]*nativeQueueOwnership\.TryTakeForNewerInput\(\s*replacingGeneration,\s*actionManager->LastUsedActionSequence,\s*current,\s*out var replaced\)[\s\S]*SynchronizeOwnedNativeQueueSafetyContext\(\);[\s\S]*actionManager->ActionQueued\s*=\s*false;' 'Newest-input preemption can clear a foreign/changed queue, bypass exact certified ownership, or retain stale provenance.'

# Every claimed queue has a semantic sidecar that remains independently watched
# after BufferEngine/Turbo ownership ends. Core ownership mutation is centralized
# so a mismatch that returns false cannot leave stale sidecar authority behind.
if ([regex]::Matches($runtime, 'nativeQueueOwnership\.TryClaimNewQueue\(').Count -ne 1) {
    throw 'Native queue claims bypass the single sidecar-publishing wrapper.'
}
if ([regex]::Matches($runtime, 'nativeQueueOwnership\.TryAuthorizeExactDrain\(').Count -ne 0) {
    throw 'Runtime consumes native queue ownership before the authoritative drain call returns.'
}
if ([regex]::Matches($runtime, 'nativeQueueOwnership\.TryBeginExactDrain\(').Count -ne 1 -or
    [regex]::Matches($runtime, 'nativeQueueOwnership\.CompleteExactDrain\(').Count -ne 1) {
    throw 'Native queue drains bypass the single two-phase sidecar-synchronizing lease path.'
}
if ([regex]::Matches($runtime, 'nativeQueueOwnership\.Reconcile\(').Count -ne 1) {
    throw 'Native queue reconciliation bypasses the single sidecar-synchronizing wrapper.'
}
Assert-Contains $runtime 'private bool TryClaimOwnedNativeQueue\([\s\S]*nativeQueueOwnership\.TryClaimNewQueue\([\s\S]*ownedNativeQueueSafetyContext\s*=\s*new OwnedNativeQueueSafetyContext\(\s*generation,\s*attempted,\s*safetySeed\.RootSnapshot,\s*safetySeed\.InvocationSnapshot,\s*safetySeed\.IncludeResolverTargets,\s*safetySeed\.ExplicitTargetAddress\);[\s\S]*ownedNativeQueueSafetyClearPending[\s\S]*RetryExactOwnedNativeQueueSafetyClear\([\s\S]*EnforceOwnedNativeQueueSafety\(' 'A successful exact claim can exist without publishing full semantic provenance before terminal retry/watch enforcement.'
Assert-Contains $runtime 'private bool TryBeginOwnedNativeQueueDrain\([\s\S]*nativeQueueOwnership\.TryBeginExactDrain\([\s\S]*out lease\);[\s\S]*SynchronizeOwnedNativeQueueSafetyContext\(\);[\s\S]*return leased;' 'Exact drain lease creation or mismatch can consume ownership early or leave stale semantic provenance.'
Assert-Contains $runtime 'private void ProcessOwnedNativeQueueDrainOutcome\([\s\S]*nativeQueueOwnership\.CompleteExactDrain\([\s\S]*SynchronizeOwnedNativeQueueSafetyContext\(\);[\s\S]*OwnershipRetained[\s\S]*OwnedQueueTuple\s*=\s*null;[\s\S]*ownedNativeQueueSafetyClearPending[\s\S]*RetryExactOwnedNativeQueueSafetyClear\([\s\S]*latestCertifiedQueueReplacementGeneration\s*>\s*attempt\.Generation[\s\S]*TryReplaceOwnedNativeQueue\(' 'Post-call drain finalization cannot retain a restored exact queue or retry terminal/newer-input tombstones safely.'
Assert-Contains $nativeOwnership 'public bool TryBeginExactDrain\([\s\S]*activeDrainLease\s+is\s+not\s+null[\s\S]*!current\.IsQueued[\s\S]*return false;[\s\S]*current\.Equals\(value\.Snapshot\)[\s\S]*attempted\s*!=\s*value\.Attempted[\s\S]*activeDrainLease\s*=\s*new ActiveDrainLease' 'Core exact drain lease is re-entrant, destroys temporarily hidden ownership, or does not prove the complete exact owner.'
Assert-Contains $nativeOwnership 'public NativeQueueDrainFinalizeResult CompleteExactDrain\([\s\S]*active\.LeaseId\s*!=\s*lease\.LeaseId[\s\S]*activeDrainLease\s*=\s*null;[\s\S]*ReferenceEquals\(value,\s*active\.Owner\)[\s\S]*current\.Equals\(value\.Snapshot\)[\s\S]*OwnershipRetained[\s\S]*owned\s*=\s*null;[\s\S]*OwnershipInvalidated[\s\S]*OwnershipConsumed' 'Core post-call drain finalization can be forged, duplicated, or lose a restored exact queue.'
Assert-Contains $nativeOwnership 'public bool CanDeferExactHiddenDrain\([\s\S]*activeDrainLease\s+is\s+null[\s\S]*generation\s*==\s*value\.Generation[\s\S]*attempted\s*==\s*value\.Attempted' 'Opposite ReAction hook order cannot attribute a hidden exact drain without permitting overlap.'
Assert-Contains $runtime 'private void ReconcileOwnedNativeQueue\([\s\S]*nativeQueueOwnership\.Reconcile\([\s\S]*SynchronizeOwnedNativeQueueSafetyContext\(\);' 'Stable queue reconciliation can leave stale semantic provenance.'
Assert-Contains $runtime 'private void SynchronizeOwnedNativeQueueSafetyContext\([\s\S]*!nativeQueueOwnership\.HasOwnership[\s\S]*ownedNativeQueueSafetyContext\s*=\s*null;' 'The semantic sidecar can outlive exact core ownership.'
Assert-Contains $runtime 'private bool EnforceOwnedNativeQueueSafety\([\s\S]*RequestExactOwnedNativeQueueSafetyClearThrough\(\s*context\.Generation,[\s\S]*RetryExactOwnedNativeQueueSafetyClear\([\s\S]*inputGenerations\.Current\s*==\s*context\.Generation' 'Owner-specific semantic cancellation can widen its cutoff to an unrelated newer generation or fail to cancel same-generation scheduling.'
Assert-Contains $runtime 'private CancelReason GetOwnedNativeQueueSafetyFailure\([\s\S]*!clientState\.IsLoggedIn[\s\S]*IsDead:\s*true[\s\S]*Unconscious[\s\S]*IsMounted[\s\S]*IsStunned[\s\S]*IsBeingMoved[\s\S]*TerritoryId[\s\S]*ContextFingerprint[\s\S]*HardTargetId[\s\S]*SoftTargetId[\s\S]*MouseOverTargetId[\s\S]*MouseOverNameplateTargetId[\s\S]*ExplicitTargetAddress' 'Standalone owned queues are not watched across every hard player/context/target/resolver transition.'
$ownedQueueSafetyMatch = [regex]::Match($runtime, 'private CancelReason GetOwnedNativeQueueSafetyFailure\([\s\S]*?(?=\r?\n\s*private Snapshot\? CaptureDirectSnapshotAtPress\()')
if (-not $ownedQueueSafetyMatch.Success -or $ownedQueueSafetyMatch.Value -match 'GetAdjustedActionId') {
    throw 'Standalone accepted native queue safety incorrectly re-resolves combo/proc actions instead of trusting the unchanged exact vanilla queue tuple.'
}
Assert-Contains $runtime 'private void OnFrameworkUpdate\([\s\S]*ReconcileOwnedNativeQueue\([\s\S]*EnforceOwnedNativeQueueSafety\(\s*observedActionManager,\s*frameGap,[\s\S]*if \(engine\.Pending\s+is\s+null\)' 'Standalone exact queue safety is not enforced before the no-buffer/Turbo framework early return.'
Assert-Contains $runtime 'else\s*\{\s*lock \(dispatchGate\)[\s\S]*ReconcileOwnedNativeQueue\(0,\s*NativeQueueSnapshot\.Empty\);[\s\S]*\}[\s\S]*if \(compatibilityQuarantineFrames' 'Queue provenance can survive ActionManager/logout teardown into another session.'

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
if ($originalCalls -ne 2) {
    throw "Expected exactly the conditional native pass-through and one exact one-shot queue replay; direct and Macro Turbo must use same-slot dispatch. Found $originalCalls."
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
Assert-Contains $compatibility 'TryReadBoolean\([^\)]*[\s\S]*?"EnableMacroQueue"' 'ReAction Macro Queue is not inspected.'
Assert-Contains $compatibility 'TryReadBoolean\([^\)]*[\s\S]*?"EnableAutoDismount"' 'ReAction Auto Dismount is not inspected.'
Assert-Contains $compatibility 'TryReadBoolean\([^\)]*[\s\S]*?"EnableCameraRelativeDirectionals"' 'ReAction Camera Relative Directionals are not inspected.'
Assert-Contains $compatibility 'configuration\.ActionStackCount\s*!=\s*0[\s\S]*conflicts\.Add' 'Non-empty ReAction Action Stacks no longer suspend buffering.'
Assert-Contains $compatibility 'configuration\.AutoTargetEnabled[\s\S]*conflicts\.Add' 'ReAction Auto Target no longer suspends buffering.'
Assert-Contains $compatibility 'configuration\.TurboHotbarsEnabled[\s\S]*conflicts\.Add' 'ReAction Turbo Hotbars no longer suspend buffering.'
Assert-Contains $compatibility 'configuration\.MacroQueueEnabled[\s\S]*conflicts\.Add' 'ReAction Macro Queue no longer suspends buffering.'
Assert-Contains $compatibility 'record struct ReActionConfigurationSnapshot\([\s\S]*bool MacroQueueEnabled,[\s\S]*bool AutoDismountEnabled,[\s\S]*bool CameraRelativeDirectionalsEnabled' 'The complete audited ReAction settings are missing from the live configuration snapshot.'
Assert-Contains $compatibility 'TryReadReActionConfigurationObject\(configuration,\s*out var current\)[\s\S]*current\s*==\s*expected\.Value' 'The live ReAction guard no longer compares the complete audited snapshot.'
Assert-Contains $compatibility 'if \(configuration\.AutoDismountEnabled\)[\s\S]*integrations\.Add\("ReAction Auto Dismount \(mounted inputs passed through, never owned\)"\)' 'ReAction Auto Dismount is not retained as an allowed, visible integration.'
Assert-Contains $compatibility 'if \(configuration\.CameraRelativeDirectionalsEnabled\)[\s\S]*integrations\.Add\("ReAction Camera Relative Directionals \(movement actions excluded\)"\)' 'ReAction Camera Relative Directionals are not retained as an allowed, visible integration.'
$assessReActionMatch = [regex]::Match(
    $compatibility,
    'private void AssessReAction\([\s\S]*?(?=\r?\n\s*private bool TryReadReActionConfiguration\()')
if (-not $assessReActionMatch.Success) {
    throw 'The audited ReAction assessment method could not be isolated.'
}
$assessReAction = $assessReActionMatch.Value
if ($assessReAction -match 'configuration\.(?:AutoDismountEnabled|CameraRelativeDirectionalsEnabled)[\s\S]{0,240}?conflicts\.Add') {
    throw 'ReAction Auto Dismount or Camera Relative Directionals incorrectly became a global compatibility conflict.'
}
Assert-Contains $compatibility 'WeakReference<object>' 'The lightweight ReAction guard must not retain the foreign plugin.'
Assert-CountAtLeast $runtime 'compatibility\.IsLiveReActionProfileCurrent\(\)' 3 'Live ReAction safety fields are not checked at capture, outcome, and final dispatch.'

# Compatibility failures must be visible in chat, deduplicated while unchanged,
# and followed by a recovery notice when the blockers clear.
Assert-Contains $plugin 'actionBuffer\.Start\(\);\s*ReportCompatibilityState\(\);' 'Initial compatibility conflicts are not reported after runtime startup.'
Assert-Contains $plugin 'private void Draw\(\)[\s\S]*ReportCompatibilityState\(\);[\s\S]*windowSystem\.Draw\(\);' 'Compatibility changes are not surfaced from the UI/framework draw loop.'
$conflictReportMatch = [regex]::Match(
    $plugin,
    'private void ReportCompatibilityState\(\)[\s\S]*?(?=\r?\n\s*private void OpenSettings\()')
if (-not $conflictReportMatch.Success) {
    throw 'The compatibility chat reporter could not be isolated.'
}
$conflictReport = $conflictReportMatch.Value
Assert-Contains $conflictReport 'string\.Join\("\\n",\s*conflicts\)[\s\S]*string\.Equals\(signature,\s*reportedConflictSignature,\s*StringComparison\.Ordinal\)\)\s*return;' 'Unchanged compatibility conflicts can spam chat or lose exact blocker identity.'
Assert-Contains $conflictReport 'conflicts\.Count\s*>\s*0[\s\S]*chatGui\.PrintError\([\s\S]*PAUSED by compatibility settings:[\s\S]*string\.Join\("; ",\s*conflicts\)[\s\S]*exact blockers' 'Compatibility suspension no longer prints the concrete blockers to chat.'
Assert-Contains $conflictReport 'else if \(previouslyBlocked\)[\s\S]*chatGui\.Print\("\[PulseQueue\] Compatibility blockers cleared\.' 'Compatibility recovery is not visible in chat.'

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
Assert-Contains $runtime 'if \(!nativeHotbarInput\)[\s\S]*IsOwnedMacroTurboQueueDrain\([\s\S]*out var macroDrainAttempt\);[\s\S]*nativeQueueDrainAttempt\s*=\s*macroDrainAttempt;[\s\S]*ownedMacroExecution\s*=\s*!ownedMacroQueueDrain[\s\S]*out observedOwnedMacroContinuation\)[\s\S]*TryObserveRetiredPhysicalMacroQueueAttempt\([\s\S]*var ownedTurboQueueDrain\s*=\s*false;[\s\S]*ownedTurboQueueDrain\s*=\s*IsOwnedTurboActionContinuation\([\s\S]*out var directDrainAttempt\);[\s\S]*nativeQueueDrainAttempt\s*=\s*directDrainAttempt;[\s\S]*!ownedMacroQueueDrain[\s\S]*!ownedMacroExecution[\s\S]*!observedOwnedMacroContinuation[\s\S]*!retiredMacroObserved[\s\S]*!suppressSyntheticContinuation[\s\S]*!ownedTurboQueueDrain[\s\S]*Cancel\(CancelReason\.Replaced' 'Independent native action invocations can cancel newer work despite a leased/hidden exact drain, owned executor, attributable denied old Macro tail, or retired observer.'
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
$exactDrainRuntimeMatch = [regex]::Match(
    $runtime,
    'private bool IsOwnedTurboActionContinuation\([\s\S]*?(?=\r?\n\s*private static bool IsMacroExecutionActive\()')
if (-not $exactDrainRuntimeMatch.Success) {
    throw 'The audited exact native queue-drain authorization method could not be isolated.'
}
$exactDrainRuntime = $exactDrainRuntimeMatch.Value
Assert-Contains $exactDrainRuntime 'var currentQueue\s*=\s*CaptureNativeQueue\(actionManager\);[\s\S]*var ownedQueueTuple\s*=\s*currentQueue\.IsQueued\s*\?\s*ownedTuple\s+with\s*\{[\s\S]*Mode\s*=\s*currentQueue\.Mode,[\s\S]*\}\s*:\s*ownedTuple;' 'Queue-drain ownership no longer uses the exact per-pulse owned tuple, preserves its stored QueueType while hidden, or normalizes the visible native QueueType.'
Assert-Contains $exactDrainRuntime 'mode\s*==\s*ActionManager\.UseActionMode\.Queue[\s\S]*exactInvocation[\s\S]*!currentQueue\.IsQueued[\s\S]*CanDeferExactHiddenDrain\(\s*runtime\.Candidate\.InputGeneration,\s*ownedQueueTuple\);[\s\S]*TryBeginOwnedNativeQueueDrain\(\s*runtime\.Candidate\.InputGeneration,\s*actionManager->LastUsedActionSequence,\s*currentQueue,\s*ownedQueueTuple,\s*out lease\);[\s\S]*new NativeQueueDrainAttempt\(\s*lease,\s*runtime\.Candidate\.InputGeneration,\s*null,\s*runtime\);[\s\S]*return authorized;' 'A direct queue drain can bypass exact invocation identity, overlap a hidden lease, or consume ownership before post-call finalization.'
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
# slots are eligible. Direct slots may either yield one physical Action/PvPAction
# invocation or zero physical invocations; the latter is armed from the certified
# base slot and still must yield exactly one live action during each repeated
# same-slot call. PvPCombo, items, movement, controller/mouse provenance, and
# arbitrary side-effect macros remain ordinary vanilla input.
Assert-Contains $runtime 'DirectActionHotbarSlotType\s*=\s*1\s*;' 'The audited direct Action slot type changed.'
Assert-Contains $runtime 'MacroHotbarSlotType\s*=\s*7\s*;' 'The audited Macro slot type changed.'
Assert-Contains $runtime 'commandType\s+is\s+not\s+\(DirectActionHotbarSlotType\s+or\s+MacroHotbarSlotType\)[\s\S]*return null' 'A slot outside the audited direct Action or Macro types can enter Turbo provenance.'
Assert-Contains $runtime 'if \(slotIdentity\.CommandType\s*==\s*MacroHotbarSlotType\)[\s\S]*TryBeginMacroTurbo\(scope,\s*press,\s*slotIdentity,\s*inputSource\);[\s\S]*return;[\s\S]*slotIdentity\.CommandType\s*!=\s*DirectActionHotbarSlotType' 'Direct Action and separately verified Macro starts are no longer routed through distinct gates.'
Assert-Contains $runtime 'candidate\.InputGeneration\s*!=\s*scope\.Generation[\s\S]*slotIdentity\.CommandId\s*!=\s*candidate\.RequestedActionId' 'Direct Action Turbo start no longer proves the exact slot command ID and input generation.'
Assert-Contains $runtime 'actionType\s+is\s+not\s+\(ActionType\.Action\s+or\s+ActionType\.PvPAction\)[\s\S]*?return null' 'A non-Action/PvPAction invocation can become a Turbo candidate.'
Assert-Contains $runtime 'inputScope\.ActionInvocationCount\+\+;[\s\S]*?ActionInvocationCount\s*>\s*1[\s\S]*?TurboCandidate\s*=\s*null;[\s\S]*?TurboDisqualified\s*=\s*true' 'A direct hotbar slot with multiple action invocations is not disqualified from exact-action Turbo.'
Assert-Contains $runtime 'scope\.TurboDisqualified\s*\|\|\s*scope\.ActionInvocationCount\s*>\s*1[\s\S]*return;' 'A disqualified or multi-action direct slot can start same-slot Turbo.'
Assert-Contains $runtime 'candidate\s+is\s+null[\s\S]*scope\.ActionInvocationCount\s*==\s*0[\s\S]*TryCreateDirectTurboCandidate\(scope,\s*slotIdentity,\s*out candidate,\s*out var failure\)[\s\S]*return;' 'A certified direct slot that emits zero physical UseAction calls cannot arm same-slot Turbo from its base command.'
Assert-Contains $runtime 'candidate\s+is\s+null\s*\|\|\s*scope\.ActionInvocationCount\s+is\s+not\s+\(0\s+or\s+1\)' 'Direct same-slot Turbo no longer restricts arming to zero or one physical action invocation.'
Assert-Contains $runtime 'private bool TryCreateDirectTurboCandidate\([\s\S]*slotIdentity\.CommandId\s*==\s*0[\s\S]*GetAdjustedActionId\(slotIdentity\.CommandId\)[\s\S]*TryGetEligibleActionProfile\([\s\S]*IsLiveMOActionUnowned\(slotIdentity\.CommandId,\s*resolvedActionId\)[\s\S]*new ExactActionTuple\([\s\S]*ActionManager\.UseActionMode\.None' 'Zero-call direct arming no longer derives an eligible, unowned Action candidate from the certified base slot.'
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

# The statically analyzed ActionCount is a per-execution 0..N native-call
# budget, not an immutable action transcript. Every live-validated action must
# reserve one call before Original; at most one exact accepted outcome may be
# marked, and any N+1 or post-acceptance action terminally closes the budget.
Assert-Contains $macroBudget 'public sealed class MacroTurboExecutionBudget[\s\S]*MacroTurboExecutionBudget\(int maxActionCalls\)[\s\S]*maxActionCalls\s*<=\s*0[\s\S]*this\.maxActionCalls\s*=\s*maxActionCalls;' 'Macro execution budgets no longer require the analyzed positive ActionCount maximum.'
foreach ($property in @('MaxActionCalls', 'ObservedActionCalls', 'AcceptedOutcomeCount', 'IsTerminal', 'TerminalResult')) {
    Assert-Contains $macroBudget ("public [^;\r\n]+ " + [regex]::Escape($property) + '\s*=>') "Macro execution budget is missing terminal API property $property."
}
$budgetObserveMatch = [regex]::Match(
    $macroBudget,
    'public MacroTurboActionObservationResult ObserveAction\(\)[\s\S]*?(?=\r?\n\s*/// <summary>\r?\n\s*/// Marks)')
if (-not $budgetObserveMatch.Success) { throw 'Macro execution budget ObserveAction could not be isolated.' }
$budgetObserve = $budgetObserveMatch.Value
Assert-Contains $budgetObserve 'terminalResult\s+is\s+not\s+null[\s\S]*MacroTurboActionObservationResult\.Closed' 'A terminal Macro budget can authorize another native call.'
Assert-Contains $budgetObserve 'acceptedOutcomeCount\s*!=\s*0[\s\S]*terminalResult\s*=\s*MacroTurboExecutionBudgetResult\.ActionAfterAcceptedOutcome;[\s\S]*MacroTurboActionObservationResult\.AcceptedOutcomeAlreadyMarked' 'A macro tail after one accepted outcome is not terminally blocked before Original.'
Assert-Contains $budgetObserve 'observedActionCalls\s*>=\s*maxActionCalls[\s\S]*terminalResult\s*=\s*MacroTurboExecutionBudgetResult\.ActionLimitExceeded;[\s\S]*MacroTurboActionObservationResult\.ActionLimitExceeded' 'The N+1 macro action is not terminally blocked before Original.'
Assert-Contains $budgetObserve 'observedActionCalls\+\+;\s*return MacroTurboActionObservationResult\.Allowed;' 'An allowed Macro action is not reserved exactly once before Original.'
$budgetMarkMatch = [regex]::Match(
    $macroBudget,
    'public MacroTurboAcceptedOutcomeMarkResult MarkAcceptedOutcome\(\)[\s\S]*?(?=\r?\n\s*/// <summary>\r?\n\s*/// Closes)')
if (-not $budgetMarkMatch.Success) { throw 'Macro execution budget MarkAcceptedOutcome could not be isolated.' }
$budgetMark = $budgetMarkMatch.Value
Assert-Contains $budgetMark 'observedActionCalls\s*==\s*0[\s\S]*AcceptedOutcomeWithoutAction[\s\S]*NoObservedAction' 'An accepted Macro outcome can be marked without a preceding observed action.'
Assert-Contains $budgetMark 'acceptedOutcomeCount\s*!=\s*0[\s\S]*MultipleAcceptedOutcomes[\s\S]*AlreadyMarked' 'A second accepted Macro outcome is not terminally rejected.'
Assert-Contains $budgetMark 'acceptedOutcomeCount\s*=\s*1;\s*return MacroTurboAcceptedOutcomeMarkResult\.Marked;' 'The one accepted Macro outcome is not represented exactly.'
Assert-Contains $macroBudget 'public MacroTurboExecutionBudgetResult Finish\(\)[\s\S]*terminalResult\s+is\s+\{\s*\}\s+terminal[\s\S]*return terminal;[\s\S]*terminalResult\s*=\s*MacroTurboExecutionBudgetResult\.Complete;[\s\S]*return terminalResult\.Value;' 'Finish no longer preserves invalid terminal results or accepts bounded 0..N executions, including zero accepted outcomes.'
if ($macroBudget -match 'MacroTurboTranscriptEntry|MacroTurboTranscriptBuilder|MacroTurboExecutionCursor') {
    throw 'The removed immutable Macro transcript model re-entered the execution budget.'
}

Assert-Contains $runtime 'private static bool IsMacroExecutionActive\(\)[\s\S]*shell->MacroLocked' 'Macro capture is no longer guarded by the native MacroLocked state.'
Assert-Contains $runtime 'private void BeginHotbarInput\([\s\S]*MacroWasLockedBeforeExecution\s*=\s*IsMacroExecutionActive\(\)[\s\S]*MacroSnapshotAtPress\s*=\s*slotIdentity\s+is\s+\{\s*CommandType:\s*MacroHotbarSlotType\s*\}[\s\S]*CaptureSnapshot\(0,\s*0,\s*includeResolverTargets:\s*true\)' 'Macro Turbo no longer snapshots the native macro lock and complete target/context state at the certified press.'
$prepareMacroMatch = [regex]::Match(
    $runtime,
    'private void PrepareCertifiedMacroInput\([\s\S]*?(?=\r?\n\s*private void ArmCertifiedOwnedQueueReplacement\()')
if (-not $prepareMacroMatch.Success) {
    throw 'The audited pre-execution Macro certification method could not be isolated.'
}
$prepareMacro = $prepareMacroMatch.Value
Assert-Contains $runtime 'private byte ExecuteSlotDetour\([\s\S]*BeginHotbarInput\([^;]*\);\s*PrepareCertifiedDirectQueueReplacement\(\);\s*PrepareCertifiedMacroInput\(\);[\s\S]*executeSlotHook\.Original' 'Pointer-based direct/Macro input is not fully certified before native slot execution.'
Assert-Contains $runtime 'private byte ExecuteSlotByIdDetour\([\s\S]*BeginHotbarInput\([^;]*\);\s*PrepareCertifiedDirectQueueReplacement\(\);\s*PrepareCertifiedMacroInput\(\);[\s\S]*executeSlotByIdHook\.Original' 'ID-based direct/Macro input is not fully certified before native slot execution.'
Assert-Contains $prepareMacro 'scope\.CertifiedPress\s+is\s+not\s+\{\s*\}\s+press[\s\S]*scope\.SlotIdentity\s+is\s+not\s+\{\s*CommandType:\s*MacroHotbarSlotType\s*\}\s+slotIdentity[\s\S]*scope\.MacroWasLockedBeforeExecution[\s\S]*scope\.MacroSnapshotAtPress\s+is\s+not\s+\{\s*\}\s+snapshot' 'A Macro can be certified without a physical press, exact Macro slot, free executor, and press-time context.'
Assert-Contains $prepareMacro '!configuration\.Enabled[\s\S]*configuration\.DryRun[\s\S]*activeConflicts\.Count\s*>\s*0[\s\S]*compatibilityQuarantineFrames\s*>\s*0' 'Pre-execution Macro replacement certification can bypass enabled, dry-run, conflict, or quarantine gates.'
Assert-Contains $prepareMacro '!inputGenerations\.IsCurrent\(scope\.Generation\)[\s\S]*latestCertifiedPressId\)\s*!=\s*press\.PressId[\s\S]*!TryReadCurrentSlotIdentity\(press,\s*out var currentIdentity\)[\s\S]*currentIdentity\s*!=\s*slotIdentity' 'Pre-execution Macro certification no longer proves the current generation, newest certified physical press, and exact binding/slot.'
if ($prepareMacro -match 'IsStillHeld\(press\)') {
    throw 'Newest-input Macro replacement incorrectly requires a continuing hold instead of the certified fresh edge.'
}
Assert-Contains $prepareMacro '!compatibility\.IsLiveReActionProfileCurrent\(\)[\s\S]*!TryReadSafeMacroProfile\(slotIdentity,\s*out var profile,\s*out _\)[\s\S]*ArmCertifiedOwnedQueueReplacement\([\s\S]*if \(!IsSafeSnapshot\(snapshot\)\)[\s\S]*return;[\s\S]*scope\.MacroProfileAtPress\s*=\s*profile;[\s\S]*scope\.MacroExecutionBudget\s*=\s*new MacroTurboExecutionBudget\(profile\.ActionCount\);' 'Macro priority is not separated from safe physical-outcome observation, or observation can bypass live profile/action-only/snapshot gates.'

$tryBeginMacroMatch = [regex]::Match(
    $runtime,
    'private void TryBeginMacroTurbo\([\s\S]*?(?=\r?\n\s*private void StartMacroTurboRuntime\()')
if (-not $tryBeginMacroMatch.Success) {
    throw 'The audited Macro Turbo ownership gate could not be isolated.'
}
$tryBeginMacro = $tryBeginMacroMatch.Value
Assert-Contains $tryBeginMacro 'scope\.MacroProfileAtPress\s+is\s+not\s+\{\s*\}\s+certifiedProfile[\s\S]*TryReadSafeMacroProfile\(slotIdentity,\s*out var profile,\s*out var failure\)[\s\S]*profile\.ContentFingerprint\s*!=\s*certifiedProfile\.ContentFingerprint' 'Macro Turbo can start from content that was not certified before execution or changed during the original slot call.'
Assert-Contains $tryBeginMacro 'scope\.MacroProvenanceDisqualified[\s\S]*scope\.MacroExecutionBudget\s+is\s+null[\s\S]*return;' 'Macro Turbo can start from failed provenance or a missing initial execution budget.'
Assert-Contains $tryBeginMacro 'scope\.MacroSnapshotAtPress\s+is\s+not\s+\{\s*\}\s+macroSnapshot[\s\S]*!IsSafeSnapshot\(macroSnapshot\)[\s\S]*StartMacroTurboRuntime\(' 'Macro Turbo can start without the exact safe target/context snapshot captured at the physical press.'
Assert-Contains $macroStart 'var currentSnapshot\s*=\s*CaptureSnapshot\(0,\s*0,\s*includeResolverTargets:\s*true\);[\s\S]*if \(candidateIdentityChanged\(\)\)[\s\S]*return;' 'Macro Turbo no longer re-captures and validates target/context before taking ownership.'
Assert-Contains $macroStart 'var macroLocked\s*=\s*IsMacroExecutionActive\(\);[\s\S]*initialBudget\s*=\s*scope\.MacroExecutionBudget;[\s\S]*if \(initialBudget\s+is\s+null\)[\s\S]*return;[\s\S]*if \(!macroLocked\)[\s\S]*completion\s*=\s*initialBudget\.Finish\(\);[\s\S]*completion\s*!=\s*MacroTurboExecutionBudgetResult\.Complete[\s\S]*return;' 'Synchronous Macro startup no longer closes the initial 0..N budget and rejects invalid terminal results.'
Assert-Contains $macroStart '!inputGenerations\.IsCurrent\(scope\.Generation\)[\s\S]*!inputSource\.IsStillHeld\(press\)[\s\S]*latestCertifiedPressId\)\s*!=\s*press\.PressId[\s\S]*!TryReadCurrentSlotIdentity\(press,\s*out var currentIdentity\)[\s\S]*currentIdentity\s*!=\s*slotIdentity' 'Macro Turbo start no longer proves the current generation, physical hold, newest press, and exact binding/slot identity.'
Assert-Contains $macroStart '!TryReadSafeMacroProfile\(slotIdentity,\s*out var currentMacro,\s*out _\)[\s\S]*currentMacro\.ContentFingerprint\s*!=\s*macroProfile\.ContentFingerprint' 'Macro content is not hash-revalidated before Macro Turbo ownership starts.'
Assert-Contains $macroStart '!macroSnapshot\.Equals\(currentSnapshot\)[\s\S]*!IsSafeSnapshot\(currentSnapshot\)[\s\S]*!compatibility\.IsLiveReActionProfileCurrent\(\)' 'Macro Turbo start no longer fails closed on target/context, safety, or live plugin-profile changes.'
Assert-Contains $macroStart 'slotIdentity\.CommandType,[\s\S]*slotIdentity\.CommandId,[\s\S]*macroSnapshot\.TargetFingerprint,[\s\S]*macroSnapshot\.ContextFingerprint,[\s\S]*Convert\.ToUInt64\(macroProfile\.ContentFingerprint\[\.\.16\],\s*16\)' 'Macro slot, target/context, and content hash are no longer encoded into the immutable hold intent.'
Assert-Contains $macroStart 'turboRuntime\s*=\s*null;[\s\S]*Interlocked\.Exchange\(ref turboAcknowledgement,\s*null\);[\s\S]*Interlocked\.Exchange\(ref macroTurboAcknowledgement,\s*null\);[\s\S]*new MacroTurboRuntime\(\s*press,\s*slotIdentity,\s*macroProfile,\s*macroSnapshot,\s*compatibilitySignature,\s*scope\.Generation,\s*request,[\s\S]*macroLocked\s*\?\s*initialBudget\s*:\s*null,\s*Math\.Min\(scope\.ActionInvocationCount,\s*macroProfile\.ActionCount\)\)' 'Macro Turbo can overlap another runtime/acknowledgement owner or omit its stored identity, asynchronous initial budget, and cumulative physical Macro call bound.'
Assert-Contains $macroStart 'InitialMacroLockObserved\s*=\s*scope\.MacroLockObservedDuringExecution\s*\|\|\s*macroLocked,[\s\S]*InitialMacroLockCompleted\s*=\s*!macroLocked,[\s\S]*OwnsMacroExecutor\s*=\s*macroLocked,[\s\S]*OwnedQueueTuple\s*=\s*scope\.OwnedMacroQueueTuple[\s\S]*scope\.InitialMacroAcknowledgement\s+is\s+\{\s*\}\s+initialAcknowledgement[\s\S]*BeginMacroTurboAcknowledgement\(\s*runtime,\s*pulse:\s*null,\s*executionEpoch:\s*0,\s*initialAcknowledgement\)' 'Macro Turbo no longer retains physical executor/queue ownership or transfers the exact original-action acknowledgement barrier.'

# Only calls inside a proven synthetic Macro execution scope may be suppressed.
# An exact owned Queue drain is authorized first. Any other action from the
# active synthetic pulse/continuation must pass live budget authorization before
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
Assert-Contains $activeMacroPulseBranch 'if \(mode\s*==\s*ActionManager\.UseActionMode\.Queue\)[\s\S]*!IsOwnedMacroTurboQueueDrain\(\s*thisPtr,\s*actionType,\s*actionId,\s*targetId,\s*extraParam,\s*mode,\s*comboRouteId,\s*out nativeQueueDrainAttempt\)\)[\s\S]*else if \(TryAuthorizeMacroPulseInvocation\([\s\S]*out var pulseEntry\)\)[\s\S]*TryCreateAuthorizedMacroQueueAttempt\([\s\S]*pulseEntry,[\s\S]*pulseExecution\.Token\);[\s\S]*else[\s\S]*suppressSyntheticMacroCall\s*=\s*true;' 'An active Macro pulse can bypass the two-phase Queue drain-first branch, capture a queue before budget authorization, or leak an unauthorized synthetic call to native execution.'
if ([regex]::Matches($activeMacroPulseBranch, 'IsOwnedMacroTurboQueueDrain\s*\(').Count -ne 1 -or
    [regex]::Matches($activeMacroPulseBranch, 'TryAuthorizeMacroPulseInvocation\s*\(').Count -ne 1) {
    throw 'The active Macro pulse no longer has exactly one drain-first gate and one bounded-call authorization gate.'
}
Assert-Contains $useActionDetour 'if \(mode\s*==\s*ActionManager\.UseActionMode\.Macro\)\s*\{\s*lock \(dispatchGate\)\s*\{\s*var now\s*=\s*NowMilliseconds;\s*ReconcileSyntheticMacroExecutorQuarantine\(now\);\s*ReconcileRetiredPhysicalMacroExecutor\(now\);\s*suppressSyntheticMacroCall\s*=\s*ShouldSuppressQuarantinedSyntheticMacroCall\(\);\s*\}\s*\}[\s\S]*if \(!suppressSyntheticMacroCall[\s\S]*if \(!suppressSyntheticMacroCall\s*&&\s*!replaying\s*&&\s*!turboDispatching\)' 'The quarantine can suppress Queue/Normal calls, or a quarantined Macro call can re-enter synthetic authorization before the native Original boundary.'
Assert-Contains $useActionDetour 'var suppressSyntheticContinuation\s*=\s*false;[\s\S]*var observedOwnedMacroContinuation\s*=\s*false;[\s\S]*IsOwnedMacroTurboExecutionContinuation\([\s\S]*out continuationEntry,[\s\S]*out firstInitialEntry,[\s\S]*out suppressSyntheticContinuation,[\s\S]*out observedOwnedMacroContinuation\);[\s\S]*suppressSyntheticMacroCall\s*\|=\s*suppressSyntheticContinuation;' 'An unauthorized asynchronous synthetic Macro continuation can escape suppression or be double-observed as a retired/unrelated invocation.'

$physicalMacroBranchMatch = [regex]::Match(
    $useActionDetour,
    'if \(nativeHotbarInput\)[\s\S]*?if \(macroScope\s+is\s+not\s+null\)[\s\S]*?(?=\r?\n\s*if \(activeHotbarInput\s+is\s+\{\s*\}\s+inputScope\))')
if (-not $physicalMacroBranchMatch.Success) {
    throw 'The physical initial Macro pass-through branch could not be isolated.'
}
$physicalMacroBranch = $physicalMacroBranchMatch.Value
Assert-Contains $physicalMacroBranch 'TryAuthorizeCertifiedMacroInvocation\([\s\S]*out var originalEntry,[\s\S]*out var firstOriginalEntry\)[\s\S]*TryCreateAuthorizedMacroQueueAttempt\([\s\S]*originalEntry,[\s\S]*pulseToken:\s*null\);[\s\S]*goto CandidateCaptureComplete;' 'The physical initial Macro no longer remains vanilla while optional bounded-call/queue provenance is collected.'
if ($physicalMacroBranch -match 'suppressSyntheticMacroCall|suppressSyntheticContinuation') {
    throw 'A failed physical initial Macro certification can suppress the player''s vanilla action call.'
}

Assert-Contains $useActionDetour 'bool result;\s*if \(suppressSyntheticMacroCall\)\s*\{[\s\S]*outOptAreaTargeted\s*!=\s*null\)\s*\*outOptAreaTargeted\s*=\s*false;[\s\S]*result\s*=\s*false;[\s\S]*\}\s*else\s*\{[\s\S]*var originalCompleted\s*=\s*false;\s*try\s*\{\s*result\s*=\s*useActionHook\.Original\(\s*thisPtr,\s*actionType,\s*actionId,\s*targetId,\s*extraParam,\s*mode,\s*comboRouteId,\s*outOptAreaTargeted\);\s*originalCompleted\s*=\s*true;\s*\}\s*finally\s*\{[\s\S]*!originalCompleted\s*&&\s*nativeQueueDrainAttempt\s+is\s+\{\s*\}\s+interruptedDrain[\s\S]*ProcessOwnedNativeQueueDrainOutcome\(\s*thisPtr,\s*interruptedDrain,[\s\S]*\}\s*\}' 'An unauthorized synthetic Macro call can reach native execution, or an exceptional authoritative drain can strand its non-reentrant lease.'
if ([regex]::Matches($useActionDetour, 'useActionHook\.Original\s*\(').Count -ne 1) {
    throw 'UseActionDetour no longer has exactly one conditional native Original boundary.'
}

# Every original or repeated Macro action must first become a live eligible
# MacroActionInvocation and reserve its 0..N execution budget. The single queue
# capture constructor is reachable only after that authorization.
$macroEntryMatch = [regex]::Match(
    $runtime,
    'private bool TryCreateMacroTranscriptEntry\([\s\S]*?(?=\r?\n\s*private bool TryAuthorizeCertifiedMacroInvocation\()')
if (-not $macroEntryMatch.Success) {
    throw 'The audited Macro action-invocation eligibility method could not be isolated.'
}
$macroEntry = $macroEntryMatch.Value
Assert-Contains $macroEntry 'actionManager\s*==\s*null[\s\S]*mode\s*!=\s*ActionManager\.UseActionMode\.Macro[\s\S]*actionType\s+is\s+not\s+\(ActionType\.Action\s+or\s+ActionType\.PvPAction\)[\s\S]*actionId\s*==\s*0' 'A Macro action can be authorized outside strict Macro mode or from an invalid invocation.'
Assert-Contains $macroEntry 'GetAdjustedActionId\(actionId\)[\s\S]*resolvedActionId\s*==\s*0[\s\S]*excludedIntegrationActionIds\.Contains\(actionId\)[\s\S]*excludedIntegrationActionIds\.Contains\(resolvedActionId\)[\s\S]*!compatibility\.IsLiveMOActionUnowned\(actionId,\s*resolvedActionId\)[\s\S]*!TryGetEligibleActionProfile\(' 'Macro action authorization no longer performs dynamic resolution, static eligibility, and cached/live MOAction exclusion checks.'
Assert-Contains $macroEntry 'CaptureSnapshot\(targetId,\s*resolvedActionId,\s*includeResolverTargets\)[\s\S]*explicitTargetAddress\s*=\s*targetId\s+is\s+0\s+or\s+InvalidObjectId[\s\S]*FindTargetAddress\(targetId\)[\s\S]*!IsSafeSnapshot\(snapshot\)[\s\S]*explicitTargetAddress\s*==\s*nint\.Zero' 'Macro action authorization no longer validates live resolver context and explicit target identity/address.'
Assert-Contains $macroEntry 'new MacroActionInvocation\(\s*\(uint\)actionType,\s*actionId,\s*resolvedActionId,\s*targetId,\s*extraParam,\s*comboRouteId,\s*snapshot,\s*includeResolverTargets,\s*explicitTargetAddress,\s*includeResolverTargets\s*\?\s*snapshot\.TargetFingerprint\s*:\s*0\)' 'Macro action authorization no longer retains the exact live tuple, per-line snapshot, and explicit-target address used for native ownership.'

$certifiedMacroInvocationMatch = [regex]::Match(
    $runtime,
    'private bool TryAuthorizeCertifiedMacroInvocation\([\s\S]*?(?=\r?\n\s*private bool TryAuthorizeRuntimeMacroInvocation\()')
if (-not $certifiedMacroInvocationMatch.Success) {
    throw 'The audited physical Macro baseline authorization could not be isolated.'
}
$certifiedMacroInvocation = $certifiedMacroInvocationMatch.Value
Assert-Contains $certifiedMacroInvocation 'scope\.MacroProfileAtPress\s+is\s+null[\s\S]*scope\.MacroExecutionBudget\s+is\s+not\s+\{\s*\}\s+budget[\s\S]*scope\.MacroProvenanceDisqualified[\s\S]*return false;' 'A physical Macro call can bypass its fresh ActionCount budget or a prior provenance failure.'
Assert-Contains $certifiedMacroInvocation '!TryCreateMacroTranscriptEntry\([\s\S]*scope\.MacroProvenanceDisqualified\s*=\s*true;[\s\S]*return false;' 'An ineligible, non-Macro-mode, resolver-unstable, or MOAction-owned physical Macro call does not permanently disqualify the baseline.'
Assert-Contains $certifiedMacroInvocation 'firstEntry\s*=\s*budget\.ObservedActionCalls\s*==\s*0;[\s\S]*observationResult\s*=\s*budget\.ObserveAction\(\);[\s\S]*MacroTurboActionObservationResult\.Allowed\)\s*return true;[\s\S]*scope\.MacroProvenanceDisqualified\s*=\s*true;' 'A physical Macro action can reach Original without first reserving the 0..N budget, or an N+1 call does not disqualify Turbo ownership.'

$runtimeMacroInvocationMatch = [regex]::Match(
    $runtime,
    'private bool TryAuthorizeRuntimeMacroInvocation\([\s\S]*?(?=\r?\n\s*private bool TryAuthorizeMacroPulseInvocation\()')
if (-not $runtimeMacroInvocationMatch.Success) {
    throw 'The audited runtime Macro budget authorization could not be isolated.'
}
$runtimeMacroInvocation = $runtimeMacroInvocationMatch.Value
Assert-Contains $runtimeMacroInvocation '!TryCreateMacroTranscriptEntry\([\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.PluginChange,[\s\S]*return false;' 'A runtime Macro call can survive failed Macro-mode, eligibility, resolver, or MOAction validation.'
Assert-Contains $runtimeMacroInvocation '!runtime\.InitialMacroLockCompleted[\s\S]*runtime\.InitialExecutionBudget\s+is\s+not\s+\{\s*\}\s+initialBudget[\s\S]*HoldRepeatCancelReason\.Fault[\s\S]*budget\s*=\s*initialBudget;[\s\S]*firstInitialEntry\s*=\s*budget\.ObservedActionCalls\s*==\s*0;' 'Asynchronous physical Macro calls can bypass their unique initial execution budget.'
Assert-Contains $runtimeMacroInvocation 'runtime\.ActiveExecutionBudget\s+is\s+not\s+\{\s*\}\s+activeBudget[\s\S]*runtime\.ActiveExecutionEpoch\s*<=\s*0[\s\S]*QuarantineSyntheticMacroExecutor[\s\S]*HoldRepeatCancelReason\.Fault[\s\S]*budget\s*=\s*activeBudget;' 'A repeated Macro call can bypass its current execution budget and epoch.'
Assert-Contains $runtimeMacroInvocation 'budget\.AcceptedOutcomeCount\s*>\s*0\)\s*return false;[\s\S]*observationResult\s*=\s*budget\.ObserveAction\(\);[\s\S]*MacroTurboActionObservationResult\.Allowed\)\s*return true;' 'Authored fallback tail calls are not suppressed before Original after one exact accepted outcome, or allowed calls are not reserved before Original.'
Assert-Contains $runtimeMacroInvocation 'QuarantineSyntheticMacroExecutor\([\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.ResolvedActionChange' 'An invalid terminal Macro budget can leak its N+1 call or remain retryable.'

$pulseMacroInvocationMatch = [regex]::Match(
    $runtime,
    'private bool TryAuthorizeMacroPulseInvocation\([\s\S]*?(?=\r?\n\s*private MacroQueueAttempt\? TryCreateAuthorizedMacroQueueAttempt\()')
if (-not $pulseMacroInvocationMatch.Success) {
    throw 'The audited synchronous Macro pulse provenance gate could not be isolated.'
}
$pulseMacroInvocation = $pulseMacroInvocationMatch.Value
Assert-Contains $pulseMacroInvocation '!ReferenceEquals\(macroTurboRuntime,\s*runtime\)[\s\S]*!turboEngine\.IsTokenCurrent\(pulseExecution\.Token\)[\s\S]*runtime\.ActiveExecutionEpoch\s*!=\s*pulseExecution\.ExecutionEpoch[\s\S]*runtime\.ActiveExecutionBudget\s+is\s+null[\s\S]*CancelTurboUnsafe\(' 'A stale synchronous Macro call-chain can reach a current execution budget.'
Assert-Contains $pulseMacroInvocation 'ObserveMacroTurbo\(runtime,\s*checkMacroHash:\s*true\)\.Safety[\s\S]*return TryAuthorizeRuntimeMacroInvocation\(' 'A synchronous Macro pulse call can bypass live safety/eligibility and budget authorization.'

$macroQueueAttemptMatch = [regex]::Match(
    $runtime,
    'private MacroQueueAttempt\? TryCreateAuthorizedMacroQueueAttempt\([\s\S]*?(?=\r?\n\s*private bool TryObserveRetiredPhysicalMacroQueueAttempt\()')
if (-not $macroQueueAttemptMatch.Success) {
    throw 'The audited budget-authorized Macro native-queue capture could not be isolated.'
}
$macroQueueAttempt = $macroQueueAttemptMatch.Value
Assert-Contains $macroQueueAttempt 'actionManager\s*==\s*null[\s\S]*generation\s*<=\s*0[\s\S]*!inputGenerations\.IsCurrent\(generation\)[\s\S]*mode\s*!=\s*ActionManager\.UseActionMode\.Macro[\s\S]*!entry\.IsValid' 'Macro native-queue capture can accept invalid generation, non-Macro mode, or an unauthorized live invocation.'
Assert-Contains $macroQueueAttempt 'runtime\s+is\s+not\s+null[\s\S]*!ReferenceEquals\(macroTurboRuntime,\s*runtime\)[\s\S]*!turboEngine\.Snapshot\.HasActiveHold[\s\S]*latestCertifiedPressId\)\s*!=\s*runtime\.Press\.PressId[\s\S]*physicalHotbarInput\?\.IsStillHeld\(runtime\.Press\)\s*!=\s*true' 'A stale Macro runtime or released/superseded physical press can capture native queue ownership.'
Assert-Contains $macroQueueAttempt 'pulseToken\s+is\s+\{\s*\}\s+token[\s\S]*!turboEngine\.IsTokenCurrent\(token\)[\s\S]*activeMacroPulseExecution\s+is\s+not\s+\{\s*\}\s+pulseExecution[\s\S]*pulseExecution\.Token\s*!=\s*token[\s\S]*pulseExecution\.ExecutionEpoch\s*!=\s*runtime\.ActiveExecutionEpoch' 'A synthetic Macro action can capture queue ownership without the exact active pulse/runtime/epoch token.'
Assert-Contains $macroQueueAttempt 'new OwnedNativeQueueSafetySeed\([\s\S]*entry\.ActionSnapshot,[\s\S]*entry\.IncludeResolverTargets,[\s\S]*entry\.ExplicitTargetAddress\),[\s\S]*new ExactActionTuple\(\s*entry\.ActionType,\s*entry\.RequestedActionId,\s*entry\.ResolvedActionId,\s*entry\.TargetId,\s*entry\.ExtraParam,\s*\(uint\)mode,\s*entry\.RouteId\),\s*CaptureNativeQueue\(actionManager\),\s*actionManager->LastUsedActionSequence,\s*pulseToken\s*\?\?\s*runtime\?\.ActiveExecutionToken,\s*runtime\?\.ActiveExecutionEpoch\s*\?\?\s*0,\s*NowMilliseconds' 'Macro queue capture no longer retains root/invocation safety provenance, the authorized tuple, queue baseline, pulse token, epoch, and acknowledgement timestamp.'

if ($runtime -match '\bTryCreateMacroQueueAttempt\s*\(') {
    throw 'Runtime restored the stale broad Macro nested-call queue-capture path.'
}
$authorizedQueueCaptureCalls = [regex]::Matches($runtime, 'TryCreateAuthorizedMacroQueueAttempt\s*\(').Count
$macroQueueConstructions = [regex]::Matches($runtime, 'new MacroQueueAttempt\s*\(').Count
if ($authorizedQueueCaptureCalls -ne 4 -or $macroQueueConstructions -ne 2) {
    throw "Macro queue capture must have exactly three budget-authorized callers plus one isolated retired-observer constructor; found $authorizedQueueCaptureCalls method/call occurrences and $macroQueueConstructions constructors."
}
Assert-Contains $runtime 'TryAuthorizeMacroPulseInvocation\([\s\S]*out var pulseEntry\)\)[\s\S]*TryCreateAuthorizedMacroQueueAttempt\([\s\S]*pulseEntry,[\s\S]*pulseExecution\.Token\)' 'Synchronous Macro pulse queue capture can occur before live budget authorization.'
Assert-Contains $runtime 'ownedMacroExecution\s*=\s*!ownedMacroQueueDrain[\s\S]*IsOwnedMacroTurboExecutionContinuation\([\s\S]*out continuationEntry,[\s\S]*out firstInitialEntry,[\s\S]*out suppressSyntheticContinuation,[\s\S]*out observedOwnedMacroContinuation\);[\s\S]*if \(ownedMacroExecution\s*&&\s*macroTurboRuntime\s+is\s+\{\s*\}\s+ownedRuntime\)[\s\S]*TryCreateAuthorizedMacroQueueAttempt\([\s\S]*continuationEntry,[\s\S]*pulseToken:\s*null\)' 'Asynchronous Macro queue capture can occur before live eligibility/budget authorization or without recording an attributable denied physical continuation.'
Assert-Contains $runtime 'TryAuthorizeCertifiedMacroInvocation\([\s\S]*out var originalEntry,[\s\S]*out var firstOriginalEntry\)[\s\S]*TryCreateAuthorizedMacroQueueAttempt\([\s\S]*originalEntry,[\s\S]*pulseToken:\s*null\)[\s\S]*goto CandidateCaptureComplete;' 'Physical Macro queue capture can occur before the exact ActionCount baseline accepts the action.'
Assert-Contains $runtime 'macroQueueAttempt\s+is\s+\{\s*\}\s+attemptedMacroQueue[\s\S]*lock \(dispatchGate\)[\s\S]*ProcessMacroQueueAttempt\(\s*thisPtr,\s*attemptedMacroQueue,\s*currentSequence\)' 'Macro queue ownership is not reconciled under the dispatch gate after the one authoritative native UseAction result.'

$retiredMacroObserverMatch = [regex]::Match(
    $runtime,
    'private bool TryObserveRetiredPhysicalMacroQueueAttempt\([\s\S]*?(?=\r?\n\s*private void ProcessMacroQueueAttempt\()')
if (-not $retiredMacroObserverMatch.Success) {
    throw 'The non-suppressing retired physical Macro outcome observer could not be isolated.'
}
$retiredMacroObserver = $retiredMacroObserverMatch.Value
Assert-Contains $retiredMacroObserver 'ReconcileRetiredPhysicalMacroExecutor\(NowMilliseconds\);[\s\S]*retiredPhysicalMacroExecutor[\s\S]*IsMacroExecutionActive\(\)[\s\S]*mode\s*!=\s*ActionManager\.UseActionMode\.Macro' 'The retired physical Macro observer can attach outside its exact still-locked Macro-mode executor window.'
Assert-Contains $retiredMacroObserver 'ObservedActionCalls\s*>=\s*retired\.MaximumActionCalls[\s\S]*retiredPhysicalMacroExecutor\s*=\s*null;[\s\S]*TryReadSafeMacroProfile\(retired\.SlotIdentity[\s\S]*ContentFingerprint\s*!=\s*retired\.ContentFingerprint[\s\S]*ActionCount\s*!=\s*retired\.MaximumActionCalls[\s\S]*retiredPhysicalMacroExecutor\s*=\s*null;' 'The retired physical Macro observer does not retire fail-closed on budget, slot-content, or profile mismatch.'
Assert-Contains $retiredMacroObserver 'resolvedActionId\s*==\s*0[\s\S]*excludedIntegrationActionIds\.Contains\(actionId\)[\s\S]*excludedIntegrationActionIds\.Contains\(resolvedActionId\)[\s\S]*return true;[\s\S]*!compatibility\.IsLiveMOActionUnowned\(actionId,\s*resolvedActionId\)[\s\S]*MarkCompatibilityProfileDirty' 'Known integration-owned retired Macro tails are not pass-through, or an unexpected live MOAction ownership change is not terminally reassessed.'
Assert-Contains $retiredMacroObserver '!TryGetEligibleActionProfile\([\s\S]*return true;[\s\S]*var actionSnapshot\s*=\s*CaptureSnapshot\([\s\S]*explicitTargetAddress[\s\S]*attempt\s*=\s*new MacroQueueAttempt\(\s*retired\.Generation,\s*null,\s*null,\s*retired,[\s\S]*new OwnedNativeQueueSafetySeed\(\s*retired\.Snapshot,\s*actionSnapshot,[\s\S]*explicitTargetAddress\)[\s\S]*CaptureNativeQueue\(actionManager\)[\s\S]*return true;' 'A bounded ownership-ineligible old Macro tail can cancel newer work, or an eligible retired outcome lacks exact per-line safety/target provenance.'

$macroQueueOutcomeMatch = [regex]::Match(
    $runtime,
    'private void ProcessMacroQueueAttempt\([\s\S]*?(?=\r?\n\s*private bool IsOwnedMacroTurboQueueDrain\()')
if (-not $macroQueueOutcomeMatch.Success) {
    throw 'The audited Macro native-queue ownership classifier could not be isolated.'
}
$macroQueueOutcome = $macroQueueOutcomeMatch.Value
Assert-Contains $macroQueueOutcome 'outcomeStillOwned\s*=\s*inputGenerations\.IsCurrent\(attempt\.Generation\)[\s\S]*ReferenceEquals\(macroTurboRuntime,\s*currentRuntime\)[\s\S]*ReferenceEquals\(activeHotbarInput,\s*currentInputScope\)[\s\S]*currentInputScope\.MacroProfileAtPress\s+is\s+not\s+null[\s\S]*if \(!outcomeStillOwned\)[\s\S]*RetryExactOwnedNativeQueueSafetyClear\([\s\S]*TryReplaceOwnedNativeQueue\([\s\S]*return;' 'A stale Macro outcome can attach to a runtime/acknowledgement path instead of only exact terminal/newer-input queue cleanup.'
Assert-Contains $macroQueueOutcome 'ReconcileOwnedNativeQueue\(currentSequence,\s*queueAfter\);[\s\S]*queueTuple\s*=\s*attempt\.Attempted\s+with\s*\{[\s\S]*Mode\s*=\s*queueAfter\.Mode' 'Macro native-queue outcome no longer reconciles prior ownership/sidecar or uses the exact stored QueueType.'
Assert-Contains $macroQueueOutcome 'currentSequence\s*==\s*attempt\.SequenceBefore[\s\S]*queueAfter\.Matches\(queueTuple\)[\s\S]*!attempt\.QueueBefore\.Matches\(queueTuple\)[\s\S]*TryClaimOwnedNativeQueue\(\s*attempt\.Generation,\s*currentSequence,\s*attempt\.QueueBefore,\s*queueAfter,\s*queueTuple,\s*attempt\.SafetySeed' 'Macro queue ownership can be claimed after a sequence transition, without a newly created exact queue tuple, or without semantic provenance.'
Assert-Contains $macroQueueOutcome 'immediateAcceptance\s*=\s*currentSequence\s*!=\s*0[\s\S]*exactAcceptance\s*=\s*immediateAcceptance\s*\|\|\s*claimed;[\s\S]*ActiveExecutionBudget\s+is\s+\{\s*\}\s+executionBudget[\s\S]*exactAcceptance[\s\S]*executionBudget\.MarkAcceptedOutcome\(\)[\s\S]*MacroTurboAcceptedOutcomeMarkResult\.Marked' 'A synthetic Macro accepted outcome is not marked only after exact immediate/owned-queue acceptance.'
Assert-Contains $macroQueueOutcome 'new MacroTurboAcknowledgementSeed\([\s\S]*attempt\.Attempted\.ActionType,[\s\S]*attempt\.Attempted\.RequestedActionId,[\s\S]*attempt\.Attempted\.ResolvedActionId,[\s\S]*ImmediateExact[\s\S]*QueuedAfterBaseline[\s\S]*attempt\.StartedAtMilliseconds' 'Macro acknowledgement identity is not derived from the exact accepted tuple, sequence mode, and attempt time.'
Assert-Contains $macroQueueOutcome 'attempt\.InputScope\s+is\s+\{\s*\}\s+physicalScope[\s\S]*physicalScope\.InitialMacroAcknowledgement\s*\?\?=\s*seed;[\s\S]*attempt\.Runtime\s+is\s+\{\s*\}\s+acknowledgementRuntime[\s\S]*BeginMacroTurboAcknowledgement\([\s\S]*attempt\.PulseToken[\s\S]*attempt\.ExecutionEpoch' 'Physical and synthetic Macro acceptances do not establish their separate exact acknowledgement barriers.'
Assert-Contains $macroQueueOutcome 'attempt\.InputScope\s+is\s+\{\s*\}\s+physicalScope[\s\S]*physicalScope\.InitialMacroAcceptedOutcomeCount\+\+;[\s\S]*InitialMacroAcceptedOutcomeCount\s*>\s*1[\s\S]*physicalScope\.MacroProvenanceDisqualified\s*=\s*true;[\s\S]*physicalScope\.InitialMacroAcknowledgement\s*\?\?=\s*seed;' 'The untouched physical Macro does not count exact accepted outcomes separately and disqualify later Turbo ownership after more than one.'
Assert-Contains $macroQueueOutcome 'attempt\.Runtime\s+is\s+\{\s*\}\s+acknowledgementRuntime[\s\S]*!acknowledgementRuntime\.InitialMacroLockCompleted[\s\S]*acknowledgementRuntime\.InitialAcceptedOutcomeCount\+\+;[\s\S]*InitialAcceptedOutcomeCount\s*>\s*1[\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.PulseRejected' 'An asynchronous initial vanilla Macro can produce more than one exact accepted outcome and still be adopted for Turbo.'
if ($physicalMacroBranch -match 'InitialMacroAcceptedOutcomeCount|MarkAcceptedOutcome|suppressSyntheticMacroCall') {
    throw 'Physical Macro pre-Original authorization can count outcomes early, mark the synthetic budget, or suppress vanilla execution.'
}
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
Assert-Contains $macroQueueDrain 'excludedIntegrationActionIds\.Contains\(ownedTuple\.RequestedActionId\)[\s\S]*excludedIntegrationActionIds\.Contains\(ownedTuple\.ResolvedActionId\)[\s\S]*!compatibility\.IsLiveMOActionUnowned\(\s*ownedTuple\.RequestedActionId,\s*ownedTuple\.ResolvedActionId\)[\s\S]*!IsTurboSafetySafe\(ObserveMacroTurbo\(runtime,\s*checkMacroHash:\s*true\)\.Safety\)' 'An exact Macro queue drain can bypass cached/live MOAction ownership or final hash/context safety.'
Assert-Contains $macroQueueDrain '!currentQueue\.IsQueued[\s\S]*CanDeferExactHiddenDrain\(\s*runtime\.Generation,\s*ownedTuple\);[\s\S]*TryBeginOwnedNativeQueueDrain\(\s*runtime\.Generation,\s*actionManager->LastUsedActionSequence,\s*currentQueue,\s*ownedTuple,\s*out var lease\)[\s\S]*new NativeQueueDrainAttempt\(\s*lease,\s*runtime\.Generation,\s*runtime,\s*null\);[\s\S]*return true;' 'Macro queue drain cannot preserve opposite-hook-order hidden ownership or starts without a non-consuming exact lease.'

$macroContinuationMatch = [regex]::Match(
    $runtime,
    'private bool IsOwnedMacroTurboExecutionContinuation\([\s\S]*?(?=\r?\n\s*private bool IsOwnedTurboActionContinuation\()')
if (-not $macroContinuationMatch.Success) {
    throw 'The audited Macro Turbo executor-continuation gate could not be isolated.'
}
$macroContinuation = $macroContinuationMatch.Value
Assert-Contains $macroContinuation 'out bool suppressCurrentCall,[\s\S]*out bool observedOwnedMacroContinuation\)[\s\S]*suppressCurrentCall\s*=\s*false;[\s\S]*observedOwnedMacroContinuation\s*=\s*false;[\s\S]*var macroLocked\s*=\s*IsMacroExecutionActive\(\);' 'Macro executor continuation no longer exposes separate fail-closed suppression and attributable-physical-call results.'
Assert-Contains $macroContinuation 'var bindingMatches\s*=\s*runtime\s+is\s+not\s+null[\s\S]*TryReadCurrentSlotIdentity\(runtime\.Press,\s*out var currentIdentity\)[\s\S]*currentIdentity\s*==\s*runtime\.SlotIdentity;[\s\S]*var ownedExecutorContext\s*=\s*runtime\s+is\s+not\s+null[\s\S]*runtime\.OwnsMacroExecutor[\s\S]*macroLocked[\s\S]*turboEngine\.Snapshot\.HasActiveHold[\s\S]*inputGenerations\.IsCurrent\(runtime\.Generation\)[\s\S]*latestCertifiedPressId\)\s*==\s*runtime\.Press\.PressId[\s\S]*physicalHotbarInput\?\.IsStillHeld\(runtime\.Press\)\s*==\s*true[\s\S]*bindingMatches;' 'Macro executor continuation no longer requires its exact owned executor, native lock, active hold, current generation, newest still-held press, and unchanged slot.'
Assert-Contains $macroContinuation 'runtime\s+is\s+not\s+null[\s\S]*runtime\.OwnsMacroExecutor[\s\S]*macroLocked[\s\S]*runtime\.ActiveExecutionBudget\s+is\s+not\s+null[\s\S]*runtime\.ActiveExecutionEpoch\s*>\s*0[\s\S]*suppressCurrentCall\s*=\s*true;' 'A denied action inside a proven repeated Macro execution budget can escape native suppression.'
Assert-Contains $macroContinuation 'runtime\s+is\s+null[\s\S]*!ownedExecutorContext[\s\S]*mode\s*!=\s*ActionManager\.UseActionMode\.Macro[\s\S]*!IsTurboSafetySafe\(ObserveMacroTurbo\(runtime,\s*checkMacroHash:\s*true\)\.Safety\)[\s\S]*return false;' 'A non-Macro, unsafe, stale, or unowned Macro executor call can continue bounded authorization.'
Assert-Contains $macroContinuation 'runtime\.InitialMacroLockObserved\s*=\s*true;[\s\S]*var authorized\s*=\s*TryAuthorizeRuntimeMacroInvocation\([\s\S]*if \(authorized\)\s*suppressCurrentCall\s*=\s*false;[\s\S]*return authorized;' 'An asynchronous Macro continuation can bypass live invocation/budget authorization or remain suppressed after successful authorization.'

# Cancellation can remove the Macro runtime while the native executor is still
# locked and capable of emitting later lines. A bounded-epoch tombstone
# must therefore survive ordinary cancellation and suppress only later
# Macro-mode calls for as long as MacroLocked remains true, even after its
# reporting deadline. Its only other clear boundaries are
# disposal or a strictly newer certified, unlocked root Macro press.
$quarantineArmMatch = [regex]::Match(
    $runtime,
    'private void QuarantineSyntheticMacroExecutor\([\s\S]*?(?=\r?\n\s*private bool ShouldSuppressQuarantinedSyntheticMacroCall\()')
if (-not $quarantineArmMatch.Success) {
    throw 'The synthetic Macro executor quarantine arm method could not be isolated.'
}
$quarantineArm = $quarantineArmMatch.Value
Assert-Contains $quarantineArm 'runtime\.ActiveExecutionBudget\s+is\s+null\s*\|\|\s*runtime\.ActiveExecutionEpoch\s*<=\s*0\)\s*return;[\s\S]*existing\s+is\s+null[\s\S]*existing\.Generation\s*!=\s*runtime\.Generation[\s\S]*existing\.ExecutionEpoch\s*!=\s*runtime\.ActiveExecutionEpoch[\s\S]*new SyntheticMacroExecutorQuarantine\(\s*runtime\.Generation,\s*runtime\.Press\.PressId,\s*runtime\.ActiveExecutionEpoch,\s*now,\s*SaturatingAdd\(now,\s*MaximumMacroCaptureMilliseconds\)\)' 'The quarantine can arm without an active bounded epoch, lose its exact generation/press/epoch identity, or extend an existing same-epoch deadline.'

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
Assert-Contains $quarantineReconcile 'var quarantine\s*=\s*syntheticMacroExecutorQuarantine;\s*if \(quarantine\s+is\s+null\)\s*return;[\s\S]*if \(IsMacroExecutionActive\(\)\)[\s\S]*return;[\s\S]*syntheticMacroExecutorQuarantine\s*=\s*null;' 'The tombstone can clear while MacroLocked remains active; expiry may report a fault but must keep suppressing until unlock.'
Assert-Contains $quarantineReconcile '!quarantine\.TimeoutReported[\s\S]*now\s*>\s*quarantine\.ExpiresAtMilliseconds[\s\S]*quarantine\s+with\s*\{\s*TimeoutReported\s*=\s*true\s*\}[\s\S]*suppression stays armed[\s\S]*return;[\s\S]*syntheticMacroExecutorQuarantine\s*=\s*null;' 'Quarantine expiry is not a one-shot diagnostic-only event while MacroLocked remains active.'
Assert-Contains $runtime 'private void OnFrameworkUpdate\([\s\S]*lock \(dispatchGate\)[\s\S]*ReconcileSyntheticMacroExecutorQuarantine\(now\);' 'Framework observation no longer reconciles quarantine under the dispatch gate.'

$quarantineRootClearMatch = [regex]::Match(
    $runtime,
    'private void TryClearSyntheticMacroQuarantineForCertifiedRoot\([\s\S]*?(?=\r?\n\s*private void CompleteHotbarInput\()')
if (-not $quarantineRootClearMatch.Success) {
    throw 'The newer certified root Macro quarantine-clear method could not be isolated.'
}
$quarantineRootClear = $quarantineRootClearMatch.Value
Assert-Contains $quarantineRootClear 'scope\.CertifiedPress\s+is\s+not\s+\{\s*\}\s+press[\s\S]*scope\.SlotIdentity\s+is\s+not\s+\{\s*CommandType:\s*MacroHotbarSlotType\s*\}[\s\S]*scope\.MacroWasLockedBeforeExecution[\s\S]*IsMacroExecutionActive\(\)[\s\S]*scope\.Generation\s*<=\s*quarantine\.Generation[\s\S]*press\.PressId\s*<=\s*quarantine\.PressId[\s\S]*return;[\s\S]*syntheticMacroExecutorQuarantine\s*=\s*null;' 'A non-Macro, uncertified, locked, stale, or non-newer hotbar root can clear the synthetic executor tombstone.'
Assert-Contains $runtime 'private void BeginHotbarInput\([\s\S]*Cancel\(CancelReason\.Replaced,[\s\S]*activeHotbarInput\s*=\s*new HotbarInputScope\([\s\S]*MacroWasLockedBeforeExecution\s*=\s*IsMacroExecutionActive\(\)[\s\S]*TryClearSyntheticMacroQuarantineForCertifiedRoot\(activeHotbarInput\);' 'A replacement input can clear quarantine before its exact certified root, pre-existing lock, generation, and press identity are captured.'
Assert-Contains $runtime 'if \(!activeHotbarInput\.MacroWasLockedBeforeExecution\)[\s\S]*ReconcileRetiredPhysicalMacroExecutor\(NowMilliseconds\);[\s\S]*TryClearSyntheticMacroQuarantineForCertifiedRoot' 'A newer root can reuse MacroLocked without first retiring an observer across the proven unlocked ABA boundary.'
Assert-Contains $runtime 'private void CompleteHotbarInput\([\s\S]*RetryExactOwnedNativeQueueSafetyClear\([\s\S]*RetainRetiredPhysicalMacroExecutor\(\s*scope,[\s\S]*if \(!inputGenerations\.IsCurrent\(scope\.Generation\)\)\s*return;' 'Hotbar completion can lose a terminal in-flight queue or asynchronous physical Macro observer before the stale-generation exit.'

$cancelTurboMatch = [regex]::Match(
    $runtime,
    'private void CancelTurboUnsafe\([\s\S]*?(?=\r?\n\s*private static HoldRepeatCancelReason ToTurboCancelReason\()')
if (-not $cancelTurboMatch.Success) {
    throw 'The central Turbo cancellation boundary could not be isolated for quarantine validation.'
}
$cancelTurbo = $cancelTurboMatch.Value
Assert-Contains $cancelTurbo 'macroTurboRuntime\s+is\s+\{\s*\}\s+macroRuntime[\s\S]*macroRuntime\.ActiveExecutionBudget\s+is\s+not\s+null[\s\S]*macroRuntime\.ActiveExecutionEpoch\s*>\s*0[\s\S]*macroRuntime\.OwnsMacroExecutor[\s\S]*IsMacroExecutionActive\(\)[\s\S]*QuarantineSyntheticMacroExecutor\([\s\S]*turboEngine\.Cancel\(reason\);[\s\S]*macroTurboRuntime\s*=\s*null;' 'Central cancellation can discard an active bounded executor under MacroLocked before arming its tombstone.'
Assert-Contains $cancelTurbo 'macroTurboRuntime\s+is\s+\{\s*\}\s+physicalMacroRuntime[\s\S]*!physicalMacroRuntime\.InitialMacroLockCompleted[\s\S]*RetainRetiredPhysicalMacroExecutor\(\s*physicalMacroRuntime,[\s\S]*turboEngine\.Cancel\(reason\);' 'Central cancellation can discard an untouched initial physical MacroLocked executor without retaining its bounded read-only outcome observer.'
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
Assert-Contains $ordinaryCancel 'lock \(dispatchGate\)[\s\S]*CancelTurboUnsafe\(\s*ToTurboCancelReason\(reason\),\s*detail,\s*ownedQueuePolicy:\s*reason\s*==\s*CancelReason\.Replaced\s*\?\s*OwnedQueueCancelPolicy\.Preserve\s*:\s*OwnedQueueCancelPolicy\.ExactClear\)' 'Ordinary cancellation bypasses the tombstone-aware boundary or clears an older owned native queue before the replacing certified root can preempt it.'
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
Assert-Contains $disposeMethod 'Cancel\(CancelReason\.Disabled,[\s\S]*lock \(dispatchGate\)[\s\S]*syntheticMacroExecutorQuarantine\s*=\s*null;[\s\S]*activeHotbarInput\s*=\s*null;' 'Disposal no longer clears the retained synthetic Macro executor tombstone and active input under the dispatch gate after terminal cancellation.'

$quarantineNullWrites = [regex]::Matches($runtime, 'syntheticMacroExecutorQuarantine\s*=\s*null;').Count
$quarantineArmWrites = [regex]::Matches($runtime, 'syntheticMacroExecutorQuarantine\s*=\s*new SyntheticMacroExecutorQuarantine\s*\(').Count
if ($quarantineNullWrites -ne 3 -or $quarantineArmWrites -ne 1) {
    throw "Synthetic Macro quarantine authority must have one arm and only three clears (observed unlock, newer certified unlocked root, dispose); found $quarantineArmWrites arm and $quarantineNullWrites clear writes."
}
Assert-Contains $runtime 'private sealed record SyntheticMacroExecutorQuarantine\(\s*long Generation,\s*long PressId,\s*long ExecutionEpoch,\s*long StartedAtMilliseconds,\s*long ExpiresAtMilliseconds,\s*bool TimeoutReported\s*=\s*false\);' 'The quarantine tombstone no longer retains exact generation, press, epoch, diagnostic deadline, and one-shot timeout-report identity.'

# Provenance failures inside a bounded synthetic epoch must arm immediately,
# before central cancellation destroys the runtime needed to identify later
# native Macro lines.
Assert-Contains $runtimeMacroInvocation '!TryCreateMacroTranscriptEntry\([\s\S]*runtime\.ActiveExecutionEpoch\s*>\s*0[\s\S]*QuarantineSyntheticMacroExecutor\([\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.PluginChange' 'A runtime eligibility/resolver/MOAction provenance failure can cancel an active epoch without arming quarantine first.'
Assert-Contains $runtimeMacroInvocation 'runtime\.ActiveExecutionBudget\s+is\s+not\s+\{\s*\}\s+activeBudget[\s\S]*runtime\.ActiveExecutionEpoch\s*<=\s*0[\s\S]*QuarantineSyntheticMacroExecutor\(runtime,[\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.Fault' 'A missing budget/epoch provenance failure can cancel without arming quarantine first.'
Assert-Contains $runtimeMacroInvocation 'if \(runtime\.InitialMacroLockCompleted\)[\s\S]*QuarantineSyntheticMacroExecutor\(\s*runtime,[\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.ResolvedActionChange' 'An N+1 or otherwise terminal synthetic budget result can cancel without arming quarantine first.'
Assert-Contains $pulseMacroInvocation '!ReferenceEquals\(macroTurboRuntime,\s*runtime\)[\s\S]*runtime\.ActiveExecutionBudget\s+is\s+null[\s\S]*QuarantineSyntheticMacroExecutor\([\s\S]*CancelTurboUnsafe\(' 'A stale synchronous pulse provenance failure can cancel without arming quarantine first.'
Assert-Contains $macroContinuation 'runtime\s+is\s+not\s+null\s*&&\s*suppressCurrentCall[\s\S]*QuarantineSyntheticMacroExecutor\([\s\S]*return false;' 'A frozen asynchronous continuation safety/mode provenance failure can return without retaining its quarantine tombstone.'

$macroProcessMatch = [regex]::Match(
    $runtime,
    'private void ProcessMacroTurboUnsafe\([\s\S]*?(?=\r?\n\s*private bool TryCompleteInitialMacroExecution\()')
if (-not $macroProcessMatch.Success) {
    throw 'The audited Macro Turbo framework processor could not be isolated.'
}
$macroProcess = $macroProcessMatch.Value
Assert-Contains $macroProcess 'if \(macroLocked\)[\s\S]*if \(!runtime\.OwnsMacroExecutor\)[\s\S]*HoldRepeatCancelReason\.PluginChange[\s\S]*return;[\s\S]*InitialMacroLockObserved\s*=\s*true;[\s\S]*else if \(runtime\.OwnsMacroExecutor\)[\s\S]*runtime\.OwnsMacroExecutor\s*=\s*false;[\s\S]*!runtime\.InitialMacroLockCompleted[\s\S]*TryCompleteInitialMacroExecution\(runtime\)[\s\S]*runtime\.ActiveExecutionBudget\s+is\s+not\s+null[\s\S]*TryCompleteMacroExecutionEpoch\(' 'Macro Turbo no longer rejects a foreign MacroLock or closes the initial/repeated bounded execution when its owned executor releases.'
Assert-Contains $macroProcess '!runtime\.InitialMacroLockCompleted[\s\S]*InitialMacroLockDeadlineMilliseconds[\s\S]*CancelTurboUnsafe\([\s\S]*HoldRepeatCancelReason\.InputLost' 'Macro Turbo can wait indefinitely without proving that the original macro executor completed.'
Assert-Contains $macroProcess 'acknowledgement\s*=\s*Volatile\.Read\(ref macroTurboAcknowledgement\);[\s\S]*observation\s*=\s*ObserveMacroTurbo\(runtime,\s*checkMacroHash:\s*due\);[\s\S]*turboEngine\.Tick\(\s*now,\s*observation\.Safety,\s*observation\.ActionReady\s*&&\s*acknowledgement\s+is\s+null\)' 'Macro acknowledgement gating now skips the full safety observation/tick or allows another pulse while pending.'
Assert-Contains $macroProcess 'acknowledgement\s*=\s*Volatile\.Read\(ref macroTurboAcknowledgement\);[\s\S]*if \(acknowledgement\s+is\s+not\s+null\)[\s\S]*IsMacroTurboAcknowledgementCurrent[\s\S]*MaximumTurboAcknowledgementMilliseconds[\s\S]*HoldRepeatCancelReason\.PulseRejected[\s\S]*hold ended without retry[\s\S]*return;[\s\S]*decision\.Kind\s*==\s*HoldRepeatDecisionKind\.Pulse[\s\S]*DispatchMacroTurboPulse\(runtime,\s*decision\.Pulse\)' 'Macro acknowledgement does not block later pulses or its 2-second timeout is retryable.'

$macroInitialCompleteMatch = [regex]::Match(
    $runtime,
    'private bool TryCompleteInitialMacroExecution\([\s\S]*?(?=\r?\n\s*private bool TryCompleteMacroExecutionEpoch\()')
if (-not $macroInitialCompleteMatch.Success) {
    throw 'The audited asynchronous initial Macro budget completion could not be isolated.'
}
$macroInitialComplete = $macroInitialCompleteMatch.Value
Assert-Contains $macroInitialComplete 'runtime\.InitialExecutionBudget\s+is\s+not\s+\{\s*\}\s+budget[\s\S]*HoldRepeatCancelReason\.Fault[\s\S]*completion\s*=\s*budget\.Finish\(\);[\s\S]*runtime\.InitialExecutionBudget\s*=\s*null;[\s\S]*completion\s*!=\s*MacroTurboExecutionBudgetResult\.Complete[\s\S]*HoldRepeatCancelReason\.ResolvedActionChange[\s\S]*runtime\.InitialMacroLockCompleted\s*=\s*true;' 'Asynchronous initial Macro completion can retain its budget, accept an invalid terminal result, or omit the lock-completion barrier.'

$macroEpochCompleteMatch = [regex]::Match(
    $runtime,
    'private bool TryCompleteMacroExecutionEpoch\([\s\S]*?(?=\r?\n\s*private void DispatchMacroTurboPulse\()')
if (-not $macroEpochCompleteMatch.Success) {
    throw 'The audited repeated Macro execution completion gate could not be isolated.'
}
$macroEpochComplete = $macroEpochCompleteMatch.Value
Assert-Contains $macroEpochComplete 'epoch\s*<=\s*0[\s\S]*runtime\.ActiveExecutionEpoch\s*!=\s*epoch[\s\S]*runtime\.ActiveExecutionBudget\s+is\s+not\s+\{\s*\}\s+budget[\s\S]*CancelTurboUnsafe\(' 'A missing or stale bounded Macro execution epoch can complete successfully.'
Assert-Contains $macroEpochComplete 'completion\s*=\s*budget\.Finish\(\);[\s\S]*runtime\.ActiveExecutionBudget\s*=\s*null;[\s\S]*runtime\.ActiveExecutionToken\s*=\s*null;[\s\S]*runtime\.ActiveExecutionEpoch\s*=\s*0;' 'Macro epoch completion no longer closes and clears its budget, pulse token, and epoch exactly once.'
Assert-Contains $macroEpochComplete 'completion\s*==\s*MacroTurboExecutionBudgetResult\.Complete[\s\S]*budget\.AcceptedOutcomeCount\s*==\s*1\)\s*turboAcceptedCount\+\+;[\s\S]*else\s*turboRejectedCount\+\+;[\s\S]*return true;[\s\S]*CancelTurboUnsafe\(\s*HoldRepeatCancelReason\.ResolvedActionChange' 'A bounded 0..N execution with zero accepted outcomes is not a nonterminal no-op, or an invalid terminal budget can remain active.'

$macroObserveMatch = [regex]::Match(
    $runtime,
    'private MacroTurboObservation ObserveMacroTurbo\([\s\S]*?(?=\r?\n\s*private void DispatchTurboPulse\()')
if (-not $macroObserveMatch.Success) {
    throw 'The audited Macro Turbo observation method could not be isolated.'
}
$macroObserve = $macroObserveMatch.Value
Assert-Contains $macroObserve 'inputSource\?\.IsStillHeld\(runtime\.Press\)\s*==\s*true' 'Macro Turbo final observation no longer requires the exact physical control to remain held.'
Assert-Contains $macroObserve 'TryReadSafeMacroProfile\(runtime\.SlotIdentity,\s*out var currentMacroProfile,\s*out _\)[\s\S]*currentMacroProfile\.ContentFingerprint\s*==\s*runtime\.MacroProfile\.ContentFingerprint' 'Macro Turbo final observation no longer revalidates the complete macro-content hash.'
Assert-Contains $macroObserve 'latestCertifiedPressId\)\s*==\s*runtime\.Press\.PressId[\s\S]*TryReadCurrentSlotIdentity\(runtime\.Press,\s*out var currentIdentity\)[\s\S]*currentIdentity\s*==\s*runtime\.SlotIdentity' 'Macro Turbo final observation no longer proves the newest certified press and unchanged exact binding/slot identity.'
Assert-Contains $macroObserve 'TargetFingerprint\s*==\s*runtime\.Snapshot\.TargetFingerprint[\s\S]*TerritoryId\s*==\s*runtime\.Snapshot\.TerritoryId[\s\S]*ContextFingerprint\s*==\s*runtime\.Snapshot\.ContextFingerprint[\s\S]*LocalGameObjectId\s*==\s*runtime\.Snapshot\.LocalGameObjectId[\s\S]*LocalAddress\s*==\s*runtime\.Snapshot\.LocalAddress' 'Macro Turbo final observation no longer requires the exact target, territory, instance/context, and local-player identity.'
Assert-Contains $macroObserve 'string\.Equals\(\s*compatibilitySignature,\s*runtime\.CompatibilitySignature,\s*StringComparison\.Ordinal\)[\s\S]*compatibility\.IsLiveReActionProfileCurrent\(\)' 'Macro Turbo final observation no longer requires the captured plugin topology and live audited ReAction profile.'
Assert-Contains $macroObserve 'configuration\.Enabled[\s\S]*configuration\.TurboEnabled[\s\S]*configuration\.TurboMacrosEnabled[\s\S]*!configuration\.DryRun[\s\S]*configuration\.TurboOutOfCombat\s*\|\|\s*condition\[ConditionFlag\.InCombat\]' 'Macro Turbo enablement, opt-in, dry-run, or combat gates are incomplete.'
Assert-Contains $macroObserve 'ConflictDetected:\s*activeConflicts\.Count\s*>\s*0\s*\|\|\s*compatibilityQuarantineFrames\s*>\s*0[\s\S]*LoggedIn:[\s\S]*IsAlive:[\s\S]*IsMounted:[\s\S]*IsStunned:[\s\S]*IsKnockbackActive:[\s\S]*PhysicalControlDown:[\s\S]*ReleaseObserved:[\s\S]*TerritoryMatches:[\s\S]*InstanceMatches:[\s\S]*TargetMatches:[\s\S]*ResolvedActionMatches:[\s\S]*BindingMatches:[\s\S]*PluginStateMatches:[\s\S]*Faulted:' 'Macro Turbo no longer constructs the complete fail-closed hold safety state.'
Assert-Contains $macroObserve 'ResolvedActionMatches:\s*macroProfileMatches' 'Macro Turbo safety no longer includes the live certified macro-content hash.'
Assert-Contains $macroObserve 'runtime\.InitialMacroLockCompleted[\s\S]*runtime\.ActiveExecutionBudget\s+is\s+null[\s\S]*runtime\.ActiveExecutionEpoch\s*==\s*0[\s\S]*engine\.Pending\s+is\s+null[\s\S]*actionManager\s*!=\s*null[\s\S]*hotbarModule\s*!=\s*null[\s\S]*!IsMacroExecutionActive\(\)[\s\S]*!actionManager->ActionQueued[\s\S]*actionManager->AnimationLock\s*<=\s*AnimationLockEpsilonSeconds' 'Macro Turbo readiness no longer requires a completed initial execution, no active budget, free macro executor, no one-shot/native queue, and a clear animation lock.'
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
Assert-Contains $runtime 'private void ProcessOriginalOutcome\([\s\S]*exactQueueClaimed\s*=\s*nativeOutcome\s*==\s*NativeActionOutcome\.MatchingNewQueue[\s\S]*TryClaimOwnedNativeQueue\([\s\S]*new OwnedNativeQueueSafetySeed\([\s\S]*candidate\.ExplicitTargetAddress[\s\S]*RecordInitialTurboOutcome\([\s\S]*exactQueueClaimed,[\s\S]*allowQueuedOutcome:\s*true\)' 'The original physical action no longer records only exact immediate/owned-queue acknowledgement with semantic safety provenance.'
Assert-Contains $runtime 'private static TurboAcknowledgementSeed CreateTurboAcknowledgementSeed\([\s\S]*new TurboActionEffectExpectation\([\s\S]*candidate\.ActionType,[\s\S]*candidate\.RequestedActionId,[\s\S]*candidate\.ResolvedActionId,[\s\S]*sequenceMode,[\s\S]*sequenceMarker\)[\s\S]*NowMilliseconds' 'Initial acknowledgement seeds no longer retain the exact action identity, sequence mode/marker, and start time.'
Assert-Contains $runtime 'private void ApplyTurboCaptureOutcome\([\s\S]*scope\.Generation\s*==\s*candidate\.InputGeneration[\s\S]*scope\.TurboCandidate\?\.ExactTuple\s*==\s*candidate\.ExactTuple[\s\S]*scope\.InitialAcknowledgement\s*=\s*seed' 'A direct original acknowledgement seed can attach to a different generation or action tuple.'
Assert-Contains $directStart 'macroTurboRuntime\s*=\s*null;[\s\S]*turboRuntime\s*=\s*runtime;[\s\S]*scope\.InitialAcknowledgement\s+is\s+\{\s*\}\s+initialAcknowledgement[\s\S]*!BeginInitialTurboAcknowledgement\(runtime,\s*initialAcknowledgement\)[\s\S]*CancelTurboUnsafe\([\s\S]*PulseRejected' 'Direct Turbo can overlap Macro Turbo or start without installing/proving its original-action acknowledgement barrier.'

Assert-Contains $runtime 'private void DispatchOnce\([\s\S]*NativeActionOutcome\.ImmediateAcceptance\s*&&\s*sequenceAdvanced[\s\S]*BeginOneShotTurboAcknowledgement\([\s\S]*ImmediateExact,[\s\S]*sequenceAfter,[\s\S]*replayTuple[\s\S]*CancelMatchingTurboAfterOneShot' 'An immediate one-shot send can leave the matching Turbo hold active without an exact acknowledgement barrier.'
Assert-Contains $runtime 'private void DispatchOnce\([\s\S]*NativeActionOutcome\.MatchingNewQueue\s*&&\s*!sequenceAdvanced[\s\S]*TryClaimOwnedNativeQueue\([\s\S]*new OwnedNativeQueueSafetySeed\([\s\S]*runtime\.Candidate\.ExplicitTargetAddress[\s\S]*BeginOneShotTurboAcknowledgement\([\s\S]*QueuedAfterBaseline,[\s\S]*sequenceBefore,[\s\S]*replayTuple[\s\S]*CancelMatchingTurboAfterOneShot' 'A queued one-shot can leave the matching Turbo hold active without exact semantic ownership and an acknowledgement barrier.'
Assert-Contains $runtime 'private bool BeginOneShotTurboAcknowledgement\([\s\S]*runtime\.Candidate\.InputGeneration\s*!=\s*candidate\.InputGeneration[\s\S]*return true;[\s\S]*new TurboActionEffectExpectation\([\s\S]*exactTuple\.ActionType,[\s\S]*exactTuple\.RequestedActionId,[\s\S]*exactTuple\.ResolvedActionId[\s\S]*BeginTurboAcknowledgement\(' 'A one-shot acknowledgement can block or attach to a different Turbo generation/action identity.'
Assert-Contains $runtime 'NativeActionOutcome\.Rejected[\s\S]*CancelMatchingTurboAfterOneShot\([\s\S]*else[\s\S]*CancelMatchingTurboAfterOneShot\(' 'Rejected or unproven one-shot outcomes no longer terminate the matching held input without retry.'

Assert-Contains $runtime 'MaximumRecentActionEffectAgeMilliseconds\s*=\s*2_000\s*;' 'The early acknowledgement cache age changed from the audited two-second bound.'
Assert-Contains $runtime 'header->SourceSequence\s*!=\s*0\s*&&\s*casterEntityId\s*==\s*currentLocalEntityId[\s\S]*recentLocalActionEffects\.Enqueue\([\s\S]*TurboActionEffectObservation\([\s\S]*header->ActionType,[\s\S]*header->ActionId,[\s\S]*header->SourceSequence[\s\S]*TryCompleteTurboAcknowledgement\(header\)' 'Local nonzero-sequence effects are not cached before live acknowledgement completion.'
Assert-Contains $runtime 'private void RemoveStaleActionEffects\([\s\S]*MaximumRecentActionEffectAgeMilliseconds[\s\S]*recentLocalActionEffects\.TryDequeue' 'Stale early acknowledgements are no longer removed at the audited bound.'
Assert-Contains $runtime 'private void OnFrameworkUpdate\([\s\S]*RemoveStaleActionEffects\(now\)' 'The recent early-acknowledgement cache is no longer pruned from the framework boundary.'
Assert-Contains $runtime 'private bool WasRecentlyAcknowledged\(\s*TurboActionEffectExpectation expectation,\s*long startedAtMilliseconds\)[\s\S]*observed\.ObservedAtMilliseconds\s*<\s*startedAtMilliseconds[\s\S]*TurboActionEffectAcknowledgementMatcher\.Matches\(\s*expectation,\s*observed\.Observation\)' 'The shared early acknowledgement cache can satisfy a direct/Macro seed with an older or nonmatching action effect.'
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
# token. Both Turbo modes invoke the same certified ExecuteSlotById exactly once
# per pulse. Direct Turbo then authorizes the one nested UseAction dynamically;
# Macro Turbo applies its per-execution 0..N budget to nested Macro calls.
Assert-Contains $runtime 'private void DispatchTurboPulse\([\s\S]*?lock \(dispatchGate\)[\s\S]*?turboEngine\.IsTokenCurrent\(token\)[\s\S]*?ObserveTurbo\(runtime,\s*checkLiveMOAction:\s*true\)' 'Direct Turbo final token and safety validation are not serialized under the dispatch gate.'
$turboDispatchMatch = [regex]::Match(
    $runtime,
    'private void DispatchTurboPulse\([\s\S]*?(?=\r?\n\s*private TurboObservation ObserveTurbo\()')
if (-not $turboDispatchMatch.Success) {
    throw 'The audited direct Turbo dispatch method could not be isolated.'
}
$turboDispatch = $turboDispatchMatch.Value
$turboExactSlotCalls = [regex]::Matches(
    $turboDispatch,
    'executeSlotByIdHook\.Original\(\s*observation\.HotbarModule,\s*runtime\.SlotIdentity\.Binding\.HotbarId,\s*runtime\.SlotIdentity\.Binding\.SlotId\)').Count
$turboSlotCalls = [regex]::Matches($turboDispatch, 'executeSlot(?:ById)?Hook\.Original\s*\(').Count
if ($turboExactSlotCalls -ne 1 -or $turboSlotCalls -ne 1) {
    throw "Direct Turbo must contain exactly one original same-slot dispatch; found $turboExactSlotCalls exact out of $turboSlotCalls total."
}
if ($turboDispatch -match 'useActionHook\.Original\s*\(' -or
    $turboDispatch -match '(?m)\b(?:while|for)\s*\(' -or
    [regex]::Matches($turboDispatch, 'DispatchTurboPulse\s*\(').Count -ne 1) {
    throw 'Direct Turbo can bypass same-slot execution, loop, recurse, or burst more than one dispatch per pulse.'
}
Assert-Contains $turboDispatch 'new DirectPulseExecutionScope\(runtime,\s*token,\s*observation\.ResolvedActionId\)[\s\S]*activeDirectPulseExecution\s*=\s*execution;[\s\S]*turboDispatching\s*=\s*true;[\s\S]*hotbarExecutionDepth\+\+;[\s\S]*try[\s\S]*executeSlotByIdHook\.Original[\s\S]*finally[\s\S]*hotbarExecutionDepth--;[\s\S]*turboDispatching\s*=\s*false;[\s\S]*activeDirectPulseExecution\s*=\s*null;' 'Direct same-slot dispatch is not bound to one active pulse scope and a guaranteed cleanup boundary.'
Assert-Contains $turboDispatch 'execution\.InvocationCount\s*!=\s*1[\s\S]*!execution\.Completed[\s\S]*!execution\.Accepted[\s\S]*execution\.ExactTuple\s+is\s+not\s+\{\s*\}\s+exactTuple[\s\S]*RejectTurboPulseUnsafe\(' 'A direct same-slot pulse can survive zero/multiple calls or an unclassified/unaccepted exact tuple.'

$directAuthorizeMatch = [regex]::Match(
    $runtime,
    'private bool TryAuthorizeDirectPulseInvocation\([\s\S]*?(?=\r?\n\s*private void ProcessDirectPulseAttempt\()')
if (-not $directAuthorizeMatch.Success) {
    throw 'The direct same-slot nested-call authorization method could not be isolated.'
}
$directAuthorize = $directAuthorizeMatch.Value
Assert-Contains $directAuthorize 'execution\.InvocationCount\+\+;[\s\S]*execution\.InvocationCount\s*!=\s*1[\s\S]*CancelTurboUnsafe\(' 'Direct same-slot Turbo can authorize more than one nested action call.'
Assert-Contains $directAuthorize '!ReferenceEquals\(turboRuntime,\s*runtime\)[\s\S]*!turboEngine\.IsTokenCurrent\(execution\.Token\)[\s\S]*activeDirectPulseExecution\s*!=\s*execution[\s\S]*mode\s*!=\s*ActionManager\.UseActionMode\.None[\s\S]*actionType\s+is\s+not\s+\(ActionType\.Action\s+or\s+ActionType\.PvPAction\)[\s\S]*actionId\s*!=\s*runtime\.SlotIdentity\.CommandId' 'Direct nested authorization no longer requires the active pulse, None mode, Action/PvPAction, and exact certified base slot command.'
Assert-Contains $directAuthorize 'resolvedActionId\s*=\s*actionManager->GetAdjustedActionId\(actionId\);[\s\S]*resolvedActionId\s*!=\s*execution\.ExpectedResolvedActionId[\s\S]*excludedIntegrationActionIds\.Contains\(actionId\)[\s\S]*excludedIntegrationActionIds\.Contains\(resolvedActionId\)[\s\S]*!compatibility\.IsLiveMOActionUnowned\(actionId,\s*resolvedActionId\)[\s\S]*!TryGetEligibleActionProfile\(' 'Direct nested authorization no longer uses the live adjusted action ID and current eligibility/MOAction ownership.'
if ($directAuthorize -match 'resolvedActionId\s*!=\s*runtime\.Candidate\.ResolvedActionId') {
    throw 'Direct same-slot Turbo incorrectly freezes the captured resolved ID instead of accepting the live per-pulse adjusted ID.'
}
Assert-Contains $directAuthorize 'CaptureSnapshot\([\s\S]*!targetMatches[\s\S]*!IsSafeSnapshot\(currentSnapshot\)[\s\S]*TargetFingerprint\s*!=\s*runtime\.Candidate\.Snapshot\.TargetFingerprint[\s\S]*TerritoryId\s*!=\s*runtime\.Candidate\.Snapshot\.TerritoryId[\s\S]*ContextFingerprint\s*!=\s*runtime\.Candidate\.Snapshot\.ContextFingerprint[\s\S]*LocalGameObjectId\s*!=\s*runtime\.Candidate\.Snapshot\.LocalGameObjectId[\s\S]*LocalAddress\s*!=\s*runtime\.Candidate\.Snapshot\.LocalAddress' 'Direct nested authorization can change target/resolver, territory, instance, or local-player identity.'
Assert-Contains $directAuthorize 'exactTuple\s*=\s*new ExactActionTuple\(\s*\(uint\)actionType,\s*actionId,\s*resolvedActionId,\s*targetId,\s*extraParam,\s*\(uint\)mode,\s*comboRouteId\);[\s\S]*safetySeed\s*=\s*new OwnedNativeQueueSafetySeed\(\s*runtime\.Candidate\.Snapshot,\s*currentSnapshot,\s*includeResolverTargets,\s*runtime\.Candidate\.ExplicitTargetAddress\);[\s\S]*execution\.ExactTuple\s*=\s*exactTuple;[\s\S]*return true;' 'Direct same-slot Turbo no longer creates one fresh exact tuple with root/invocation/explicit-target safety provenance.'

$directUseActionBranchMatch = [regex]::Match(
    $useActionDetour,
    'if \(!suppressSyntheticMacroCall[\s\S]*activeDirectPulseExecution\s+is\s+\{\s*\}\s+directPulseExecution\)[\s\S]*?(?=\r?\n\s*if \(!suppressSyntheticMacroCall[\s\S]*activeMacroPulseExecution)')
if (-not $directUseActionBranchMatch.Success) {
    throw 'The direct same-slot UseAction authorization branch could not be isolated.'
}
$directUseActionBranch = $directUseActionBranchMatch.Value
Assert-Contains $directUseActionBranch 'TryAuthorizeDirectPulseInvocation\([\s\S]*out var directTuple,[\s\S]*out var directSafetySeed\)[\s\S]*new DirectPulseAttempt\(\s*directPulseExecution,\s*directSafetySeed,\s*directTuple,\s*CaptureNativeQueue\(thisPtr\),\s*sequenceBefore\)[\s\S]*else[\s\S]*suppressSyntheticMacroCall\s*=\s*true;' 'Direct same-slot Turbo does not capture a per-pulse exact tuple/safety provenance/queue baseline or suppress an unauthorized nested call before Original.'

$directOutcomeMatch = [regex]::Match(
    $runtime,
    'private void ProcessDirectPulseAttempt\([\s\S]*?(?=\r?\n\s*private bool IsOwnedMacroTurboQueueDrain\()')
if (-not $directOutcomeMatch.Success) {
    throw 'The direct same-slot native-outcome method could not be isolated.'
}
$directOutcome = $directOutcomeMatch.Value
Assert-Contains $directOutcome 'NativeActionOutcomeClassifier\.Classify\(\s*result\s*\|\|\s*sequenceAdvanced,\s*attempt\.QueueBefore,\s*queueAfter,\s*attempt\.ExactTuple\)' 'Direct pulse outcome is not classified against its exact per-pulse tuple and queue baseline.'
Assert-Contains $directOutcome 'ImmediateAcceptance\s*&&\s*sequenceAdvanced[\s\S]*BeginTurboAcknowledgement\(\s*runtime,\s*execution\.Token,\s*TurboAcknowledgementSequenceMode\.ImmediateExact,\s*currentSequence,\s*attempt\.ExactTuple\)' 'Immediate direct acceptance lacks an exact per-pulse action-effect acknowledgement identity.'
Assert-Contains $directOutcome 'MatchingNewQueue\s*&&\s*!sequenceAdvanced[\s\S]*TryClaimOwnedNativeQueue\([\s\S]*attempt\.ExactTuple,[\s\S]*attempt\.SafetySeed[\s\S]*outcomeStillOwned\s*=\s*ReferenceEquals\(turboRuntime,\s*runtime\)[\s\S]*if \(!outcomeStillOwned\)[\s\S]*RetryExactOwnedNativeQueueSafetyClear\([\s\S]*TryReplaceOwnedNativeQueue\([\s\S]*return;[\s\S]*BeginTurboAcknowledgement\(\s*runtime,\s*execution\.Token,\s*TurboAcknowledgementSequenceMode\.QueuedAfterBaseline,\s*attempt\.SequenceBefore,\s*attempt\.ExactTuple\)' 'Queued direct acceptance lacks semantic ownership, stale post-Original cleanup, or a per-pulse acknowledgement baseline.'
Assert-Contains $directOutcome 'RejectTurboPulseUnsafe\(' 'An unproven direct same-slot outcome can be retried.'

$macroDispatchMatch = [regex]::Match(
    $runtime,
    'private void DispatchMacroTurboPulse\([\s\S]*?(?=\r?\n\s*private MacroTurboObservation ObserveMacroTurbo\()')
if (-not $macroDispatchMatch.Success) {
    throw 'The audited Macro Turbo slot dispatch method could not be isolated.'
}
$macroDispatch = $macroDispatchMatch.Value
Assert-Contains $macroDispatch 'lock \(dispatchGate\)[\s\S]*turboEngine\.IsTokenCurrent\(token\)[\s\S]*ReferenceEquals\(macroTurboRuntime,\s*runtime\)[\s\S]*activeMacroPulseExecution\s+is\s+not\s+null[\s\S]*engine\.Pending\s+is\s+not\s+null' 'Macro Turbo final dispatch is not serialized with the exact current pulse/runtime, single execution scope, and one-shot exclusion.'
Assert-Contains $macroDispatch 'ObserveMacroTurbo\(runtime,\s*checkMacroHash:\s*true\)[\s\S]*IsTurboSafetySafe\(observation\.Safety\)[\s\S]*observation\.ActionReady[\s\S]*observation\.HotbarModule\s*==\s*null' 'Macro Turbo does not repeat complete content/context safety and readiness validation immediately before slot replay.'
Assert-Contains $macroDispatch 'runtime\.NextExecutionEpoch\s*==\s*long\.MaxValue[\s\S]*CancelTurboUnsafe\([\s\S]*executionEpoch\s*=\s*\+\+runtime\.NextExecutionEpoch;[\s\S]*runtime\.ActiveExecutionEpoch\s*=\s*executionEpoch;[\s\S]*executionBudget\s*=\s*new MacroTurboExecutionBudget\(runtime\.MacroProfile\.ActionCount\);[\s\S]*runtime\.ActiveExecutionBudget\s*=\s*executionBudget;[\s\S]*runtime\.ActiveExecutionToken\s*=\s*token;' 'A Macro pulse can begin without a unique epoch, fresh analyzed ActionCount budget, and exact pulse token.'
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
Assert-Contains $macroDispatch 'activeMacroPulseExecution\s*=\s*new MacroPulseExecutionScope\(runtime,\s*token,\s*executionEpoch\);[\s\S]*runtime\.OwnsMacroExecutor\s*=\s*true;[\s\S]*turboDispatching\s*=\s*true;[\s\S]*hotbarExecutionDepth\+\+;[\s\S]*try[\s\S]*executeSlotByIdHook\.Original[\s\S]*finally[\s\S]*hotbarExecutionDepth--;[\s\S]*turboDispatching\s*=\s*false;[\s\S]*activeMacroPulseExecution\s*=\s*null;[\s\S]*runtime\.OwnsMacroExecutor\s*=\s*ReferenceEquals\(macroTurboRuntime,\s*runtime\)[\s\S]*IsMacroExecutionActive\(\)[\s\S]*ReferenceEquals\(macroTurboRuntime,\s*runtime\)[\s\S]*!runtime\.OwnsMacroExecutor[\s\S]*TryCompleteMacroExecutionEpoch\(\s*runtime,\s*executionEpoch,\s*"synchronous slot return"\)' 'Macro slot replay is not bound to one bounded execution epoch/executor owner or can skip synchronous Finish validation.'
Assert-Contains $macroDispatch 'turboPulseCount\+\+;[\s\S]*!ReferenceEquals\(macroTurboRuntime,\s*runtime\)\) return;' 'A terminal budget cancellation during Macro slot execution can be mistaken for a still-owned pulse.'

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
if ($executeSlotByIdDetourCalls -ne 1 -or $executeSlotByIdOriginalCalls -ne 3) {
    throw "Expected only the manual pass-through plus one direct and one Macro same-slot Turbo dispatch; found $executeSlotByIdDetourCalls manual and $executeSlotByIdOriginalCalls total call sites."
}
Assert-Contains $runtime 'private void RejectTurboPulseUnsafe\([\s\S]*?HoldRepeatCancelReason\.PulseRejected,[\s\S]*?hold ended without retry' 'An unproven direct-Turbo pulse is not terminal without retry.'

$processTurboMatch = [regex]::Match(
    $runtime,
    'private void ProcessTurbo\([\s\S]*?(?=\r?\n\s*private void ProcessMacroTurboUnsafe\()')
if (-not $processTurboMatch.Success) {
    throw 'The audited direct/Macro Turbo runtime router could not be isolated.'
}
$processTurbo = $processTurboMatch.Value
Assert-Contains $runtime '\[ThreadStatic\][\s\S]*private static MacroPulseExecutionScope\? activeMacroPulseExecution;[\s\S]*\[ThreadStatic\][\s\S]*private static DirectPulseExecutionScope\? activeDirectPulseExecution;[\s\S]*private TurboRuntime\? turboRuntime;[\s\S]*private MacroTurboRuntime\? macroTurboRuntime;' 'Direct and Macro pulse scopes/runtimes no longer have distinct ownership state.'
Assert-Contains $runtime 'private sealed class MacroTurboRuntime\([\s\S]*MacroTurboExecutionBudget\? initialExecutionBudget,\s*int initialPhysicalActionCallCount\)[\s\S]*public MacroTurboExecutionBudget\? InitialExecutionBudget\s*\{\s*get;\s*set;\s*\}\s*=\s*initialExecutionBudget;[\s\S]*public int InitialPhysicalActionCallCount\s*\{\s*get;\s*set;\s*\}\s*=\s*initialPhysicalActionCallCount;[\s\S]*public int InitialAcceptedOutcomeCount\s*\{\s*get;\s*set;\s*\}[\s\S]*public bool OwnsMacroExecutor\s*\{\s*get;\s*set;\s*\}[\s\S]*public ExactActionTuple\? OwnedQueueTuple\s*\{\s*get;\s*set;\s*\}[\s\S]*public long NextExecutionEpoch\s*\{\s*get;\s*set;\s*\}[\s\S]*public long ActiveExecutionEpoch\s*\{\s*get;\s*set;\s*\}[\s\S]*public MacroTurboExecutionBudget\? ActiveExecutionBudget\s*\{\s*get;\s*set;\s*\}[\s\S]*public HoldRepeatPulseToken\? ActiveExecutionToken\s*\{\s*get;\s*set;\s*\}[\s\S]*private sealed record MacroPulseExecutionScope\(\s*MacroTurboRuntime Runtime,\s*HoldRepeatPulseToken Token,\s*long ExecutionEpoch\);' 'Macro Turbo runtime no longer retains its cumulative physical-call bound, initial/active budgets, exact accepted-outcome count, executor/queue owner, epoch, and pulse token.'
Assert-Contains $runtime 'private sealed class DirectPulseExecutionScope\([\s\S]*TurboRuntime Runtime[\s\S]*HoldRepeatPulseToken Token[\s\S]*uint ExpectedResolvedActionId[\s\S]*int InvocationCount[\s\S]*bool Completed[\s\S]*bool Accepted[\s\S]*ExactActionTuple\? ExactTuple[\s\S]*ushort SequenceBefore[\s\S]*ushort SequenceAfter[\s\S]*NativeQueueSnapshot QueueAfter' 'Direct pulse scope no longer retains one pulse token, dynamic expected action ID, call cardinality, exact tuple, and native outcome state.'
Assert-Contains $processTurbo '!snapshot\.HasActiveHold[\s\S]*turboRuntime\s*=\s*null;[\s\S]*macroTurboRuntime\s*=\s*null;[\s\S]*turboAcknowledgement,\s*null[\s\S]*macroTurboAcknowledgement,\s*null' 'An inactive hold does not clear both runtime owners and both acknowledgement barriers.'
Assert-Contains $processTurbo 'if \(macroTurboRuntime\s+is\s+\{\s*\}\s+macroRuntime\)[\s\S]*ProcessMacroTurboUnsafe\(macroRuntime,\s*snapshot,\s*now\);[\s\S]*return;[\s\S]*if \(turboRuntime\s+is\s+not\s+\{\s*\}\s+runtime\)' 'Macro and direct Turbo are no longer routed as mutually exclusive runtime owners.'
Assert-Contains $processTurbo 'observation\.ActionReady\s*&&\s*acknowledgement\s+is\s+null[\s\S]*if \(acknowledgement\s+is\s+not\s+null\)[\s\S]*MaximumTurboAcknowledgementMilliseconds[\s\S]*PulseRejected[\s\S]*return;[\s\S]*DispatchTurboPulse' 'Direct Turbo can issue another pulse while an original, one-shot, or prior pulse acknowledgement is pending or timed out.'
if (($macroProcess + "`n" + $macroDispatch + "`n" + $macroObserve) -match '\bBeginTurboAcknowledgement\s*\(' -or
    ($macroDispatch + "`n" + $macroObserve) -match 'useActionHook\.Original') {
    throw 'Macro Turbo incorrectly borrows the direct acknowledgement method or bypasses same-slot dispatch through UseAction.'
}
Assert-Contains $runtime 'private void CancelTurboUnsafe\([\s\S]*?turboEngine\.Cancel\(reason\);[\s\S]*?turboRuntime\s*=\s*null;[\s\S]*?macroTurboRuntime\s*=\s*null;[\s\S]*?turboAcknowledgement,\s*null[\s\S]*?macroTurboAcknowledgement,\s*null' 'Turbo cancellation does not invalidate the hold, both runtimes, and both acknowledgement barriers together.'
Assert-Contains $runtime 'MaximumTurboAcknowledgementMilliseconds\s*=\s*2_000\s*;' 'Turbo acknowledgement timeout changed from the audited bound.'
Assert-Contains $runtime 'new TurboActionEffectExpectation\([\s\S]*?exactTuple\.ActionType,[\s\S]*?exactTuple\.RequestedActionId,[\s\S]*?exactTuple\.ResolvedActionId,[\s\S]*?sequenceMode,[\s\S]*?sequenceMarker' 'Turbo acknowledgement does not retain exact type, requested ID, resolved ID, and sequence identity.'
Assert-Contains $runtime 'TryCompleteTurboAcknowledgement\(header\)[\s\S]*?TurboActionEffectAcknowledgementMatcher\.Matches\([\s\S]*?acknowledgement\.Expectation,[\s\S]*?observation\)' 'Local-player action effects are not matched against the exact Turbo acknowledgement identity.'
Assert-Contains $runtime 'header->SourceSequence\s*!=\s*0\s*&&\s*casterEntityId\s*==\s*currentLocalEntityId[\s\S]*?TryCompleteTurboAcknowledgement\(header\)' 'A zero-sequence or foreign-caster action effect can reach Turbo acknowledgement matching.'
Assert-Contains $runtime 'private bool BeginTurboAcknowledgement\([\s\S]*pulse\s+is\s+\{\s*\}\s+pulseToken[\s\S]*!pulseToken\.IsValid\s*\|\|\s*!turboEngine\.IsTokenCurrent\(pulseToken\)[\s\S]*new TurboAcknowledgement\([\s\S]*snapshot\.HoldId,[\s\S]*snapshot\.PressId' 'A pulse acknowledgement can be installed without the exact current pulse, hold, and press token.'
Assert-Contains $runtime 'private bool IsTurboAcknowledgementCurrent\([\s\S]*snapshot\.HoldId\s*==\s*acknowledgement\.HoldId[\s\S]*snapshot\.PressId\s*==\s*acknowledgement\.PressId[\s\S]*latestCertifiedPressId[\s\S]*acknowledgement\.Pulse\s+is\s+not\s+\{\s*\}\s+pulse[\s\S]*turboEngine\.IsTokenCurrent\(pulse\)' 'A stale hold, press, or pulse token can complete a newer Turbo acknowledgement.'
Assert-Contains $runtime 'now\s*-\s*acknowledgement\.StartedAtMilliseconds\s*>\s*MaximumTurboAcknowledgementMilliseconds[\s\S]*?HoldRepeatCancelReason\.PulseRejected' 'A missing Turbo acknowledgement is not terminal without retry.'
Assert-Contains $runtime 'private sealed record MacroTurboAcknowledgementSeed\(\s*TurboActionEffectExpectation Expectation,\s*long StartedAtMilliseconds\);[\s\S]*private sealed record MacroTurboAcknowledgement\(\s*MacroTurboRuntime Runtime,\s*HoldRepeatPulseToken\? Pulse,\s*long ExecutionEpoch,\s*long HoldId,\s*long PressId,\s*TurboActionEffectExpectation Expectation,\s*long StartedAtMilliseconds\);' 'Macro Turbo acknowledgement no longer retains its exact tuple/sequence expectation, runtime, pulse, epoch, hold, press, and start time.'
Assert-Contains $runtime 'private bool BeginMacroTurboAcknowledgement\([\s\S]*WasRecentlyAcknowledged\(seed\)[\s\S]*seed\.Expectation\.IsValid[\s\S]*snapshot\.PressId\s*!=\s*runtime\.Press\.PressId[\s\S]*!ReferenceEquals\(macroTurboRuntime,\s*runtime\)[\s\S]*pulse\s+is\s+null\s*&&\s*executionEpoch\s*!=\s*0[\s\S]*!turboEngine\.IsTokenCurrent\(pulseToken\)[\s\S]*new MacroTurboAcknowledgement\(' 'A physical or synthetic Macro acknowledgement can be installed without exact current hold/pulse/epoch identity or the early exact-ack cache.'
Assert-Contains $runtime 'private bool IsMacroTurboAcknowledgementCurrent\([\s\S]*snapshot\.HoldId\s*==\s*acknowledgement\.HoldId[\s\S]*snapshot\.PressId\s*==\s*acknowledgement\.PressId[\s\S]*latestCertifiedPressId[\s\S]*acknowledgement\.ExecutionEpoch\s*>\s*0[\s\S]*turboEngine\.IsTokenCurrent\(pulse\)' 'A stale Macro runtime, hold, press, epoch, or pulse can complete a newer acknowledgement.'
Assert-Contains $runtime 'TryCompleteTurboAcknowledgement\([\s\S]*TryCompleteMacroTurboAcknowledgementUnsafe\(header\)[\s\S]*TurboActionEffectAcknowledgementMatcher\.Matches\([\s\S]*acknowledgement\.Expectation,[\s\S]*observation' 'The local ActionEffect path no longer checks both the Macro and direct exact acknowledgement barriers.'
Assert-Contains $runtime 'private void TryCompleteMacroTurboAcknowledgementUnsafe\([\s\S]*IsMacroTurboAcknowledgementCurrent\(acknowledgement\)[\s\S]*TurboActionEffectAcknowledgementMatcher\.Matches\(\s*acknowledgement\.Expectation,\s*observation\)[\s\S]*CompareExchange\(\s*ref macroTurboAcknowledgement,\s*null,\s*acknowledgement\)' 'Macro ActionEffect acknowledgement no longer requires exact current identity and one atomic consumption.'
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
Assert-Contains $configuration 'CurrentVersion\s*=\s*4\s*;' 'Turbo configuration schema is not version 4.'
Assert-Contains $configuration 'DefaultTurboInitialDelayMilliseconds\s*=\s*0\s*;' 'The responsive default Turbo initial delay is not exactly 0 ms.'
Assert-Contains $configuration 'DefaultTurboRepeatIntervalMilliseconds\s*=\s*60\s*;' 'The default Turbo cadence is not exactly the hard 60 ms floor.'
Assert-Contains $configuration 'LegacyTurboInitialDelayMilliseconds\s*=\s*180\s*;[\s\S]*LegacyTurboRepeatIntervalMilliseconds\s*=\s*80\s*;' 'The exact schema-3 legacy default pair is no longer explicit.'
Assert-Contains $configuration 'if \(Version\s*==\s*3[\s\S]*TurboInitialDelayMs\s*==\s*LegacyTurboInitialDelayMilliseconds[\s\S]*TurboRepeatIntervalMs\s*==\s*LegacyTurboRepeatIntervalMilliseconds\)[\s\S]*TurboInitialDelayMs\s*=\s*DefaultTurboInitialDelayMilliseconds;[\s\S]*TurboRepeatIntervalMs\s*=\s*DefaultTurboRepeatIntervalMilliseconds;' 'Schema 3 exact legacy 180/80 defaults are not migrated to 0/60 without rewriting custom timing.'
Assert-Contains $configuration 'if \(Version\s*<=\s*1\)[\s\S]*TurboEnabled\s*=\s*false' 'Existing configurations can silently opt into native Turbo.'
Assert-Contains $configuration 'if \(Version\s*<=\s*2\)[\s\S]*TurboMacrosEnabled\s*=\s*false' 'Existing configurations can silently opt into Macro Turbo.'
Assert-Contains $configuration 'ResetToDefaults\(\)[\s\S]*TurboEnabled\s*=\s*false' 'Reset defaults no longer keep native Turbo opt-in.'
Assert-Contains $configuration 'ResetToDefaults\(\)[\s\S]*TurboMacrosEnabled\s*=\s*false' 'Reset defaults no longer keep Macro Turbo opt-in.'
Assert-Contains $compatibility 'if \(configuration\.TurboHotbarsEnabled\)\s*\{[\s\S]*?conflicts\.Add\(\s*"Disable ReAction''s Turbo Hotbars;' 'ReAction Turbo no longer creates an actionable hard conflict.'
Assert-Contains $compatibility 'if \(configuration\.MacroQueueEnabled\)\s*\{[\s\S]*?conflicts\.Add\(\s*"Disable ReAction''s Macro Queue;' 'ReAction Macro Queue no longer creates an actionable hard conflict.'

# No background scheduler and no new mutation authority: framework ticks are
# the only clock, both ActionQueued clears consume exact certified ownership,
# and neither input/core/config code may write target or lock state.
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
