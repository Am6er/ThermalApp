namespace ThermalApp.Core;

/// <summary>
/// Палитры, которые применяются к сырым 16-битным данным на нашей стороне.
/// Это даёт полный контроль над диапазоном (в отличие от псевдоцвета самой камеры).
/// LUT хранится как BGRA по 4 байта на запись — сразу в формате WriteableBitmap (Bgra32).
/// </summary>
public sealed class Palette
{
    public string Name { get; }
    /// <summary>1024 байта: 256 записей по BGRA.</summary>
    public byte[] Lut { get; }

    private Palette(string name, byte[] lut) { Name = name; Lut = lut; }

    public override string ToString() => Name;

    private readonly record struct Stop(double Pos, byte R, byte G, byte B);

    private static Palette FromStops(string name, params Stop[] stops)
    {
        var lut = new byte[256 * 4];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            int s = 0;
            while (s < stops.Length - 2 && t > stops[s + 1].Pos) s++;
            var a = stops[s];
            var b = stops[s + 1];
            double span = b.Pos - a.Pos;
            double f = span <= 0 ? 0 : Math.Clamp((t - a.Pos) / span, 0, 1);
            lut[i * 4 + 0] = (byte)Math.Round(a.B + (b.B - a.B) * f); // B
            lut[i * 4 + 1] = (byte)Math.Round(a.G + (b.G - a.G) * f); // G
            lut[i * 4 + 2] = (byte)Math.Round(a.R + (b.R - a.R) * f); // R
            lut[i * 4 + 3] = 255;                                     // A
        }
        return new Palette(name, lut);
    }

    public static readonly Palette WhiteHot = FromStops("White Hot",
        new Stop(0, 0, 0, 0), new Stop(1, 255, 255, 255));

    public static readonly Palette BlackHot = FromStops("Black Hot",
        new Stop(0, 255, 255, 255), new Stop(1, 0, 0, 0));

    public static readonly Palette Ironbow = FromStops("Ironbow",
        new Stop(0.00, 0, 0, 0),
        new Stop(0.20, 40, 0, 90),
        new Stop(0.40, 130, 10, 130),
        new Stop(0.60, 215, 60, 60),
        new Stop(0.80, 255, 160, 10),
        new Stop(0.93, 255, 230, 100),
        new Stop(1.00, 255, 255, 255));

    public static readonly Palette Rainbow = FromStops("Rainbow",
        new Stop(0.00, 0, 0, 40),
        new Stop(0.20, 0, 0, 255),
        new Stop(0.40, 0, 255, 255),
        new Stop(0.60, 0, 255, 0),
        new Stop(0.80, 255, 255, 0),
        new Stop(1.00, 255, 0, 0));

    public static readonly Palette RainbowHc = FromStops("Rainbow HC",
        new Stop(0.00, 0, 0, 0),
        new Stop(0.14, 60, 0, 130),
        new Stop(0.28, 0, 0, 255),
        new Stop(0.42, 0, 200, 255),
        new Stop(0.56, 0, 255, 90),
        new Stop(0.70, 230, 255, 0),
        new Stop(0.84, 255, 100, 0),
        new Stop(1.00, 255, 255, 255));

    public static readonly Palette Lava = FromStops("Lava",
        new Stop(0.00, 0, 0, 0),
        new Stop(0.35, 120, 0, 0),
        new Stop(0.65, 240, 90, 0),
        new Stop(0.88, 255, 200, 40),
        new Stop(1.00, 255, 255, 220));

    public static readonly Palette Arctic = FromStops("Arctic",
        new Stop(0.00, 0, 10, 40),
        new Stop(0.35, 0, 90, 160),
        new Stop(0.65, 120, 200, 230),
        new Stop(0.85, 240, 240, 160),
        new Stop(1.00, 255, 255, 255));

    public static readonly Palette Amber = FromStops("Amber",
        new Stop(0.00, 10, 4, 0),
        new Stop(0.55, 150, 70, 0),
        new Stop(0.85, 255, 170, 40),
        new Stop(1.00, 255, 245, 200));

    public static readonly Palette Medical = FromStops("Medical",
        new Stop(0.00, 0, 0, 0),
        new Stop(0.25, 0, 0, 140),
        new Stop(0.45, 0, 150, 150),
        new Stop(0.60, 0, 180, 0),
        new Stop(0.75, 220, 220, 0),
        new Stop(0.88, 230, 90, 0),
        new Stop(1.00, 255, 255, 255));

    public static readonly Palette Jungle = FromStops("Jungle",
        new Stop(0.00, 0, 0, 0),
        new Stop(0.30, 20, 70, 20),
        new Stop(0.60, 90, 170, 40),
        new Stop(0.85, 220, 220, 90),
        new Stop(1.00, 255, 255, 255));

    public static readonly IReadOnlyList<Palette> All = new[]
    {
        Ironbow, WhiteHot, BlackHot, Rainbow, RainbowHc, Lava, Arctic, Amber, Medical, Jungle
    };
}
