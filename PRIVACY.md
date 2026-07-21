# Privacy

PulseQueue has no server and makes no network requests. It does not collect or
publish character, account, combat-log, target, or identity data.

The plugin keeps a small rolling action-response timing sample in memory to
select a local buffer window. Samples are discarded when the plugin unloads
and are never written to disk by PulseQueue. Only the user's local on/off and
diagnostics preferences are saved through Dalamud.

Detailed logging is off by default. If the user enables it, PulseQueue writes
numeric action IDs, relative timing values, sequence counters, and cancellation
reasons to the local Dalamud log. It does not log character names, account
identifiers, target IDs, chat, or world positions. Dalamud controls retention of
that local log.
