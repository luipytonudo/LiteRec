using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LiteRec.Core;

namespace LiteRec;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly IRecordingEngine? _engine;
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Process _proc = Process.GetCurrentProcess();

    private IntPtr _hwnd;
    private bool _ready;
    private bool _closeAfterSave;
    private RecState _state = RecState.Idle;

    private TimeSpan _lastCpu;
    private long _lastTicks;
    private int _lastFrame;
    private bool _blink;

    private static readonly IBrush OkFg = new SolidColorBrush(Color.Parse("#2FD07A"));
    private static readonly IBrush OkBg = new SolidColorBrush(Color.Parse("#1A2D24"));
    private static readonly IBrush RecFg = new SolidColorBrush(Color.Parse("#FF4D6A"));
    private static readonly IBrush RecBg = new SolidColorBrush(Color.Parse("#361720"));
    private static readonly IBrush WarnFg = new SolidColorBrush(Color.Parse("#FFB547"));
    private static readonly IBrush WarnBg = new SolidColorBrush(Color.Parse("#332916"));

    public MainWindow()
    {
        InitializeComponent();

        // Refresco de la interfaz a 1 Hz: suficiente para el reloj y casi sin coste.
        // (este constructor no arranca el timer; solo corre mientras se graba)
        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => UpdateStats();

        try
        {
            _engine = RecordingEngineFactory.Create();
            _engine.StateChanged += s => Dispatcher.UIThread.Post(() => OnState(s));
            _engine.Completed += p => Dispatcher.UIThread.Post(() => OnCompleted(p));
            _engine.Failed += e => Dispatcher.UIThread.Post(() => OnFailed(e));
        }
        catch (Exception ex)
        {
            FooterText.Text = ex.Message;
        }

        ApplySettingsToUi();
        ApplyCapabilities();
        LoadSources();
        _ready = true;
        UpdateSummary();

        TitleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        KeyDown += OnKeyDown;
        Opened += OnOpened;
    }

    // ================= Inicialización =================

    private void OnOpened(object? sender, EventArgs e)
    {
        _hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (OperatingSystem.IsWindows() && _hwnd != IntPtr.Zero)
        {
            Win32Properties.AddWndProcHookCallback(this, WndProc);
            bool f9 = NativeMethods.RegisterHotKey(_hwnd, 1, NativeMethods.MOD_NOREPEAT, NativeMethods.VK_F9);
            bool f10 = NativeMethods.RegisterHotKey(_hwnd, 2, NativeMethods.MOD_NOREPEAT, NativeMethods.VK_F10);
            if (!f9 || !f10) FooterText.Text = "⚠ Algún atajo (F9/F10) lo usa otra app";
            try { _proc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        }
        ApplyExclusion();
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            if (wParam == 1) ToggleRecord();
            else if (wParam == 2) TogglePause();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>En Linux los atajos funcionan con la ventana enfocada.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9) { ToggleRecord(); e.Handled = true; }
        else if (e.Key == Key.F10) { TogglePause(); e.Handled = true; }
    }

    private void ApplyCapabilities()
    {
        if (_engine == null)
        {
            RecordButton.IsEnabled = false;
            return;
        }
        ClicksRow.IsVisible = _engine.SupportsClicks;
        HideRow.IsVisible = OperatingSystem.IsWindows();
        PauseButton.IsVisible = _engine.SupportsPause;
        if (FooterText.Text is null or "") FooterText.Text = _engine.BackendName;
    }

    private void ApplySettingsToUi()
    {
        (_settings.ResolutionIndex switch { 1 => Res900, 2 => Res1080, 3 => ResNative, _ => Res720 }).IsChecked = true;
        (_settings.FpsIndex == 1 ? Fps30 : Fps60).IsChecked = true;
        (_settings.QualityIndex switch { 0 => QLow, 2 => QHigh, _ => QMid }).IsChecked = true;
        (_settings.UseHevc ? CodecH265 : CodecH264).IsChecked = true;
        SysAudioToggle.IsChecked = _settings.SystemAudio;
        MicToggle.IsChecked = _settings.Microphone;
        CursorToggle.IsChecked = _settings.ShowCursor;
        ClicksToggle.IsChecked = _settings.ShowClicks;
        HideToggle.IsChecked = _settings.HideFromCapture;
        MinimizeToggle.IsChecked = _settings.MinimizeOnRecord;
        FolderText.Text = _settings.OutputFolder;
    }

    private void ReadUiToSettings()
    {
        _settings.ResolutionIndex = Res900.IsChecked == true ? 1 : Res1080.IsChecked == true ? 2 : ResNative.IsChecked == true ? 3 : 0;
        _settings.FpsIndex = Fps30.IsChecked == true ? 1 : 0;
        _settings.QualityIndex = QLow.IsChecked == true ? 0 : QHigh.IsChecked == true ? 2 : 1;
        _settings.UseHevc = CodecH265.IsChecked == true;
        _settings.SystemAudio = SysAudioToggle.IsChecked == true;
        _settings.Microphone = MicToggle.IsChecked == true;
        _settings.ShowCursor = CursorToggle.IsChecked == true;
        _settings.ShowClicks = ClicksToggle.IsChecked == true;
        _settings.HideFromCapture = HideToggle.IsChecked == true;
        _settings.MinimizeOnRecord = MinimizeToggle.IsChecked == true;
    }

    private void LoadSources()
    {
        if (_engine == null) return;
        var prev = (SourceCombo.SelectedItem as CaptureSource)?.Label;
        try
        {
            var items = _engine.GetSources();
            SourceCombo.ItemsSource = items;
            SourceCombo.SelectedItem = items.FirstOrDefault(i => i.Label == prev) ?? items.FirstOrDefault();
        }
        catch (Exception ex)
        {
            FooterText.Text = "No se pudieron listar las pantallas: " + ex.Message;
        }
    }

    // ================= Ajustes =================

    private (int w, int h) Resolution => _settings.ResolutionIndex switch
    {
        1 => (1600, 900),
        2 => (1920, 1080),
        3 => (0, 0),
        _ => (1280, 720),
    };

    private int Fps => _settings.FpsIndex == 1 ? 30 : 60;

    /// <summary>Bitrate ajustado a resolución y fps (kbps).</summary>
    private int BitrateKbps
    {
        get
        {
            double baseKbps = _settings.QualityIndex switch { 0 => 4500, 2 => 12000, _ => 7500 }; // para 720p60
            var (w, h) = Resolution;
            double pixels = w > 0 ? w * h : 1920 * 1080;
            double scale = Math.Pow(pixels / (1280.0 * 720.0), 0.75);
            double fpsScale = Fps == 30 ? 0.65 : 1.0;
            if (_settings.UseHevc) scale *= 0.7;
            return (int)(Math.Round(baseKbps * scale * fpsScale / 500) * 500);
        }
    }

    private void UpdateSummary()
    {
        var (w, h) = Resolution;
        string res = w > 0 ? $"{w}×{h}" : "Nativa";
        SummaryText.Text = $"{res} · {Fps} fps · {BitrateKbps / 1000.0:0.#} Mbps";
        EncoderBadge.Text = _engine?.EncoderLabel(_settings.UseHevc) ?? "Sin backend";
    }

    private void Option_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        // Los RadioButton disparan el evento también al desmarcarse: ignoramos ese caso.
        if (sender is RadioButton { IsChecked: not true }) return;
        ReadUiToSettings();
        _settings.Save();
        UpdateSummary();
        if (ReferenceEquals(sender, HideToggle)) ApplyExclusion();
    }

    private void ApplyExclusion() => NativeMethods.ExcludeFromCapture(_hwnd, _settings.HideFromCapture);

    // ================= Grabación =================

    private void Record_Click(object? sender, RoutedEventArgs e) => ToggleRecord();
    private void Pause_Click(object? sender, RoutedEventArgs e) => TogglePause();

    private void ToggleRecord()
    {
        if (_engine == null || _state == RecState.Finishing) return;
        if (_state is RecState.Recording or RecState.Paused) { _engine.Stop(); _clock.Stop(); return; }
        StartRecording();
    }

    private void StartRecording()
    {
        if (_engine == null) return;
        if (SourceCombo.SelectedItem is not CaptureSource src)
        {
            FooterText.Text = "Elige una fuente de captura";
            return;
        }

        try
        {
            Directory.CreateDirectory(_settings.OutputFolder);
            string file = Path.Combine(_settings.OutputFolder, $"LiteRec_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
            var (w, h) = Resolution;

            _clock.Reset(); // arranca cuando el backend confirma que ya está grabando
            _engine.Start(src, new RecordingSettings
            {
                Width = w,
                Height = h,
                Fps = Fps,
                BitrateKbps = BitrateKbps,
                UseHevc = _settings.UseHevc,
                SystemAudio = _settings.SystemAudio,
                Microphone = _settings.Microphone,
                ShowCursor = _settings.ShowCursor,
                ShowClicks = _settings.ShowClicks,
            }, file);

            FooterText.Text = _engine.BackendName;
            _lastFrame = 0;
            _lastCpu = TotalCpu();
            _lastTicks = Stopwatch.GetTimestamp();
            _uiTimer.Start();
            SetRecordingUi(true);
            UpdateSummary();

            if (_settings.MinimizeOnRecord) WindowState = WindowState.Minimized;
        }
        catch (Exception ex)
        {
            OnFailed(ex.Message);
        }
    }

    private void TogglePause()
    {
        if (_engine is not { SupportsPause: true }) return;
        if (_state == RecState.Recording) { _engine.Pause(); _clock.Stop(); }
        else if (_state == RecState.Paused) { _engine.Resume(); _clock.Start(); }
    }

    private void OnState(RecState s)
    {
        _state = s;
        switch (s)
        {
            case RecState.Recording:
                SetPill("GRABANDO", RecFg, RecBg);
                _clock.Start();
                PauseIcon.IsVisible = true;
                PlayIcon.IsVisible = false;
                break;
            case RecState.Paused:
                SetPill("EN PAUSA", WarnFg, WarnBg);
                PauseIcon.IsVisible = false;
                PlayIcon.IsVisible = true;
                break;
            case RecState.Finishing:
                SetPill("GUARDANDO…", WarnFg, WarnBg);
                RecordButton.IsEnabled = false;
                PauseButton.IsEnabled = false;
                break;
        }
    }

    private void OnCompleted(string path)
    {
        ResetAfterRecording();
        var fi = new FileInfo(path);
        FooterText.Text = fi.Exists ? $"✔ Guardado: {fi.Name} ({FormatSize(fi.Length)})" : "✔ Grabación guardada";
        if (_closeAfterSave) Close();
    }

    private async void OnFailed(string error)
    {
        ResetAfterRecording();
        FooterText.Text = "✖ " + error.Split('\n')[0];
        await ShowMessage("La grabación falló", error);
        if (_closeAfterSave) Close();
    }

    private void ResetAfterRecording()
    {
        _state = RecState.Idle;
        _uiTimer.Stop();
        _clock.Reset();
        SetRecordingUi(false);
        SetPill("LISTO", OkFg, OkBg);
        TimerText.Text = "00:00:00";
        FpsStat.Text = SizeStat.Text = CpuStat.Text = RamStat.Text = "—";
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        // Devolver la memoria al sistema tras grabar.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        NativeMethods.TrimMemory(_proc.Handle);
    }

    private void SetRecordingUi(bool recording)
    {
        RecIconCircle.IsVisible = !recording;
        RecIconStop.IsVisible = recording;
        RecordButton.IsEnabled = true;
        PauseButton.IsEnabled = recording;
        PauseIcon.IsVisible = true;
        PlayIcon.IsVisible = false;
        VideoPanel.IsEnabled = !recording;
        SourceCombo.IsEnabled = !recording;
        SysAudioToggle.IsEnabled = MicToggle.IsEnabled = CursorToggle.IsEnabled = ClicksToggle.IsEnabled = !recording;
        ChangeFolderButton.IsEnabled = !recording;
    }

    private void SetPill(string text, IBrush fg, IBrush bg)
    {
        StatusText.Text = text;
        StatusText.Foreground = fg;
        StatusDot.Fill = fg;
        StatusDot.Opacity = 1;
        StatusPill.Background = bg;
    }

    /// <summary>CPU de LiteRec más la del proceso auxiliar (FFmpeg / gpu-screen-recorder).</summary>
    private TimeSpan TotalCpu()
    {
        _proc.Refresh();
        var t = _proc.TotalProcessorTime;
        if (_engine?.HelperProcess is { } h)
            try { h.Refresh(); t += h.TotalProcessorTime; } catch { }
        return t;
    }

    private void UpdateStats()
    {
        if (_engine == null) return;
        var t = _clock.Elapsed;
        TimerText.Text = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

        if (_state == RecState.Recording)
        {
            _blink = !_blink;
            StatusDot.Opacity = _blink ? 1 : 0.25;
        }

        long now = Stopwatch.GetTimestamp();
        double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
        var cpu = TotalCpu();
        if (dt > 0.2)
        {
            double pct = (cpu - _lastCpu).TotalSeconds / (dt * Environment.ProcessorCount) * 100;
            CpuStat.Text = $"{Math.Max(0, pct):0}%";

            if (_engine.FrameNumber is int frame)
            {
                if (_state == RecState.Recording && frame >= _lastFrame)
                    FpsStat.Text = $"{Math.Min((frame - _lastFrame) / dt, Fps):0}";
                else if (_state == RecState.Paused)
                    FpsStat.Text = "0";
                _lastFrame = frame;
            }
            else
            {
                FpsStat.Text = _state == RecState.Recording ? Fps.ToString() : "0";
            }
        }
        _lastCpu = cpu;
        _lastTicks = now;

        long ram = _proc.WorkingSet64;
        if (_engine.HelperProcess is { } h)
            try { ram += h.WorkingSet64; } catch { }
        RamStat.Text = $"{ram / (1024 * 1024)} MB";

        if (_engine.CurrentFile is { } f)
            try { SizeStat.Text = FormatSize(new FileInfo(f).Length); } catch { }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    // ================= Otros botones =================

    private void Refresh_Click(object? sender, RoutedEventArgs e) => LoadSources();

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.OutputFolder);
        var opener = OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open";
        try
        {
            var psi = new ProcessStartInfo(opener) { UseShellExecute = false };
            psi.ArgumentList.Add(_settings.OutputFolder);
            Process.Start(psi);
        }
        catch (Exception ex) { FooterText.Text = "No se pudo abrir la carpeta: " + ex.Message; }
    }

    private async void ChangeFolder_Click(object? sender, RoutedEventArgs e)
    {
        var start = Directory.Exists(_settings.OutputFolder)
            ? await StorageProvider.TryGetFolderFromPathAsync(_settings.OutputFolder)
            : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Carpeta donde guardar las grabaciones",
            SuggestedStartLocation = start,
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            _settings.OutputFolder = path;
            FolderText.Text = path;
            _settings.Save();
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Si se cierra grabando, primero se termina de guardar el archivo.
        if (_engine != null && _state is RecState.Recording or RecState.Paused or RecState.Finishing)
        {
            e.Cancel = true;
            _closeAfterSave = true;
            if (_state != RecState.Finishing) { _engine.Stop(); _clock.Stop(); }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (OperatingSystem.IsWindows() && _hwnd != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_hwnd, 1);
            NativeMethods.UnregisterHotKey(_hwnd, 2);
        }
        _engine?.Dispose();
        _settings.Save();
        base.OnClosed(e);
    }

    // ================= Utilidades =================

    private async Task ShowMessage(string title, string text)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)this.FindResource("Card")!,
        };
        var ok = new Button { Content = "Aceptar", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        ok.Classes.Add("ghost");
        ok.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.Bold },
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 },
                ok,
            },
        };
        await dlg.ShowDialog(this);
    }

    /// <summary>Guarda una captura PNG de la ventana cuando termina de cargar y cierra la app.</summary>
    public void SnapshotOnLoad(string path)
    {
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var root = (Control)Content!;
            double scale = RenderScaling;
            var size = new PixelSize((int)(root.Bounds.Width * scale), (int)(root.Bounds.Height * scale));
            using var rtb = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            rtb.Render(root);
#pragma warning disable CS0618
            rtb.Save(path);
#pragma warning restore CS0618
            Close();
        }, DispatcherPriority.Background);
    }
}
