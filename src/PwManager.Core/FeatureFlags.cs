namespace PwManager.Core;

/// <summary>
/// 保留機能の有効化フラグ。既定はすべて無効。
/// </summary>
public static class FeatureFlags
{
    /// <summary>
    /// FIDO2 スロット（ADR-0005）。FIDO2 キー入手後、実機 PoC（P0-2）を経て有効化する。
    /// </summary>
    public static bool Fido2Enabled { get; set; }
}
