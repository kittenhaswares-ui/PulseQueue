using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using PulseQueue.Plugin.Models;
using PulseQueue.Plugin.Services;

namespace PulseQueue.Plugin.UI;

internal sealed class SettingsWindow : Window
{
    private readonly PluginConfiguration configuration;
    private readonly ActionBufferService actionBuffer;
    private readonly Action<string> apply;
    private readonly Action reset;

    public SettingsWindow(
        PluginConfiguration configuration,
        ActionBufferService actionBuffer,
        Action<string> apply,
        Action reset)
        : base("PulseQueue settings###PulseQueueSettings")
    {
        this.configuration = configuration;
        this.actionBuffer = actionBuffer;
        this.apply = apply;
        this.reset = reset;

        Size = new Vector2(610, 790);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(430, 430),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var diagnostics = actionBuffer.Diagnostics;

        ImGui.TextColored(new Vector4(0.42f, 0.84f, 1f, 1f), "PulseQueue");
        ImGui.TextWrapped("A strict one-shot smart buffer plus optional same-control keyboard Turbo for direct actions and explicitly opted-in action-only macro slots.");
        ImGui.Spacing();

        DrawStatus(diagnostics);
        ImGui.Separator();

        DrawCheckbox("Enable PulseQueue", configuration.Enabled, value => configuration.Enabled = value);
        DrawCheckbox("Dry run (detect only; Turbo pauses)", configuration.DryRun, value => configuration.DryRun = value);
        DrawCheckbox("Detailed Dalamud logging", configuration.DetailedLogging, value => configuration.DetailedLogging = value);

        ImGui.Separator();
        ImGui.TextUnformatted("Native Turbo (experimental)");
        ImGui.TextWrapped("Hold one physical keyboard-bound standard-hotbar control. PulseQueue reruns only that certified slot once per bounded pulse; FFXIV may resolve its current combo/transformed action, which is revalidated before the one native call is allowed.");
        if (configuration.Version > PluginConfiguration.CurrentVersion)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.55f, 0.3f, 1f),
                "Turbo is locked off because this settings file belongs to a newer PulseQueue version.");
            ImGui.TextWrapped("Keep it untouched for downgrade safety, or use Reset settings only if you deliberately want to replace it with this version's defaults.");
        }
        else
        {
            DrawCheckbox("Enable native Turbo", configuration.TurboEnabled, value => configuration.TurboEnabled = value);
            DrawSlider(
                "Initial delay (ms)",
                configuration.TurboInitialDelayMs,
                PluginConfiguration.MinimumTurboInitialDelayMilliseconds,
                PluginConfiguration.MaximumTurboInitialDelayMilliseconds,
                value => configuration.TurboInitialDelayMs = value);
            DrawSlider(
                "Repeat interval (ms)",
                configuration.TurboRepeatIntervalMs,
                PluginConfiguration.MinimumTurboRepeatIntervalMilliseconds,
                PluginConfiguration.MaximumTurboRepeatIntervalMilliseconds,
                value => configuration.TurboRepeatIntervalMs = value);
            DrawCheckbox(
                "Allow Turbo outside combat",
                configuration.TurboOutOfCombat,
                value => configuration.TurboOutOfCombat = value);
            DrawCheckbox(
                "Enable action-only macro-slot Turbo (separate explicit opt-in)",
                configuration.TurboMacrosEnabled,
                value => configuration.TurboMacrosEnabled = value);
            if (configuration.TurboMacrosEnabled)
            {
                ImGui.TextColored(
                    new Vector4(1f, 0.68f, 0.25f, 1f),
                    "This is real same-control Macro Turbo: FFXIV reruns the same held macro slot.");
                ImGui.TextWrapped("Allowed: one or more /ac, /action, /pvpac, or /pvpaction lines plus icon/error metadata. /assist, waits, chat, target, marker, item, gearset, hotbar, and every unknown command fail closed.");
                ImGui.TextWrapped("PulseQueue never chooses a macro line or target itself. FFXIV executes the unchanged player-authored macro once per bounded pulse, so different action lines may succeed as game state changes.");
                ImGui.TextWrapped("The static action-line count is a hard maximum per run, not an exact transcript. Each observed line is live-validated; after one line is accepted or queued, every later fallback line in that pulse is stopped before native execution.");
                ImGui.TextWrapped("Cast-time, ground-target, movement, and MOAction-owned lines remain excluded. Zero locally accepted lines may wait for the next bounded hold pulse; a sent action still requires an exact acknowledgement and is never retried after rejection.");
                if (!configuration.TurboEnabled)
                {
                    ImGui.TextDisabled("Macro Turbo is armed as a preference but remains inactive until native Turbo is also enabled.");
                }
            }
        }
        ImGui.TextWrapped($"Turbo: {diagnostics.TurboStatus}");
        ImGui.TextDisabled("Testing scope: exact keyboard chord on standard-hotbar direct Action slots, plus instant non-ground action-only Macro slots with the separate Macro Turbo opt-in. Items, movement, MOAction-owned actions, side-effect macros, mouse, controller, cross-hotbar, and plugin-originated input cannot own Turbo.");
        ImGui.TextDisabled("Every accepted direct or macro action waits for an exact action-effect acknowledgement. Local unavailability can wait for a later bounded pulse; server rejection ends ownership. Every hold stops after 30 s.");
        ImGui.TextDisabled("A newer certified direct or certified action-only macro press always preempts an older exact PulseQueue-owned queue, even if the new slot is too early or emits no action call. Foreign or changed queues are never cleared.");
        ImGui.TextDisabled("ReAction Turbo Hotbars and Macro Queue must be off so there is only one repeat/queue owner. Auto Dismount and Camera Relative Directionals may stay on; their mounted or movement inputs are passed through and never owned. NoClippy may stay on.");

        ImGui.Spacing();
        if (ImGui.Button("Clear pending input and stop Turbo"))
        {
            actionBuffer.Cancel(PulseQueue.Core.CancelReason.Explicit, "Cleared from settings");
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset settings and fault latch")) reset();

        ImGui.Separator();
        ImGui.TextUnformatted("Hard safety contract");
        ImGui.BulletText("Smart buffer: the original player action is sent first; at most that exact tuple can be replayed once.");
        ImGui.BulletText("Direct Turbo: one held key reruns only its certified hotbar slot; the current same-slot adjusted action is freshly validated for every bounded pulse.");
        ImGui.BulletText("Macro Turbo: one held key repeats only the same certified macro slot; FFXIV, not PulseQueue, evaluates its action lines.");
        ImGui.BulletText("Every macro pulse has the authored action-line count as a maximum and may produce at most one accepted native action; later fallback calls are suppressed before Original.");
        ImGui.BulletText("Each Macro Turbo pulse invokes the slot once with no catch-up burst; PulseQueue never writes targets, lock, recast, or resources.");
        ImGui.BulletText("A newer hotbar action replaces the old token.");
        ImGui.BulletText("Any newer certified direct or certified action-only macro root clears one exact older PulseQueue-owned queue before its slot runs, regardless of readiness or whether it emits an action call.");
        ImGui.BulletText("Ordinary release and a physical original that declines Turbo preserve accepted vanilla queue intent.");
        ImGui.BulletText("Death, stun, forced movement, target/resolver/context/zone change, frame stall, or another terminal safety event clears every hold and exact-clears owned queue state, including deferred outcomes.");
        ImGui.BulletText("Mounted state and movement actions are never buffered.");
        ImGui.BulletText("NoClippy 0.5.0.24 may own animation-lock timing; PulseQueue never writes that lock.");
        ImGui.BulletText("ReAction 1.3.5.1 requires empty Action Stacks plus Auto Target, Turbo Hotbars, and Macro Queue off. Auto Dismount and Camera Relative Directionals are allowed because their affected inputs are excluded.");
        ImGui.BulletText("MOAction-targeted skills are passed through normally but excluded from replay.");
        ImGui.BulletText("Only instant, non-ground-targeted Action/PvPAction hotbar inputs are eligible.");
        ImGui.TextWrapped("The one-shot smart-buffer hold window is learned from your own acknowledged action timing, stays between 80 and 180 ms after warm-up, and never decides whether an action is legal.");

        ImGui.Separator();
        ImGui.TextDisabled("Live diagnostics");
        ImGui.TextUnformatted($"Window: {diagnostics.HoldWindowMilliseconds} ms");
        ImGui.TextUnformatted($"Response estimate: {diagnostics.EstimatedResponseMilliseconds:0.0} ms ({diagnostics.AcceptedTimingSamples} accepted samples)");
        ImGui.TextUnformatted($"Captured / dispatched / dry-run: {diagnostics.Captured} / {diagnostics.Dispatched} / {diagnostics.DryRunDispatches}");
        ImGui.TextUnformatted($"Replay rejected (never retried): {diagnostics.ReplayRejected}");
        ImGui.TextUnformatted($"Hotbar inputs / replaced pending: {diagnostics.ObservedHotbarInputs} / {diagnostics.ReplacedPendingInputs}");
        ImGui.TextUnformatted($"Native queue accepted / blocked: {diagnostics.NativeQueueAccepted} / {diagnostics.NativeQueueBlocked}");
        ImGui.TextUnformatted($"Owned native queues replaced by newer input: {diagnostics.OwnedNativeQueueReplacements}");
        ImGui.TextUnformatted($"Owned native queues exact-cleared by terminal safety: {diagnostics.OwnedNativeQueueSafetyClears}");
        ImGui.TextUnformatted($"MOAction exclusions observed: {diagnostics.IntegrationExclusions} ({diagnostics.ExcludedIntegrationActions} configured IDs)");
        ImGui.TextUnformatted($"Turbo starts / pulses / accepted / rejected: {diagnostics.TurboStarts} / {diagnostics.TurboPulses} / {diagnostics.TurboAccepted} / {diagnostics.TurboRejected}");
        ImGui.TextUnformatted($"Native held repeats suppressed by the active Turbo owner: {diagnostics.TurboSuppressedHeldRepeats}");
        ImGui.TextUnformatted($"Turbo state / last cancellation: {diagnostics.TurboState} / {diagnostics.TurboLastCancelReason}");
        ImGui.TextUnformatted($"Last cancellation: {diagnostics.LastCancelReason}");
        ImGui.TextWrapped($"Last event: {diagnostics.LastEvent}");

        ImGui.Separator();
        ImGui.TextDisabled("Custom testing plugin");
        ImGui.TextWrapped("Use at your own risk. Third-party tools are not endorsed by Square Enix, and this plugin cannot make an account safe from enforcement. It makes no network requests and stores no character or combat history.");
    }

    private static void DrawStatus(BufferDiagnostics diagnostics)
    {
        var color = diagnostics.State switch
        {
            RuntimeState.Ready => new Vector4(0.42f, 0.9f, 0.55f, 1f),
            RuntimeState.Pending or RuntimeState.DryRun => new Vector4(1f, 0.78f, 0.28f, 1f),
            RuntimeState.Faulted => new Vector4(1f, 0.35f, 0.35f, 1f),
            _ => new Vector4(0.76f, 0.76f, 0.76f, 1f),
        };

        ImGui.TextColored(color, diagnostics.Status);
        if (diagnostics.Conflicts.Count > 0)
        {
            foreach (var conflict in diagnostics.Conflicts)
            {
                ImGui.TextWrapped($"Needs attention: {conflict}");
            }
        }

        if (diagnostics.Integrations.Count > 0)
        {
            ImGui.TextWrapped($"Active compatibility: {string.Join("; ", diagnostics.Integrations)}");
        }
    }

    private void DrawCheckbox(string label, bool current, Action<bool> assign)
    {
        var value = current;
        if (!ImGui.Checkbox(label, ref value)) return;
        assign(value);
        apply($"{label} changed");
    }

    private void DrawSlider(
        string label,
        int current,
        int minimum,
        int maximum,
        Action<int> assign)
    {
        var value = current;
        if (!ImGui.SliderInt(label, ref value, minimum, maximum)) return;
        assign(value);
        apply($"{label} changed");
    }
}
