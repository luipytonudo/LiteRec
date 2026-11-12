using System.Diagnostics;
using Avalonia;
using LiteRec.Core;

namespace LiteRec;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Modo de prueba sin interfaz:  LiteRec --selftest <segundos> <salida.mp4>
        if (args.Length >= 1 && args[0] == "--selftest")
            return SelfTest(args.Length > 1 ? int.Parse(args[1]) : 5,
                            args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "literec_test.mp4"));

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

    /// <summary>Graba unos segundos de la primera pantalla y escribe un informe. Devuelve 0 si salió bien.</summary>
    private static int SelfTest(int secs, string outFile)
    {
        var log = new List<string>();
        int code = 1;
        using var engine = RecordingEngineFactory.Create();
        var done = new TaskCompletionSource<string>();
        engine.Completed += p => done.TrySetResult("OK " + p);
        engine.Failed += e => done.TrySetResult("FAIL " + e);
        var proc = Process.GetCurrentProcess();
        try
        {
            log.Add("Backend: " + engine.BackendName);
            var sources = engine.GetSources();
            log.Add("Fuentes: " + string.Join(" | ", sources.Select(s => s.Label)));
            var src = sources[0];
            var cpu0 = proc.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            engine.Start(src, new RecordingSettings { Width = 1280, Height = 720, Fps = 60, BitrateKbps = 7500, SystemAudio = OperatingSystem.IsWindows() }, outFile);
            long peak = 0;
            TimeSpan helperCpu = TimeSpan.Zero;
            for (int i = 0; i < secs * 4; i++)
            {
                Thread.Sleep(250);
                proc.Refresh();
                long ws = proc.WorkingSet64;
                if (engine.HelperProcess is { } h)
                {
                    try { h.Refresh(); ws += h.WorkingSet64; helperCpu = h.TotalProcessorTime; } catch { }
                }
                peak = Math.Max(peak, ws);
            }
            double cpu = ((proc.TotalProcessorTime - cpu0) + helperCpu).TotalSeconds / (sw.Elapsed.TotalSeconds * Environment.ProcessorCount) * 100;
            if (engine.FrameNumber is int frames)
                log.Add($"Frames: {frames} en {sw.Elapsed.TotalSeconds:0.0}s => {frames / sw.Elapsed.TotalSeconds:0.0} fps");
            log.Add($"CPU media: {cpu:0.0}%  RAM pico: {peak / 1048576} MB");
            engine.Stop();
            var res = done.Task.Wait(20000) ? done.Task.Result : "TIMEOUT";
            log.Add(res);
            if (File.Exists(outFile)) log.Add($"Tamano: {new FileInfo(outFile).Length / 1024} KB");
            if (res.StartsWith("OK") && File.Exists(outFile) && new FileInfo(outFile).Length > 0) code = 0;
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); }

        File.WriteAllLines(outFile + ".log.txt", log);
        foreach (var l in log) Console.WriteLine(l);
        return code;
    }
}
