using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ThermalApp.Core;

public sealed record CaptureDeviceInfo(int Index, int Width, int Height, double Fps, string Backend)
{
    public bool LooksLikeThermal => Width == ThermalFrame.Width && Height == ThermalFrame.Height * 2;
    public override string ToString() =>
        $"#{Index}  {Width}x{Height} @ {Fps:0.#} fps  ({Backend}){(LooksLikeThermal ? "  ← тепловизор" : "")}";
}

/// <summary>
/// Захват UVC-потока камеры и разбор кадра на картинку + радиометрию.
///
/// Проверено на Mileseey TR256i (0BDA:5840), Windows 11, OpenCvSharp 4.13:
///   * бэкенд обязательно Media Foundation (MSMF). DirectShow всегда конвертирует поток
///     в BGR и игнорирует CAP_PROP_CONVERT_RGB, из-за чего сырая радиометрия теряется;
///   * порядок вызовов важен: сначала CONVERT_RGB = 0, потом FRAME_WIDTH/FRAME_HEIGHT.
///     Если размер не задать, MSMF отдаёт чёрный кадр (0x00 0x80 0x00 0x80 ...);
///   * FOURCC задавать НЕ надо — это ломает переговоры о формате;
///   * в итоге Mat приходит как 1 x 196608 CV_8UC1 — это ровно сырой кадр 256x384 YUY2.
/// </summary>
public sealed class ThermalCapture : IDisposable
{
    public const int FrameWidth = ThermalFrame.Width;         // 256
    public const int FrameHeight = ThermalFrame.Height * 2;   // 384
    public const int FrameBytes = FrameWidth * FrameHeight * 2;
    public const double ExpectedFps = 25.0;

    private VideoCapture? _cap;
    private Thread? _thread;
    private volatile bool _running;
    private readonly byte[] _buffer = new byte[FrameBytes];
    private long _counter;

    /// <summary>Декодировать также псевдоцветную картинку самой камеры (чуть дороже по CPU).</summary>
    public bool DecodeCameraImage { get; set; } = true;

    public int DeviceIndex { get; private set; } = -1;
    public bool IsRunning => _running;
    public double MeasuredFps { get; private set; }

    /// <summary>true, как только пришёл первый настоящий кадр (а не заглушка при инициализации).</summary>
    public bool IsWarmedUp { get; private set; }
    public int SkippedWarmupFrames { get; private set; }

    public event Action<ThermalFrame>? FrameReady;
    public event Action<Exception>? Failed;

    /// <summary>Перебрать индексы устройств захвата и вернуть те, что открываются.</summary>
    public static List<CaptureDeviceInfo> Enumerate(int maxIndex = 8)
    {
        var result = new List<CaptureDeviceInfo>();
        for (int i = 0; i <= maxIndex; i++)
        {
            VideoCapture? cap = null;
            try
            {
                cap = new VideoCapture(i, VideoCaptureAPIs.MSMF);
                if (!cap.IsOpened()) continue;
                int w = (int)cap.Get(VideoCaptureProperties.FrameWidth);
                int h = (int)cap.Get(VideoCaptureProperties.FrameHeight);
                double fps = cap.Get(VideoCaptureProperties.Fps);
                result.Add(new CaptureDeviceInfo(i, w, h, fps, "MSMF"));
            }
            catch { /* индекс занят или отсутствует */ }
            finally { cap?.Release(); cap?.Dispose(); }
        }
        return result;
    }

    /// <summary>Найти индекс тепловизора по характерному разрешению 256x384.</summary>
    public static int? FindThermalDevice(int maxIndex = 8) =>
        Enumerate(maxIndex).FirstOrDefault(d => d.LooksLikeThermal)?.Index;

    public void Start(int deviceIndex = -1)
    {
        if (_running) return;

        if (deviceIndex < 0)
        {
            deviceIndex = FindThermalDevice()
                ?? throw new InvalidOperationException(
                    "Тепловизор не найден. Проверьте, что камера подключена и не занята другим приложением " +
                    "(искали устройство захвата с разрешением 256x384).");
        }

        var cap = new VideoCapture(deviceIndex, VideoCaptureAPIs.MSMF);
        if (!cap.IsOpened())
        {
            cap.Dispose();
            throw new InvalidOperationException($"Не удалось открыть устройство захвата #{deviceIndex}.");
        }

        // Порядок критичен, см. комментарий к классу.
        cap.Set(VideoCaptureProperties.ConvertRgb, 0);
        cap.Set(VideoCaptureProperties.FrameWidth, FrameWidth);
        cap.Set(VideoCaptureProperties.FrameHeight, FrameHeight);

        int w = (int)cap.Get(VideoCaptureProperties.FrameWidth);
        int h = (int)cap.Get(VideoCaptureProperties.FrameHeight);
        if (w != FrameWidth || h != FrameHeight)
        {
            cap.Release();
            cap.Dispose();
            throw new InvalidOperationException(
                $"Устройство #{deviceIndex} отдаёт {w}x{h}, а ожидается {FrameWidth}x{FrameHeight}. " +
                "Похоже, это не тепловизор.");
        }

        // Убедимся, что действительно приходит сырой буфер, а не сконвертированный BGR.
        using (var probe = new Mat())
        {
            bool gotRaw = false;
            for (int i = 0; i < 40 && !gotRaw; i++)
            {
                if (!cap.Read(probe) || probe.Empty()) { Thread.Sleep(5); continue; }
                gotRaw = probe.Total() * probe.ElemSize() == FrameBytes;
            }
            if (!gotRaw)
            {
                cap.Release();
                cap.Dispose();
                throw new InvalidOperationException(
                    "Камера отдаёт уже сконвертированный кадр — сырая радиометрия недоступна. " +
                    "Обычно это значит, что выбран бэкенд DirectShow или устройство занято другим приложением.");
            }
        }

        _cap = cap;
        DeviceIndex = deviceIndex;
        _running = true;
        _counter = 0;
        IsWarmedUp = false;
        SkippedWarmupFrames = 0;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ThermalCapture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1500);
        _thread = null;
        _cap?.Release();
        _cap?.Dispose();
        _cap = null;
    }

    private void Loop()
    {
        var mat = new Mat();
        var sw = Stopwatch.StartNew();
        int fpsFrames = 0;
        try
        {
            while (_running)
            {
                if (_cap is null) break;
                if (!_cap.Read(mat) || mat.Empty()) { Thread.Sleep(1); continue; }

                long total = mat.Total() * mat.ElemSize();
                if (total < FrameBytes) continue;

                Marshal.Copy(mat.Data, _buffer, 0, FrameBytes);

                var frame = Parse(_buffer, DecodeCameraImage);

                // После открытия потока модуль ~1-2 с отдаёт заглушку: равномерный YUY2-чёрный
                // (все отсчёты 0x8000). Реальных кадров с одинаковыми min и max не бывает.
                if (frame.RawMin == frame.RawMax)
                {
                    SkippedWarmupFrames++;
                    continue;
                }
                IsWarmedUp = true;

                frame.Number = _counter++;
                frame.Timestamp = DateTime.Now;

                fpsFrames++;
                if (sw.ElapsedMilliseconds >= 1000)
                {
                    MeasuredFps = fpsFrames * 1000.0 / sw.ElapsedMilliseconds;
                    fpsFrames = 0;
                    sw.Restart();
                }

                FrameReady?.Invoke(frame);
            }
        }
        catch (Exception ex)
        {
            _running = false;
            Failed?.Invoke(ex);
        }
        finally
        {
            mat.Dispose();
        }
    }

    /// <summary>Разобрать сырой UVC-кадр 256x384 YUY2 на картинку и радиометрию.</summary>
    public static ThermalFrame Parse(byte[] frameBytes, bool decodeImage)
    {
        int half = FrameBytes / 2;

        // нижняя половина: 256*192 uint16 LE
        var raw = new ushort[ThermalFrame.Pixels];
        Buffer.BlockCopy(frameBytes, half, raw, 0, half);

        var frame = new ThermalFrame(raw);
        frame.ComputeStats();

        if (decodeImage)
        {
            // верхняя половина: 192 строки YUY2 (2 байта на пиксель)
            using var yuy2 = new Mat(ThermalFrame.Height, ThermalFrame.Width, MatType.CV_8UC2);
            Marshal.Copy(frameBytes, 0, yuy2.Data, half);
            using var bgr = new Mat();
            Cv2.CvtColor(yuy2, bgr, ColorConversionCodes.YUV2BGR_YUY2);
            var bytes = new byte[ThermalFrame.Pixels * 3];
            Marshal.Copy(bgr.Data, bytes, 0, bytes.Length);
            frame.CameraBgr = bytes;
        }

        return frame;
    }

    public void Dispose() => Stop();
}
