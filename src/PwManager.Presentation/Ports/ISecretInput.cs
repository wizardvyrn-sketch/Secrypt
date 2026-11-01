namespace PwManager.Presentation.Ports;

/// <summary>
/// 秘密の入力欄（PIN・パスワード）。画面の PasswordBox とはバインドせず、操作の実行時に UTF-8 バイト列として
/// 取り出す（string に載せない: ADR-0006）。View 側のビヘイビアが <see cref="SecretInput"/> に PasswordBox をつなぐ。
/// </summary>
public interface ISecretInput
{
    /// <summary>文字数（UTF-16 単位）。</summary>
    int Length { get; }

    /// <summary>内容を UTF-8 で取り出す。呼び出し側が使用後にゼロ化する。</summary>
    byte[] ReadUtf8();

    /// <summary>内容を置き換える（パスワード生成・項目の表示）。</summary>
    void SetUtf8(ReadOnlySpan<byte> utf8);

    void Clear();

    /// <summary>入力欄にフォーカスを移す。</summary>
    void Focus();
}

/// <summary>
/// ViewModel が持つ入力欄の受け口。View がつながるまでは空として振る舞う（テストでは <see cref="Attach"/> で差し込む）。
/// </summary>
public sealed class SecretInput : ISecretInput
{
    private ISecretInput? _target;

    /// <summary>内容が変わったとき（未保存の判定・入力規則の表示用）。</summary>
    public event EventHandler? Changed;

    public void Attach(ISecretInput? target) => _target = target;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public int Length => _target?.Length ?? 0;

    public byte[] ReadUtf8() => _target?.ReadUtf8() ?? [];

    public void SetUtf8(ReadOnlySpan<byte> utf8)
    {
        _target?.SetUtf8(utf8);
        RaiseChanged();
    }

    public void Clear()
    {
        _target?.Clear();
        RaiseChanged();
    }

    public void Focus() => _target?.Focus();
}
