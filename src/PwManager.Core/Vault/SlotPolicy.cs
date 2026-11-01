namespace PwManager.Core.Vault;

/// <summary>「鍵の喪失」も脅威として扱う（脅威モデル T12）。保存時に解錠経路の冗長性を強制する。</summary>
public static class SlotPolicy
{
    public static void Validate(IReadOnlyList<KeySlot> slots)
    {
        bool hasRecovery = slots.Any(s => s.Type == SlotType.Recovery);
        bool hasOther = slots.Any(s => s.Type != SlotType.Recovery);
        if (!hasRecovery)
            throw new SlotPolicyException("復旧コードのスロットがありません。復旧コードは必須です。");
        if (!hasOther)
            throw new SlotPolicyException("復旧コード以外の解錠手段（TPM またはキーファイル）が 1 つ以上必要です。");
    }

    public static bool IsSatisfied(IReadOnlyList<KeySlot> slots)
    {
        try { Validate(slots); return true; }
        catch (SlotPolicyException) { return false; }
    }
}
