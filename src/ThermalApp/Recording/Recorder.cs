using System.IO;
using OpenCvSharp;
using ThermalApp.Core;

namespace ThermalApp.Recording;

/// <summary>
/// Запись видео: раскрашенные кадры в .mp4 + параллельно сырая радиометрия в .r16,
/// чтобы потом можно было заново пересчитать температуры и палитры.
/// </summary>
public sealed class Recorder : IDisposable
{
    private VideoWriter? _video;
    private RadiometryFile.Writer? _raw;
    private Mat? _bgr;
    private OpenCvSharp.Size _outSize;
    private int _srcW, _srcH;
    private readonly object _gate = new();

    public bool IsRecording { get; private set; }
    public string? VideoPath { get; private set; }
    public string? RawPath { get; private set; }
    public long FrameCount { get; private set; }
    public DateTime StartedAt { get; private set; }
    public TimeSpan Duration => IsRecording ? DateTime.Now - StartedAt : TimeSpan.Zero;

    /// <param name="srcWidth">Ширина кадра, который будет приходить в Append (с учётом поворота).</param>
    /// <param name="srcHeight">Высота кадра, который будет приходить в Append.</param>
    /// <param name="scale">Во сколько раз увеличить кадр в видеофайле (256x192 слишком мелко для плееров).</param>
    public void Start(string basePath, int srcWidth, int srcHeight,
                      double fps = 25.0, int scale = 3, bool writeRaw = true)
    {
        lock (_gate)
        {
            if (IsRecording) return;
            Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);

            _srcW = srcWidth;
            _srcH = srcHeight;
            int w = srcWidth * scale;
            int h = srcHeight * scale;
            VideoPath = basePath + ".mp4";
            _outSize = new OpenCvSharp.Size(w, h);
            int fourcc = VideoWriter.FourCC('m', 'p', '4', 'v');
            _video = new VideoWriter(VideoPath, fourcc, fps, _outSize, true);
            if (!_video.IsOpened())
            {
                _video.Dispose();
                _video = null;
                throw new IOException("Не удалось создать видеофайл (нет кодека MP4V?).");
            }

            if (writeRaw)
            {
                RawPath = basePath + ".r16";
                _raw = new RadiometryFile.Writer(RawPath);
            }

            _bgr = new Mat(srcHeight, srcWidth, MatType.CV_8UC3);
            FrameCount = 0;
            StartedAt = DateTime.Now;
            IsRecording = true;
        }
    }

    /// <param name="bgra">Отрисованный кадр width*height*4, BGRA (уже с поворотом и зеркалом).</param>
    /// <param name="width">Ширина кадра; если не совпадает с заданной при Start, кадр пропускается.</param>
    /// <param name="height">Высота кадра.</param>
    public void Append(byte[] bgra, int width, int height, ThermalFrame frame)
    {
        lock (_gate)
        {
            if (!IsRecording || _video is null || _bgr is null) return;
            if (width != _srcW || height != _srcH) return;

            int pixels = _srcW * _srcH;
            unsafe
            {
                byte* dst = (byte*)_bgr.DataPointer;
                for (int i = 0, j = 0; i < pixels; i++, j += 3)
                {
                    int s = i * 4;
                    dst[j + 0] = bgra[s + 0];
                    dst[j + 1] = bgra[s + 1];
                    dst[j + 2] = bgra[s + 2];
                }
            }

            using var scaled = new Mat();
            Cv2.Resize(_bgr, scaled, _outSize, 0, 0, InterpolationFlags.Nearest);
            _video.Write(scaled);
            _raw?.Append(frame);
            FrameCount++;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            IsRecording = false;
            _video?.Release();
            _video?.Dispose();
            _video = null;
            _raw?.Dispose();
            _raw = null;
            _bgr?.Dispose();
            _bgr = null;
        }
    }

    public void Dispose() => Stop();
}
