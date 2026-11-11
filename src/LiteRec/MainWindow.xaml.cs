using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LiteRec.Core;
using ScreenRecorderLib;

namespace LiteRec;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly RecordingEngine _engine = new();
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Process _proc = Process.GetCurrentProcess();

    private IntPtr _hwnd;
    private bool _ready;
    private bool _closingAfterSave;
    private RecorderStatus _status = RecorderStatus.Idle;

    // Estadísticas
    private TimeSpan _lastCpu;
    private long _lastTicks;
    private int _lastFrame;
    private bool _blink;

    private static readonly SolidColorBrush OkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2F, 0xD0, 0x7A)));
    private static readonly SolidColorBrush OkBg = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x2D, 0x24)));
    private static readonly SolidColorBrush RecBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x6A)));
    private static readonly SolidColorBrush RecBg = Freeze(new SolidColorBrush(Color.FromRgb(0x36, 0x17, 0x20)));
    private static readonly SolidColorBrush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB5, 0x47)));
    private static readonly SolidColorBrush WarnBg = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x29, 0x16)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public MainWindow()
    {
        InitializeComponent();

        // Timer de UI a 1 Hz: suficiente para el reloj y casi sin coste.
        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => UpdateStats();

        _engine.StatusChanged += s => Dispatcher.BeginInvoke(() => OnStatus(s));
        _engine.Completed += path => Dispatcher.BeginInvoke(() => OnCompleted(path));
        _engine.Failed += err => Dispatcher.BeginInvoke(() => OnFailed(err));

        ApplySettingsToUi();
        LoadSources();
        _ready = true;
        UpdateSummary();
    }

    // ================= Inicialización =================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        bool f9 = NativeMethods.RegisterHotKey(_hwnd, 1, NativeMethods.MOD_NOREPEAT, NativeMethods.VK_F9);
        bool f10 = NativeMethods.RegisterHotKey(_hwnd, 2, NativeMethods.MOD_NOREPEAT, NativeMethods.VK_F10);
        if (!f9 || !f10) FooterText.Text = "⚠ Algún atajo (F9/F10) lo usa otra app";

        ApplyExclusion();
        _proc.PriorityClass = ProcessPriorityClass.AboveNormal; // evita tirones cuando un juego satura la CPU
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            if (wParam == 1) ToggleRecord();
            else if (wParam == 2) TogglePause();
            handled = true;
        }
        return IntPtr.Zero;
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
        var prev = (SourceCombo.SelectedItem as SourceItem)?.Label;
        try
        {
            var items = RecordingEngine.GetSources();
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
            if (_settings.UseHevc) scale *= 0.7; // H.265 da la misma calidad con menos bitrate
            return (int)(Math.Round(baseKbps * scale * fpsScale / 500) * 500);
        }
    }

    private void UpdateSummary()
    {
        var (w, h) = Resolution;
        string res = w > 0 ? $"{w}×{h}" : "Nativa";
        SummaryText.Text = $"{res} · {Fps} fps · {BitrateKbps / 1000.0:0.#} Mbps";
        EncoderBadge.Text = _settings.UseHevc ? "GPU · H.265" : "GPU · H.264";
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ReadUiToSettings();
        _settings.Save();
        UpdateSummary();
        if (ReferenceEquals(sender, HideToggle)) ApplyExclusion();
    }

    private void ApplyExclusion()
    {
        if (_hwnd == IntPtr.Zero) return;
        try { Recorder.SetExcludeFromCapture(_hwnd, _settings.HideFromCapture); } catch { }
    }

    // ================= Grabación =================

    private void Record_Click(object sender, RoutedEventArgs e) => ToggleRecord();
    private void Pause_Click(object sender, RoutedEventArgs e) => TogglePause();

    private void ToggleRecord()
    {
        if (_status == RecorderStatus.Finishing) return;
        if (_status is RecorderStatus.Recording or RecorderStatus.Paused) { StopRecording(); return; }
        StartRecording();
    }

    private void StartRecording()
    {
        if (SourceCombo.SelectedItem is not SourceItem src)
        {
            FooterText.Text = "Elige una fuente de captura";
            return;
        }

        if (src.IsWindow && src.Source is RecordableWindow win && !win.IsValidWindow())
        {
            FooterText.Text = "Esa ventana ya no existe. Actualiza la lista.";
            LoadSources();
            return;
        }

        try
        {
            Directory.CreateDirectory(_settings.OutputFolder);
            string file = Path.Combine(_settings.OutputFolder, $"LiteRec_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
            var (w, h) = Resolution;

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

            FooterText.Text = "";
            _lastFrame = 0;
            _lastCpu = _proc.TotalProcessorTime;
            _lastTicks = Stopwatch.GetTimestamp();
            _clock.Reset(); // arranca cuando el encoder confirma que ya está grabando
            _uiTimer.Start();
            SetRecordingUi(true);

            if (_settings.MinimizeOnRecord) WindowState = WindowState.Minimized;
        }
        catch (Exception ex)
        {
            OnFailed(ex.Message);
        }
    }

    private void StopRecording()
    {
        _engine.Stop();
        _clock.Stop();
    }

    private void TogglePause()
    {
        if (_status == RecorderStatus.Recording) { _engine.Pause(); _clock.Stop(); }
        else if (_status == RecorderStatus.Paused) { _engine.Resume(); _clock.Start(); }
    }

    private void OnStatus(RecorderStatus s)
    {
        _status = s;
        switch (s)
        {
            case RecorderStatus.Recording:
                SetPill("GRABANDO", RecBrush, RecBg);
                _clock.Start();
                PauseButton.Content = "";
                break;
            case RecorderStatus.Paused:
                SetPill("EN PAUSA", WarnBrush, WarnBg);
                PauseButton.Content = "";
                break;
            case RecorderStatus.Finishing:
                SetPill("GUARDANDO…", WarnBrush, WarnBg);
                RecordButton.IsEnabled = false;
                PauseButton.IsEnabled = false;
                break;
            case RecorderStatus.Idle:
                break;
        }
    }

    private void OnCompleted(string path)
    {
        ResetAfterRecording();
        var fi = new FileInfo(path);
        FooterText.Text = fi.Exists ? $"✔ Guardado: {fi.Name} ({FormatSize(fi.Length)})" : "✔ Grabación guardada";
        if (_closingAfterSave) Close();
    }

    private void OnFailed(string error)
    {
        ResetAfterRecording();
        FooterText.Text = "✖ Error: " + error;
        MessageBox.Show(this, "La grabación falló:\n\n" + error, "LiteRec", MessageBoxButton.OK, MessageBoxImage.Error);
        if (_closingAfterSave) Close();
    }

    private void ResetAfterRecording()
    {
        _status = RecorderStatus.Idle;
        _uiTimer.Stop();
        _clock.Reset();
        _engine.DisposeRecorder();
        SetRecordingUi(false);
        SetPill("LISTO", OkBrush, OkBg);
        TimerText.Text = "00:00:00";
        FpsStat.Text = SizeStat.Text = CpuStat.Text = RamStat.Text = "—";
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        // Devolver la memoria al sistema tras grabar.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        NativeMethods.EmptyWorkingSet(_proc.Handle);
    }

    private void SetRecordingUi(bool recording)
    {
        RecIconCircle.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        RecIconStop.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.IsEnabled = true;
        PauseButton.IsEnabled = recording;
        PauseButton.Content = "";
        VideoPanel.IsEnabled = !recording;
        SourceCombo.IsEnabled = !recording;
        SysAudioToggle.IsEnabled = MicToggle.IsEnabled = CursorToggle.IsEnabled = ClicksToggle.IsEnabled = !recording;
        ChangeFolderButton.IsEnabled = !recording;
    }

    private void SetPill(string text, Brush fg, Brush bg)
    {
        StatusText.Text = text;
        StatusText.Foreground = fg;
        StatusDot.Fill = fg;
        StatusDot.Opacity = 1;
        StatusPill.Background = bg;
    }

    private void UpdateStats()
    {
        var t = _clock.Elapsed;
        TimerText.Text = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

        if (_status == RecorderStatus.Recording)
        {
            _blink = !_blink;
            StatusDot.Opacity = _blink ? 1 : 0.25;
        }

        long now = Stopwatch.GetTimestamp();
        double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
        _proc.Refresh();
        var cpu = _proc.TotalProcessorTime;
        if (dt > 0.2)
        {
            double cpuPct = (cpu - _lastCpu).TotalSeconds / (dt * Environment.ProcessorCount) * 100;
            CpuStat.Text = $"{cpuPct:0}%";

            int frame = _engine.FrameNumber;
            if (_status == RecorderStatus.Recording && frame >= _lastFrame)
                FpsStat.Text = $"{Math.Min((frame - _lastFrame) / dt, Fps):0}";
            else if (_status == RecorderStatus.Paused)
                FpsStat.Text = "0";
            _lastFrame = frame;
        }
        _lastCpu = cpu;
        _lastTicks = now;

        RamStat.Text = $"{_proc.WorkingSet64 / (1024 * 1024)} MB";

        var f = _engine.CurrentFile;
        if (f != null)
        {
            try { SizeStat.Text = FormatSize(new FileInfo(f).Length); } catch { }
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    // ================= Otros botones =================

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadSources();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.OutputFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.OutputFolder}\"") { UseShellExecute = true });
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Carpeta donde guardar las grabaciones",
            InitialDirectory = Directory.Exists(_settings.OutputFolder) ? _settings.OutputFolder : null,
        };
        if (dlg.ShowDialog(this) == true)
        {
            _settings.OutputFolder = dlg.FolderName;
            FolderText.Text = dlg.FolderName;
            _settings.Save();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Si se cierra grabando, primero se termina de guardar el archivo.
        if (_status is RecorderStatus.Recording or RecorderStatus.Paused)
        {
            e.Cancel = true;
            _closingAfterSave = true;
            StopRecording();
            return;
        }
        if (_status == RecorderStatus.Finishing) { e.Cancel = true; _closingAfterSave = true; return; }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        NativeMethods.UnregisterHotKey(_hwnd, 1);
        NativeMethods.UnregisterHotKey(_hwnd, 2);
        _engine.Dispose();
        _settings.Save();
        base.OnClosed(e);
    }
}
