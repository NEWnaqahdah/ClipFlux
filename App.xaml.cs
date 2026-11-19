using System;
using System.Threading;
using System.Windows;

namespace ClipFlux;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        Services.LocalizationService.ApplyLanguage(Services.AppSettings.Load().LanguageMode);

        const string appName = "ClipFlux_SingleInstance_{B32F1A7E-8D29-417E-971D-3C842B768822}";
        _mutex = new Mutex(true, appName, out bool isNewInstance);

        if (!isNewInstance)
        {
            MessageBox.Show(
                Services.LocalizationService.Get("Str_Dlg_AlreadyRunning", "ClipFlux 已在运行中。\r\n请通过任务栏通知区域图标或全局快捷键呼出窗口。"),
                "ClipFlux",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            MessageBox.Show($"ClipFlux Error: {args.ExceptionObject}", "ClipFlux", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"ClipFlux Error: {args.Exception.Message}", "ClipFlux", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}
