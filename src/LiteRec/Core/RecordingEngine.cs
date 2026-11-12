using System.Diagnostics;

namespace LiteRec.Core;

public enum RecState { Idle, Recording, Paused, Finishing }

/// <summary>Fuente de captura (pantalla, ventana o selector del sistema).</summary>
public sealed class CaptureSource
{
    public required string Label { get; init; }
    /// <summary>Identificador que entiende el backend (nombre de monitor, "portal", etc.).</summary>
    public required string Id { get; init; }
    /// <summary>Área en píxeles (solo la usa FFmpeg/x11grab).</summary>
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Objeto nativo del backend (p. ej. RecordableDisplay en Windows).</summary>
    public object? Native { get; init; }
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

/// <summary>Motor de grabación. Cada sistema operativo tiene su implementación.</summary>
public interface IRecordingEngine : IDisposable
{
    /// <summary>Texto corto para la etiqueta de la barra de título (p. ej. "GPU · H.264").</summary>
    string EncoderLabel(bool hevc);
    /// <summary>Descripción del backend en uso (se muestra en el pie).</summary>
    string BackendName { get; }
    bool SupportsPause { get; }
    bool SupportsClicks { get; }
    bool SupportsWindows { get; }

    RecState State { get; }
    /// <summary>Fotogramas grabados, si el backend los informa.</summary>
    int? FrameNumber { get; }
    string? CurrentFile { get; }
    /// <summary>Procesos auxiliares (FFmpeg, gpu-screen-recorder) para sumar su CPU/RAM.</summary>
    Process? HelperProcess { get; }

    event Action<RecState>? StateChanged;
    event Action<string>? Completed;
    event Action<string>? Failed;

    IReadOnlyList<CaptureSource> GetSources();
    void Start(CaptureSource source, RecordingSettings settings, string filePath);
    void Stop();
    void Pause();
    void Resume();
}

public static class RecordingEngineFactory
{
    public static IRecordingEngine Create()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()) return new Platforms.Windows.WindowsRecordingEngine();
#endif
        if (OperatingSystem.IsLinux()) return new Platforms.Linux.LinuxRecordingEngine();
        throw new PlatformNotSupportedException(
            OperatingSystem.IsWindows()
                ? "Esta compilación es la de Linux. Descarga la versión de Windows."
                : "LiteRec funciona en Windows y Linux.");
    }
}
