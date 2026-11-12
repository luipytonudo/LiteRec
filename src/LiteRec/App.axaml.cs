using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace LiteRec;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var win = new MainWindow();
            desktop.MainWindow = win;

            // Captura de la interfaz a PNG (uso interno):  LiteRec --snap <archivo.png>
            var args = desktop.Args ?? [];
            if (args.Length >= 2 && args[0] == "--snap")
                win.SnapshotOnLoad(args[1]);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
