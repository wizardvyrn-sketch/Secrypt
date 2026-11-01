using PwManager.Core.Vault;

namespace PwManager.Application.Services;

/// <summary>解錠の方法（画面のタブ・ログの method に対応）。</summary>
public enum UnlockMethod
{
    /// <summary>この PC の TPM + PIN。</summary>
    Pin,
    /// <summary>鍵ファイル（USB メモリ／印刷 QR のテキスト）+ パスワード。</summary>
    Keyfile,
    /// <summary>復旧コード（紙）。</summary>
    Recovery,
}

/// <summary>解錠の要求。秘密のバイト列は UnlockService が使用後にゼロ化する（呼び出し側は渡したら使わない）。</summary>
public abstract record UnlockRequest(UnlockMethod Method);

public sealed record PinUnlockRequest(byte[] Pin) : UnlockRequest(UnlockMethod.Pin);

/// <summary>鍵ファイルはパス（KeyfilePath）か、印刷した QR のテキスト（KeyfileText）のどちらかで指定する。</summary>
public sealed record KeyfileUnlockRequest(string? KeyfilePath, string? KeyfileText, byte[] Password) : UnlockRequest(UnlockMethod.Keyfile);

public sealed record RecoveryUnlockRequest(string Code) : UnlockRequest(UnlockMethod.Recovery);

/// <summary>解錠画面に出す方法の一覧と、既定で選ぶ方法。</summary>
public sealed record UnlockOptions(
    bool VaultReadable,
    bool PinAvailable,
    bool KeyfileAvailable,
    UnlockMethod DefaultMethod,
    string? LastKeyfilePath,
    Results.ErrorCode HeaderError,
    /// <summary>TPM のスロットはあるが、この PC の TPM のものではない（PC 買い替え・TPM クリア後）。</summary>
    bool TpmSlotsForOtherDevicesOnly);

/// <summary>Vault 内のスロットの表示用情報（秘密を含まない）。</summary>
public sealed record SlotInfo(Guid Id, SlotType Type, string Label, DateTimeOffset Created, DateTimeOffset? LastVerified, bool? IsThisDevice);
