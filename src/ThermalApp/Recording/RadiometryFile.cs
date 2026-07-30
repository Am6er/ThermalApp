using System.IO;
using System.Text;
using ThermalApp.Core;

namespace ThermalApp.Recording;

/// <summary>
/// Простой контейнер для сырой радиометрии (.r16).
/// Заголовок 32 байта:
///   0  : "TRAW"        (4 байта ASCII)
///   4  : uint16 version = 1
///   6  : uint16 width
///   8  : uint16 height
///   10 : uint16 flags (0)
///   12 : int64  frameCount (дописывается при закрытии)
///   20 : 12 байт резерв
/// Далее на каждый кадр: int64 DateTime.Ticks + width*height*2 байта uint16 LE.
/// Температура: T(°C) = raw / 64 - 273.15
/// </summary>
public static class RadiometryFile
{
    public const int HeaderSize = 32;
    private static readonly byte[] Magic = "TRAW"u8.ToArray();

    public sealed class Writer : IDisposable
    {
        private readonly FileStream _fs;
        private readonly BinaryWriter _bw;
        private long _frames;

        public string Path { get; }
        public long FrameCount => _frames;

        public Writer(string path)
        {
            Path = path;
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            _bw = new BinaryWriter(_fs, Encoding.ASCII, leaveOpen: true);
            _bw.Write(Magic);
            _bw.Write((ushort)1);
            _bw.Write((ushort)ThermalFrame.Width);
            _bw.Write((ushort)ThermalFrame.Height);
            _bw.Write((ushort)0);
            _bw.Write((long)0);
            _bw.Write(new byte[12]);
        }

        public void Append(ThermalFrame frame)
        {
            _bw.Write(frame.Timestamp.Ticks);
            var bytes = new byte[ThermalFrame.Pixels * 2];
            Buffer.BlockCopy(frame.Raw, 0, bytes, 0, bytes.Length);
            _bw.Write(bytes);
            _frames++;
        }

        public void Dispose()
        {
            _bw.Flush();
            _fs.Position = 12;
            _bw.Write(_frames);
            _bw.Flush();
            _bw.Dispose();
            _fs.Dispose();
        }
    }

    public sealed record Header(int Version, int Width, int Height, long FrameCount);

    public static Header ReadHeader(Stream s)
    {
        var br = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);
        var magic = br.ReadBytes(4);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("Это не файл .r16 (TRAW).");
        int version = br.ReadUInt16();
        int w = br.ReadUInt16();
        int h = br.ReadUInt16();
        br.ReadUInt16();
        long count = br.ReadInt64();
        br.ReadBytes(12);
        return new Header(version, w, h, count);
    }

    /// <summary>Прочитать все кадры из .r16.</summary>
    public static IEnumerable<ThermalFrame> ReadFrames(string path)
    {
        using var fs = File.OpenRead(path);
        var hdr = ReadHeader(fs);
        int pixels = hdr.Width * hdr.Height;
        var bytes = new byte[pixels * 2];
        var br = new BinaryReader(fs);
        long n = 0;
        while (fs.Position + 8 + bytes.Length <= fs.Length)
        {
            long ticks = br.ReadInt64();
            if (br.Read(bytes, 0, bytes.Length) != bytes.Length) break;
            var raw = new ushort[pixels];
            Buffer.BlockCopy(bytes, 0, raw, 0, bytes.Length);
            var f = new ThermalFrame(raw) { Number = n++, Timestamp = new DateTime(ticks) };
            f.ComputeStats();
            yield return f;
        }
    }

    /// <summary>Экспорт одного кадра в CSV с температурами в °C.</summary>
    public static void WriteCsv(string path, ThermalFrame frame)
    {
        using var sw = new StreamWriter(path, false, Encoding.UTF8);
        var sb = new StringBuilder(ThermalFrame.Width * 8);
        for (int y = 0; y < ThermalFrame.Height; y++)
        {
            sb.Clear();
            for (int x = 0; x < ThermalFrame.Width; x++)
            {
                if (x > 0) sb.Append(';');
                sb.Append(ThermalFrame.ToCelsius(frame.Raw[y * ThermalFrame.Width + x]).ToString("0.00",
                    System.Globalization.CultureInfo.InvariantCulture));
            }
            sw.WriteLine(sb.ToString());
        }
    }
}
