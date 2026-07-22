using System.Diagnostics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace PulseQueue.Plugin.Services;

internal readonly record struct StandardHotbarBinding(
    InputId InputId,
    uint HotbarId,
    uint SlotId)
{
    private const int FirstInputId = (int)InputId.HOTBAR_1_1;
    private const int LastInputId = (int)InputId.HOTBAR_10_B;
    private const int SlotsPerHotbar = 12;

    public static int BindingCount => LastInputId - FirstInputId + 1;

    public static bool TryFromInputId(InputId inputId, out StandardHotbarBinding binding)
    {
        var raw = (int)inputId;
        if (raw < FirstInputId || raw > LastInputId)
        {
            binding = default;
            return false;
        }

        var offset = raw - FirstInputId;
        binding = new StandardHotbarBinding(
            inputId,
            (uint)(offset / SlotsPerHotbar),
            (uint)(offset % SlotsPerHotbar));
        return true;
    }
}

internal readonly record struct CertifiedHotbarPress(
    long PressId,
    StandardHotbarBinding Binding,
    SeVirtualKey PhysicalKey,
    KeyModifierFlag RequiredModifiers,
    KeyModifierFlag ActiveModifiers,
    byte KeySettingIndex,
    nint InputDataAddress,
    long ObservedAtMilliseconds);

/// <summary>
/// Observes native keyboard hotbar edges without changing the game's pressed result.
/// A key that was already held when observation began is latched until release and can
/// never create a certified press. Controller/cross-hotbar and mouse invocations remain
/// ordinary manual inputs but are deliberately ineligible to start keyboard Turbo.
/// </summary>
internal sealed unsafe class PhysicalHotbarInputSource : IDisposable
{
    private const long MaximumCorrelationAgeMilliseconds = 50;
    private const long MaximumInputObservationAgeMilliseconds = 100;

    private readonly Hook<InputData.Delegates.IsInputIdPressed> pressedHook;
    private readonly Action<CertifiedHotbarPress> onCertifiedPress;
    private readonly object gate = new();
    private readonly bool[] downLatches = new bool[StandardHotbarBinding.BindingCount];
    private CertifiedHotbarPress? pendingPress;
    private nint currentInputDataAddress;
    private long lastInputObservationAtMilliseconds;
    private long nextPressId;
    private bool disposed;

    public PhysicalHotbarInputSource(
        IGameInteropProvider interop,
        Action<CertifiedHotbarPress> onCertifiedPress)
    {
        this.onCertifiedPress = onCertifiedPress ?? throw new ArgumentNullException(nameof(onCertifiedPress));
        pressedHook = interop.HookFromAddress<InputData.Delegates.IsInputIdPressed>(
            InputData.MemberFunctionPointers.IsInputIdPressed,
            IsInputIdPressedDetour);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        pressedHook.Enable();
    }

    public bool TryConsume(
        RaptureHotbarModule* hotbarModule,
        RaptureHotbarModule.HotbarSlot* slot,
        long nowMilliseconds,
        out CertifiedHotbarPress press)
    {
        press = default;
        if (hotbarModule == null || slot == null || !TryTakeFresh(nowMilliseconds, out var candidate))
        {
            return false;
        }

        var expected = hotbarModule->GetSlotById(candidate.Binding.HotbarId, candidate.Binding.SlotId);
        if (expected != slot)
        {
            return false;
        }

        press = candidate;
        return true;
    }

    public bool TryConsume(
        uint hotbarId,
        uint slotId,
        long nowMilliseconds,
        out CertifiedHotbarPress press)
    {
        press = default;
        if (!TryTakeFresh(nowMilliseconds, out var candidate)
            || candidate.Binding.HotbarId != hotbarId
            || candidate.Binding.SlotId != slotId)
        {
            return false;
        }

        press = candidate;
        return true;
    }

    public bool IsStillHeld(CertifiedHotbarPress press)
    {
        if (disposed || press.InputDataAddress == nint.Zero) return false;
        var now = NowMilliseconds;
        lock (gate)
        {
            if (currentInputDataAddress != press.InputDataAddress
                || now - lastInputObservationAtMilliseconds is < 0 or > MaximumInputObservationAgeMilliseconds)
            {
                return false;
            }
        }

        var inputData = (InputData*)press.InputDataAddress;
        var keybind = inputData->GetKeybind(press.Binding.InputId);
        if (keybind == null) return false;
        var settings = keybind->KeySettings;
        if (press.KeySettingIndex >= settings.Length) return false;
        var currentSetting = settings[press.KeySettingIndex];
        if (currentSetting.Key != press.PhysicalKey
            || currentSetting.KeyModifier != press.RequiredModifiers
            || inputData->CurrentKeyModifier != press.ActiveModifiers)
        {
            return false;
        }

        var keyIndex = (int)press.PhysicalKey;
        var keyStates = inputData->KeyboardInputs.KeyState;
        return keyIndex >= 0
            && keyIndex < keyStates.Length
            && (keyStates[keyIndex] & KeyStateFlags.Down) != 0;
    }

    public void DiscardPending()
    {
        lock (gate) pendingPress = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (gate)
        {
            pendingPress = null;
            currentInputDataAddress = nint.Zero;
            lastInputObservationAtMilliseconds = 0;
            Array.Clear(downLatches);
        }

        pressedHook.Dispose();
    }

    private bool IsInputIdPressedDetour(InputData* inputData, InputId inputId)
    {
        var pressed = pressedHook.Original(inputData, inputId);
        if (disposed
            || !StandardHotbarBinding.TryFromInputId(inputId, out var binding)
            || inputData == null)
        {
            return pressed;
        }

        var now = NowMilliseconds;
        var index = (int)inputId - (int)InputId.HOTBAR_1_1;
        var down = inputData->IsInputIdDown(inputId);
        CertifiedHotbarPress? certifiedPress = null;
        lock (gate)
        {
            currentInputDataAddress = (nint)inputData;
            lastInputObservationAtMilliseconds = now;
            if (!down)
            {
                downLatches[index] = false;
                if (pendingPress is { } pending && pending.Binding.InputId == inputId)
                {
                    pendingPress = null;
                }

                return pressed;
            }

            if (downLatches[index]) return pressed;

            // A down key without a native pressed edge was already down when observation
            // began. Latch it until release; never manufacture provenance for it.
            downLatches[index] = true;
            if (!pressed) return pressed;

            if (!TryFindFreshKeyboardChord(
                    inputData,
                    inputId,
                    out var physicalKey,
                    out var requiredModifiers,
                    out var activeModifiers,
                    out var keySettingIndex))
            {
                return pressed;
            }

            var pressId = Interlocked.Increment(ref nextPressId);
            certifiedPress = new CertifiedHotbarPress(
                pressId,
                binding,
                physicalKey,
                requiredModifiers,
                activeModifiers,
                keySettingIndex,
                (nint)inputData,
                now);
            pendingPress = certifiedPress;
        }

        try
        {
            onCertifiedPress(certifiedPress.Value);
        }
        catch
        {
            // Physical provenance is useful only if the owner can be invalidated
            // atomically. If that callback fails, consume nothing and preserve the
            // native pressed result rather than letting an unowned hold start.
            lock (gate)
            {
                if (pendingPress?.PressId == certifiedPress.Value.PressId)
                {
                    pendingPress = null;
                }
            }
        }

        return pressed;
    }

    private static bool TryFindFreshKeyboardChord(
        InputData* inputData,
        InputId inputId,
        out SeVirtualKey physicalKey,
        out KeyModifierFlag requiredModifiers,
        out KeyModifierFlag activeModifiers,
        out byte keySettingIndex)
    {
        physicalKey = SeVirtualKey.NO_KEY;
        requiredModifiers = KeyModifierFlag.None;
        activeModifiers = KeyModifierFlag.None;
        keySettingIndex = 0;
        var keybind = inputData->GetKeybind(inputId);
        if (keybind == null) return false;

        var currentModifiers = inputData->CurrentKeyModifier;
        var keyStates = inputData->KeyboardInputs.KeyState;
        var found = false;
        var settings = keybind->KeySettings;
        for (var index = 0; index < settings.Length; index++)
        {
            var setting = settings[index];
            var keyIndex = (int)setting.Key;
            if (!IsKeyboardKey(setting.Key)
                || keyIndex >= keyStates.Length
                || (currentModifiers & setting.KeyModifier) != setting.KeyModifier)
            {
                continue;
            }

            var state = keyStates[keyIndex];
            if ((state & KeyStateFlags.Pressed) == 0
                || (state & KeyStateFlags.Down) == 0)
            {
                continue;
            }

            // Two simultaneous physical keys for the same logical bind are
            // ambiguous. Fail closed instead of guessing which key owns release.
            if (found) return false;
            found = true;
            physicalKey = setting.Key;
            requiredModifiers = setting.KeyModifier;
            activeModifiers = currentModifiers;
            keySettingIndex = checked((byte)index);
        }

        return found;
    }

    private static bool IsKeyboardKey(SeVirtualKey key)
    {
        var raw = (int)key;
        return raw >= (int)SeVirtualKey.BACK && raw < (int)SeVirtualKey.PAD_LMB;
    }

    private bool TryTakeFresh(long nowMilliseconds, out CertifiedHotbarPress press)
    {
        lock (gate)
        {
            press = default;
            var pending = pendingPress;
            pendingPress = null;
            if (pending is not { } candidate) return false;
            var age = nowMilliseconds - candidate.ObservedAtMilliseconds;
            if (age is < 0 or > MaximumCorrelationAgeMilliseconds) return false;
            press = candidate;
            return true;
        }
    }

    private static long NowMilliseconds => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
}
