using PwManager.Application.Diagnostics;
using PwManager.Application.Infrastructure;
using PwManager.Application.Services;
using PwManager.Application.Session;
using PwManager.Application.Tests.Support;
using PwManager.Cli;
using PwManager.Core.Crypto;

namespace PwManager.Application.Tests;

/// <summary>診断用 CLI `pwm`（R-18）。秘密を表示しないこと、壊れたファイルでも落ちないこと。</summary>
public sealed class CliTests : IDisposable
{
    private readonly TestWorld _world = new();

    public void Dispose() => _world.Dispose();

    private (int Code, string Out, string Err) Run(params string[] args) => Run(null, args);

    private (int Code, string Out, string Err) Run(FakeTpm? tpm, params string[] args)
    {
        using var o = new StringWriter();
        using var e = new StringWriter();
        var code = Program.Run(args, o, e, tpm, new AppDataPaths(Path.Combine(_world.Root, "pc1")));
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task diag_vault_スロット一覧を表示し_秘密は表示しない()
    {
        var pc = _world.NewPc();
        var secrets = await pc.CompleteSetupAsync(_world.Secret("Pk7x9q"), _world.Secret("correct horse battery"));
        await pc.AddEntryAsync("ネット銀行", "taro-bank-user", "bank-secret-01");
        var slots = pc.Session.Slots.ToList();
        var vaultId = pc.Session.VaultId;
        pc.Session.RequestLock(LockReason.Exit);

        var (code, output, _) = Run(pc.Tpm, "diag", "vault", _world.VaultPath);
        Assert.Equal(0, code);
        Assert.Contains(vaultId.ToString(), output, StringComparison.Ordinal);
        Assert.Contains("解錠方法: 3 件", output, StringComparison.Ordinal);
        Assert.Contains("方針（復旧コード＋他 1 つ以上）: OK", output, StringComparison.Ordinal);
        Assert.Contains("この PC", output, StringComparison.Ordinal);
        Assert.Contains("MAC の検証", output, StringComparison.Ordinal);
        foreach (var s in slots) Assert.Contains(s.Id.ToString(), output, StringComparison.Ordinal);
        foreach (var secret in new[] { secrets.RecoveryCode, secrets.KeyfileText, "bank-secret-01", "ネット銀行", "taro-bank-user", "Pk7x9q" })
            Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        foreach (var s in slots) Assert.DoesNotContain(Convert.ToHexString(s.Salt), output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void diag_vault_壊れた_新しい版_ないファイル()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_world.VaultPath)!);
        File.WriteAllBytes(_world.VaultPath, [1, 2, 3]);
        Assert.Contains("壊れている可能性", Run("diag", "vault", _world.VaultPath).Out, StringComparison.Ordinal);
        File.WriteAllBytes(_world.VaultPath, [.. "PWMV"u8, 7, 0, 0, 0, 0, 0]);
        Assert.Contains("新しい版", Run("diag", "vault", _world.VaultPath).Out, StringComparison.Ordinal);
        Assert.Contains("見つかりません", Run("diag", "vault", Path.Combine(_world.Root, "none.pwmv")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task backups_世代と保存回数を一覧する()
    {
        var pc = _world.NewPc();
        await pc.CompleteSetupAsync(_world.Secret("Pk7x9q"), _world.Secret("correct horse battery"));
        await pc.AddEntryAsync("a", "u", "backup-secret-1");
        await pc.AddEntryAsync("b", "u", "backup-secret-2");
        var (code, output, _) = Run("backups", _world.VaultPath);
        Assert.Equal(0, code);
        Assert.Contains("現在  保存回数 3", output, StringComparison.Ordinal);
        Assert.Contains(".bak1  保存回数 2", output, StringComparison.Ordinal);
        Assert.Contains(".bak2  保存回数 1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task logs_警告とエラーを種類ごとに数える()
    {
        var pc = _world.NewPc();
        await pc.CompleteSetupAsync(_world.Secret("Pk7x9q"), _world.Secret("correct horse battery"));
        pc.Session.RequestLock(LockReason.Manual);
        await pc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B("wrong-pin-1")));
        await pc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B("wrong-pin-2")));
        var summary = VaultDiagnostics.SummarizeLogs(pc.LogDirectory, 7, _world.Time);
        Assert.Contains("2 件  Warning 2201 UnlockFailed (WrongPin)", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void diag_tpm_とbench_と使い方()
    {
        Assert.Contains("Windows でのみ", Run("diag", "tpm").Out, StringComparison.Ordinal);
        Assert.Contains("利用できます", Run(new FakeTpm(), "diag", "tpm").Out, StringComparison.Ordinal);
        Assert.Contains("利用できません", Run(new FakeTpm { Available = false }, "diag", "tpm").Out, StringComparison.Ordinal);
        Assert.Contains("ms", VaultDiagnostics.BenchArgon2([("Test", new KdfParameters(8 * 1024, 1))]), StringComparison.Ordinal);

        var (code, _, err) = Run("unknown");
        Assert.Equal(2, code);
        Assert.Contains("使い方", err, StringComparison.Ordinal);
        Assert.Equal(2, Run("logs", "--days", "-1").Code);
    }
}
