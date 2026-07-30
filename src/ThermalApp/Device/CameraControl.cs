using System.IO;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace ThermalApp.Device;

public enum PseudoColor
{
    WhiteHot = 1,
    Reserved = 2,
    IronRed = 3,
    Rainbow1 = 4,
    Rainbow2 = 5,
    Rainbow3 = 6,
    RedHot = 7,
    HotRed = 8,
    Rainbow4 = 9,
    Rainbow5 = 10,
    BlackHot = 11
}

/// <summary>Радиометрические параметры (TPD_PROP_*), см. InfiRay IRCMD SDK.</summary>
public enum TpdParam
{
    /// <summary>Дистанция, шаг 1/163.835 м, 0..32767.</summary>
    Distance = 0,
    /// <summary>Температура отражения, K, 0..1024.</summary>
    ReflectedTemp = 1,
    /// <summary>Температура атмосферы, K, 0..1024.</summary>
    AtmosphericTemp = 2,
    /// <summary>Коэффициент излучения, шаг 1/127, 0..127.</summary>
    Emissivity = 3,
    /// <summary>Пропускание атмосферы, шаг 1/127, 0..127.</summary>
    Transmittance = 4,
    /// <summary>Выбор усиления: 0 = low gain (широкий диапазон), 1 = high gain (точнее, узкий диапазон).</summary>
    GainSelect = 5
}

public enum DeviceInfoType
{
    ChipId = 0,
    FwCompileDate = 1,
    DeviceQualification = 2,
    IrInfo = 3,
    ProjectInfo = 4,
    FwBuildVersion = 5,
    PartNumber = 6,
    SerialNumber = 7,
    SensorId = 8
}

/// <summary>
/// Vendor-команды модуля InfiRay Tiny1-C через libusb.
///
/// Протокол (отреверсен LeoDJ, P2Pro-Viewer):
///   запись:          bmRequestType=0x41, bRequest=0x45, wValue=0x78, wIndex=0x1d00/0x9d00/0x1d08...
///   чтение:          bmRequestType=0xC1, bRequest=0x44, wValue=0x78, wIndex=0x1d08/0x1d10
///   опрос готовности: 0xC1, 0x44, wValue=0x78, wIndex=0x0200, 1 байт
///
/// ВАЖНО на Windows:
///   1) нужен libusb-фильтр на "USB Camera (Interface 0)" — ставится через Zadig
///      (Options → List all devices → USB Camera (Interface 0) → libusb-win32 → Install Filter Driver);
///   2) видеопоток надо открыть ДО отправки команд, иначе вызов libusb зависает;
///   3) рядом с exe должен лежать libusb-1.0.dll (см. tools/get-libusb.ps1).
/// </summary>
public sealed class CameraControl : IDisposable
{
    // Известные PID: 0x5840 — Mileseey TR256i, 0x5830 — InfiRay P2 Pro / Topdon TC001
    public static readonly int VendorId = 0x0BDA;
    public static readonly int[] KnownProductIds = { 0x5840, 0x5830 };

    private const byte ReqTypeWrite = 0x41;
    private const byte ReqWrite = 0x45;
    private const byte ReqTypeRead = 0xC1;
    private const byte ReqRead = 0x44;
    private const int WValue = 0x78;

    private const int IdxCmd = 0x1d00;
    private const int IdxCmdLong = 0x9d00;
    private const int IdxData = 0x1d08;
    private const int IdxDataLong = 0x9d08;
    private const int IdxLongResult = 0x1d10;
    private const int IdxStatus = 0x0200;

    private const int CmdSetFlag = 0x4000;
    private const int CmdGetDeviceInfo = 0x8405;
    private const int CmdPseudoColor = 0x8409;
    private const int CmdShutterVtemp = 0x840c;
    private const int CmdPropTpdParams = 0x8514;
    private const int CmdCurVtemp = 0x8b0d;

    private static readonly int[] DeviceInfoLength = { 8, 8, 8, 26, 4, 50, 48, 16, 4 };

    private UsbContext? _ctx;
    private IUsbDevice? _dev;
    private readonly object _gate = new();

    public bool IsConnected => _dev is { IsOpen: true };
    public int ProductId { get; private set; }

    /// <summary>Пытается подключиться. Возвращает false и текст проблемы вместо исключения.</summary>
    public bool TryConnect(out string error)
    {
        error = "";
        lock (_gate)
        {
            if (IsConnected) return true;
            try
            {
                _ctx = new UsbContext();
                foreach (int pid in KnownProductIds)
                {
                    var dev = _ctx.Find(new UsbDeviceFinder { Vid = VendorId, Pid = pid });
                    if (dev is null) continue;
                    dev.Open();
                    try { dev.ClaimInterface(0); } catch { /* с filter-драйвером claim не нужен */ }
                    _dev = dev;
                    ProductId = pid;
                    return true;
                }
                error = $"Устройство {VendorId:X4}:{string.Join('/', KnownProductIds.Select(p => p.ToString("X4")))} " +
                        "не найдено через libusb. Установлен ли libusb-фильтр (Zadig)?";
            }
            catch (DllNotFoundException)
            {
                error = "Не найден libusb-1.0.dll. Положите его рядом с exe (см. tools/get-libusb.ps1).";
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("access", StringComparison.OrdinalIgnoreCase))
                {
                    error += "\nПохоже, для интерфейса 0 камеры не установлен libusb-фильтр. " +
                             "Запустите Zadig: Options → List all devices → «USB Camera (Interface 0)» → " +
                             "libusb-win32 → Install Filter Driver. Затем перезапустите приложение " +
                             "и подключайте управление уже ПОСЛЕ старта видеопотока.";
                }
            }
            Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _dev?.Close(); } catch { }
            _dev?.Dispose();
            _dev = null;
            _ctx?.Dispose();
            _ctx = null;
        }
    }

    // ---------- низкий уровень ----------

    private void Write(int wIndex, byte[] data)
    {
        var dev = _dev ?? throw new InvalidOperationException("Камера не подключена по libusb.");
        var setup = new UsbSetupPacket(ReqTypeWrite, ReqWrite, WValue, wIndex, data.Length);
        int n = dev.ControlTransfer(setup, data, 0, data.Length);
        if (n < 0) throw new IOException($"Ошибка control-write на 0x{wIndex:x4} (код {n}).");
    }

    private byte[] Read(int wIndex, int length)
    {
        var dev = _dev ?? throw new InvalidOperationException("Камера не подключена по libusb.");
        var buf = new byte[length];
        var setup = new UsbSetupPacket(ReqTypeRead, ReqRead, WValue, wIndex, length);
        int n = dev.ControlTransfer(setup, buf, 0, length);
        if (n < 0) throw new IOException($"Ошибка control-read на 0x{wIndex:x4} (код {n}).");
        return n == length ? buf : buf[..Math.Max(0, n)];
    }

    /// <summary>i2c_usb_check_access_done из SDK.</summary>
    private bool IsReady()
    {
        var r = Read(IdxStatus, 1);
        if (r.Length == 0) return false;
        byte s = r[0];
        if ((s & 0x01) == 0 && (s & 0x02) == 0) return true;
        if ((s & 0xFC) != 0) throw new IOException($"vdcmd status error 0x{s:X2}");
        return false;
    }

    private void WaitReady(int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (IsReady()) return;
            Thread.Sleep(1);
        }
        throw new TimeoutException("Камера не подтвердила готовность (таймаут vdcmd).");
    }

    private static void WriteLe16(byte[] b, int off, int v) { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); }
    private static void WriteLe32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
    }
    private static void WriteBe16(byte[] b, int off, int v) { b[off] = (byte)(v >> 8); b[off + 1] = (byte)v; }
    private static void WriteBe32(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16); b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }

    // ---------- "standard cmd" ----------

    private void StandardCmdWrite(int cmd, uint cmdParam, byte[]? payload = null)
    {
        lock (_gate)
        {
            if (payload is null || payload.Length == 0)
            {
                var d = new byte[8];
                WriteLe16(d, 0, cmd);
                WriteLe32(d, 2, cmdParam);
                Write(IdxCmd, d);
                WaitReady();
                return;
            }

            const int outerChunk = 0x100;
            const int innerChunk = 0x40;

            for (int i = 0; i < payload.Length; i += outerChunk)
            {
                int outerLen = Math.Min(outerChunk, payload.Length - i);

                var head = new byte[8];
                WriteLe16(head, 0, cmd);
                WriteLe32(head, 2, cmdParam + (uint)i);
                WriteBe16(head, 6, outerLen);
                Write(IdxCmdLong, head);
                WaitReady();

                for (int j = 0; j < outerLen; j += innerChunk)
                {
                    int innerLen = Math.Min(innerChunk, outerLen - j);
                    int toSend = outerLen - j;
                    var chunk = payload[(i + j)..(i + j + innerLen)];

                    if (toSend <= 8)
                    {
                        Write(IdxData + j, chunk);
                        WaitReady();
                    }
                    else if (toSend <= 64)
                    {
                        Write(IdxDataLong + j, chunk[..^8]);
                        Write(IdxData + j + toSend - 8, chunk[^8..]);
                        WaitReady();
                    }
                    else
                    {
                        Write(IdxDataLong + j, chunk);
                    }
                }
            }
        }
    }

    private byte[] StandardCmdRead(int cmd, uint cmdParam, int length)
    {
        if (length <= 0) return Array.Empty<byte>();
        lock (_gate)
        {
            const int outerChunk = 0x100;
            var result = new List<byte>(length);
            for (int i = 0; i < length; i += outerChunk)
            {
                int toRead = Math.Min(length - i, outerChunk);
                var head = new byte[8];
                WriteLe16(head, 0, cmd);
                WriteLe32(head, 2, cmdParam + (uint)i);
                WriteBe16(head, 6, toRead);
                Write(IdxCmd, head);
                WaitReady();
                result.AddRange(Read(IdxData, toRead));
            }
            return result.ToArray();
        }
    }

    // ---------- "long cmd" ----------

    private void LongCmdWrite(int cmd, int p1, uint p2, uint p3 = 0, uint p4 = 0)
    {
        lock (_gate)
        {
            var d1 = new byte[8];
            WriteLe16(d1, 0, cmd);
            WriteBe16(d1, 2, p1);
            WriteBe32(d1, 4, p2);
            var d2 = new byte[8];
            WriteBe32(d2, 0, p3);
            WriteBe32(d2, 4, p4);
            Write(IdxCmdLong, d1);
            Write(IdxData, d2);
            WaitReady();
        }
    }

    private byte[] LongCmdRead(int cmd, int p1, uint p2 = 0, uint p3 = 0, int dataLen = 2)
    {
        lock (_gate)
        {
            var d1 = new byte[8];
            WriteLe16(d1, 0, cmd);
            WriteBe16(d1, 2, p1);
            WriteBe32(d1, 4, p2);
            var d2 = new byte[8];
            WriteBe32(d2, 0, p3);
            WriteBe32(d2, 4, (uint)dataLen);
            Write(IdxCmdLong, d1);
            Write(IdxData, d2);
            WaitReady();
            return Read(IdxLongResult, dataLen);
        }
    }

    // ---------- высокий уровень ----------

    public void SetPseudoColor(PseudoColor color, int previewPath = 0) =>
        StandardCmdWrite(CmdPseudoColor | CmdSetFlag, (uint)previewPath, new[] { (byte)color });

    public PseudoColor GetPseudoColor(int previewPath = 0)
    {
        var r = StandardCmdRead(CmdPseudoColor, (uint)previewPath, 1);
        return r.Length > 0 ? (PseudoColor)r[0] : PseudoColor.WhiteHot;
    }

    public void SetTpdParam(TpdParam param, int value) =>
        LongCmdWrite(CmdPropTpdParams | CmdSetFlag, (int)param, (uint)value);

    public int GetTpdParam(TpdParam param)
    {
        var r = LongCmdRead(CmdPropTpdParams, (int)param, dataLen: 2);
        return r.Length >= 2 ? (r[0] << 8) | r[1] : -1;
    }

    /// <summary>Коэффициент излучения 0.01..1.0 (внутри 1/127).</summary>
    public double Emissivity
    {
        get => Math.Clamp(GetTpdParam(TpdParam.Emissivity) / 127.0, 0, 1);
        set => SetTpdParam(TpdParam.Emissivity, (int)Math.Clamp(Math.Round(value * 127.0), 1, 127));
    }

    /// <summary>Дистанция до объекта в метрах (внутри 1/163.835 м).</summary>
    public double DistanceMeters
    {
        get => GetTpdParam(TpdParam.Distance) / 163.835;
        set => SetTpdParam(TpdParam.Distance, (int)Math.Clamp(Math.Round(value * 163.835), 0, 32767));
    }

    /// <summary>Температура отражения, °C (внутри целые кельвины).</summary>
    public double ReflectedTempC
    {
        get => GetTpdParam(TpdParam.ReflectedTemp) - 273.15;
        set => SetTpdParam(TpdParam.ReflectedTemp, (int)Math.Clamp(Math.Round(value + 273.15), 0, 1024));
    }

    public double AtmosphericTempC
    {
        get => GetTpdParam(TpdParam.AtmosphericTemp) - 273.15;
        set => SetTpdParam(TpdParam.AtmosphericTemp, (int)Math.Clamp(Math.Round(value + 273.15), 0, 1024));
    }

    public double Transmittance
    {
        get => Math.Clamp(GetTpdParam(TpdParam.Transmittance) / 127.0, 0, 1);
        set => SetTpdParam(TpdParam.Transmittance, (int)Math.Clamp(Math.Round(value * 127.0), 1, 127));
    }

    /// <summary>true = high gain (узкий точный диапазон), false = low gain (широкий).</summary>
    public bool HighGain
    {
        get => GetTpdParam(TpdParam.GainSelect) != 0;
        set => SetTpdParam(TpdParam.GainSelect, value ? 1 : 0);
    }

    public byte[] GetDeviceInfoRaw(DeviceInfoType type) =>
        StandardCmdRead(CmdGetDeviceInfo, (uint)type, DeviceInfoLength[(int)type]);

    public string GetDeviceInfoString(DeviceInfoType type)
    {
        var raw = GetDeviceInfoRaw(type);
        int end = Array.IndexOf(raw, (byte)0);
        if (end < 0) end = raw.Length;
        var text = System.Text.Encoding.ASCII.GetString(raw, 0, end).Trim();
        return string.IsNullOrWhiteSpace(text) ? Convert.ToHexString(raw) : text;
    }

    /// <summary>Температура корпуса сенсора (сырой vtemp, единицы не документированы).</summary>
    public int GetCurrentVtemp()
    {
        var r = StandardCmdRead(CmdCurVtemp, 0, 2);
        return r.Length >= 2 ? (r[1] << 8) | r[0] : -1;
    }

    public int GetShutterVtemp()
    {
        var r = StandardCmdRead(CmdShutterVtemp, 0, 2);
        return r.Length >= 2 ? (r[1] << 8) | r[0] : -1;
    }

    // ---------- ручные эксперименты ----------

    /// <summary>Отправить произвольную standard-cmd (для исследования недокументированных команд).</summary>
    public void RawStandardWrite(int cmd, uint param, byte[]? payload) => StandardCmdWrite(cmd, param, payload);

    /// <summary>Прочитать произвольной standard-cmd.</summary>
    public byte[] RawStandardRead(int cmd, uint param, int length) => StandardCmdRead(cmd, param, length);

    public void RawLongWrite(int cmd, int p1, uint p2) => LongCmdWrite(cmd, p1, p2);

    public byte[] RawLongRead(int cmd, int p1, int length) => LongCmdRead(cmd, p1, dataLen: length);
}
