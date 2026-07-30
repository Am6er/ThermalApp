namespace ThermalApp.Core;

public enum RangeMode
{
    /// <summary>min/max текущего кадра.</summary>
    Auto,
    /// <summary>Отсечение выбросов по процентилям — картинка не «слепнет» от одной горячей точки.</summary>
    AutoRobust,
    /// <summary>Диапазон задан вручную в °C.</summary>
    Manual
}

/// <summary>
/// Красит сырые 16-битные данные выбранной палитрой в буфер BGRA (для WriteableBitmap).
/// </summary>
public sealed class FrameRenderer
{
    public Palette Palette { get; set; } = Core.Palette.Ironbow;
    public RangeMode Mode { get; set; } = RangeMode.AutoRobust;

    /// <summary>Границы для RangeMode.Manual, °C.</summary>
    public double ManualMinC { get; set; } = 20;
    public double ManualMaxC { get; set; } = 40;

    /// <summary>Процентили отсечения для AutoRobust, 0..0.5.</summary>
    public double LowPercentile { get; set; } = 0.02;
    public double HighPercentile { get; set; } = 0.02;

    /// <summary>0 = без сглаживания, 1 = диапазон не меняется. Убирает мерцание при авто-диапазоне.</summary>
    public double Smoothing { get; set; } = 0.6;

    /// <summary>Гамма для распределения уровней: &lt;1 подтягивает детали в холодной части.</summary>
    public double Gamma { get; set; } = 1.0;

    public ushort RangeLo { get; private set; }
    public ushort RangeHi { get; private set; }
    public double RangeLoC => ThermalFrame.ToCelsius(RangeLo);
    public double RangeHiC => ThermalFrame.ToCelsius(RangeHi);

    private bool _hasRange;
    private readonly int[] _hist = new int[HistBins];
    private const int HistBins = 512;
    private readonly byte[] _gammaLut = new byte[256];
    private double _gammaCached = -1;

    public void ResetRange() => _hasRange = false;

    /// <summary>Отрисовать кадр в dest (Width*Height*4 байта, BGRA).</summary>
    public void Render(ThermalFrame frame, byte[] dest)
    {
        if (dest.Length < ThermalFrame.Pixels * 4)
            throw new ArgumentException("Буфер слишком мал", nameof(dest));

        var (lo, hi) = ComputeRange(frame);
        RangeLo = lo;
        RangeHi = hi;

        var lut = Palette.Lut;
        var raw = frame.Raw;
        int span = Math.Max(1, hi - lo);
        bool useGamma = Math.Abs(Gamma - 1.0) > 1e-3;
        if (useGamma) EnsureGammaLut();

        for (int i = 0; i < raw.Length; i++)
        {
            int v = (raw[i] - lo) * 255 / span;
            if (v < 0) v = 0; else if (v > 255) v = 255;
            if (useGamma) v = _gammaLut[v];
            int s = v * 4;
            int d = i * 4;
            dest[d + 0] = lut[s + 0];
            dest[d + 1] = lut[s + 1];
            dest[d + 2] = lut[s + 2];
            dest[d + 3] = 255;
        }
    }

    private void EnsureGammaLut()
    {
        if (Math.Abs(_gammaCached - Gamma) < 1e-6) return;
        for (int i = 0; i < 256; i++)
            _gammaLut[i] = (byte)Math.Clamp(Math.Round(Math.Pow(i / 255.0, Gamma) * 255.0), 0, 255);
        _gammaCached = Gamma;
    }

    private (ushort lo, ushort hi) ComputeRange(ThermalFrame frame)
    {
        ushort lo, hi;
        switch (Mode)
        {
            case RangeMode.Manual:
                lo = ThermalFrame.FromCelsius(Math.Min(ManualMinC, ManualMaxC));
                hi = ThermalFrame.FromCelsius(Math.Max(ManualMinC, ManualMaxC));
                if (hi <= lo) hi = (ushort)Math.Min(65535, lo + 1);
                _hasRange = true;
                return (lo, hi);

            case RangeMode.AutoRobust:
                (lo, hi) = Percentiles(frame);
                break;

            default:
                lo = frame.RawMin;
                hi = frame.RawMax;
                break;
        }

        if (hi <= lo) hi = (ushort)Math.Min(65535, lo + 1);

        if (_hasRange && Smoothing > 0)
        {
            double k = Math.Clamp(Smoothing, 0, 0.98);
            lo = (ushort)Math.Round(RangeLo * k + lo * (1 - k));
            hi = (ushort)Math.Round(RangeHi * k + hi * (1 - k));
            if (hi <= lo) hi = (ushort)Math.Min(65535, lo + 1);
        }
        _hasRange = true;
        return (lo, hi);
    }

    private (ushort lo, ushort hi) Percentiles(ThermalFrame frame)
    {
        ushort min = frame.RawMin, max = frame.RawMax;
        if (max <= min) return (min, (ushort)Math.Min(65535, min + 1));

        Array.Clear(_hist);
        int range = max - min;
        var raw = frame.Raw;
        for (int i = 0; i < raw.Length; i++)
        {
            int bin = (raw[i] - min) * (HistBins - 1) / range;
            _hist[bin]++;
        }

        int total = raw.Length;
        int loTarget = (int)(total * Math.Clamp(LowPercentile, 0, 0.45));
        int hiTarget = (int)(total * Math.Clamp(HighPercentile, 0, 0.45));

        int acc = 0, loBin = 0;
        for (int b = 0; b < HistBins; b++)
        {
            acc += _hist[b];
            if (acc > loTarget) { loBin = b; break; }
        }
        acc = 0;
        int hiBin = HistBins - 1;
        for (int b = HistBins - 1; b >= 0; b--)
        {
            acc += _hist[b];
            if (acc > hiTarget) { hiBin = b; break; }
        }
        if (hiBin <= loBin) hiBin = Math.Min(HistBins - 1, loBin + 1);

        ushort lo = (ushort)(min + loBin * range / (HistBins - 1));
        ushort hi = (ushort)(min + hiBin * range / (HistBins - 1));
        return (lo, hi);
    }
}
