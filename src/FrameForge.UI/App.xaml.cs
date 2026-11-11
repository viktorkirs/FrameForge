using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using FrameForge.Core;
using FrameForge.Diagnostics;

namespace FrameForge.UI;

public partial class App : Application
{
    // Setup and Uninstall check this object before replacing files. Each process
    // retains a handle; ownership is unnecessary and multiple app instances still work.
    Mutex? _installationGuard;

    protected override void OnStartup(StartupEventArgs e)
    {
        _installationGuard = new Mutex(false, @"Local\FrameForge.Recorder.Running");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Необработанное исключение интерфейса.", args.Exception);
            MessageBox.Show(args.Exception.Message, "FrameForge Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Error("Необработанное исключение процесса.", ex);
        };

        if (e.Args.Any(arg => arg.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            AttachParentConsole();
            int code = SelfTest.Run(e.Args);
            Shutdown(code);
            return;
        }

        AppSettings settings = SettingsStore.Load();
        // The first-run dialog can close before the main window exists.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!LanguageWindow.ChooseIfNeeded(settings))
        {
            Shutdown();
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        GC.KeepAlive(_installationGuard);
    }

    static void AttachParentConsole()
    {
        if (!AttachConsole(0xFFFFFFFF))
            AllocConsole();
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    static extern bool AllocConsole();
}
