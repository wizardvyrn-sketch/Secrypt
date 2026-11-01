using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PwManager.Application.Infrastructure;
using PwManager.Application.Logging;
using PwManager.Application.Policies;
using PwManager.Application.Ports;
using PwManager.Presentation.Ports;
using PwManager.Application.Services;
using PwManager.Application.Session;
using PwManager.Presentation.ViewModels;

namespace PwManager.Presentation;

/// <summary>
/// 依存の組み立て（D-3）。OS・UI に依存しない部分をここに集め、App は画面・TPM・クリップボードの実装だけを足す。
/// Linux のテストでも同じ組み立てを検証できる。
/// </summary>
public static class Composition
{
    /// <summary>
    /// アプリの共通部分を登録する。呼び出し側で IDialogService・IShellService・IClipboardService・ITpmSealer を登録すること。
    /// </summary>
    public static IServiceCollection AddPwManager(this IServiceCollection s, AppDataPaths paths, string vaultPath, DevOptions devOptions, Assembly appAssembly)
    {
        SettingsService? settingsForLog = null;
        var logs = new JsonlFileLoggerProvider(paths.LogDirectory,
            () => settingsForLog?.Current.VerboseLogging == true ? LogLevel.Debug : LogLevel.Information);
        s.AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        s.AddSingleton(logs);
        s.AddSingleton(paths);
        s.AddSingleton(devOptions);
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton(new VaultOptions());

        s.AddSingleton<IVaultStore>(_ => new FileVaultStore(vaultPath));
        // ISettingsProtector は App が Windows の DPAPI 版を登録する（無ければ平文: OS 非依存のテスト）
        s.AddSingleton<ISettingsStore>(sp => new JsonSettingsStore(paths.SettingsPath, sp.GetRequiredService<ILogger<JsonSettingsStore>>(), sp.GetService<ISettingsProtector>()));
        s.AddSingleton<IKeyfileStore, FileKeyfileStore>();

        s.AddSingleton(sp =>
        {
            var settings = new SettingsService(sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<ILogger<SettingsService>>());
            settingsForLog = settings;
            return settings;
        });
        s.AddSingleton<VaultSession>();
        s.AddSingleton<UnlockBackoffPolicy>();
        s.AddSingleton<AutoLockMonitor>();
        s.AddSingleton<UnlockService>();
        s.AddSingleton<SetupService>();
        s.AddSingleton<SlotService>();
        s.AddSingleton<EntryService>();
        s.AddSingleton<ImportService>();

        s.AddSingleton(new DiagnosticsInfo(
            appAssembly.GetName().Version?.ToString() ?? "?",
            RuntimeInformation.FrameworkDescription,
            logs.Directory));
        s.AddSingleton<IUiDispatcher, SynchronizationContextUiDispatcher>(); // App が WPF 版で上書きする
        s.AddSingleton<LanguageViewModel>();
        s.AddTransient<SetupViewModel>();
        s.AddTransient<UnlockViewModel>();
        s.AddTransient<VaultViewModel>();
        s.AddTransient<KeysViewModel>();
        s.AddTransient<Func<SetupViewModel>>(sp => sp.GetRequiredService<SetupViewModel>);
        s.AddTransient<Func<UnlockViewModel>>(sp => sp.GetRequiredService<UnlockViewModel>);
        s.AddTransient<Func<VaultViewModel>>(sp => sp.GetRequiredService<VaultViewModel>);
        s.AddTransient<Func<KeysViewModel>>(sp => sp.GetRequiredService<KeysViewModel>);
        s.AddSingleton<ShellViewModel>();
        return s;
    }

    /// <summary>組み立てを検証してから作る（登録漏れは起動時に分かる）。</summary>
    public static ServiceProvider Build(this IServiceCollection s)
    {
        var provider = s.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        provider.GetRequiredService<SettingsService>(); // ログの詳細設定を有効にする
        return provider;
    }
}
