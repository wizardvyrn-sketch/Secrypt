using PwManager.Core.Crypto;

namespace PwManager.Application.Services;

/// <summary>新しく登録するスロットの KDF パラメータ（テストでは軽い値に差し替える）。</summary>
public sealed record VaultOptions
{
    public KdfParameters PasswordKdf { get; init; } = KdfParameters.Password;
    public KdfParameters PinKdf { get; init; } = KdfParameters.Pin;
    public KdfParameters RecoveryKdf { get; init; } = KdfParameters.Recovery;
}
