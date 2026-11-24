using System.Runtime.Serialization;

namespace ClipNest.Models;

[DataContract]
public sealed class AppSettings
{
    private const int CurrentLayoutVersion = 1;

    public const double MinimumWidth = 320;
    public const double DefaultWidth = 400;
    public const double DefaultHeight = 400;
    public const double MinimumHeight = 240;
    public const double MaximumWidth = 900;
    public const double MaximumHeight = 900;
    public const int MinimumCardTextLimit = 20;
    public const int MaximumCardTextLimit = 200;
    public const double MinimumCardFontSize = 11;
    public const double MaximumCardFontSize = 20;

    private bool? _autoStartOnLogin;
    private int _layoutVersion = CurrentLayoutVersion;

    [DataMember(Name = "width", Order = 0)]
    public double Width { get; set; } = DefaultWidth;

    [DataMember(Name = "height", Order = 1)]
    public double Height { get; set; } = DefaultHeight;

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
        get => _autoStartOnLogin ?? false;
        set => _autoStartOnLogin = value;
    }

    internal AppSettings CreateSnapshot()
    {
        return (AppSettings)MemberwiseClone();
    }

    public void Normalize()
    {
        if (_layoutVersion < CurrentLayoutVersion)
        {
            Width = DefaultWidth;
            Height = DefaultHeight;
            _layoutVersion = CurrentLayoutVersion;
        }

        Width = IsFinite(Width)
            ? Math.Min(Math.Max(Width, MinimumWidth), MaximumWidth)
            : DefaultWidth;
        Height = IsFinite(Height)
            ? Math.Min(Math.Max(Height, MinimumHeight), MaximumHeight)
            : DefaultHeight;

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
        _autoStartOnLogin ??= false;
    }

    [DataMember(Name = "layoutVersion", Order = 10, EmitDefaultValue = false)]
    public int LayoutVersion
    {
        get => _layoutVersion;
        set => _layoutVersion = value;
    }

    [OnDeserializing]
    private void OnDeserializing(StreamingContext context)
    {
        _layoutVersion = 0;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
