using ThermalApp.Core;
using ThermalApp.Device;
using ThermalApp.Recording;

namespace ThermalApp.Probe;

/// <summary>
/// Консольная диагностика: перечислить устройства захвата, снять несколько кадров,
/// показать статистику температур, при желании — проверить vendor-команды.
///
///   ThermalApp.Probe                 — список устройств + 25 кадров с автоопределением
///   ThermalApp.Probe --device 1      — конкретный индекс
///   ThermalApp.Probe --frames 100    — сколько кадров снять
///   ThermalApp.Probe --usb           — плюс проверка vendor-команд по libusb
///   ThermalApp.Probe --save out      — сохранить первый кадр в out.r16 / out.csv
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        int device = -1, frames = 25;
        bool usb = false;
        string? save = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--device" when i + 1 < args.Length: device = int.Parse(args[++i]); break;
                case "--frames" when i + 1 < args.Length: frames = int.Parse(args[++i]); break;
                case "--usb": usb = true; break;
                case "--save" when i + 1 < args.Length: save = args[++i]; break;
                case "--dump" when i + 1 < args.Length: return Dump(device, args[++i]);
                case "--probe2": return Probe2(device);
            }
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("=== Устройства захвата ===");
        var devices = ThermalCapture.Enumerate();
        if (devices.Count == 0) Console.WriteLine("(ничего не найдено)");
        foreach (var d in devices) Console.WriteLine("  " + d);

        using var capture = new ThermalCapture { DecodeCameraImage = true };
        int got = 0;
        ThermalFrame? first = null;
        var renderer = new FrameRenderer { Palette = Palette.Ironbow, Mode = RangeMode.AutoRobust };
        var buf = new byte[ThermalFrame.Pixels * 4];
        Exception? failure = null;
        using var done = new ManualResetEventSlim();

        capture.FrameReady += f =>
        {
            first ??= f;
            renderer.Render(f, buf);
            if (got % 5 == 0)
                Console.WriteLine($"  кадр {f.Number,4}: min {f.MinC,7:0.00}  max {f.MaxC,7:0.00}  " +
                                  $"центр {f.CenterC,7:0.00}  сред {f.MeanC,7:0.00} °C   " +
                                  $"диапазон рендера {renderer.RangeLoC:0.0}..{renderer.RangeHiC:0.0}");
            if (++got >= frames) done.Set();
        };
        capture.Failed += ex => { failure = ex; done.Set(); };

        Console.WriteLine("\n=== Поток ===");
        try
        {
            capture.Start(device);
            Console.WriteLine($"  открыто устройство #{capture.DeviceIndex}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ОШИБКА: " + ex.Message);
            return 2;
        }

        if (!done.Wait(TimeSpan.FromSeconds(15)))
            Console.WriteLine($"  ВНИМАНИЕ: получено только {got} кадров за 15 с");
        capture.Stop();

        if (failure is not null) Console.WriteLine("  ОШИБКА захвата: " + failure.Message);
        Console.WriteLine($"  кадров получено: {got}, замер fps: {capture.MeasuredFps:0.#}");

        if (first is not null)
        {
            Console.WriteLine($"  картинка камеры декодирована: {(first.CameraBgr is not null ? "да" : "нет")}");
            Console.WriteLine("  проверка формулы: raw 19136 -> " +
                              $"{ThermalFrame.ToCelsius(19136):0.00} °C (ожидается ≈ 25.85)");
        }

        if (save is not null && first is not null)
        {
            using (var w = new RadiometryFile.Writer(save + ".r16")) w.Append(first);
            RadiometryFile.WriteCsv(save + ".csv", first);
            Console.WriteLine($"  сохранено: {save}.r16, {save}.csv");
        }

        if (usb)
        {
            Console.WriteLine("\n=== Vendor-команды (libusb) ===");
            using var cam = new CameraControl();
            if (!cam.TryConnect(out var err))
            {
                Console.WriteLine("  не подключилось: " + err);
            }
            else
            {
                Console.WriteLine($"  подключено, PID 0x{cam.ProductId:X4}");
                Try("PN", () => cam.GetDeviceInfoString(DeviceInfoType.PartNumber));
                Try("SN", () => cam.GetDeviceInfoString(DeviceInfoType.SerialNumber));
                Try("FW", () => cam.GetDeviceInfoString(DeviceInfoType.FwBuildVersion));
                Try("палитра", () => cam.GetPseudoColor().ToString());
                Try("ε", () => cam.Emissivity.ToString("0.000"));
                Try("дистанция, м", () => cam.DistanceMeters.ToString("0.00"));
                Try("high gain", () => cam.HighGain.ToString());
            }
        }

        return got > 0 ? 0 : 1;
    }

    /// <summary>Подбор рабочей комбинации бэкенда и порядка set-вызовов.</summary>
    private static int Probe2(int device)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (device < 0) device = ThermalCapture.FindThermalDevice() ?? 0;

        var variants = new (string Name, OpenCvSharp.VideoCaptureAPIs Api, bool SetSize, bool SetFourCc)[]
        {
            ("DSHOW, только CONVERT_RGB",       OpenCvSharp.VideoCaptureAPIs.DSHOW, false, false),
            ("DSHOW, CONVERT_RGB + размер",     OpenCvSharp.VideoCaptureAPIs.DSHOW, true,  false),
            ("DSHOW, CONVERT_RGB + fourcc",     OpenCvSharp.VideoCaptureAPIs.DSHOW, false, true),
            ("MSMF, только CONVERT_RGB",        OpenCvSharp.VideoCaptureAPIs.MSMF,  false, false),
            ("MSMF, CONVERT_RGB + размер",      OpenCvSharp.VideoCaptureAPIs.MSMF,  true,  false),
            ("MSMF, CONVERT_RGB + fourcc",      OpenCvSharp.VideoCaptureAPIs.MSMF,  false, true),
        };

        foreach (var v in variants)
        {
            Console.WriteLine($"\n=== {v.Name} ===");
            using var cap = new OpenCvSharp.VideoCapture(device, v.Api);
            if (!cap.IsOpened()) { Console.WriteLine("  не открылось"); continue; }

            cap.Set(OpenCvSharp.VideoCaptureProperties.ConvertRgb, 0);
            if (v.SetFourCc)
                cap.Set(OpenCvSharp.VideoCaptureProperties.FourCC,
                    OpenCvSharp.VideoWriter.FourCC('Y', 'U', 'Y', '2'));
            if (v.SetSize)
            {
                cap.Set(OpenCvSharp.VideoCaptureProperties.FrameWidth, 256);
                cap.Set(OpenCvSharp.VideoCaptureProperties.FrameHeight, 384);
            }

            using var mat = new OpenCvSharp.Mat();
            byte[]? buf = null;
            string info = "";
            for (int i = 0; i < 60; i++)
            {
                if (!cap.Read(mat) || mat.Empty()) { Thread.Sleep(5); continue; }
                long bytes = mat.Total() * mat.ElemSize();
                info = $"{mat.Rows}x{mat.Cols} type={mat.Type()} байт={bytes}";
                var b = new byte[bytes];
                System.Runtime.InteropServices.Marshal.Copy(mat.Data, b, 0, (int)bytes);
                buf = b;
            }
            Console.WriteLine("  Mat: " + info);
            if (buf is null) { Console.WriteLine("  кадров нет"); continue; }

            Console.WriteLine("  первые 32 байта: " + Convert.ToHexString(buf.AsSpan(0, Math.Min(32, buf.Length))));
            Console.WriteLine("  уникальных значений байт: " + buf.Distinct().Count());

            if (buf.Length == 196608)
            {
                AnalyzeHalf("верхняя половина как YUY2", buf, 0);
                AnalyzeRaw("нижняя половина как uint16", buf, 98304);
                AnalyzeRaw("ВЕРХНЯЯ половина как uint16", buf, 0);
            }
        }
        return 0;
    }

    private static void AnalyzeHalf(string title, byte[] buf, int offset)
    {
        int min = 255, max = 0;
        long sum = 0;
        for (int i = offset; i < offset + 98304; i += 2) { byte y = buf[i]; if (y < min) min = y; if (y > max) max = y; sum += y; }
        Console.WriteLine($"  {title}: Y min={min} max={max} avg={sum / 49152.0:0.0}");
    }

    private static void AnalyzeRaw(string title, byte[] buf, int offset)
    {
        ushort min = ushort.MaxValue, max = 0;
        double sum = 0;
        for (int i = offset; i < offset + 98304; i += 2)
        {
            ushort v = (ushort)(buf[i] | (buf[i + 1] << 8));
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }
        Console.WriteLine($"  {title}: raw {min}..{max} -> {ThermalFrame.ToCelsius(min):0.0}..{ThermalFrame.ToCelsius(max):0.0} °C, " +
                          $"средняя {sum / 49152.0 / 64.0 - 273.15:0.0} °C");
    }

    /// <summary>Низкоуровневая диагностика: что реально отдаёт OpenCV.</summary>
    private static int Dump(int device, string path)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (device < 0) device = ThermalCapture.FindThermalDevice() ?? 0;

        var backends = new[]
        {
            (Name: "DSHOW", Api: OpenCvSharp.VideoCaptureAPIs.DSHOW),
            (Name: "MSMF",  Api: OpenCvSharp.VideoCaptureAPIs.MSMF),
            (Name: "ANY",   Api: OpenCvSharp.VideoCaptureAPIs.ANY),
        };

        foreach (var be in backends)
        foreach (bool convertRgb in new[] { false, true })
        {
            Console.WriteLine($"\n=== device #{device}, {be.Name}, CONVERT_RGB={(convertRgb ? 1 : 0)} ===");
            using var cap = new OpenCvSharp.VideoCapture(device, be.Api);
            if (!cap.IsOpened()) { Console.WriteLine("  не открылось"); continue; }

            cap.Set(OpenCvSharp.VideoCaptureProperties.ConvertRgb, convertRgb ? 1 : 0);
            cap.Set(OpenCvSharp.VideoCaptureProperties.FourCC,
                OpenCvSharp.VideoWriter.FourCC('Y', 'U', 'Y', '2'));
            cap.Set(OpenCvSharp.VideoCaptureProperties.FrameWidth, 256);
            cap.Set(OpenCvSharp.VideoCaptureProperties.FrameHeight, 384);
            cap.Set(OpenCvSharp.VideoCaptureProperties.ConvertRgb, convertRgb ? 1 : 0);

            Console.WriteLine($"  W={cap.Get(OpenCvSharp.VideoCaptureProperties.FrameWidth)} " +
                              $"H={cap.Get(OpenCvSharp.VideoCaptureProperties.FrameHeight)} " +
                              $"FPS={cap.Get(OpenCvSharp.VideoCaptureProperties.Fps)} " +
                              $"CONVERT_RGB={cap.Get(OpenCvSharp.VideoCaptureProperties.ConvertRgb)} " +
                              $"FOURCC=0x{(long)cap.Get(OpenCvSharp.VideoCaptureProperties.FourCC):X}");

            using var mat = new OpenCvSharp.Mat();
            int read = 0, nonBlack = 0;
            byte[]? best = null;
            long bytes = 0;
            string matInfo = "";
            for (int i = 0; i < 60; i++)
            {
                if (!cap.Read(mat) || mat.Empty()) { Thread.Sleep(5); continue; }
                read++;
                bytes = mat.Total() * mat.ElemSize();
                matInfo = $"{mat.Rows}x{mat.Cols} type={mat.Type()} elem={mat.ElemSize()} байт={bytes}";
                var buf = new byte[bytes];
                System.Runtime.InteropServices.Marshal.Copy(mat.Data, buf, 0, (int)bytes);
                bool any = false;
                for (int k = 0; k < buf.Length; k++) if (buf[k] != 0) { any = true; break; }
                if (any) { nonBlack++; best = buf; }
            }
            Console.WriteLine($"  Mat: {matInfo}");
            Console.WriteLine($"  прочитано кадров: {read}, из них не полностью чёрных: {nonBlack}");
            if (best is null) continue;

            string f = $"{path}_{be.Name}_rgb{(convertRgb ? 1 : 0)}.bin";
            File.WriteAllBytes(f, best);
            Console.WriteLine($"  записано в {f}");

            int q = best.Length / 4;
            for (int k = 0; k < 4; k++)
            {
                int from = k * q, to = Math.Min(best.Length, from + q);
                int min = 255, max = 0, nz = 0;
                long sum = 0;
                for (int i = from; i < to; i++)
                {
                    byte v = best[i];
                    if (v != 0) nz++;
                    if (v < min) min = v;
                    if (v > max) max = v;
                    sum += v;
                }
                Console.WriteLine($"  четверть {k}: min={min} max={max} avg={(double)sum / (to - from):0.0} " +
                                  $"ненулевых={nz * 100.0 / (to - from):0.0}%");
            }
        }
        return 0;
    }

    private static void Try(string name, Func<string> f)
    {
        try { Console.WriteLine($"  {name}: {f()}"); }
        catch (Exception ex) { Console.WriteLine($"  {name}: ОШИБКА {ex.Message}"); }
    }
}
