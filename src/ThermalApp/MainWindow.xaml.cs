using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;
using ThermalApp.Core;
using ThermalApp.Device;
using ThermalApp.Recording;
using ThermalApp.Settings;

namespace ThermalApp;

public partial class MainWindow : Window
{
    private const int W = ThermalFrame.Width;
    private const int H = ThermalFrame.Height;

    private readonly ThermalCapture _capture = new();
    private readonly FrameRenderer _renderer = new();
    private readonly Recorder _recorder = new();
    private readonly CameraControl _camera = new();

    private WriteableBitmap _bmp = new(W, H, 96, 96, PixelFormats.Bgra32, null);
    private readonly byte[] _renderBuf = new byte[W * H * 4];   // кадр в исходной ориентации
    private readonly byte[] _uiBuf = new byte[W * H * 4];       // то же с зеркалом и поворотом
    private readonly object _bufGate = new();

    /// <summary>Размеры содержимого _uiBuf (меняются при повороте на 90/270).</summary>
    private int _uiBufW = W, _uiBufH = H;
    /// <summary>Размеры того, что реально показано на экране (обновляются в PushToScreen).</summary>
    private int _dispW = W, _dispH = H;

    /// <summary>Поворот в шагах по 90° по часовой стрелке: 0..3.</summary>
    private volatile int _rotation;

    private ThermalFrame? _lastFrame;
    private volatile bool _uiPending;
    private volatile bool _mirror;
    private bool _useCameraImage;
    private int _spotSize = 3;
    private (int X, int Y)? _cursor;
    private readonly List<(int X, int Y)> _spots = new();
    private readonly DispatcherTimerHelper _timer;

    private Marker _minMarker = null!, _maxMarker = null!, _centerMarker = null!, _cursorMarker = null!;
    private readonly List<Marker> _spotMarkers = new();

    private readonly string _outputDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ThermalApp");

    private readonly AppSettings _settings;
    private readonly DispatcherTimerHelper _saveTimer;
    private bool _loadingSettings = true;

    public MainWindow()
    {
        _settings = AppSettings.Load();

        InitializeComponent();

        Video.Source = _bmp;
        RenderOptions.SetBitmapScalingMode(Video, BitmapScalingMode.HighQuality);

        foreach (var p in Palette.All) PaletteCombo.Items.Add(p);
        PaletteCombo.SelectedItem = Palette.Ironbow;

        foreach (var c in Enum.GetValues<PseudoColor>())
            if (c != PseudoColor.Reserved) CamPaletteCombo.Items.Add(c);

        BuildMarkers();
        RefreshDevices();

        Directory.CreateDirectory(_outputDir);
        FolderText.Text = _outputDir;

        _capture.FrameReady += OnFrameReady;
        _capture.Failed += ex => Dispatcher.BeginInvoke(() => SetStatus("Ошибка захвата: " + ex.Message));

        _timer = new DispatcherTimerHelper(TimeSpan.FromMilliseconds(400), OnTick);
        _saveTimer = new DispatcherTimerHelper(TimeSpan.FromSeconds(1.5), SaveSettingsNow);

        ApplySettingsToUi();
        UpdateScaleBar();
        ApplyUiToRenderer();
        _loadingSettings = false;

        foreach (var sec in Sections)
        {
            sec.Expanded += Section_StateChanged;
            sec.Collapsed += Section_StateChanged;
        }

        Loaded += OnLoadedFirstTime;
        Closing += (_, _) => SaveSettingsNow();
        Closed += (_, _) => { _capture.Dispose(); _recorder.Dispose(); _camera.Dispose(); _timer.Stop(); _saveTimer.Stop(); };
    }

    private Expander[] Sections => new[] { SecDevice, SecImage, SecMeasure, SecCapture, SecCamera };

    private void OnLoadedFirstTime(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedFirstTime;
        if (_settings.LastError is { } err) SetStatus(err);
        if (AutoStartCheck.IsChecked == true) StartCapture(silentIfMissing: true);
    }

    // ---------------- настройки ----------------

    private void ApplySettingsToUi()
    {
        var s = _settings;

        AutoStartCheck.IsChecked = s.AutoStart;

        PaletteCombo.SelectedItem = Palette.All.FirstOrDefault(p => p.Name == s.Palette) ?? Palette.Ironbow;

        _rotation = s.Rotation;
        RotationLabel.Text = $"Поворот: {_rotation * 90}°";
        _uiBufW = _dispW = (_rotation & 1) != 0 ? H : W;
        _uiBufH = _dispH = (_rotation & 1) != 0 ? W : H;
        if (_bmp.PixelWidth != _uiBufW)
        {
            _bmp = new WriteableBitmap(_uiBufW, _uiBufH, 96, 96, PixelFormats.Bgra32, null);
            Video.Source = _bmp;
        }

        MirrorCheck.IsChecked = s.Mirror;
        UseCameraImageCheck.IsChecked = s.UseCameraImage;
        SmoothScalingCheck.IsChecked = s.SmoothScaling;

        MinBox.Text = s.ManualMinC.ToString("0.##", CultureInfo.InvariantCulture);
        MaxBox.Text = s.ManualMaxC.ToString("0.##", CultureInfo.InvariantCulture);
        switch (s.RangeMode)
        {
            case nameof(RangeMode.Manual): RangeManualRadio.IsChecked = true; break;
            case nameof(RangeMode.Auto): RangeAutoRadio.IsChecked = true; break;
            default: RangeRobustRadio.IsChecked = true; break;
        }

        GammaSlider.Value = Math.Clamp(s.Gamma, GammaSlider.Minimum, GammaSlider.Maximum);
        SmoothSlider.Value = Math.Clamp(s.Smoothing, SmoothSlider.Minimum, SmoothSlider.Maximum);
        SpotSizeSlider.Value = Math.Clamp(s.SpotSize, SpotSizeSlider.Minimum, SpotSizeSlider.Maximum);

        ShowMinMaxCheck.IsChecked = s.ShowMinMax;
        ShowCenterCheck.IsChecked = s.ShowCenter;
        ShowSpotsCheck.IsChecked = s.ShowSpots;

        _spots.Clear();
        foreach (var p in s.Spots)
            if (p.Length == 2 && (uint)p[0] < W && (uint)p[1] < H) _spots.Add((p[0], p[1]));

        EmsBox.Text = s.Emissivity.ToString("0.##", CultureInfo.InvariantCulture);
        DistBox.Text = s.DistanceM.ToString("0.##", CultureInfo.InvariantCulture);
        ReflBox.Text = s.ReflectedC.ToString("0.#", CultureInfo.InvariantCulture);
        AtmBox.Text = s.AtmosphericC.ToString("0.#", CultureInfo.InvariantCulture);
        HighGainCheck.IsChecked = s.HighGain;

        foreach (var sec in Sections)
            if (sec.Name is { Length: > 0 } name && s.Sections.TryGetValue(name, out bool expanded))
                sec.IsExpanded = expanded;

        if (s.WindowWidth is > 200 && s.WindowHeight is > 200)
        {
            Width = s.WindowWidth.Value;
            Height = s.WindowHeight.Value;
            if (s.WindowLeft is { } l && s.WindowTop is { } t && IsOnScreen(l, t))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l;
                Top = t;
            }
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private static bool IsOnScreen(double left, double top) =>
        left > -2000 && top > -2000 &&
        left < SystemParameters.VirtualScreenWidth + SystemParameters.VirtualScreenLeft &&
        top < SystemParameters.VirtualScreenHeight + SystemParameters.VirtualScreenTop;

    /// <summary>Отложенное сохранение: настройки пишутся через 1.5 с после последнего изменения.</summary>
    private void MarkSettingsDirty()
    {
        if (_loadingSettings) return;
        _saveTimer.Restart();
    }

    private void CollectSettings()
    {
        var s = _settings;

        s.DeviceIndex = (DeviceCombo.SelectedItem as CaptureDeviceInfo)?.Index ?? s.DeviceIndex;
        s.AutoStart = AutoStartCheck.IsChecked == true;

        s.Palette = (PaletteCombo.SelectedItem as Palette)?.Name ?? s.Palette;
        s.Rotation = _rotation;
        s.Mirror = MirrorCheck.IsChecked == true;
        s.UseCameraImage = UseCameraImageCheck.IsChecked == true;
        s.SmoothScaling = SmoothScalingCheck.IsChecked == true;
        s.RangeMode = _renderer.Mode.ToString();
        s.ManualMinC = _renderer.ManualMinC;
        s.ManualMaxC = _renderer.ManualMaxC;
        s.Gamma = GammaSlider.Value;
        s.Smoothing = SmoothSlider.Value;

        s.SpotSize = _spotSize;
        s.ShowMinMax = ShowMinMaxCheck.IsChecked == true;
        s.ShowCenter = ShowCenterCheck.IsChecked == true;
        s.ShowSpots = ShowSpotsCheck.IsChecked == true;
        s.Spots = _spots.Select(p => new[] { p.X, p.Y }).ToList();

        s.Emissivity = ParseD(EmsBox.Text, s.Emissivity);
        s.DistanceM = ParseD(DistBox.Text, s.DistanceM);
        s.ReflectedC = ParseD(ReflBox.Text, s.ReflectedC);
        s.AtmosphericC = ParseD(AtmBox.Text, s.AtmosphericC);
        s.HighGain = HighGainCheck.IsChecked == true;

        s.Sections.Clear();
        foreach (var sec in Sections)
            if (sec.Name is { Length: > 0 } name) s.Sections[name] = sec.IsExpanded;

        s.WindowMaximized = WindowState == WindowState.Maximized;
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (bounds.Width > 200 && bounds.Height > 200)
        {
            s.WindowLeft = bounds.Left;
            s.WindowTop = bounds.Top;
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
        }
    }

    private void SaveSettingsNow()
    {
        _saveTimer.Stop();
        if (_loadingSettings) return;
        CollectSettings();
        _settings.Save();
        if (_settings.LastError is { } err) SetStatus(err);
    }

    private void Section_StateChanged(object sender, RoutedEventArgs e) => MarkSettingsDirty();

    private void AutoStart_Changed(object sender, RoutedEventArgs e) => MarkSettingsDirty();

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => MarkSettingsDirty();

    private void Marker_Changed(object sender, RoutedEventArgs e) => MarkSettingsDirty();

    private void Tpd_Changed(object sender, TextChangedEventArgs e) => MarkSettingsDirty();

    // ---------------- захват и отрисовка ----------------

    private void OnFrameReady(ThermalFrame frame)
    {
        _lastFrame = frame;

        lock (_bufGate)
        {
            if (_useCameraImage && frame.CameraBgr is { } bgr)
            {
                for (int i = 0; i < ThermalFrame.Pixels; i++)
                {
                    int s = i * 3, d = i * 4;
                    _renderBuf[d + 0] = bgr[s + 0];
                    _renderBuf[d + 1] = bgr[s + 1];
                    _renderBuf[d + 2] = bgr[s + 2];
                    _renderBuf[d + 3] = 255;
                }
            }
            else
            {
                _renderer.Render(frame, _renderBuf);
            }

            TransformToUiBuf(_mirror, _rotation);
        }

        if (_recorder.IsRecording) _recorder.Append(_uiBuf, _uiBufW, _uiBufH, frame);

        if (_uiPending) return;
        _uiPending = true;
        Dispatcher.BeginInvoke(PushToScreen);
    }

    /// <summary>Переносит _renderBuf в _uiBuf с учётом зеркала и поворота. Вызывается под _bufGate.</summary>
    private void TransformToUiBuf(bool mirror, int rot)
    {
        bool swap = (rot & 1) != 0;
        _uiBufW = swap ? H : W;
        _uiBufH = swap ? W : H;

        for (int fy = 0; fy < H; fy++)
        {
            for (int fx = 0; fx < W; fx++)
            {
                int mx = mirror ? W - 1 - fx : fx;
                int my = fy;
                int dx, dy;
                switch (rot)
                {
                    case 1: dx = H - 1 - my; dy = mx; break;
                    case 2: dx = W - 1 - mx; dy = H - 1 - my; break;
                    case 3: dx = my; dy = W - 1 - mx; break;
                    default: dx = mx; dy = my; break;
                }
                int s = (fy * W + fx) * 4;
                int d = (dy * _uiBufW + dx) * 4;
                _uiBuf[d + 0] = _renderBuf[s + 0];
                _uiBuf[d + 1] = _renderBuf[s + 1];
                _uiBuf[d + 2] = _renderBuf[s + 2];
                _uiBuf[d + 3] = 255;
            }
        }
    }

    /// <summary>Координаты пикселя кадра -> координаты на экранной картинке.</summary>
    private (int X, int Y) FrameToDisplay(int fx, int fy)
    {
        int mx = _mirror ? W - 1 - fx : fx;
        int my = fy;
        return _rotation switch
        {
            1 => (H - 1 - my, mx),
            2 => (W - 1 - mx, H - 1 - my),
            3 => (my, W - 1 - mx),
            _ => (mx, my)
        };
    }

    /// <summary>Обратное преобразование: точка на экранной картинке -> пиксель кадра.</summary>
    private (int X, int Y)? DisplayToFrame(int dx, int dy)
    {
        if ((uint)dx >= _dispW || (uint)dy >= _dispH) return null;
        int mx, my;
        switch (_rotation)
        {
            case 1: my = H - 1 - dx; mx = dy; break;
            case 2: mx = W - 1 - dx; my = H - 1 - dy; break;
            case 3: mx = W - 1 - dy; my = dx; break;
            default: mx = dx; my = dy; break;
        }
        if ((uint)mx >= W || (uint)my >= H) return null;
        int fx = _mirror ? W - 1 - mx : mx;
        return (fx, my);
    }

    private void PushToScreen()
    {
        _uiPending = false;
        var frame = _lastFrame;
        if (frame is null) return;

        lock (_bufGate)
        {
            if (_bmp.PixelWidth != _uiBufW || _bmp.PixelHeight != _uiBufH)
            {
                _bmp = new WriteableBitmap(_uiBufW, _uiBufH, 96, 96, PixelFormats.Bgra32, null);
                Video.Source = _bmp;
            }
            _dispW = _uiBufW;
            _dispH = _uiBufH;
            _bmp.WritePixels(new Int32Rect(0, 0, _dispW, _dispH), _uiBuf, _dispW * 4, 0);
        }

        FitViewArea();
        UpdateOverlay(frame);
        UpdateReadouts(frame);
    }

    // ---------------- оверлей ----------------

    private sealed class Marker
    {
        public ShapePath Cross = null!;
        public TextBlock Label = null!;
        public void SetVisible(bool v)
        {
            var vis = v ? Visibility.Visible : Visibility.Collapsed;
            Cross.Visibility = vis;
            Label.Visibility = vis;
        }
    }

    private Marker CreateMarker(Brush brush, double size = 12)
    {
        var geo = new GeometryGroup();
        geo.Children.Add(new LineGeometry(new Point(0, size / 2), new Point(size, size / 2)));
        geo.Children.Add(new LineGeometry(new Point(size / 2, 0), new Point(size / 2, size)));
        var cross = new ShapePath
        {
            Data = geo,
            Stroke = brush,
            StrokeThickness = 1.5,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { ShadowDepth = 0, BlurRadius = 3, Color = Colors.Black, Opacity = 0.9 }
        };
        var label = new TextBlock
        {
            Foreground = brush,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { ShadowDepth = 0, BlurRadius = 3, Color = Colors.Black, Opacity = 0.9 }
        };
        Overlay.Children.Add(cross);
        Overlay.Children.Add(label);
        var m = new Marker { Cross = cross, Label = label };
        m.SetVisible(false);
        return m;
    }

    private void BuildMarkers()
    {
        _maxMarker = CreateMarker(Brushes.OrangeRed);
        _minMarker = CreateMarker(Brushes.DeepSkyBlue);
        _centerMarker = CreateMarker(Brushes.White, 10);
        _cursorMarker = CreateMarker(Brushes.Yellow, 14);
    }

    private void PlaceMarker(Marker m, int px, int py, string text, bool visible)
    {
        m.SetVisible(visible);
        if (!visible) return;
        double sx = ViewArea.Width / _dispW;
        double sy = ViewArea.Height / _dispH;
        var (dx, dy) = FrameToDisplay(px, py);
        double cx = (dx + 0.5) * sx;
        double cy = (dy + 0.5) * sy;
        double size = ((GeometryGroup)m.Cross.Data).Children[0].Bounds.Width;
        Canvas.SetLeft(m.Cross, cx - size / 2);
        Canvas.SetTop(m.Cross, cy - size / 2);
        m.Label.Text = text;
        m.Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double lx = cx + 9;
        if (lx + m.Label.DesiredSize.Width > ViewArea.Width) lx = cx - 9 - m.Label.DesiredSize.Width;
        Canvas.SetLeft(m.Label, lx);
        Canvas.SetTop(m.Label, Math.Clamp(cy - 9, 0, Math.Max(0, ViewArea.Height - 18)));
    }

    private void UpdateOverlay(ThermalFrame frame)
    {
        bool minmax = ShowMinMaxCheck.IsChecked == true;
        var maxP = frame.MaxPoint;
        var minP = frame.MinPoint;
        PlaceMarker(_maxMarker, maxP.X, maxP.Y, $"{frame.MaxC:0.0}°", minmax);
        PlaceMarker(_minMarker, minP.X, minP.Y, $"{frame.MinC:0.0}°", minmax);
        PlaceMarker(_centerMarker, W / 2, H / 2, $"{frame.TemperatureAt(W / 2, H / 2, _spotSize):0.0}°",
            ShowCenterCheck.IsChecked == true);

        if (_cursor is { } c)
            PlaceMarker(_cursorMarker, c.X, c.Y, $"{frame.TemperatureAt(c.X, c.Y, _spotSize):0.0}°", true);
        else
            _cursorMarker.SetVisible(false);

        while (_spotMarkers.Count < _spots.Count) _spotMarkers.Add(CreateMarker(Brushes.Lime, 11));
        bool showSpots = ShowSpotsCheck.IsChecked == true;
        for (int i = 0; i < _spotMarkers.Count; i++)
        {
            if (i < _spots.Count && showSpots)
                PlaceMarker(_spotMarkers[i], _spots[i].X, _spots[i].Y,
                    $"{frame.TemperatureAt(_spots[i].X, _spots[i].Y, _spotSize):0.0}°", true);
            else
                _spotMarkers[i].SetVisible(false);
        }
    }

    private void UpdateReadouts(ThermalFrame frame)
    {
        string cursor = _cursor is { } c
            ? $"курсор {c.X},{c.Y}: {frame.TemperatureAt(c.X, c.Y, _spotSize):0.0} °C"
            : "курсор: —";
        TempText.Text = $"min {frame.MinC:0.0}  |  max {frame.MaxC:0.0}  |  центр {frame.CenterC:0.0} °C  |  {cursor}";

        StatsText.Text =
            $"min    {frame.MinC,7:0.00} °C  ({frame.MinPoint.X},{frame.MinPoint.Y})\n" +
            $"max    {frame.MaxC,7:0.00} °C  ({frame.MaxPoint.X},{frame.MaxPoint.Y})\n" +
            $"сред.  {frame.MeanC,7:0.00} °C\n" +
            $"центр  {frame.CenterC,7:0.00} °C\n" +
            $"raw    {frame.RawMin} … {frame.RawMax}";

        ScaleHiText.Text = $"{_renderer.RangeHiC:0.0}°";
        ScaleLoText.Text = $"{_renderer.RangeLoC:0.0}°";
    }

    private void UpdateScaleBar()
    {
        var lut = _renderer.Palette.Lut;
        var stops = new GradientStopCollection();
        for (int i = 0; i <= 16; i++)
        {
            int idx = i * 255 / 16;
            var c = Color.FromRgb(lut[idx * 4 + 2], lut[idx * 4 + 1], lut[idx * 4 + 0]);
            stops.Add(new GradientStop(c, 1.0 - i / 16.0));
        }
        ScaleBar.Fill = new LinearGradientBrush(stops, new Point(0, 0), new Point(0, 1));
    }

    // ---------------- таймер ----------------

    private void OnTick()
    {
        FpsText.Text = !_capture.IsRunning ? "—"
            : !_capture.IsWarmedUp ? "инициализация модуля…"
            : $"{_capture.MeasuredFps:0.#} fps (#{_capture.DeviceIndex})";
        RecordText.Text = _recorder.IsRecording
            ? $"● запись {_recorder.Duration:mm\\:ss}, кадров {_recorder.FrameCount}"
            : "—";
    }

    // ---------------- устройство ----------------

    private void RefreshDevices()
    {
        bool wasLoading = _loadingSettings;
        _loadingSettings = true;   // перевыбор устройства не должен считаться правкой настроек
        try
        {
            DeviceCombo.Items.Clear();
            var list = ThermalCapture.Enumerate();
            foreach (var d in list) DeviceCombo.Items.Add(d);

            // приоритет: запомненное устройство -> похожее на тепловизор -> первое доступное
            var saved = list.FirstOrDefault(d => d.Index == _settings.DeviceIndex);
            var thermal = list.FirstOrDefault(d => d.LooksLikeThermal);
            DeviceCombo.SelectedItem = saved ?? thermal ?? list.FirstOrDefault();

            SetStatus(thermal is null && saved is null
                ? "Тепловизор не найден среди устройств захвата. Подключите камеру и нажмите «Обновить»."
                : $"Выбрано устройство #{(DeviceCombo.SelectedItem as CaptureDeviceInfo)?.Index}");
        }
        finally
        {
            _loadingSettings = wasLoading;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void Start_Click(object sender, RoutedEventArgs e) => StartCapture(false);

    private void StartCapture(bool silentIfMissing)
    {
        if (_capture.IsRunning) return;
        try
        {
            int idx = (DeviceCombo.SelectedItem as CaptureDeviceInfo)?.Index ?? -1;
            _capture.DecodeCameraImage = true;
            _capture.Start(idx);
            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
            DeviceCombo.IsEnabled = false;
            _timer.Start();
            SetStatus($"Поток запущен (устройство #{_capture.DeviceIndex})");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            if (!silentIfMissing)
                MessageBox.Show(ex.Message, "Не удалось запустить поток", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording) StopRecording();
        _capture.Stop();
        _timer.Stop();
        StartBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
        DeviceCombo.IsEnabled = true;
        SetStatus("Поток остановлен");
    }

    // ---------------- настройки изображения ----------------

    private void ApplyUiToRenderer()
    {
        _renderer.Gamma = GammaSlider.Value;
        _renderer.Smoothing = SmoothSlider.Value;
        _renderer.Mode = RangeManualRadio.IsChecked == true ? RangeMode.Manual
            : RangeAutoRadio.IsChecked == true ? RangeMode.Auto
            : RangeMode.AutoRobust;
        ApplyManualRange();
    }

    private void ApplyManualRange()
    {
        // может вызваться из TextChanged во время разбора XAML, когда второй TextBox ещё не создан
        if (MinBox is null || MaxBox is null) return;
        if (double.TryParse(MinBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var lo))
            _renderer.ManualMinC = lo;
        if (double.TryParse(MaxBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var hi))
            _renderer.ManualMaxC = hi;
    }

    private void PaletteCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PaletteCombo.SelectedItem is Palette p)
        {
            _renderer.Palette = p;
            if (IsLoaded) UpdateScaleBar();
            MarkSettingsDirty();
        }
    }

    private void RangeMode_Changed(object sender, RoutedEventArgs e)
    {
        if (RangeManualRadio is null) return;
        _renderer.Mode = RangeManualRadio.IsChecked == true ? RangeMode.Manual
            : RangeAutoRadio?.IsChecked == true ? RangeMode.Auto
            : RangeMode.AutoRobust;
        _renderer.ResetRange();
        MarkSettingsDirty();
    }

    private void ManualRange_Changed(object sender, TextChangedEventArgs e)
    {
        ApplyManualRange();
        MarkSettingsDirty();
    }

    private void Gamma_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _renderer.Gamma = e.NewValue;
        if (GammaLabel is not null) GammaLabel.Text = $"Гамма: {e.NewValue:0.00}";
        MarkSettingsDirty();
    }

    private void Smooth_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _renderer.Smoothing = e.NewValue;
        if (SmoothLabel is not null) SmoothLabel.Text = $"Инерция диапазона: {e.NewValue:0.00}";
        MarkSettingsDirty();
    }

    private void SpotSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _spotSize = Math.Max(1, (int)e.NewValue | 1);
        if (SpotSizeLabel is not null) SpotSizeLabel.Text = $"Усреднение точки: {_spotSize}x{_spotSize}";
        MarkSettingsDirty();
    }

    private void UseCameraImage_Changed(object sender, RoutedEventArgs e)
    {
        _useCameraImage = UseCameraImageCheck.IsChecked == true;
        MarkSettingsDirty();
    }

    private void SmoothScaling_Changed(object sender, RoutedEventArgs e)
    {
        RenderOptions.SetBitmapScalingMode(Video,
            SmoothScalingCheck.IsChecked == true ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
        MarkSettingsDirty();
    }

    private void Mirror_Changed(object sender, RoutedEventArgs e)
    {
        _mirror = MirrorCheck.IsChecked == true;
        MarkSettingsDirty();
    }

    // ---------------- поворот ----------------

    private void RotateCw_Click(object sender, RoutedEventArgs e) => SetRotation(_rotation + 1);

    private void RotateCcw_Click(object sender, RoutedEventArgs e) => SetRotation(_rotation + 3);

    private void RotateReset_Click(object sender, RoutedEventArgs e) => SetRotation(0);

    private void SetRotation(int steps)
    {
        if (_recorder.IsRecording)
        {
            SetStatus("Во время записи поворот менять нельзя — размер кадра в видеофайле уже задан.");
            return;
        }
        _rotation = ((steps % 4) + 4) % 4;
        RotationLabel.Text = $"Поворот: {_rotation * 90}°";

        // если поток стоит, перерисовать последний кадр в новой ориентации
        if (!_capture.IsRunning && _lastFrame is not null)
        {
            lock (_bufGate) TransformToUiBuf(_mirror, _rotation);
            PushToScreen();
        }
        else
        {
            FitViewArea();
        }
        MarkSettingsDirty();
    }

    // ---------------- мышь ----------------

    private (int X, int Y)? ToPixel(Point p)
    {
        double sx = ViewArea.Width / _dispW, sy = ViewArea.Height / _dispH;
        return DisplayToFrame((int)(p.X / sx), (int)(p.Y / sy));
    }

    private void ViewArea_MouseMove(object sender, MouseEventArgs e) => _cursor = ToPixel(e.GetPosition(ViewArea));

    private void ViewArea_MouseLeave(object sender, MouseEventArgs e) => _cursor = null;

    private void ViewArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ToPixel(e.GetPosition(ViewArea)) is { } p)
        {
            // повторный клик рядом с существующей точкой — удалить её
            int near = _spots.FindIndex(s => Math.Abs(s.X - p.X) <= 3 && Math.Abs(s.Y - p.Y) <= 3);
            if (near >= 0) _spots.RemoveAt(near);
            else if (_spots.Count < 12) _spots.Add(p);
            MarkSettingsDirty();
        }
    }

    private void ClearSpots_Click(object sender, RoutedEventArgs e)
    {
        _spots.Clear();
        MarkSettingsDirty();
    }

    private void ViewHost_SizeChanged(object sender, SizeChangedEventArgs e) => FitViewArea();

    /// <summary>Подогнать область показа под доступное место, сохраняя пропорции текущей ориентации.</summary>
    private void FitViewArea()
    {
        double aspect = (double)_dispW / _dispH;
        double aw = Math.Max(64, ViewHost.ActualWidth - 8);
        double ah = Math.Max(48, ViewHost.ActualHeight - 8);
        double w = aw, h = aw / aspect;
        if (h > ah) { h = ah; w = ah * aspect; }
        ViewArea.Width = Math.Floor(w);
        ViewArea.Height = Math.Floor(h);
    }

    // ---------------- vendor-команды ----------------

    private void UsbConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_camera.IsConnected)
        {
            _camera.Dispose();
            UsbPanel.IsEnabled = false;
            UsbConnectBtn.Content = "Подключить управление";
            UsbStatusText.Text = "Не подключено";
            return;
        }

        if (!_capture.IsRunning)
            UsbStatusText.Text = "Совет: сначала запустите поток — иначе libusb может зависнуть.";

        if (_camera.TryConnect(out var err))
        {
            UsbPanel.IsEnabled = true;
            UsbConnectBtn.Content = "Отключить управление";
            UsbStatusText.Text = $"Подключено, PID 0x{_camera.ProductId:X4}";
            ReadTpd_Click(sender, e);
        }
        else
        {
            UsbStatusText.Text = err;
        }
    }

    private void RunUsb(string what, Action action)
    {
        Task.Run(() =>
        {
            try
            {
                action();
                Dispatcher.BeginInvoke(() => UsbStatusText.Text = what + ": ок");
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => UsbStatusText.Text = what + ": " + ex.Message);
            }
        });
    }

    private void CamPalette_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_camera.IsConnected || CamPaletteCombo.SelectedItem is not PseudoColor c) return;
        RunUsb("палитра камеры", () => _camera.SetPseudoColor(c));
    }

    private void HighGain_Click(object sender, RoutedEventArgs e)
    {
        bool high = HighGainCheck.IsChecked == true;
        MarkSettingsDirty();
        RunUsb("gain", () => _camera.HighGain = high);
    }

    private void ApplyTpd_Click(object sender, RoutedEventArgs e)
    {
        double ems = ParseD(EmsBox.Text, 0.95);
        double dist = ParseD(DistBox.Text, 0.5);
        double refl = ParseD(ReflBox.Text, 25);
        double atm = ParseD(AtmBox.Text, 25);
        RunUsb("параметры", () =>
        {
            _camera.Emissivity = ems;
            _camera.DistanceMeters = dist;
            _camera.ReflectedTempC = refl;
            _camera.AtmosphericTempC = atm;
        });
    }

    private void ReadTpd_Click(object sender, RoutedEventArgs e)
    {
        if (!_camera.IsConnected) return;
        Task.Run(() =>
        {
            try
            {
                double ems = _camera.Emissivity;
                double dist = _camera.DistanceMeters;
                double refl = _camera.ReflectedTempC;
                double atm = _camera.AtmosphericTempC;
                bool gain = _camera.HighGain;
                var pal = _camera.GetPseudoColor();
                Dispatcher.BeginInvoke(() =>
                {
                    EmsBox.Text = ems.ToString("0.00", CultureInfo.InvariantCulture);
                    DistBox.Text = dist.ToString("0.00", CultureInfo.InvariantCulture);
                    ReflBox.Text = refl.ToString("0.0", CultureInfo.InvariantCulture);
                    AtmBox.Text = atm.ToString("0.0", CultureInfo.InvariantCulture);
                    HighGainCheck.IsChecked = gain;
                    CamPaletteCombo.SelectedItem = pal;
                    UsbStatusText.Text = "Параметры считаны";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => UsbStatusText.Text = "Чтение: " + ex.Message);
            }
        });
    }

    private void DeviceInfo_Click(object sender, RoutedEventArgs e)
    {
        if (!_camera.IsConnected) return;
        Task.Run(() =>
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var t in Enum.GetValues<DeviceInfoType>())
                {
                    string v;
                    try { v = _camera.GetDeviceInfoString(t); }
                    catch (Exception ex) { v = "ошибка: " + ex.Message; }
                    sb.AppendLine($"{t}: {v}");
                }
                var text = sb.ToString();
                Dispatcher.BeginInvoke(() => MessageBox.Show(text, "Информация об устройстве"));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => UsbStatusText.Text = ex.Message);
            }
        });
    }

    private void RawRead_Click(object sender, RoutedEventArgs e)
    {
        if (!_camera.IsConnected) return;
        int cmd = ParseHex(RawCmdBox.Text);
        uint param = (uint)ParseHex(RawParamBox.Text);
        int len = int.TryParse(RawLenBox.Text, out var l) ? l : 1;
        Task.Run(() =>
        {
            try
            {
                var data = _camera.RawStandardRead(cmd, param, len);
                var hex = Convert.ToHexString(data);
                Dispatcher.BeginInvoke(() => RawResultText.Text = hex);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => RawResultText.Text = ex.Message);
            }
        });
    }

    private void RawWrite_Click(object sender, RoutedEventArgs e)
    {
        if (!_camera.IsConnected) return;
        int cmd = ParseHex(RawCmdBox.Text);
        uint param = (uint)ParseHex(RawParamBox.Text);
        byte[]? payload = null;
        var hexIn = RawPayloadBox.Text.Replace(" ", "").Replace("0x", "");
        if (hexIn.Length >= 2)
        {
            try { payload = Convert.FromHexString(hexIn); }
            catch { RawResultText.Text = "payload: не hex"; return; }
        }
        Task.Run(() =>
        {
            try
            {
                _camera.RawStandardWrite(cmd, param, payload);
                Dispatcher.BeginInvoke(() => RawResultText.Text = "записано");
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => RawResultText.Text = ex.Message);
            }
        });
    }

    private static double ParseD(string s, double fallback) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static int ParseHex(string s)
    {
        s = s.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    // ---------------- съёмка ----------------

    private void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        var frame = _lastFrame;
        if (frame is null) { SetStatus("Нет кадра"); return; }

        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
        string basePath = Path.Combine(_outputDir, stamp);

        // PNG с оверлеями — рендерим то, что видно на экране.
        // Через VisualBrush, иначе RenderTargetBitmap захватит и отступы от родителя.
        int vw = Math.Max(1, (int)ViewArea.Width);
        int vh = Math.Max(1, (int)ViewArea.Height);
        var rtb = new RenderTargetBitmap(vw, vh, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(ViewArea) { Stretch = Stretch.None }, null, new Rect(0, 0, vw, vh));
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(basePath + ".png")) enc.Save(fs);

        // сырой кадр без оверлеев (в текущей ориентации)
        var raw = new PngBitmapEncoder();
        lock (_bufGate)
            raw.Frames.Add(BitmapFrame.Create(BitmapSource.Create(
                _uiBufW, _uiBufH, 96, 96, PixelFormats.Bgra32, null, _uiBuf, _uiBufW * 4)));
        using (var fs = File.Create(basePath + "_raw.png")) raw.Save(fs);

        using (var w = new RadiometryFile.Writer(basePath + ".r16")) w.Append(frame);
        RadiometryFile.WriteCsv(basePath + ".csv", frame);

        SetStatus($"Сохранено: {stamp}.png / _raw.png / .r16 / .csv");
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording) { StopRecording(); return; }
        if (!_capture.IsRunning) { SetStatus("Сначала запустите поток"); return; }
        try
        {
            string basePath = Path.Combine(_outputDir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            int rw, rh;
            lock (_bufGate) { rw = _uiBufW; rh = _uiBufH; }
            _recorder.Start(basePath, rw, rh, fps: ThermalCapture.ExpectedFps);
            RecordBtn.Content = "Остановить запись";
            SetRotationButtonsEnabled(false);
            SetStatus("Запись начата: " + Path.GetFileName(basePath));
        }
        catch (Exception ex)
        {
            SetStatus("Запись: " + ex.Message);
        }
    }

    private void StopRecording()
    {
        string? p = _recorder.VideoPath;
        _recorder.Stop();
        RecordBtn.Content = "Начать запись";
        SetRotationButtonsEnabled(true);
        SetStatus("Запись остановлена: " + (p is null ? "" : Path.GetFileName(p)));
    }

    private void SetRotationButtonsEnabled(bool enabled)
    {
        RotCwBtn.IsEnabled = enabled;
        RotCcwBtn.IsEnabled = enabled;
        RotResetBtn.IsEnabled = enabled;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_outputDir}\"") { UseShellExecute = true });

    private void SetStatus(string text) => StatusText.Text = text;
}

/// <summary>Тонкая обёртка над DispatcherTimer, чтобы не тащить using в основной файл.</summary>
internal sealed class DispatcherTimerHelper
{
    private readonly System.Windows.Threading.DispatcherTimer _t;
    public DispatcherTimerHelper(TimeSpan interval, Action tick)
    {
        _t = new System.Windows.Threading.DispatcherTimer { Interval = interval };
        _t.Tick += (_, _) => tick();
    }
    public void Start() => _t.Start();
    public void Stop() => _t.Stop();
    /// <summary>Перезапустить отсчёт с нуля (для отложенных действий).</summary>
    public void Restart() { _t.Stop(); _t.Start(); }
}
