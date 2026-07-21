# Validation report — 0.1.0.0

- Date: 2026-07-21
- Target: Dalamud API 15 / .NET 10
- Release channel: custom repository, testing-exclusive

## Completed

- Full solution compiles against the locally installed Dalamud API 15
  development assemblies with zero warnings and zero errors.
- 52/52 dependency-free state-machine and timing tests pass.
- The suite includes exact-deadline behavior, exact action/target preservation,
  every required cancellation reason, no-retry server rejection behavior,
  RTT outlier/rebase behavior, and a seeded 10,000-step adversarial trace.
- `dotnet format --verify-no-changes` passes for all 18 C# source files.
- Static native-safety regression checks pass: 180 ms cap, consume-before-send,
  one replay call site, non-requeueing replay mode, protected native fields never
  written, no target writes, conflict gates present, immediate knockback
  invalidation, final generation check, and final monotonic deadline check.
- Release ZIP structure, testing-channel manifest/API/version consistency, DLL
  assembly version, and source fingerprint pass the release verifier. The
  verifier now models Dalamud's effective testing version selection so a missing
  `TestingAssemblyVersion` or `TestingDalamudApiLevel` cannot be published again.

Verified ZIP SHA-256:

```text
a559ce08300c81b360b88e582c5038c35ed551eb16eeb6032088f8d1187f103a
```

## Still required before any production claim

No in-game native-hook test was performed in this build environment. A human
maintainer must run every case in `LIVE_TEST_MATRIX.md` on the current FFXIV
patch, starting in the Wolves' Den outside a competitive match. The two-hour
soak test and the complete NoClippy/ReAction load/unload matrix are also still
open.

Until those gates pass, version 0.1.0.0 must remain testing-exclusive and must
not be described as production-validated or account-safe.
