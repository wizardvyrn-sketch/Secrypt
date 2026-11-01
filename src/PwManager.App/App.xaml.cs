using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PwManager.App.Services;
using PwManager.App.Views;
using PwManager.Application.Infrastructure;
using PwManager.Application.Logging;
using PwManager.Application.Ports;
using PwManager.Application.Session;
using PwManager.Core.Localization;
using PwManager.Core.Slots;
using PwManager.Platform.Windows;
using PwManager.Presentation;
using PwManager.Presentation.Ports;
using PwManager.Presentation.ViewModels;

namespace PwManager.App;

/// <summary>
/// 起動と依存の組み立て（D-3: Microsoft.Extensions.DependencyInjection）。
/// 多重起動の防止（R-16）・未処理例外の記録とロック（R-19）もここで行う。
/// </summary>
public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;
    private SingleInstance? _single;
    private ILogger _log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = AppDataPaths.FromEnvironment();
        // 引数で Vault ファイルのパスを指定できる（テスト・別ファイル用）
        var vaultPath = System.IO.Path.GetFullPath(e.Args.Length > 0 ? e.Args[0] : paths.DefaultVaultPath);

        _services = BuildServices(paths, vaultPath);
        _log = _services.GetRequiredService<ILoggerFactory>().CreateLogger("App");
        // 表示言語: 保存した言語、無ければ Windows の表示言語（docs/08 §8.1）
        _services.GetRequiredService<LanguageViewModel>().ApplySaved(System.Globalization.CultureInfo.CurrentUICulture);
        HookUnhandledExceptions();

        _single = SingleInstance.TryAcquire(vaultPath, () => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront()));
        if (_single is null)
        {
            Log.SecondInstanceBlocked(_log);
            Shutdown();
            return;
        }

        Log.AppStarted(_log, Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?", RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription);
        Log.VaultLocation(_log, PathRedactor.Redact(vaultPath), System.IO.File.Exists(vaultPath));

        // 起動時ロゴ画面（08 §1.1）。最初に開いたウィンドウが MainWindow になるため、メインを出したら差し替えてから閉じる
        var splash = new SplashWindow();
        splash.BootCompleted += (_, _) =>
        {
            ShowMainWindow();
            splash.FadeOutAndClose();
        };
        splash.Show();
    }

    private void ShowMainWindow()
    {
        var services = _services!;
        CaptureProtection.AllowCapture = services.GetRequiredService<DevOptions>().AllowCapture;
        var window = new MainWindow(services.GetRequiredService<ShellViewModel>());
        MainWindow = window;
        window.Show();
    }

    private static ServiceProvider BuildServices(AppDataPaths paths, string vaultPath)
    {
#if DEBUG
        const bool isDebugBuild = true;
#else
        const bool isDebugBuild = false;
#endif
        var s = new ServiceCollection();
        s.AddPwManager(paths, vaultPath, DevOptions.Create(Environment.GetEnvironmentVariable, isDebugBuild), Assembly.GetExecutingAssembly());
        // Windows・WPF の実装
        s.AddSingleton<ITpmSealer, WindowsTpmSealer>();
        s.AddSingleton<IClipboardService, WpfClipboardService>();
        s.AddSingleton<IDialogService, WpfDialogService>();
        s.AddSingleton<IShellService, ShellService>();
        s.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        s.AddSingleton<ISettingsProtector, DpapiSettingsProtector>(); // 鍵ファイルの場所を settings.json に平文で置かない
        return s.Build();
    }

    /// <summary>
    /// 未処理例外（R-19）: ログに記録 → 解錠中ならロック → 案内（ログフォルダを開く／続ける／終了）。
    /// 背景スレッドの例外も記録する。
    /// </summary>
    private void HookUnhandledExceptions()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            OnUnhandled("Dispatcher", args.Exception, canContinue: true);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.UnhandledException(_log, "Task", args.Exception.GetType().Name, args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.UnhandledException(_log, "AppDomain", ex.GetType().Name, ex);
        };
    }

    private bool _handlingError;

    private void OnUnhandled(string source, Exception ex, bool canContinue)
    {
        Log.UnhandledException(_log, source, ex.GetType().Name, ex);
        if (_handlingError || _services is null) return;
        _handlingError = true;
        try
        {
            _services.GetRequiredService<VaultSession>().RequestLock(LockReason.Error);
            var choice = _services.GetRequiredService<IDialogService>().Choose(L.T("予期しないエラー", "Unexpected error"),
                L.T("予期しないエラーが発生したため、ロックしました。詳細はログフォルダに記録しました（パスワードや項目の内容は記録していません）。",
                    "An unexpected error occurred, so the vault was locked. Details were written to the log folder (passwords and entry contents are not recorded)."),
                canContinue ? 1 : 2, L.T("ログフォルダを開く", "Open log folder"), L.T("続ける", "Continue"), L.T("終了", "Exit"));
            if (choice == 0) _services.GetRequiredService<IShellService>().OpenFolder(_services.GetRequiredService<JsonlFileLoggerProvider>().Directory);
            if (choice == 2) Shutdown();
        }
        catch (Exception inner) when (inner is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.UnhandledException(_log, "ErrorDialog", inner.GetType().Name, inner);
        }
        finally
        {
            _handlingError = false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is not null)
        {
            _services.GetRequiredService<VaultSession>().RequestLock(LockReason.Exit);
            _services.GetRequiredService<IClipboardService>().ClearIfOurs();
            Log.AppExiting(_log);
            _services.Dispose();
        }
        _single?.Dispose();
        base.OnExit(e);
    }
}
