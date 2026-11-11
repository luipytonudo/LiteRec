using ScreenRecorderLib;

namespace LiteRec.Core;

/// <summary>Fuente seleccionable en la interfaz (pantalla o ventana).</summary>
public sealed class SourceItem
{
    public required string Label { get; init; }
    public required RecordingSourceBase Source { get; init; }
    public bool IsWindow { get; init; }
    public override string ToString() => Label;
}

public sealed class RecordingSettings
{
    public int Width { get; init; } = 1280;   // 0 = resolución nativa
    public int Height { get; init; } = 720;
    public int Fps { get; init; } = 60;
    public int BitrateKbps { get; init; } = 8000;
    public bool UseHevc { get; init; }
    public bool SystemAudio { get; init; } = true;
    public bool Microphone { get; init; }
    public bool ShowCursor { get; init; } = true;
    public bool ShowClicks { get; init; }
}

/// <summary>
/// Motor de grabación. Captura por GPU (Desktop Duplication / Windows.Graphics.Capture),
/// escala en GPU y codifica con el encoder por hardware (Quick Sync / NVENC / AMF)
/// vía Media Foundation. Los fotogramas nunca pasan por memoria administrada.
/// </summary>
public sealed class RecordingEngine : IDisposable
{
    private Recorder? _recorder;

    public event Action<RecorderStatus>? StatusChanged;
    public event Action<string>? Completed;
    public event Action<string>? Failed;

    public RecorderStatus Status => _recorder?.Status ?? RecorderStatus.Idle;
    public int FrameNumber => _recorder?.CurrentFrameNumber ?? 0;
    public string? CurrentFile { get; private set; }

    public static List<SourceItem> GetSources()
    {
        var list = new List<SourceItem>();
        int i = 1;
        foreach (var d in Recorder.GetDisplays())
        {
            list.Add(new SourceItem
            {
                Label = $"🖥  Pantalla {i++}  ·  {d.FriendlyName}",
                Source = d,
            });
        }
        foreach (var w in Recorder.GetWindows())
        {
            if (!w.IsValidWindow() || w.IsMinmimized() || string.IsNullOrWhiteSpace(w.Title)) continue;
            if (w.Title == "LiteRec") continue;
            var title = w.Title.Length > 48 ? w.Title[..45] + "…" : w.Title;
            list.Add(new SourceItem { Label = $"🗔  {title}", Source = w, IsWindow = true });
        }
        return list;
    }

    public void Start(SourceItem item, RecordingSettings s, string filePath)
    {
        DisposeRecorder();

        var src = item.Source;
        if (src is DisplayRecordingSource ds)
        {
            ds.IsCursorCaptureEnabled = s.ShowCursor;
            ds.RecorderApi = RecorderApi.DesktopDuplication; // el más ligero para pantalla completa
        }
        else if (src is WindowRecordingSource ws)
        {
            ws.IsCursorCaptureEnabled = s.ShowCursor;
            ws.IsBorderRequired = false; // las ventanas siempre usan Windows.Graphics.Capture
        }
        src.IsVideoFramePreviewEnabled = false; // sin vista previa = cero copias extra

        var audioSources = new List<AudioSourceBase>();
        if (s.SystemAudio)
        {
            var dev = Recorder.GetSystemAudioLoopbackDevices().FirstOrDefault(d => d.IsDefaultDevice)
                      ?? Recorder.GetSystemAudioLoopbackDevices().FirstOrDefault();
            if (dev != null) audioSources.Add(dev);
        }
        if (s.Microphone)
        {
            var mic = Recorder.GetSystemAudioCaptureDevices().FirstOrDefault(d => d.IsDefaultDevice)
                      ?? Recorder.GetSystemAudioCaptureDevices().FirstOrDefault();
            if (mic != null) audioSources.Add(mic);
        }

        IVideoEncoder encoder = s.UseHevc
            ? new H265VideoEncoder { BitrateMode = H265BitrateControlMode.CBR, EncoderProfile = H265Profile.Main }
            : new H264VideoEncoder { BitrateMode = H264BitrateControlMode.CBR, EncoderProfile = H264Profile.High };

        var opts = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = { src } },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                OutputFrameSize = s.Width > 0 ? new ScreenSize(s.Width, s.Height) : null,
                Stretch = StretchMode.Uniform,
                IsVideoFramePreviewEnabled = false,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = encoder,
                Framerate = s.Fps,
                Bitrate = s.BitrateKbps * 1000,
                IsFixedFramerate = true,          // 60 fps constantes, ideal para editores
                IsHardwareEncodingEnabled = true, // GPU
                IsLowLatencyEnabled = false,
                IsThrottlingDisabled = false,
                IsMp4FastStartEnabled = false,    // evita reescribir el archivo al final
                IsFragmentedMp4Enabled = false,
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = audioSources.Count > 0,
                Bitrate = AudioBitrate.bitrate_160kbps,
                Channels = AudioChannels.Stereo,
                AudioSources = audioSources,
            },
            MouseOptions = new MouseOptions
            {
                IsMousePointerEnabled = s.ShowCursor,
                IsMouseClicksDetected = s.ShowClicks,
                MouseClickDetectionMode = MouseDetectionMode.Hook,
                MouseLeftClickDetectionColor = "#FFE5484D",
                MouseRightClickDetectionColor = "#FF4C8DFF",
                MouseClickDetectionRadius = 22,
                MouseClickDetectionDuration = 120,
            },
            LogOptions = new LogOptions { IsLogEnabled = false },
        };

        _recorder = Recorder.CreateRecorder(opts);
        _recorder.OnStatusChanged += (_, e) => StatusChanged?.Invoke(e.Status);
        _recorder.OnRecordingComplete += (_, e) => Completed?.Invoke(e.FilePath);
        _recorder.OnRecordingFailed += (_, e) => Failed?.Invoke(e.Error);

        CurrentFile = filePath;
        _recorder.Record(filePath);
    }

    public void Stop() => _recorder?.Stop();
    public void Pause() => _recorder?.Pause();
    public void Resume() => _recorder?.Resume();

    public void DisposeRecorder()
    {
        _recorder?.Dispose();
        _recorder = null;
    }

    public void Dispose() => DisposeRecorder();
}
