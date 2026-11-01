# PoC（技術検証）

本番コードに入れる前に、実機でしか確かめられない部分を検証する小さなプログラム。

## TpmSealPoc（P0-1b / ADR-0004）

TPM の「シール」で秘密を封印・取り出しできるか、PIN 間違いが TPM の総当たり対策の対象になるかを確認する。

```powershell
# リポジトリのルートで（管理者権限は不要）
dotnet run --project poc\TpmSealPoc -c Release

# PC を再起動した後に、保存した封印データが取り出せるか確認
dotnet run --project poc\TpmSealPoc -c Release -- verify-saved
```

- 間違った PIN の試行は 1 回だけ（TPM の失敗カウンタは時間で自然に減る）。行いたくない場合は `-- --no-da-test`。
- 結果はカレントフォルダの `tpm-seal-poc-result.md` に出力される。
- 封印テスト用データは `%LOCALAPPDATA%\PwManagerPoc\sealed-test.bin` に保存される（PoC 用の乱数で、実データではない）。
