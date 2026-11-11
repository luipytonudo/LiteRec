using System.Diagnostics;
using System.IO;
using System.Windows;
using LiteRec.Core;

namespace LiteRec;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "LiteRec - Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        // Modo de prueba sin interfaz:  LiteRec.exe --selftest <segundos> <salida.mp4>
        if (e.Args.Length >= 1 && e.Args[0] == "--selftest")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int secs = e.Args.Length > 1 ? int.Parse(e.Args[1]) : 5;
            string outFile = e.Args.Length > 2 ? e.Args[2] : Path.Combine(Path.GetTempPath(), "literec_test.mp4");
            _ = RunSelfTest(secs, outFile);
            return;
        }

        base.OnStartup(e);
        var win = new MainWindow();
        win.Show();

        // Captura de la interfaz a PNG (uso interno):  LiteRec.exe --snap <archivo.png>
        if (e.Args.Length >= 2 && e.Args[0] == "--snap")
        {
            win.Dispatcher.BeginInvoke(() =>
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(win);
                var root = (FrameworkElement)win.Content;
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(root);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = File.Create(e.Args[1])) enc.Save(fs);
                Shutdown();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    private async Task RunSelfTest(int secs, string outFile)
    {
        var log = new List<string>();
        var engine = new RecordingEngine();
        var done = new TaskCompletionSource<string>();
        engine.Completed += p => done.TrySetResult("OK " + p);
        engine.Failed += err => done.TrySetResult("FAIL " + err);
        var proc = Process.GetCurrentProcess();
        try
        {
            var src = RecordingEngine.GetSources().First(s => !s.IsWindow);
            log.Add("Fuente: " + src.Label);
            var cpu0 = proc.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            engine.Start(src, new RecordingSettings { Width = 1280, Height = 720, Fps = 60, BitrateKbps = 7500, SystemAudio = true }, outFile);
            long peakWs = 0;
            for (int i = 0; i < secs * 4; i++)
            {
                await Task.Delay(250);
                proc.Refresh();
                peakWs = Math.Max(peakWs, proc.WorkingSet64);
            }
            var cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / (sw.Elapsed.TotalSeconds * Environment.ProcessorCount) * 100;
            int frames = engine.FrameNumber;
            log.Add($"Frames: {frames} en {sw.Elapsed.TotalSeconds:0.0}s => {frames / sw.Elapsed.TotalSeconds:0.0} fps");
            log.Add($"CPU media: {cpu:0.0}%  RAM pico: {peakWs / 1048576} MB");
            engine.Stop();
            var res = await Task.WhenAny(done.Task, Task.Delay(15000)) == done.Task ? done.Task.Result : "TIMEOUT";
            log.Add(res);
            if (File.Exists(outFile)) log.Add($"Tamano: {new FileInfo(outFile).Length / 1024} KB");
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); }
        finally { engine.Dispose(); }
        File.WriteAllLines(outFile + ".log.txt", log);
        Shutdown();
    }
}
