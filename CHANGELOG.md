# Changelog

## 0.2.0.0 — 2026-07-22

- Supports NoClippy 0.5.0.24 while leaving animation-lock correction entirely
  under NoClippy's control.
- Supports ReAction 1.3.5.1 only when Turbo Hotbars, Auto Target, Auto Dismount,
  and Camera Relative Directionals are off and Action Stacks is empty.
- Excludes action IDs retargeted through MOAction 4.10.1's published IPC.
- Makes every newly observed standard/cross-hotbar input invalidate the pending
  generation so the newest input replaces an older buffered weave.
- Lets a newer valid hotbar input replace one exact unchanged native queue entry
  only when that entry was proven to come from an older certified hotbar input;
  foreign/native integration queues remain untouched.
- Classifies native acceptance using an exact before/after queue tuple rather
  than crediting unrelated or pre-existing queue activity.
- Clears pending input on plugin/configuration changes and mounting; movement
  actions and ReAction's camera-relative action 29494 exception are no longer
  eligible for buffering.
- Handles the next charge boundary for multi-charge recasts and linearizes
  topology/knockback cancellation with final dispatch.
- Fails closed for unknown integration versions or unreadable/unsafe settings.

This remains a testing-only custom-repository release pending completion of the
expanded live validation matrix.

## 0.1.0.0 — 2026-07-21

- Initial one-shot buffer with a 180 ms hard cap, adaptive local response
  timing, immutable action/target capture, and fail-closed cancellation.
- Suspended entirely while NoClippy or ReAction variants were loaded.
