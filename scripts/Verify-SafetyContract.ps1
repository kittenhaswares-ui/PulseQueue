param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$runtimePath = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/Services/ActionBufferService.cs'
$enginePath = Join-Path $RepositoryRoot 'src/PulseQueue.Core/BufferEngine.cs'
$manifestPath = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/PulseQueue.Plugin.json'

$runtime = Get-Content -LiteralPath $runtimePath -Raw
$engine = Get-Content -LiteralPath $enginePath -Raw
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

function Assert-Contains([string]$Value, [string]$Pattern, [string]$Message) {
    if ($Value -notmatch $Pattern) { throw $Message }
}

Assert-Contains $engine 'AbsoluteHoldCapMilliseconds\s*=\s*180\s*;' 'The hard hold cap is no longer exactly 180 ms.'
Assert-Contains $engine 'pending\s*=\s*null;[\s\S]*BufferDecisionKind\.Dispatch' 'The core no longer consumes before dispatch.'
Assert-Contains $engine 'ActionFailureKind\.Cooldown' 'Short local cooldown handling is missing from the core policy.'

$nativeWritePattern = '->(?:AnimationLock|ActionQueued|QueuedActionType|QueuedActionId|QueuedTargetId|QueuedExtraParam|QueueType|QueuedComboRouteId)\s*='
if ($runtime -match $nativeWritePattern) {
    throw 'Runtime source writes a protected native lock/queue field.'
}

if ($runtime -match 'targetManager\.(?:Target|SoftTarget|MouseOverTarget|MouseOverNameplateTarget|FocusTarget|PreviousTarget)\s*=') {
    throw 'Runtime source writes a target-manager field.'
}

if ($runtime -match 'UseActionLocation\s*\(') {
    throw 'Runtime source calls UseActionLocation; location/target substitution is forbidden.'
}

$queueModeCalls = [regex]::Matches($runtime, 'ActionManager\.UseActionMode\.Queue').Count
if ($queueModeCalls -ne 1) {
    throw "Expected exactly one explicit non-requeueing replay mode; found $queueModeCalls."
}

$originalCalls = [regex]::Matches($runtime, 'useActionHook\.Original\s*\(').Count
if ($originalCalls -ne 2) {
    throw "Expected exactly the original pass-through and one replay call site; found $originalCalls."
}

foreach ($name in @('NoClippy', 'NoClippyUnchained', 'ReAction', 'ReActionEx')) {
    Assert-Contains $runtime ('"' + [regex]::Escape($name) + '"') "Conflict gate $name is missing."
}

Assert-Contains $runtime 'runtime\.Candidate\.InterruptEpoch\s*!=\s*Volatile\.Read\(ref interruptEpoch\)' 'Final generation invalidation check is missing.'
Assert-Contains $runtime 'NowMilliseconds\s*>=\s*runtime\.ExpiresAtMilliseconds' 'Final monotonic deadline check is missing.'
Assert-Contains $runtime 'Interlocked\.Increment\(ref interruptEpoch\);[\s\S]*forcedMovementObserved' 'Immediate knockback invalidation is missing.'

if (-not [bool]$manifest.IsTestingExclusive) {
    throw 'The initial native-hook release must remain testing-exclusive.'
}

Write-Host 'PulseQueue static safety-contract checks passed.'
