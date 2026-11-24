using System.Runtime.Serialization;

namespace ClipNest.Models;

[DataContract]
public sealed class AppSettings
{
    public const double MinimumWidth = 450;
    public const double DefaultWidth = 450;
    public const double MaximumWidth = 900;
    public const double AspectRatio = 3d / 2d;
    public const int MinimumCardTextLimit = 20;
    public const int MaximumCardTextLimit = 200;
    public const double MinimumCardFontSize = 11;
    public const double MaximumCardFontSize = 20;

    private bool? _autoStartOnLogin = true;

    [DataMember(Name = "width", Order = 0)]
    public double Width { get; set; } = DefaultWidth;

    [DataMember(Name = "height", Order = 1)]
    public double Height { get; set; } = DefaultWidth / AspectRatio;

    [DataMember(Name = "left", Order = 2)]
    public double? Left { get; set; }

    [DataMember(Name = "top", Order = 3)]
    public double? Top { get; set; }

    [DataMember(Name = "hotKeyModifiers", Order = 4)]
    public uint HotKeyModifiers { get; set; }

    [DataMember(Name = "hotKeyKey", Order = 5)]
    public uint HotKeyKey { get; set; } = 0x73;

    [DataMember(Name = "hideAfterCopy", Order = 6)]
    public bool HideAfterCopy { get; set; }

    [DataMember(Name = "cardTextLimit", Order = 7)]
    public int CardTextLimit { get; set; } = 40;

    [DataMember(Name = "cardFontSize", Order = 8)]
    public double CardFontSize { get; set; } = 13.5;

    [DataMember(Name = "autoStartOnLogin", Order = 9)]
    private bool? SerializedAutoStartOnLogin
    {
        get => _autoStartOnLogin;
        set => _autoStartOnLogin = value;
    }

    [IgnoreDataMember]
    public bool AutoStartOnLogin
    {
        get => _autoStartOnLogin ?? true;
        set => _autoStartOnLogin = value;
    }

    public void Normalize()
    {
        Width = IsFinite(Width)
            ? Math.Min(Math.Max(Width, MinimumWidth), MaximumWidth)
            : MinimumWidth;
        Height = Width / AspectRatio;

        if (Left is not null && !IsFinite(Left.Value))
        {
            Left = null;
        }

        if (Top is not null && !IsFinite(Top.Value))
        {
            Top = null;
        }

        if (!HotKeyDefinition.IsValid(HotKeyModifiers, HotKeyKey))
        {
            HotKeyModifiers = HotKeyDefinition.Default.Modifiers;
            HotKeyKey = HotKeyDefinition.Default.VirtualKey;
        }

        CardTextLimit = CardTextLimit <= 0
            ? 40
            : Math.Min(
                Math.Max(CardTextLimit, MinimumCardTextLimit),
                MaximumCardTextLimit);
        CardFontSize = !IsFinite(CardFontSize) || CardFontSize <= 0
            ? 13.5
            : Math.Min(
                Math.Max(CardFontSize, MinimumCardFontSize),
                MaximumCardFontSize);
        _autoStartOnLogin ??= true;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
