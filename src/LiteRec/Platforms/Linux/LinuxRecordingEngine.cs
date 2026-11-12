using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LiteRec.Core;

namespace LiteRec.Platforms.Linux;

/// <summary>
/// Linux. Dos backends, elegidos automáticamente:
///  1) gpu-screen-recorder (recomendado): captura y codifica en la GPU (VAAPI / NVENC),
///     funciona en X11 y en Wayland (vía portal), admite pausa.
///  2) FFmpeg + x11grab (solo X11): codifica con NVENC, VAAPI o x264 ultrafast, lo que exista.
/// </summary>
public sealed partial class LinuxRecordingEngine : IRecordingEngine
{
    private enum Backend { None, Gsr, Ffmpeg }

    private readonly Backend _backend;
    private readonly string[] _gsrCommand = [];      // ["gpu-screen-recorder"] o ["flatpak","run",...]
    private readonly bool _wayland;

    private Process? _proc;
    private readonly StringBuilder _stderr = new();
    private bool _stopRequested;
    private volatile int _frames = -1;
    private RecState _state = RecState.Idle;
    private static string? _ffmpegVideoEncoder;      // caché del encoder que funciona

    public LinuxRecordingEngine()
    {
        _wayland = string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
                   || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        if (Which("gpu-screen-recorder") is { } gsr)
        {
            _backend = Backend.Gsr;
            _gsrCommand = [gsr];
        }
        else if (Which("flatpak") is { } flatpak && Run(flatpak, ["info", "com.dec05eba.gpu_screen_recorder"], 4000).exit == 0)
        {
            _backend = Backend.Gsr;
            _gsrCommand = [flatpak, "run", "--command=gpu-screen-recorder", "com.dec05eba.gpu_screen_recorder"];
        }
        else if (!_wayland && Which("ffmpeg") != null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            _backend = Backend.Ffmpeg;
        }
        else
        {
            _backend = Backend.None;
        }
    }

    // ================= Información =================

    public string BackendName => _backend switch
    {
        Backend.Gsr => "Linux · gpu-screen-recorder" + (_wayland ? " (Wayland)" : " (X11)"),
        Backend.Ffmpeg => "Linux · FFmpeg x11grab",
        _ => "Linux · sin backend",
    };

    public string EncoderLabel(bool hevc) => _backend switch
    {
        Backend.Gsr => hevc ? "GPU · H.265" : "GPU · H.264",
        Backend.Ffmpeg when _ffmpegVideoEncoder is { } e && (e.Contains("nvenc") || e.Contains("vaapi")) => hevc ? "GPU · H.265" : "GPU · H.264",
        Backend.Ffmpeg => hevc ? "FFmpeg · H.265" : "FFmpeg · H.264",
        _ => "Sin backend",
    };

    public bool SupportsPause => _backend == Backend.Gsr;
    public bool SupportsClicks => false;
    public bool SupportsWindows => false;
    public RecState State => _state;
    public int? FrameNumber => _frames >= 0 ? _frames : null;
    public string? CurrentFile { get; private set; }
    public Process? HelperProcess => _proc is { HasExited: false } ? _proc : null;

    public event Action<RecState>? StateChanged;
    public event Action<string>? Completed;
    public event Action<string>? Failed;

    private static string MissingBackendMessage(bool wayland) => wayland
        ? "En Wayland LiteRec necesita gpu-screen-recorder.\nInstálalo desde tu gestor de paquetes o con:\n  flatpak install flathub com.dec05eba.gpu_screen_recorder"
        : "LiteRec necesita gpu-screen-recorder (recomendado) o FFmpeg.\nEjemplo en Ubuntu/Debian:  sudo apt install ffmpeg";

    // ================= Fuentes =================

    public IReadOnlyList<CaptureSource> GetSources()
    {
        var list = new List<CaptureSource>();
        if (_backend == Backend.Gsr)
        {
            var (exit, output) = Run(_gsrCommand[0], [.. _gsrCommand[1..], "--list-capture-options"], 5000);
            if (exit == 0)
            {
                foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = raw.Split('|');
                    switch (parts[0])
                    {
                        case "portal": list.Insert(0, new CaptureSource { Label = "Elegir pantalla o ventana…", Id = "portal" }); break;
                        case "screen": list.Add(new CaptureSource { Label = "Todas las pantallas", Id = "screen" }); break;
                        case "window" or "focused" or "region": break;
                        default:
                            var size = parts.Length > 1 ? $"  ·  {parts[1]}" : "";
                            list.Add(new CaptureSource { Label = $"Pantalla  ·  {parts[0]}{size}", Id = parts[0] });
                            break;
                    }
                }
            }
            if (list.Count == 0)
                list.Add(_wayland
                    ? new CaptureSource { Label = "Elegir pantalla o ventana…", Id = "portal" }
                    : new CaptureSource { Label = "Todas las pantallas", Id = "screen" });
            return list;
        }

        if (_backend == Backend.Ffmpeg)
        {
            // xrandr --listmonitors  ->  " 0: +*eDP-1 1920/344x1080/194+0+0  eDP-1"
            var (exit, output) = Run("xrandr", ["--listmonitors"], 3000);
            if (exit == 0)
            {
                int n = 1;
                foreach (Match m in MonitorRegex().Matches(output))
                {
                    int w = int.Parse(m.Groups[1].Value), h = int.Parse(m.Groups[2].Value);
                    list.Add(new CaptureSource
                    {
                        Label = $"Pantalla {n++}  ·  {m.Groups[5].Value}  ·  {w}x{h}",
                        Id = m.Groups[5].Value,
                        Width = w, Height = h,
                        X = int.Parse(m.Groups[3].Value), Y = int.Parse(m.Groups[4].Value),
                    });
                }
            }
            if (list.Count == 0)
            {
                var (w, h) = ScreenSizeFromXdpyinfo();
                list.Add(new CaptureSource { Label = $"Pantalla  ·  {w}x{h}", Id = "screen", Width = w, Height = h });
            }
        }
        return list;
    }

    [GeneratedRegex(@"^\s*\d+:\s+\S+\s+(\d+)/\d+x(\d+)/\d+\+(\d+)\+(\d+)\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex MonitorRegex();

    private static (int w, int h) ScreenSizeFromXdpyinfo()
    {
        var (exit, output) = Run("xdpyinfo", [], 3000);
        var m = Regex.Match(output, @"dimensions:\s+(\d+)x(\d+)");
        return exit == 0 && m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (1920, 1080);
    }

    // ================= Grabación =================

    public void Start(CaptureSource source, RecordingSettings s, string filePath)
    {
        if (_backend == Backend.None) throw new InvalidOperationException(MissingBackendMessage(_wayland));
        DisposeProcess();

        var args = _backend == Backend.Gsr ? GsrArgs(source, s, filePath) : FfmpegArgs(source, s, filePath);
        string exe = _backend == Backend.Gsr ? _gsrCommand[0] : "ffmpeg";

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = _backend == Backend.Ffmpeg,
            CreateNoWindow = true,
        };
        if (_backend == Backend.Gsr) foreach (var a in _gsrCommand[1..]) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        _stderr.Clear();
        _stopRequested = false;
        _frames = _backend == Backend.Ffmpeg ? 0 : -1;
        CurrentFile = filePath;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) { if (_stderr.Length < 8000) _stderr.AppendLine(e.Data); } };
        p.OutputDataReceived += (_, e) =>
        {
            // FFmpeg -progress pipe:1  ->  "frame=123"
            if (e.Data is { } line && line.StartsWith("frame=", StringComparison.Ordinal) &&
                int.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f))
                _frames = f;
        };
        p.Exited += (_, _) => OnExited(p);

        if (!p.Start()) throw new InvalidOperationException("No se pudo iniciar " + exe);
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        _proc = p;
        SetState(RecState.Recording);
    }

    private static List<string> GsrArgs(CaptureSource src, RecordingSettings s, string file)
    {
        var a = new List<string>
        {
            "-w", src.Id,
            "-c", "mp4",
            "-f", s.Fps.ToString(CultureInfo.InvariantCulture),
            "-k", s.UseHevc ? "hevc" : "h264",
            "-q", s.BitrateKbps switch { < 6000 => "medium", < 10000 => "high", _ => "very_high" },
            "-cursor", s.ShowCursor ? "yes" : "no",
        };
        if (src.Id == "portal") a.AddRange(["-restore-portal-session", "yes"]);
        if (s.Width > 0) a.AddRange(["-s", $"{s.Width}x{s.Height}"]);

        var audio = (s.SystemAudio, s.Microphone) switch
        {
            (true, true) => "default_output|default_input",
            (true, false) => "default_output",
            (false, true) => "default_input",
            _ => null,
        };
        if (audio != null) a.AddRange(["-a", audio]);
        a.AddRange(["-o", file]);
        return a;
    }

    private List<string> FfmpegArgs(CaptureSource src, RecordingSettings s, string file)
    {
        string display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
        int srcW = src.Width > 0 ? src.Width : 1920, srcH = src.Height > 0 ? src.Height : 1080;
        string fps = s.Fps.ToString(CultureInfo.InvariantCulture);

        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-y",
            "-thread_queue_size", "512", "-f", "x11grab", "-framerate", fps,
            "-draw_mouse", s.ShowCursor ? "1" : "0",
            "-video_size", $"{srcW}x{srcH}", "-i", $"{display}+{src.X},{src.Y}",
        };

        // ----- audio (PulseAudio / PipeWire-pulse) -----
        var audioInputs = new List<string>();
        if (s.SystemAudio && DefaultSinkMonitor() is { } monitor) audioInputs.Add(monitor);
        if (s.Microphone) audioInputs.Add("default");
        foreach (var dev in audioInputs) a.AddRange(["-thread_queue_size", "512", "-f", "pulse", "-i", dev]);

        // ----- tamaño de salida manteniendo la proporción (pares) -----
        int outW = srcW, outH = srcH;
        if (s.Width > 0 && (srcW > s.Width || srcH > s.Height))
        {
            double k = Math.Min(s.Width / (double)srcW, s.Height / (double)srcH);
            outW = (int)(srcW * k) & ~1;
            outH = (int)(srcH * k) & ~1;
        }
        bool scale = outW != srcW || outH != srcH;

        // ----- vídeo: el encoder más ligero que funcione -----
        string enc = PickFfmpegEncoder(s.UseHevc);
        string br = $"{s.BitrateKbps}k", buf = $"{s.BitrateKbps * 2}k";
        if (enc.EndsWith("_vaapi"))
        {
            a.InsertRange(0, ["-vaapi_device", "/dev/dri/renderD128"]);
            a.AddRange(["-vf", "format=nv12,hwupload" + (scale ? $",scale_vaapi=w={outW}:h={outH}" : "")]);
            a.AddRange(["-c:v", enc, "-rc_mode", "CBR", "-b:v", br, "-maxrate", br]);
        }
        else
        {
            a.AddRange(["-vf", (scale ? $"scale={outW}:{outH}:flags=fast_bilinear," : "") + "format=yuv420p"]);
            if (enc.EndsWith("_nvenc"))
                a.AddRange(["-c:v", enc, "-preset", "p2", "-rc", "cbr", "-b:v", br]);
            else
                a.AddRange(["-c:v", enc, "-preset", "ultrafast", "-b:v", br, "-maxrate", br, "-bufsize", buf]);
        }
        a.AddRange(["-r", fps, "-g", (s.Fps * 2).ToString(CultureInfo.InvariantCulture)]);

        if (audioInputs.Count == 2)
            a.AddRange(["-filter_complex", "[1:a][2:a]amix=inputs=2:duration=longest[aout]", "-map", "0:v", "-map", "[aout]"]);
        if (audioInputs.Count > 0) a.AddRange(["-c:a", "aac", "-b:a", "160k"]);

        a.Add(file);
        return a;
    }

    /// <summary>Prueba NVENC, luego VAAPI y por último x264/x265 (CPU).</summary>
    private static string PickFfmpegEncoder(bool hevc)
    {
        lock (EncoderCache)
        {
            if (!EncoderCache.TryGetValue(hevc, out var enc))
                EncoderCache[hevc] = enc = ProbeFfmpegEncoder(hevc);
            return _ffmpegVideoEncoder = enc;
        }
    }

    private static readonly Dictionary<bool, string> EncoderCache = new();

    private static string ProbeFfmpegEncoder(bool hevc)
    {
        string codec = hevc ? "hevc" : "h264";
        var (_, list) = Run("ffmpeg", ["-hide_banner", "-encoders"], 5000);

        if (list.Contains($"{codec}_nvenc") &&
            Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=256x144", "-frames:v", "1",
                           "-c:v", $"{codec}_nvenc", "-f", "null", "-"], 8000).exit == 0)
            return $"{codec}_nvenc";

        if (list.Contains($"{codec}_vaapi") && File.Exists("/dev/dri/renderD128") &&
            Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-vaapi_device", "/dev/dri/renderD128", "-f", "lavfi",
                           "-i", "color=black:s=256x144", "-frames:v", "1", "-vf", "format=nv12,hwupload",
                           "-c:v", $"{codec}_vaapi", "-f", "null", "-"], 8000).exit == 0)
            return $"{codec}_vaapi";

        return hevc && list.Contains("libx265") ? "libx265" : "libx264";
    }

    private static string? DefaultSinkMonitor()
    {
        var (exit, sink) = Run("pactl", ["get-default-sink"], 3000);
        sink = sink.Trim();
        return exit == 0 && sink.Length > 0 ? sink + ".monitor" : null;
    }

    public void Stop()
    {
        var p = _proc;
        if (p == null || p.HasExited) return;
        _stopRequested = true;
        SetState(RecState.Finishing);
        if (_backend == Backend.Gsr)
        {
            if (_state == RecState.Paused) NativeMethods.SendSignal(p.Id, NativeMethods.SIGUSR2);
            NativeMethods.SendSignal(p.Id, NativeMethods.SIGINT);
        }
        else
        {
            try { p.StandardInput.Write('q'); p.StandardInput.Flush(); }
            catch { NativeMethods.SendSignal(p.Id, NativeMethods.SIGINT); }
        }
    }

    public void Pause()
    {
        if (_backend != Backend.Gsr || _proc is not { HasExited: false } p || _state != RecState.Recording) return;
        if (NativeMethods.SendSignal(p.Id, NativeMethods.SIGUSR2)) SetState(RecState.Paused);
    }

    public void Resume()
    {
        if (_backend != Backend.Gsr || _proc is not { HasExited: false } p || _state != RecState.Paused) return;
        if (NativeMethods.SendSignal(p.Id, NativeMethods.SIGUSR2)) SetState(RecState.Recording);
    }

    private void OnExited(Process p)
    {
        if (!ReferenceEquals(p, _proc)) return;
        int code;
        try { code = p.ExitCode; } catch { code = -1; }
        p.WaitForExit(); // vacía stdout/stderr
        bool fileOk = CurrentFile != null && File.Exists(CurrentFile) && new FileInfo(CurrentFile).Length > 0;
        _state = RecState.Idle;

        if (_stopRequested && fileOk && (code == 0 || code == 255 || code == 130))
        {
            Completed?.Invoke(CurrentFile!);
        }
        else
        {
            string err;
            lock (_stderr) err = _stderr.ToString().Trim();
            if (err.Length > 1200) err = "…" + err[^1200..];
            Failed?.Invoke(string.IsNullOrEmpty(err) ? $"El grabador terminó con código {code}." : err);
        }
    }

    private void SetState(RecState s)
    {
        _state = s;
        StateChanged?.Invoke(s);
    }

    private void DisposeProcess()
    {
        var p = _proc;
        _proc = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                NativeMethods.SendSignal(p.Id, NativeMethods.SIGINT);
                if (!p.WaitForExit(4000)) p.Kill(true);
            }
        }
        catch { }
        p.Dispose();
    }

    public void Dispose() => DisposeProcess();

    // ================= Utilidades =================

    private static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static (int exit, string output) Run(string exe, IEnumerable<string> args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, ""); }
            return (p.ExitCode, stdout.Result);
        }
        catch
        {
            return (-1, "");
        }
    }
}
