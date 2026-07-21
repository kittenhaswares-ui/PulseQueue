# PulseQueue

PulseQueue is a conservative one-shot smart input buffer for Final Fantasy XIV.
If a direct hotbar action is pressed a little too early and vanilla FFXIV rejects
it only because of a short GCD/local recast or animation lock, PulseQueue can
retain that exact action briefly and submit it once when the client reports it
ready. This includes the intended PvP Guard cooldown-edge use case.

This is an open testing release, not an official Dalamud plugin.

## Safety contract

- One captured hotbar intent can produce at most one replay.
- Vanilla FFXIV always receives the original press first. An action the game
  accepts or queues is never buffered again.
- The action type, base and adjusted action IDs, target, extra parameter, combo
  route, hard target, and soft target are immutable while pending. Mouseover
  resolver targets are also bound when an action arrives without a concrete
  target ID and can target something other than self.
- A new hotbar input cancels the old pending intent before the new input runs.
- There is no alternative-action selection, target selection, target fallback,
  key repeat, automatic retry, or retry after a server rejection.
- The token is consumed before replay. The replay uses the game's non-requeueing
  queue-execution mode, so a failed replay is terminal.
- The hard lifetime cap is 180 ms. Latency detection may shorten that window but
  can never extend it.
- Death, stun, forced movement, target change, action transformation, native
  queue activity, logout, job/PvP context change, instance or territory change,
  a frame stall, plugin disable, or any uncertain state clears the token.
- PulseQueue never writes animation lock, cooldowns, resources, targets, or the
  game's native queue fields.

## Supported testing scope

Version 0.1 accepts only instant, non-ground-target `Action` and `PvPAction`
attempts reached through the standard or cross hotbar with the normal action
mode. Macros, items, casts, combo-mode calls, ground placement, mounts, pets,
duty actions, crafting, and direct calls from other plugins are excluded.

The hotbar scope supports keyboard, mouse, and controller paths that run through
FFXIV's standard slot executor. It cannot prove that an invocation was a
physical electrical key event, so the strict guarantee is one certified hotbar
intent, not one hardware event.

## Adaptive timing

PulseQueue does not ping a server and does not inspect packet opcodes. It matches
the local action sequence with the later local-player action effect and keeps a
small in-memory rolling estimate of effective action-response time. That value
only selects a hold window between the conservative floor and the 180 ms cap;
actual client readiness is still mandatory before replay.

Timing samples are session-only and never leave the computer.

## Conflicts

Buffering suspends and clears immediately while any of these loaded plugins is
detected:

- NoClippy
- NoClippyUnchained
- ReAction
- ReActionEx

PulseQueue does not unload, reconfigure, or modify them. ReAction variants can
replace actions or targets, while NoClippy variants alter the timing layer; the
v0.1 policy is intentionally fail-closed until a real compatibility matrix has
been completed.

## Install

After a testing release has been published, add this URL under **Dalamud
Settings -> Experimental -> Custom Plugin Repositories**:

```text
https://raw.githubusercontent.com/kittenhaswares-ui/PulseQueue/main/repo.json
```

Enable testing plugins in Dalamud, then install **PulseQueue**.

Use `/pulsequeue` to open the status window. Useful commands:

```text
/pulsequeue
/pulsequeue on
/pulsequeue off
/pulsequeue status
/pulsequeue dry on
/pulsequeue dry off
/pulsequeue log on
/pulsequeue reset
```

## Important account-risk notice

Square Enix prohibits third-party tools and unauthorized gameplay-modifying
software. A bounded, direct-input design can reduce technical mistakes, but it
cannot make plugin use sanctioned, undetectable, or account-safe. PulseQueue is
published as transparent source and a testing-only custom-repository build; use
is at the user's own risk.

The official Dalamud repository also restricts combat automation and PvP
advantages, so this project does not claim eligibility for that repository.

## Development disclosure

This initial implementation and its review were substantially AI-assisted. The
repository publishes the complete source, deterministic tests, build scripts,
release fingerprint, and mandatory live-test matrix so a human maintainer can
audit and validate every native interaction. No claim of human in-game
validation is made for version 0.1.0.0.

## Validation status

The dependency-free state machine is covered by 52 deterministic invariant
tests, including a seeded 10,000-step adversarial trace.
The native integration must additionally pass the live matrix in
[`docs/LIVE_TEST_MATRIX.md`](docs/LIVE_TEST_MATRIX.md) on the current FFXIV
patch before this testing flag can be removed. Until that evidence exists, do
not describe the plugin as production-validated. The exact automated results
and release hash are recorded in
[`docs/VALIDATION_REPORT.md`](docs/VALIDATION_REPORT.md).
The published ZIP checksum is also stored beside it as
[`dist/latest.zip.sha256`](dist/latest.zip.sha256).

## Development

The project targets Dalamud API 15 and .NET 10.

```powershell
dotnet run --project tests/PulseQueue.Core.SelfTest -c Release
dotnet restore src/PulseQueue.Plugin/PulseQueue.Plugin.csproj --use-lock-file
dotnet build src/PulseQueue.Plugin/PulseQueue.Plugin.csproj -c Release --no-restore
./scripts/Verify-SafetyContract.ps1
./scripts/Build-Release.ps1
./scripts/Verify-Release.ps1
```

## License

MIT. The implementation is original and does not copy code from NoClippy or
ReAction.
