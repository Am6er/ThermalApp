using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThermalApp.Settings;

/// <summary>
/// Настройки приложения. Лежат в settings.json рядом с exe, поэтому конфиг
/// переносится вместе с папкой приложения. Если файл недоступен для записи
/// (например, папка только для чтения), сохранение молча пропускается —
/// причина остаётся в <see cref="LastError"/>.
/// </summary>
public sealed class AppSettings
{
    // ---- устройство ----
    public int DeviceIndex { get; set; } = -1;
    public bool AutoStart { get; set; }

    // ---- изображение ----
    public string Palette { get; set; } = "Ironbow";
    /// <summary>Поворот в шагах по 90° по часовой стрелке, 0..3.</summary>
    public int Rotation { get; set; }
    public bool Mirror { get; set; }
    public bool UseCameraImage { get; set; }
    public bool SmoothScaling { get; set; } = true;
    public string RangeMode { get; set; } = "AutoRobust";
    public double ManualMinC { get; set; } = 20;
    public double ManualMaxC { get; set; } = 40;
    public double Gamma { get; set; } = 1.0;
    public double Smoothing { get; set; } = 0.6;

    // ---- измерения ----
    public int SpotSize { get; set; } = 3;
    public bool ShowMinMax { get; set; } = true;
    public bool ShowCenter { get; set; } = true;
    public bool ShowSpots { get; set; } = true;
    /// <summary>Закреплённые точки в координатах кадра.</summary>
    public List<int[]> Spots { get; set; } = new();

    // ---- параметры камеры (применяются vendor-командами) ----
    public double Emissivity { get; set; } = 0.95;
    public double DistanceM { get; set; } = 0.5;
    public double ReflectedC { get; set; } = 25;
    public double AtmosphericC { get; set; } = 25;
    public bool HighGain { get; set; } = true;

    // ---- состояние интерфейса ----
    /// <summary>Свёрнута/развёрнута каждая секция панели, ключ — имя Expander'а.</summary>
    public Dictionary<string, bool> Sections { get; set; } = new();
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    [JsonIgnore]
    public string? LastError { get; private set; }

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    loaded.Rotation = ((loaded.Rotation % 4) + 4) % 4;
                    loaded.SpotSize = Math.Clamp(loaded.SpotSize | 1, 1, 9);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            return new AppSettings { LastError = "Не удалось прочитать settings.json: " + ex.Message };
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = "Не удалось сохранить settings.json: " + ex.Message;
        }
    }
}
