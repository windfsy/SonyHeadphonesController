using System.Windows;

namespace SonyCtrl;

public partial class App : Application
{
    public static AppConfig Config { get; private set; } = new();
    public ConnectionManager? Connection { get; private set; }

    private static readonly string DiagLog = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "SonyHeadphonesController.diag.log");

    private static void Log(string msg) =>
        System.IO.File.AppendAllText(DiagLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Catch-all diagnostics: WinExe has no console, so unhandled dispatcher exceptions
        // silently kill the app. Log them to %TEMP% instead.
        DispatcherUnhandledException += (_, ex) =>
        {
            Log($"UNHANDLED: {ex.Exception}");
            ex.Handled = true; // keep the app alive; the failure is logged for diagnosis
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log($"FATAL: {ex.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log($"UNOBSERVED TASK: {ex.Exception}");
            ex.SetObserved();
        };

        Log("startup begin");
        Config = AppConfig.Load();
        Log($"config loaded: target={Config.TargetAddress:X12}");

        if (Config.TargetAddress is null)
        {
            Log("no target, showing picker");
            var picker = new UI.DevicePickerWindow();
            picker.ShowDialog();
            if (Config.TargetAddress is null)
            {
                Log("picker cancelled, exiting");
                Shutdown();
                return;
            }
            Log($"picker selected: {Config.TargetName} {Config.TargetAddress:X12}");
        }

        var host = new UI.TrayHostWindow();
        host.EnsureHandleForWatcher();
        Log("host HWND created");

        Connection = new ConnectionManager(Config, host);
        Log("ConnectionManager created");

        host.Initialize(Connection);
        Log("host initialized (tray should be up)");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Connection?.Dispose();
        base.OnExit(e);
    }
}
