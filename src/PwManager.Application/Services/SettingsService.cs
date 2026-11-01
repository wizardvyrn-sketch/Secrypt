using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PwManager.Application.Logging;
using PwManager.Application.Ports;

namespace PwManager.Application.Services;

/// <summary>設定をメモリに保持し、変更時に保存する。</summary>
public sealed class SettingsService
{
    private readonly ISettingsStore _store;
    private readonly ILogger _log;
    private AppSettings _current;

    public SettingsService(ISettingsStore store, ILogger<SettingsService>? logger = null)
    {
        _store = store;
        _log = logger ?? NullLogger<SettingsService>.Instance;
        _current = store.Load().Normalize();
    }

    /// <summary>現在の設定（複製）。</summary>
    public AppSettings Current => _current.Clone();

    /// <summary>設定が変わったとき（自動ロック時間の反映など）。</summary>
    public event EventHandler? Changed;

    /// <summary>設定を変更して保存する。範囲外の値は既定値に戻す。保存できなくてもメモリ上は反映する。</summary>
    public bool Update(Action<AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = _current.Clone();
        change(next);
        next.Normalize();
        if (next.AutoLockMinutes != _current.AutoLockMinutes) Log.SettingsChanged(_log, nameof(AppSettings.AutoLockMinutes), next.AutoLockMinutes);
        if (next.ClipboardClearSeconds != _current.ClipboardClearSeconds) Log.SettingsChanged(_log, nameof(AppSettings.ClipboardClearSeconds), next.ClipboardClearSeconds);
        if (next.VerboseLogging != _current.VerboseLogging) Log.SettingsChanged(_log, nameof(AppSettings.VerboseLogging), next.VerboseLogging ? 1 : 0);
        _current = next;
        var saved = _store.Save(next);
        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }
}
