namespace ThermalApp.Core;

/// <summary>
/// Один разобранный кадр с камеры.
/// Модуль InfiRay Tiny1-C (Mileseey TR256i / InfiRay P2 Pro / Topdon TC001 / HTI HT-203U)
/// отдаёт по UVC кадр 256x384 YUY2, где:
///   - верхние 192 строки  - псевдоцветная картинка в YUY2 (её считает сама камера);
///   - нижние  192 строки  - сырая радиометрия, 256*192 значений uint16 little-endian.
/// Температура: T(°C) = raw / 64 - 273.15
/// </summary>
public sealed class ThermalFrame
{
    public const int Width = 256;
    public const int Height = 192;
    public const int Pixels = Width * Height;

    /// <summary>Сырые 16-битные отсчёты, Pixels элементов, порядок row-major.</summary>
    public ushort[] Raw { get; }

    /// <summary>Псевдоцветная картинка от самой камеры, BGR24 (Width*Height*3). null, если не запрашивалась.</summary>
    public byte[]? CameraBgr { get; internal set; }

    public long Number { get; internal set; }
    public DateTime Timestamp { get; internal set; }

    public ushort RawMin { get; private set; }
    public ushort RawMax { get; private set; }
    public int MinIndex { get; private set; }
    public int MaxIndex { get; private set; }
    public double RawMean { get; private set; }

    public ThermalFrame(ushort[] raw)
    {
        if (raw.Length != Pixels)
            throw new ArgumentException($"Ожидалось {Pixels} отсчётов, получено {raw.Length}", nameof(raw));
        Raw = raw;
    }

    public static double ToCelsius(ushort raw) => raw / 64.0 - 273.15;
    public static ushort FromCelsius(double c) => (ushort)Math.Clamp(Math.Round((c + 273.15) * 64.0), 0, 65535);

    public double MinC => ToCelsius(RawMin);
    public double MaxC => ToCelsius(RawMax);
    public double MeanC => RawMean / 64.0 - 273.15;

    public double CenterC => TemperatureAt(Width / 2, Height / 2);
    public (int X, int Y) MinPoint => (MinIndex % Width, MinIndex / Width);
    public (int X, int Y) MaxPoint => (MaxIndex % Width, MaxIndex / Width);

    public double TemperatureAt(int x, int y)
    {
        if ((uint)x >= Width || (uint)y >= Height) return double.NaN;
        return ToCelsius(Raw[y * Width + x]);
    }

    /// <summary>Средняя температура в окне size*size вокруг точки — меньше шума, чем один пиксель.</summary>
    public double TemperatureAt(int x, int y, int size)
    {
        if (size <= 1) return TemperatureAt(x, y);
        int r = size / 2;
        long sum = 0;
        int n = 0;
        for (int yy = y - r; yy <= y + r; yy++)
        {
            if ((uint)yy >= Height) continue;
            for (int xx = x - r; xx <= x + r; xx++)
            {
                if ((uint)xx >= Width) continue;
                sum += Raw[yy * Width + xx];
                n++;
            }
        }
        return n == 0 ? double.NaN : (double)sum / n / 64.0 - 273.15;
    }

    /// <summary>Пересчитать min/max/среднее. Вызывается захватом сразу после разбора кадра.</summary>
    public void ComputeStats()
    {
        ushort min = ushort.MaxValue, max = 0;
        int minIdx = 0, maxIdx = 0;
        long sum = 0;
        var raw = Raw;
        for (int i = 0; i < raw.Length; i++)
        {
            ushort v = raw[i];
            sum += v;
            if (v < min) { min = v; minIdx = i; }
            if (v > max) { max = v; maxIdx = i; }
        }
        RawMin = min;
        RawMax = max;
        MinIndex = minIdx;
        MaxIndex = maxIdx;
        RawMean = (double)sum / raw.Length;
    }
}
