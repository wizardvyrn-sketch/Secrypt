# ADR-0006: Application 層を新設し、画面は MVVM にする

- Status: Accepted（2026-10-25、D-2・D-3 は推奨案で決定）
- Date: 2026-10-25

## Context
MVP では業務フロー（スロット登録・解錠の試行・取り込み・保存）が WPF のコードビハインドに入っており、
テストできない・画面を変えると処理が壊れやすい・背景処理と自動ロックが競合する、などの問題がある（R-01, R-02, R-13）。

## Decision
- OS・UI に依存しない `PwManager.Application`（net10.0）を新設し、`VaultSession`・ユースケース・ポリシー・ポート（インターフェース）を置く。
- 画面は CommunityToolkit.Mvvm による ViewModel から Application のサービスだけを呼ぶ。
- 依存は Microsoft.Extensions.DependencyInjection で `App.xaml.cs` にまとめて組み立てる。
- `PasswordBox` の内容は `string` にせず、ビヘイビア経由で UTF-8 バイト列として ViewModel に渡す。

## Consequences
- ＋ 処理の大半が Linux CI でテストできる。CLI（`pwm`）からも同じ処理を使える。
- ＋ 自動ロックと操作の競合を `VaultSession` の操作ゲートで一元管理できる。
- − プロジェクトとファイルが増える。段階的に移す（R1〜R4）。

## 実装（2026-10-25）
- ViewModel は WPF に依存しない `PwManager.Presentation`（net10.0）に置き、Linux の CI でテストする。
- 依存の組み立ては `Composition.AddPwManager`（Presentation）にまとめ、App は WPF・TPM の実装だけを足す。
- 詳細は [09 §6](../09-structure-review.md) と [デバッグ結果書](../test/2026-10-25-debug-report.md)。
