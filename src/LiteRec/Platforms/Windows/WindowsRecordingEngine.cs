using System.Diagnostics;
using LiteRec.Core;
using ScreenRecorderLib;

namespace LiteRec.Platforms.Windows;

/// <summary>
/// Windows: captura por GPU (Desktop Duplication / Windows.Graphics.Capture), escalado en GPU
/// y codificación por hardware (Quick Sync / NVENC / AMF) vía Media Foundation.
/// Los fotogramas nunca pasan por memoria administrada.
/// </summary>
public sealed class WindowsRecordingEngine : IRecordingEngine
{
    private Recorder? _recorder;

    public string BackendName => "Windows · Desktop Duplication + Media Foundation";
    public string EncoderLabel(bool hevc) => hevc ? "GPU · H.265" : "GPU · H.264";
    public bool SupportsPause => true;
    public bool SupportsClicks => true;
    public bool SupportsWindows => true;

    public RecState State => _recorder?.Status switch
    {
        RecorderStatus.Recording => RecState.Recording,
        RecorderStatus.Paused => RecState.Paused,
        RecorderStatus.Finishing => RecState.Finishing,
        _ => RecState.Idle,
    };
    public int? FrameNumber => _recorder?.CurrentFrameNumber;
    public string? CurrentFile { get; private set; }
    public Process? HelperProcess => null; // todo ocurre dentro de nuestro proceso

    public event Action<RecState>? StateChanged;
    public event Action<string>? Completed;
    public event Action<string>? Failed;

    public IReadOnlyList<CaptureSource> GetSources()
    {
        var list = new List<CaptureSource>();
        int i = 1;
        foreach (var d in Recorder.GetDisplays())
            list.Add(new CaptureSource { Label = $"Pantalla {i++}  ·  {d.FriendlyName}", Id = d.DeviceName, Native = d });

        foreach (var w in Recorder.GetWindows())
        {
            if (!w.IsValidWindow() || w.IsMinmimized() || string.IsNullOrWhiteSpace(w.Title) || w.Title == "LiteRec") continue;
            var title = w.Title.Length > 48 ? w.Title[..45] + "…" : w.Title;
            list.Add(new CaptureSource { Label = $"Ventana  ·  {title}", Id = w.Handle.ToString(), Native = w });
        }
        return list;
    }

    public void Start(CaptureSource source, RecordingSettings s, string filePath)
    {
        Dispose();

        if (source.Native is RecordableWindow win && !win.IsValidWindow())
            throw new InvalidOperationException("Esa ventana ya no existe. Actualiza la lista.");

        var src = (RecordingSourceBase)source.Native!;
        if (src is DisplayRecordingSource ds)
        {
            ds.IsCursorCaptureEnabled = s.ShowCursor;
            ds.RecorderApi = RecorderApi.DesktopDuplication; // el más ligero para pantalla completa
        }
        else if (src is WindowRecordingSource ws)
        {
            ws.IsCursorCaptureEnabled = s.ShowCursor;
            ws.IsBorderRequired = false; // las ventanas usan Windows.Graphics.Capture
        }
        src.IsVideoFramePreviewEnabled = false; // sin vista previa = cero copias extra

        var audio = new List<AudioSourceBase>();
        if (s.SystemAudio)
        {
            var devs = Recorder.GetSystemAudioLoopbackDevices();
            var dev = devs.FirstOrDefault(d => d.IsDefaultDevice) ?? devs.FirstOrDefault();
            if (dev != null) audio.Add(dev);
        }
        if (s.Microphone)
        {
            var mics = Recorder.GetSystemAudioCaptureDevices();
            var mic = mics.FirstOrDefault(d => d.IsDefaultDevice) ?? mics.FirstOrDefault();
            if (mic != null) audio.Add(mic);
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
                IsFixedFramerate = true,
                IsHardwareEncodingEnabled = true,
                IsLowLatencyEnabled = false,
                IsThrottlingDisabled = false,
                IsMp4FastStartEnabled = false,
                IsFragmentedMp4Enabled = false,
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = audio.Count > 0,
                Bitrate = AudioBitrate.bitrate_160kbps,
                Channels = AudioChannels.Stereo,
                AudioSources = audio,
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
        _recorder.OnStatusChanged += (_, e) => StateChanged?.Invoke(State);
        _recorder.OnRecordingComplete += (_, e) => Completed?.Invoke(e.FilePath);
        _recorder.OnRecordingFailed += (_, e) => Failed?.Invoke(e.Error);

        CurrentFile = filePath;
        _recorder.Record(filePath);
    }

    public void Stop() => _recorder?.Stop();
    public void Pause() => _recorder?.Pause();
    public void Resume() => _recorder?.Resume();

    public void Dispose()
    {
        _recorder?.Dispose();
        _recorder = null;
    }
}
