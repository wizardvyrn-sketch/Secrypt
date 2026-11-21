using PwManager.Application.Policies;

namespace PwManager.Application.Tests;

/// <summary>PIN の失敗を窓（8 時間）で数え、Windows に TPM をブロックされる前に止める規則。</summary>
public sealed class PinAttemptPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 20, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void 窓の外の失敗は数えず_新しいものだけ古い順に返す()
    {
        var recent = PinAttemptPolicy.Recent(
            [Now.AddMinutes(-10), Now - PinAttemptPolicy.Window, Now.AddMinutes(-481), Now.AddMinutes(-479), Now.AddMinutes(-100)], Now);
        Assert.Equal([Now.AddMinutes(-479), Now.AddMinutes(-100), Now.AddMinutes(-10)], recent); // ちょうど窓の長さの失敗は外れる
    }

    [Fact]
    public void 未来の記録_時計が戻った_はいまの失敗として数える()
    {
        var recent = PinAttemptPolicy.Recent([Now.AddDays(3)], Now);
        Assert.Equal([Now], recent);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(5, 0)]
    public void 残り回数(int failures, int left)
        => Assert.Equal(left, PinAttemptPolicy.AttemptsLeft(Enumerable.Repeat(Now, failures).ToList()));

    [Fact]
    public void 上限に達するまでは再開時刻なし_達したら上限を下回る時刻()
    {
        var t1 = Now.AddMinutes(-30);
        var t2 = Now.AddMinutes(-20);
        var t3 = Now.AddMinutes(-10);
        Assert.Null(PinAttemptPolicy.ResumeAt([t1, t2]));
        Assert.Equal(t1 + PinAttemptPolicy.Window, PinAttemptPolicy.ResumeAt([t1, t2, t3]));

        // 上限を超えている記録（手で編集された設定など）でも、「上限を下回る」時刻を返す
        // （4 件のうち古い 2 件が窓から外れて、残りが 2 件になる時刻 = 2 件目の失敗 + 窓）
        var t0 = Now.AddMinutes(-40);
        Assert.Equal(t1 + PinAttemptPolicy.Window, PinAttemptPolicy.ResumeAt([t0, t1, t2, t3]));
    }

    [Fact]
    public void 設定の失敗の記録は_読み書きで共有されず_上限を超えて溜め込まない()
    {
        var a = new Ports.AppSettings { PinFailures = [.. Enumerable.Range(0, 50).Select(i => Now.AddMinutes(-i))] }.Normalize();
        Assert.Equal(PinAttemptPolicy.MaxFailuresInWindow * 2, a.PinFailures.Count);
        Assert.Equal(Now, a.PinFailures[^1]); // 新しいものを残す

        var b = a.Clone();
        b.PinFailures.Clear();
        Assert.NotEmpty(a.PinFailures); // 複製が元の一覧を変えない
    }
}
