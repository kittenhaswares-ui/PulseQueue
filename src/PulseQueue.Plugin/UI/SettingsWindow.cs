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
        ImGui.TextWrapped("A one-shot smart action buffer plus native held-input Turbo for standard hotbars.");
        ImGui.Spacing();

        DrawStatus(diagnostics);
        ImGui.Separator();

        DrawCheckbox("Enable PulseQueue", configuration.Enabled, value => configuration.Enabled = value);
        DrawCheckbox("Dry run (detect only; Turbo pauses)", configuration.DryRun, value => configuration.DryRun = value);
        DrawCheckbox("Detailed Dalamud logging", configuration.DetailedLogging, value => configuration.DetailedLogging = value);

        ImGui.Separator();
        ImGui.TextUnformatted("Native held-input Turbo (standard hotbars)");
        ImGui.TextWrapped("While you hold a logical standard-hotbar input, PulseQueue periodically reports that same input as pressed through FFXIV's native binding path. FFXIV resolves the current slot, action, target, combo state, or complete player-authored macro normally.");
        ImGui.TextWrapped("The newest pressed hotbar input owns Turbo. It immediately replaces an older held input; there is no catch-up burst.");
        if (configuration.Version > PluginConfiguration.CurrentVersion)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.55f, 0.3f, 1f),
                "Turbo is locked off because this settings file belongs to a newer PulseQueue version.");
            ImGui.TextWrapped("Keep it untouched for downgrade safety, or use Reset settings only if you deliberately want to replace it with this version's defaults.");
        }
        else
        {
            DrawCheckbox("Enable native held-input Turbo", configuration.TurboEnabled, value => configuration.TurboEnabled = value);
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
                "Queue actions invoked by macros",
                configuration.TurboMacrosEnabled,
                value => configuration.TurboMacrosEnabled = value);
            ImGui.TextDisabled("Native Turbo repeats the complete held slot, including arbitrary or multi-command macros, regardless of this checkbox. This checkbox controls only the queue mode used by action calls inside macros.");
            if (configuration.TurboMacrosEnabled)
            {
                ImGui.TextWrapped("When ReAction Macro Queue is not active, action calls made by macros use FFXIV's normal queue mode. PulseQueue does not parse the macro, choose a line, or change its target.");
            }
            else
            {
                ImGui.TextDisabled("This setting controls macro action queueing, not whether a held macro slot repeats.");
            }
        }
        ImGui.TextWrapped($"Turbo: {diagnostics.TurboStatus}");
        ImGui.TextDisabled("Current scope: logical inputs scanned for standard hotbars. Cross-hotbar/controller Turbo is not implemented yet. Directly clicking a slot has no held logical input to repeat.");
        ImGui.TextDisabled("Held macro slots repeat the entire authored macro through FFXIV. Commands and side effects in that macro can therefore run again; you control its contents.");
        ImGui.TextDisabled("ReAction Turbo Hotbars may stay on: PulseQueue detects it and delegates repeats instead of creating a second repeat stream. ReAction Macro Queue likewise owns macro queueing when enabled. NoClippy is compatible and may stay on.");

        ImGui.Spacing();
        if (ImGui.Button("Clear pending smart-buffer input"))
        {
            actionBuffer.Cancel(PulseQueue.Core.CancelReason.Explicit, "Cleared from settings");
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset settings and fault latch")) reset();

        ImGui.Separator();
        ImGui.TextUnformatted("Hard safety contract");
        ImGui.BulletText("Smart buffer: the original player action is sent first; at most that exact tuple can be replayed once.");
        ImGui.BulletText("Turbo changes only FFXIV's answer to 'is this same held logical input pressed?' during its native hotbar scan.");
        ImGui.BulletText("A genuine new physical press always passes through. Repeat signals from an older continuously held input are suppressed after a newer press wins.");
        ImGui.BulletText("Each due interval can report one press; missed intervals never produce a catch-up burst.");
        ImGui.BulletText("FFXIV resolves the binding, slot, adjusted action, target, and macro at the moment of each press.");
        ImGui.BulletText("A held macro slot reruns the complete unchanged macro. PulseQueue does not whitelist, rewrite, or suppress its commands.");
        ImGui.BulletText("Macro action queueing changes only the action-call queue mode when ReAction Macro Queue is not already active.");
        ImGui.BulletText("ReAction can own Turbo or Macro Queue without disabling PulseQueue's separate smart buffer.");
        ImGui.BulletText("NoClippy may own animation-lock timing; PulseQueue does not write animation lock.");
        ImGui.BulletText("Turbo currently covers standard hotbar logical inputs, not cross-hotbar/controller input.");
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
        ImGui.TextUnformatted($"Repeat queues proven / replaced by newer input: {diagnostics.RepeatNativeQueueClaims} / {diagnostics.RepeatNativeQueueReplacements}");
        ImGui.TextUnformatted($"MOAction exclusions observed: {diagnostics.IntegrationExclusions} ({diagnostics.ExcludedIntegrationActions} configured IDs)");
        ImGui.TextUnformatted($"Native input edges / PulseQueue repeats / ReAction repeats: {diagnostics.TurboPhysicalPresses} / {diagnostics.TurboInjectedRepeats} / {diagnostics.TurboDelegatedRepeats}");
        ImGui.TextUnformatted($"Newer-input preemptions / releases / older repeats suppressed: {diagnostics.TurboPreemptions} / {diagnostics.TurboReleases} / {diagnostics.TurboSuppressedHeldRepeats}");
        ImGui.TextUnformatted($"Input-hook fail-open events: {diagnostics.TurboFailedOpenEvents}");
        ImGui.TextUnformatted($"Turbo state: {diagnostics.TurboState}");
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
