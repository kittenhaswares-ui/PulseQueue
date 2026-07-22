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
    private readonly Func<CertifiedHotbarPress, bool> shouldSuppressHeldRepeat;
    private readonly object gate = new();
    private readonly CertifiedHotbarPress?[] rawHoldOwners = new CertifiedHotbarPress?[StandardHotbarBinding.BindingCount];
    private readonly bool[] needsRawRelease = new bool[StandardHotbarBinding.BindingCount];
    // Ownership is keyed by the physical main key, not by its modifier chord.
    // Changing Ctrl/Alt/Shift while that key remains down must never certify a
    // second logical binding from the same hardware hold.
    private readonly Dictionary<SeVirtualKey, long> activePhysicalKeyOwners = new();
    private CertifiedHotbarPress? pendingPress;
    private nint currentInputDataAddress;
    private long lastInputObservationAtMilliseconds;
    private long nextPressId;
    private long suppressedHeldRepeatCount;
    private bool disposed;

    public PhysicalHotbarInputSource(
        IGameInteropProvider interop,
        Action<CertifiedHotbarPress> onCertifiedPress,
        Func<CertifiedHotbarPress, bool> shouldSuppressHeldRepeat)
    {
        this.onCertifiedPress = onCertifiedPress ?? throw new ArgumentNullException(nameof(onCertifiedPress));
        this.shouldSuppressHeldRepeat = shouldSuppressHeldRepeat
            ?? throw new ArgumentNullException(nameof(shouldSuppressHeldRepeat));
        // Hook installation has no historical raw-edge information. Every
        // binding must first be observed fully released before it can certify a
        // press, even if the first callback happens to be a typematic Pressed.
        Array.Fill(needsRawRelease, true);
        pressedHook = interop.HookFromAddress<InputData.Delegates.IsInputIdPressed>(
            InputData.MemberFunctionPointers.IsInputIdPressed,
            IsInputIdPressedDetour);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        pressedHook.Enable();
    }

    public long SuppressedHeldRepeatCount => Interlocked.Read(ref suppressedHeldRepeatCount);

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
            Array.Clear(rawHoldOwners);
            Array.Clear(needsRawRelease);
            activePhysicalKeyOwners.Clear();
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
        CertifiedHotbarPress? certifiedPress = null;
        CertifiedHotbarPress? heldRepeat = null;
        lock (gate)
        {
            currentInputDataAddress = (nint)inputData;
            lastInputObservationAtMilliseconds = now;

            if (rawHoldOwners[index] is { } existingOwner)
            {
                // Modifier or binding changes may invalidate Turbo liveness, but
                // they are not a physical key-up. Retain the raw-key latch until
                // the original hardware key is actually released so the same
                // hold can never be recertified under a different chord.
                if (IsPhysicalKeyDown(inputData, existingOwner.PhysicalKey))
                {
                    if (pressed) heldRepeat = existingOwner;
                    goto ObservationComplete;
                }

                rawHoldOwners[index] = null;
                if (activePhysicalKeyOwners.TryGetValue(existingOwner.PhysicalKey, out var ownerPressId)
                    && ownerPressId == existingOwner.PressId)
                {
                    activePhysicalKeyOwners.Remove(existingOwner.PhysicalKey);
                }
                if (pendingPress?.PressId == existingOwner.PressId) pendingPress = null;
            }

            if (needsRawRelease[index])
            {
                if (IsAnyBoundKeyboardKeyDown(inputData, inputId))
                {
                    goto ObservationComplete;
                }

                needsRawRelease[index] = false;
            }

            if (!pressed)
            {
                // The hook may be enabled or reset while a key is already held.
                // Such a key has no observable raw edge and must remain ineligible
                // until every keyboard key for this logical binding is released.
                needsRawRelease[index] = IsAnyBoundKeyboardKeyDown(inputData, inputId);
                goto ObservationComplete;
            }

            if (!TryFindFreshKeyboardChord(
                    inputData,
                    inputId,
                    out var physicalKey,
                    out var requiredModifiers,
                    out var activeModifiers,
                    out var keySettingIndex))
            {
                needsRawRelease[index] = IsAnyBoundKeyboardKeyDown(inputData, inputId);
                goto ObservationComplete;
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
            if (activePhysicalKeyOwners.ContainsKey(physicalKey))
            {
                // One held hardware key can drive multiple logical bindings as
                // modifiers change. Preserve vanilla input, but never manufacture
                // another Turbo owner until that main key has physically gone up.
                certifiedPress = null;
                goto ObservationComplete;
            }

            rawHoldOwners[index] = certifiedPress;
            activePhysicalKeyOwners[physicalKey] = pressId;
            pendingPress = certifiedPress;

        ObservationComplete:;
        }

        if (heldRepeat is { } repeated)
        {
            try
            {
                if (shouldSuppressHeldRepeat(repeated))
                {
                    Interlocked.Increment(ref suppressedHeldRepeatCount);
                    return false;
                }
            }
            catch
            {
                // If ownership cannot be queried, vanilla remains authoritative.
            }

            return pressed;
        }

        if (certifiedPress is null) return pressed;

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

    private static bool IsExactChordDown(InputData* inputData, CertifiedHotbarPress press)
    {
        if (inputData == null) return false;
        var keybind = inputData->GetKeybind(press.Binding.InputId);
        if (keybind == null) return false;
        var settings = keybind->KeySettings;
        if (press.KeySettingIndex >= settings.Length) return false;
        var setting = settings[press.KeySettingIndex];
        if (setting.Key != press.PhysicalKey
            || setting.KeyModifier != press.RequiredModifiers
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

    private static bool IsPhysicalKeyDown(InputData* inputData, SeVirtualKey physicalKey)
    {
        if (inputData == null) return false;
        var keyIndex = (int)physicalKey;
        var keyStates = inputData->KeyboardInputs.KeyState;
        return keyIndex >= 0
            && keyIndex < keyStates.Length
            && (keyStates[keyIndex] & KeyStateFlags.Down) != 0;
    }

    private static bool IsAnyBoundKeyboardKeyDown(InputData* inputData, InputId inputId)
    {
        if (inputData == null) return false;
        var keybind = inputData->GetKeybind(inputId);
        if (keybind == null) return false;
        var keyStates = inputData->KeyboardInputs.KeyState;
        foreach (var setting in keybind->KeySettings)
        {
            var keyIndex = (int)setting.Key;
            if (IsKeyboardKey(setting.Key)
                && keyIndex >= 0
                && keyIndex < keyStates.Length
                && (keyStates[keyIndex] & KeyStateFlags.Down) != 0)
            {
                return true;
            }
        }

        return false;
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
