using System.Text;
using PwManager.Presentation.Ports;
using PwManager.Presentation.ViewModels;

namespace PwManager.Application.Tests.Support;

/// <summary>PasswordBox の代わり。</summary>
internal sealed class FakeSecretBox : ISecretInput
{
    public string Text { get; set; } = string.Empty;
    public int FocusCount { get; private set; }

    public int Length => Text.Length;

    public byte[] ReadUtf8() => Encoding.UTF8.GetBytes(Text);

    public void SetUtf8(ReadOnlySpan<byte> utf8) => Text = Encoding.UTF8.GetString(utf8);

    public void Clear() => Text = string.Empty;

    public void Focus() => FocusCount++;

    /// <summary>SecretInput にこの入力欄をつなぎ、内容を入れる。</summary>
    public static FakeSecretBox Attach(SecretInput input, string text = "")
    {
        var box = new FakeSecretBox { Text = text };
        input.Attach(box);
        return box;
    }
}

/// <summary>
/// 利用者の応答を順に返すダイアログ。呼ばれた内容は Calls に記録する。応答が足りなければテストを失敗させる。
/// </summary>
internal sealed class ScriptedDialogs : IDialogService
{
    private readonly Queue<object?> _answers = new();

    public List<string> Calls { get; } = [];
    public int CloseAllCount { get; private set; }

    /// <summary>次の応答（Confirm: bool、Choose: int、PromptText/ShowRecoveryCode の入力: string、PromptSecrets: string[]、ファイル: string?）。</summary>
    public ScriptedDialogs Then(object? answer)
    {
        _answers.Enqueue(answer);
        return this;
    }

    private T Next<T>(string call)
    {
        Calls.Add(call);
        Assert.True(_answers.Count > 0, $"ダイアログ「{call}」への応答が用意されていません");
        return (T)_answers.Dequeue()!;
    }

    public bool Confirm(string title, string message, string okText = "OK") => Next<bool>($"Confirm:{title}");

    public int Choose(string title, string message, int cancelIndex, params string[] options) => Next<int>($"Choose:{title}");

    public void Info(string title, string message) => Calls.Add($"Info:{title}:{message}");

    public (bool Ok, bool Option) ConfirmWithOption(string title, string message, string okText, string optionText, bool optionDefault)
    {
        Calls.Add($"ConfirmWithOption:{title}:{message}");
        var a = (bool[])_answers.Dequeue()!;
        return (a[0], a[1]);
    }

    public string? PromptText(string title, string message, string label, Func<string, string?>? validate = null)
    {
        var text = Next<string?>($"PromptText:{title}");
        if (text is not null && validate?.Invoke(text) is { } err) { Calls.Add($"ValidationError:{err}"); return null; }
        return text;
    }

    public byte[][]? PromptSecrets(string title, string message, IReadOnlyList<string> labels, Func<IReadOnlyList<byte[]>, string?>? validate = null)
    {
        var values = Next<string[]?>($"PromptSecrets:{title}");
        if (values is null) return null;
        var bytes = values.Select(Encoding.UTF8.GetBytes).ToArray();
        if (validate?.Invoke(bytes) is { } err) { Calls.Add($"ValidationError:{err}"); return null; }
        return bytes;
    }

    public bool ShowRecoveryCode(string title, string message, string code, string checkLabel, Func<string, string?> validate)
    {
        Calls.Add($"ShowRecoveryCode:{title}");
        LastRecoveryCode = code;
        LastCheckLabel = checkLabel;
        var answer = (Func<string, string>)_answers.Dequeue()!; // 表示されたコードから入力を作る
        while (true)
        {
            var input = answer(code);
            if (input == "<cancel>") return false;
            var err = validate(input);
            if (err is null) return true;
            Calls.Add($"ValidationError:{err}");
            answer = (Func<string, string>)_answers.Dequeue()!;
        }
    }

    public string? LastRecoveryCode { get; private set; }
    public string? LastCheckLabel { get; private set; }

    public string? PickOpenFile(string title, string filter, string? initialPath = null) => Next<string?>($"PickOpenFile:{title}");

    public string? PickSaveFile(string title, string defaultFileName, string filter) => Next<string?>($"PickSaveFile:{title}");

    public bool PrintKeyfileQr(string keyfileText, Guid vaultId)
    {
        Calls.Add("PrintKeyfileQr");
        return true;
    }

    public Action<KeysViewModel>? OnShowKeys { get; set; }

    public void ShowKeys(KeysViewModel keys)
    {
        Calls.Add("ShowKeys");
        OnShowKeys?.Invoke(keys);
    }

    public void CloseAll() => CloseAllCount++;
}

internal sealed class RecordingShell : IShellService
{
    public List<string> Opened { get; } = [];

    public void OpenUrl(Uri url) => Opened.Add(url.AbsoluteUri);

    public void OpenFolder(string path) => Opened.Add(path);
}
