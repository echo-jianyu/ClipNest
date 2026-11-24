using System.Windows.Input;

namespace ClipNest.Models;

public sealed class HotKeyDefinition : IEquatable<HotKeyDefinition>
{
    public const uint AltModifier = 0x0001;
    public const uint ControlModifier = 0x0002;
    public const uint ShiftModifier = 0x0004;
    public const uint WindowsModifier = 0x0008;
    public const uint SupportedModifiers =
        AltModifier | ControlModifier | ShiftModifier | WindowsModifier;

    public HotKeyDefinition(uint modifiers, uint virtualKey)
    {
        Modifiers = modifiers & SupportedModifiers;
        VirtualKey = virtualKey;
    }

    public static HotKeyDefinition Default { get; } = new(
        modifiers: 0,
        virtualKey: 0x73);

    public uint Modifiers { get; }

    public uint VirtualKey { get; }

    public string DisplayText
    {
        get
        {
            var parts = new List<string>(5);
            if ((Modifiers & ControlModifier) != 0)
            {
                parts.Add("Ctrl");
            }

            if ((Modifiers & AltModifier) != 0)
            {
                parts.Add("Alt");
            }

            if ((Modifiers & ShiftModifier) != 0)
            {
                parts.Add("Shift");
            }

            if ((Modifiers & WindowsModifier) != 0)
            {
                parts.Add("Win");
            }

            var key = KeyInterop.KeyFromVirtualKey((int)VirtualKey);
            parts.Add(key == Key.None ? $"VK{VirtualKey:X2}" : key.ToString());
            return string.Join("+", parts);
        }
    }

    public static bool IsValid(uint modifiers, uint virtualKey)
    {
        if (virtualKey == 0
            || virtualKey > 0xFE
            || virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C)
        {
            return false;
        }

        if ((modifiers & ~SupportedModifiers) != 0)
        {
            return false;
        }

        var isFunctionKey = virtualKey is >= 0x70 and <= 0x87;
        var hasPrimaryModifier =
            (modifiers & (ControlModifier | AltModifier | WindowsModifier)) != 0;
        if (!hasPrimaryModifier
            && !isFunctionKey)
        {
            return false;
        }

        return modifiers != ShiftModifier;
    }

    public static HotKeyDefinition FromSettings(AppSettings settings)
    {
        return IsValid(settings.HotKeyModifiers, settings.HotKeyKey)
            ? new HotKeyDefinition(settings.HotKeyModifiers, settings.HotKeyKey)
            : Default;
    }

    public static HotKeyDefinition? FromKeyboard(Key key, ModifierKeys modifiers)
    {
        if (key is Key.None
            or Key.LeftAlt
            or Key.RightAlt
            or Key.LeftCtrl
            or Key.RightCtrl
            or Key.LeftShift
            or Key.RightShift
            or Key.LWin
            or Key.RWin)
        {
            return null;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        var nativeModifiers = 0u;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            nativeModifiers |= ControlModifier;
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            nativeModifiers |= AltModifier;
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            nativeModifiers |= ShiftModifier;
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            nativeModifiers |= WindowsModifier;
        }

        return IsValid(nativeModifiers, (uint)virtualKey)
            ? new HotKeyDefinition(nativeModifiers, (uint)virtualKey)
            : null;
    }

    public bool Equals(HotKeyDefinition? other)
    {
        return other is not null
            && Modifiers == other.Modifiers
            && VirtualKey == other.VirtualKey;
    }

    public override bool Equals(object? obj)
    {
        return obj is HotKeyDefinition other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return ((int)Modifiers * 397) ^ (int)VirtualKey;
        }
    }
}
