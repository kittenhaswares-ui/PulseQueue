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

        Size = new Vector2(570, 690);
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
        ImGui.TextWrapped("A strict, one-shot buffer for direct hotbar actions that vanilla rejected only because a GCD/local recast or animation lock was about to end.");
        ImGui.Spacing();

        DrawStatus(diagnostics);
        ImGui.Separator();

        DrawCheckbox("Enable smart buffer", configuration.Enabled, value => configuration.Enabled = value);
        DrawCheckbox("Dry run (detect, never replay)", configuration.DryRun, value => configuration.DryRun = value);
        DrawCheckbox("Detailed Dalamud logging", configuration.DetailedLogging, value => configuration.DetailedLogging = value);

        ImGui.Spacing();
        if (ImGui.Button("Clear pending input"))
        {
            actionBuffer.Cancel(PulseQueue.Core.CancelReason.Explicit, "Cleared from settings");
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset settings and fault latch")) reset();

        ImGui.Separator();
        ImGui.TextUnformatted("Hard safety contract");
        ImGui.BulletText("The original player action is sent first; at most that exact tuple can be replayed once.");
        ImGui.BulletText("No action substitution, target selection, target change, retry, loop, or lock/recast mutation.");
        ImGui.BulletText("A newer hotbar action replaces the old token.");
        ImGui.BulletText("It may clear one exact older native queue entry only when ownership is proven and the newer eligible action is ready or inside the hold window.");
        ImGui.BulletText("Death, stun, forced movement, target/resolver/context/zone change, frame stall, or native queue activity clears it.");
        ImGui.BulletText("Mounted state and movement actions are never buffered.");
        ImGui.BulletText("NoClippy 0.5.0.24 may own animation-lock timing; PulseQueue never writes that lock.");
        ImGui.BulletText("ReAction 1.3.5.1 requires empty Action Stacks plus Auto Target, Turbo Hotbars, Auto Dismount, and Camera Relative Directionals off.");
        ImGui.BulletText("MOAction-targeted skills are passed through normally but excluded from replay.");
        ImGui.BulletText("Only instant, non-ground-targeted Action/PvPAction hotbar inputs are eligible.");
        ImGui.TextWrapped("The hold window is learned from your own acknowledged action timing, stays between 80 and 180 ms after warm-up, and never decides whether an action is legal.");

        ImGui.Separator();
        ImGui.TextDisabled("Live diagnostics");
        ImGui.TextUnformatted($"Window: {diagnostics.HoldWindowMilliseconds} ms");
        ImGui.TextUnformatted($"Response estimate: {diagnostics.EstimatedResponseMilliseconds:0.0} ms ({diagnostics.AcceptedTimingSamples} accepted samples)");
        ImGui.TextUnformatted($"Captured / dispatched / dry-run: {diagnostics.Captured} / {diagnostics.Dispatched} / {diagnostics.DryRunDispatches}");
        ImGui.TextUnformatted($"Replay rejected (never retried): {diagnostics.ReplayRejected}");
        ImGui.TextUnformatted($"Hotbar inputs / replaced pending: {diagnostics.ObservedHotbarInputs} / {diagnostics.ReplacedPendingInputs}");
        ImGui.TextUnformatted($"Native queue accepted / blocked: {diagnostics.NativeQueueAccepted} / {diagnostics.NativeQueueBlocked}");
        ImGui.TextUnformatted($"Owned native queues replaced by newer input: {diagnostics.OwnedNativeQueueReplacements}");
        ImGui.TextUnformatted($"MOAction exclusions observed: {diagnostics.IntegrationExclusions} ({diagnostics.ExcludedIntegrationActions} configured IDs)");
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
}
