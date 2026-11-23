using System.Runtime.Serialization;

namespace ClipNest.Models;

[DataContract]
public sealed class AppSettings
{
    public const double MinimumWidth = 450;
    public const double DefaultWidth = 450;
    public const double MaximumWidth = 900;
    public const double AspectRatio = 3d / 2d;

    [DataMember(Name = "width", Order = 0)]
    public double Width { get; set; } = DefaultWidth;

    [DataMember(Name = "height", Order = 1)]
    public double Height { get; set; } = DefaultWidth / AspectRatio;

    [DataMember(Name = "left", Order = 2)]
    public double? Left { get; set; }

    [DataMember(Name = "top", Order = 3)]
    public double? Top { get; set; }

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
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
