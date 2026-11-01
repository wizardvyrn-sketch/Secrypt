# 09. 構造検討書（リファクタリング計画）

- 対象: PR #3 時点のコード（`feat/tpm-slot-and-app`、2026-10-25）
- 観点: 拡張性・保守性・デバッグ性、UI と処理の分離
- 結論: Core（暗号・形式・スロット）は分離できているが、**アプリの業務フローがコードビハインドに集まっている**。
  間に「Application 層」を新設して画面から処理を切り離し、MVVM・ログ・診断ツールを順に入れる。
  あわせて、レビューで見つかった **不具合につながる問題 4 件（R-02, R-03, R-04, R-15）** を先に直す。

---

## 1. 現状の構造

### 1.1 プロジェクトと依存関係

```
PwManager.App (WPF, net10.0-windows)          … 画面 + 業務フロー + 状態保持（AppState）
   ├─→ PwManager.Platform.Windows              … TPM シール（TSS.MSR）
   │      └─→ PwManager.Core
   └─→ PwManager.Core (net10.0, OS 非依存)     … 暗号・Vault 形式・スロット・生成・取り込み・保存
tests/PwManager.Core.Tests                     … 40 件（Linux / Windows CI）
tests/PwManager.Platform.Windows.Tests         … 4 件（うち 3 件は実機 TPM）
poc/TpmSealPoc                                 … 技術検証
```

### 1.2 規模（行数）

| 層 | ファイル | 行数 | テスト |
|---|---|---|---|
| Core | 19 | 約 1,500 | あり（40 件） |
| Platform.Windows | 1 | 212 | あり（4 件） |
| App（C#） | 12 | 約 1,600 | **なし** |
| App（XAML） | 6 | 約 880 | — |

App のコードビハインド上位: `VaultView.xaml.cs` 366 行、`SetupView.xaml.cs` 258 行、`KeysWindow.xaml.cs` 203 行、`UnlockView.xaml.cs` 191 行。
この 4 ファイルに、スロット登録・解錠の試行・鍵ファイルの書き出し・取り込みの重複判定・入力規則などの **業務ロジックが入っている**。

### 1.3 良い点（維持する）

- Core が OS・UI に依存せず、暗号・形式・スロット・ポリシーが単体テストされている。
- 秘密は `SecretBuffer` に置き、ゼロ化の方針が一貫している。
- TPM は `ITpmSealer` で抽象化され、フェイクでテストできる（FIDO2 も同様）。
- 保存はアトミック書き込み＋世代バックアップ。

---

## 2. 指摘事項

重要度: **高** = 不具合・データ消失・設計の根幹 / **中** = 変更や追加のたびに手間・事故の元 / **低** = 整理

### A. UI と処理の分離

| ID | 重要度 | 問題 | 根拠 | 改善案 |
|---|---|---|---|---|
| R-01 | 高 | 業務フローがコードビハインドに集中。画面を変えると処理も壊れやすく、テストできない | `SetupView.xaml.cs`（スロット登録 3 種・鍵ファイル書き出し・復旧コード確認）、`UnlockView.xaml.cs`（解錠方法の分岐・復旧スロットの総当たり・失敗時の待ち時間）、`VaultView.xaml.cs`（取り込みの重複判定・CSV 削除）、`KeysWindow.xaml.cs`（スロットの置き換え） | **Application 層**（§3）のユースケースに移す。画面は入力を集めて結果を表示するだけにする |
| R-02 | **高（不具合）** | 背景スレッドで Vault を変更しており、**自動ロックと競合**する。処理中に 5 分経過・画面ロック・スリープが起きると、Vault が破棄された後に書き込みが走る | `KeysWindow.xaml.cs:91-101`（`Task.Run` 内で `Vault.AddSlot`）、`MainWindow.xaml.cs` の自動ロック（処理中かどうかを見ない） | `VaultSession` に **操作ゲート**を設け、実行中の自動ロックは「終わり次第ロック」に保留する。原則「重い計算（Argon2id・TPM）だけ背景、状態変更は UI スレッド／ゲート内」 |
| R-03 | **高（不具合）** | **編集中の内容が警告なく失われる**（別の項目を選ぶ・新規・ロック時） | `VaultView.xaml.cs` の `EntryList_SelectionChanged` → `ShowEntry` が入力欄を上書き | 編集用の `EntryEditorViewModel` に `IsDirty` を持たせ、離れる前に「保存／破棄／戻る」を確認。自動ロック時は破棄する旨を仕様に明記 |
| R-04 | 中（不具合の芽） | 保存に失敗すると **メモリとファイルが食い違う**。削除は「メモリから削除 → 保存失敗」でもメモリでは消えたまま、次の保存で確定してしまう。保存回数（counter）も書き込み前に増える | `VaultView.xaml.cs` の `Delete_Click`、`PasswordVault.Serialize()` の `Counter++` | 変更をコマンド化し「適用 → 保存成功で確定／失敗で元に戻す」。counter は書き込み成功後に確定 |
| R-05 | 中 | 入力規則・正規化が重複 | PIN 6 文字: `SetupView:77`・`KeysWindow:121` / パスワード 10 文字: `SetupView:136`・`KeysWindow:75` / 復旧コードの O→0 等: `SetupView:217`・`KeysWindow:160`（`CrockfordBase32` とも重複） | Core に `CredentialPolicy`（最小長・確認一致）と `RecoveryCode.MatchesGroup()` を置き、全画面で共有 |
| R-06 | 中 | 画面が `MainWindow` に直接依存（`RunBusyAsync`・`Navigate`・`ShowVault`・`ThemedDialog`） | 各 View のコンストラクタが `MainWindow` を受け取る | `INavigationService`・`IBusyService`・`IDialogService` に置き換え、ViewModel から使う |
| R-07 | 中 | `AppState` の責務が多い（パス・設定・TPM・解錠中の Vault・保存・ロック） | `Services/AppState.cs` | `VaultSession`（状態）・`IVaultStore`（ファイル）・`ISettingsStore`（設定）に分割 |
| R-08 | 中 | 静的な可変状態。差し替え・テスト・並列実行の障害 | `SecureClipboard`（static タイマー・ハッシュ）、`FeatureFlags`（static）、`WindowsTpmSealer` の static ロック | インターフェース化して DI で注入（`IClipboardService`、`AppOptions`、`ITpmSealer` は単一インスタンス登録） |

### B. 拡張性

| ID | 重要度 | 問題 | 根拠 | 改善案 |
|---|---|---|---|---|
| R-09 | 中 | スロット種別ごとに静的クラス・引数がばらばらで、UI も種別ごとに作り込み。FIDO2・パスキーを足すと画面 3 か所の修正が必要 | `KeyfileSlot.DeriveKek(slot, keyfile, pw)` / `TpmSlot.DeriveKek(slot, sealer, pin)` / `RecoverySlot.DeriveKek(slot, code)`、`UnlockView` のタブ固定、`KeysWindow` のボタン固定 | `ISlotProvider`（種別・表示名・必要な入力・登録・解錠）＋レジストリ。画面はプロバイダの「必要な入力」から組み立てる。新しい方式＝プロバイダ追加＋入力部品だけ |
| R-10 | 中 | 本体（エントリ）が固定順のバイナリ。項目追加（TOTP・任意項目・変更履歴）のたびに形式が変わり、古い版で開くと読めない | `EntrySerializer`（フィールド順に書く v1） | **タグ付きフィールド（TLV）**にし、知らないタグは読み飛ばして保持（前方互換）。`IVaultMigration` で v1→v2 を自動変換 |
| R-11 | 低 | 画面の文言がコードに散在（C# に約 145 か所、XAML に約 85 か所） | 各 `.xaml.cs` と `.xaml` の文字列 | `Strings.resx` に集約（文言の統一・見直しが容易に） |
| R-12 | 低 | テーマ定義が 1 ファイルに集中（469 行） | `App.xaml` | `Themes/Colors.xaml`・`Themes/Controls.xaml` に分割。FocusCoach と同じ定義を共有しやすくする |

### C. 保守性・テストのしやすさ

| ID | 重要度 | 問題 | 根拠 | 改善案 |
|---|---|---|---|---|
| R-13 | 高 | App 層（C# 約 1,600 行）にテストがない。CI は App をビルドするだけ | `tests/` に App 向けがない | Application 層（net10.0）に移したロジックは Linux CI でテスト。ViewModel もテスト対象にする |
| R-14 | 中 | 想定内の失敗（PIN 違い・鍵違い）を例外で表現。呼び出し側ごとに catch と文言変換が必要 | `UnlockErrors.Describe`、各画面の `catch (Exception)`（12 か所） | 解錠は `UnlockResult`（成功／鍵違い／PIN 違い／ロック中／TPM 不可／改ざん／形式不正）を返す。文言の対応表は App に 1 か所 |
| R-15 | **中（不具合）** | **解錠のたびに保存**している（最終使用日の記録のため）。保存のたびにバックアップが回るので、**5 回解錠すると変更前のバックアップがすべて押し出される** | `UnlockView.xaml.cs:167` | 最終使用日は「日付が変わったときだけ」記録し、その保存ではバックアップを回さない（バックアップは内容の変更時のみ） |
| R-16 | 中 | 多重起動・外部変更の検知がない。2 つ起動して両方で保存すると後勝ちで片方の変更が消える | 単一インスタンス制御なし、保存前の確認なし | 単一インスタンス（名前付き Mutex）。保存前にディスク上の counter を確認し、読み込み時と違えば上書きしない |

### D. デバッグ性

| ID | 重要度 | 問題 | 根拠 | 改善案 |
|---|---|---|---|---|
| R-17 | 高 | **ログが一切ない**。利用者から「開けない」と言われても原因（PIN 違い／TPM クリア／ファイル破損）を後から追えない | — | 構造化ログ（秘密を書かない規則付き）。詳細は [10. デバッグ・ログ仕様](10-debug-logging.md) |
| R-18 | 中 | 診断手段がない（ヘッダ・スロット・TPM 状態を見るにはデバッガが必要） | — | CLI `pwm`（`diag vault` / `diag tpm` / `bench argon2` / `backups`）。FocusCoach の `fc diag` と同じ考え方 |
| R-19 | 中 | 未処理例外はメッセージを出すだけで、状態（解錠中のまま等）や原因が残らない | `App.xaml.cs` の `DispatcherUnhandledException` | ログに記録 → ロック → 案内（ログフォルダを開く）の順で処理 |
| R-20 | 低 | 開発用の設定（キャプチャ許可）が環境変数で散在 | `MainWindow.OnSourceInitialized` | 開発用設定を `DevOptions` に集約し、Release では無効であることをテストで保証 |

---

## 3. 目標構成

### 3.1 層と依存の向き

```
┌────────────────────────────────────────────┐   ┌──────────────────────┐
│ PwManager.App (WPF)                        │   │ PwManager.Cli (pwm)  │
│  Views（XAML・最小限のコードビハインド）     │   │  diag / bench        │
│  ViewModels（CommunityToolkit.Mvvm）        │   └──────────┬───────────┘
└──────────────────┬─────────────────────────┘              │
                   ▼                                         ▼
┌──────────────────────────────────────────────────────────────────────┐
│ PwManager.Application (net10.0, OS 非依存) … 新設                        │
│  VaultSession（状態・操作ゲート・自動ロック保留）                         │
│  ユースケース: Setup / Unlock / Entry / Slot / Import                     │
│  ポリシー: CredentialPolicy / AutoLockPolicy / UnlockBackoffPolicy        │
│  ポート（インターフェース）: IVaultStore / ISettingsStore / IKeyfileStore │
│    IClipboardService / ISystemEvents / IClock / ITpmSealer(Core) / ILogger │
│  結果型: Result<T> / UnlockResult / ErrorCode                            │
└──────────────────┬──────────────────────────────────▲─────────────────┘
                   ▼                                  │ 実装
┌──────────────────────────────┐   ┌───────────────────┴──────────────────┐
│ PwManager.Core               │◀──│ PwManager.Platform.Windows            │
│  Crypto / Format / Domain     │   │  WindowsTpmSealer / WindowsClipboard  │
│  Slots / Policies / Import    │   │  WindowsSystemEvents / FileVaultStore │
└──────────────────────────────┘   │  JsonlFileLogger                      │
                                   └──────────────────────────────────────┘
```

規則:
- App → Application → Core の一方向。Platform は Application のポートを実装する。
- **Application と Core は OS・UI に依存しない**（Linux CI でテストできる）。
- 画面は ViewModel にだけ依存し、ViewModel は Application のサービスにだけ依存する。

### 3.2 Application 層の主要部品

| 部品 | 役割 |
|---|---|
| `VaultSession` | 状態（Locked / Unlocked / Busy）と `StateChanged` イベント。解錠中の `PasswordVault` を唯一保持。`RunExclusiveAsync()` で操作を直列化し、実行中の自動ロック要求は終了後に実行 |
| `UnlockService` | `TryUnlockAsync(UnlockRequest) → UnlockResult`。方式ごとの処理はスロットプロバイダへ委譲。連続失敗の待ち時間（`UnlockBackoffPolicy`） |
| `SetupService` | 初期設定の手順（TPM → 鍵ファイル → 復旧コード → 完了）。途中離脱時の破棄 |
| `EntryService` | 作成・更新・削除。保存成功で確定、失敗で元に戻す（R-04） |
| `SlotService` | 鍵ファイル追加・TPM 置き換え・復旧コード再発行・削除。スロットポリシーの事前確認 |
| `ImportService` | CSV 解析 → プレビュー（重複・スキップ件数）→ 確定 → CSV 削除 |
| `IVaultStore` | 読み込み（counter を記憶）・保存（counter 不一致なら拒否、R-16）・バックアップ一覧。保存理由（内容変更／メタ情報のみ）でバックアップの要否を切り替え（R-15） |
| `ISlotProvider` | 種別・表示名・必要な入力（PIN／パスワード／鍵ファイル／復旧コード／デバイス操作）・登録・KEK 導出（R-09） |

### 3.3 ViewModel 構成

| ViewModel | 画面 | 主な状態・コマンド |
|---|---|---|
| `ShellViewModel` | MainWindow | 現在の画面、状態表示（ロック中／解錠中）、ロック |
| `SetupViewModel` | 初期設定 | Step、TPM 可否、入力検証、`NextCommand` |
| `UnlockViewModel` | 解錠 | 利用できる方式（プロバイダから）、`UnlockCommand`、待ち時間 |
| `VaultViewModel` + `EntryListViewModel` + `EntryEditorViewModel` | 一覧・詳細 | 検索、選択、`IsDirty`（R-03）、保存・削除・生成・コピー |
| `KeysViewModel` | 鍵と設定 | スロット一覧、追加・削除、設定値 |

パスワード入力（`PasswordBox`）は `string` にしない方針を維持するため、ビヘイビアでコマンド実行時に UTF-8 バイト列として ViewModel に渡す（バインドしない）。

---

## 4. リファクタリング計画

1 段階 = 1 PR。各段階で既存テストを通し、画面の挙動は（不具合修正を除き）変えない。

| 段階 | 内容 | 解消する指摘 | 完了条件 |
|---|---|---|---|
| **R0 先行修正** | 不具合につながる 3 件を最小修正（自動ロックを処理中は保留、未保存の確認ダイアログ、解錠時の保存でバックアップを回さない） | R-02, R-03, R-15 | 再現手順のテスト（自動ロック中の追加、選択切り替え、5 回解錠）が通る |
| R1 基盤 | ログ（JSONL・秘密を書かない）、単一インスタンス、未処理例外の処理、`PwManager.Application` と `Application.Tests` の作成 | R-17, R-19, R-16（多重起動） | ログのカナリアテスト（秘密が出力されない）が通る |
| R2 状態の分離 | `VaultSession`・`IVaultStore`（counter 確認）・`ISettingsStore`・`IClipboardService`、DI 導入、`AppState` 廃止 | R-02, R-04, R-07, R-08, R-16 | App から `PasswordVault` を直接触る箇所がなくなる |
| R3 ユースケース移行 | Unlock → Setup → Slot → Entry → Import の順に移し、規則を `CredentialPolicy` へ集約、結果型を導入 | R-01, R-05, R-14 | 各ユースケースのテストが Linux CI で通る。コードビハインドから `Core` の直接呼び出しが消える |
| R4 MVVM 化 | ViewModel・ナビゲーション／ダイアログ／Busy サービス、`IsDirty` の本実装 | R-03, R-06, R-13 | ViewModel テストあり。View のコードビハインドは表示専用（目安 50 行以下） |
| R5 拡張基盤 | `ISlotProvider` とレジストリ、本体形式 v2（TLV）と v1 からの自動移行 | R-09, R-10 | FIDO2（保留機能）がプロバイダ追加だけで画面に出る。v1 ファイルを開くと v2 で保存される |
| R6 診断・整理 | CLI `pwm`、設定画面の診断情報、文言リソース化、テーマ分割、`DevOptions` | R-11, R-12, R-18, R-20 | `pwm diag vault` で秘密なしの診断が出る |

### 4.1 リスクと対策

| リスク | 対策 |
|---|---|
| 移行中に暗号・形式の挙動が変わる | Core は R5 まで触らない。R5 の形式変更は v1 読み込みテストと往復テストを先に書く |
| 画面の挙動が知らないうちに変わる | 段階ごとに `docs/test/` の実機確認表を更新し、主要操作を手で確認する |
| MVVM で `PasswordBox` の秘密が `string` に漏れる | ビヘイビア経由のバイト列受け渡しに限定し、`Password` プロパティへのバインドを禁止（コードレビュー項目） |
| 1 回の変更が大きくなる | 1 段階 1 PR、ユースケースは 1 つずつ移す |

---

## 5. 決めていただきたいこと

2026-10-25: **D-1〜D-5 はすべて推奨案で決定**。

| # | 内容 | 推奨（＝決定） |
|---|---|---|
| D-1 | R0（不具合 3 件の先行修正）をリファクタリングより先に行うか | **先に行う**（小さく、効果が大きい） |
| D-2 | MVVM ライブラリ | **CommunityToolkit.Mvvm**（Microsoft 製、ソース生成で記述が少ない） |
| D-3 | DI コンテナ | **Microsoft.Extensions.DependencyInjection**（標準的、ログと統合しやすい） |
| D-4 | ログに Vault のファイルパスを書くか | **ユーザー名部分を伏せて書く**（`%USERPROFILE%\…`） |
| D-5 | 本体形式 v2（TLV）を MVP 後すぐに入れるか | **R5 で入れる**（TOTP 等を足す前に） |

---

## 6. 実施状況（2026-10-25）

詳細と動作確認の結果は [デバッグ結果書](test/2026-10-25-debug-report.md)。

| 指摘 | 状況 | 実装 |
|---|---|---|
| R-01 | 済 | 業務フローを `PwManager.Application` のサービス（Setup / Unlock / Slot / Entry / Import）に移した |
| R-02 | 済 | `VaultSession.RunExclusiveAsync` で操作を直列化し、実行中のロック要求は完了後に実行 |
| R-03 | 済 | `EntryEditorViewModel.IsDirty` と「保存／破棄／戻る」。自動ロックでは破棄してログに記録 |
| R-04 | 済 | `VaultCheckpoint` で変更前に戻す。counter は書き込み成功後に確定（`PrepareSave` / `CommitSave`） |
| R-05 | 済 | `CredentialPolicy`、`RecoveryCode.MatchesGroup` |
| R-06 | 済 | `IDialogService`・`IShellService`。処理中の表示は VaultSession の BusyChanged から |
| R-07 | 済 | `VaultSession`・`IVaultStore`・`ISettingsStore`（`SettingsService`）に分割、`AppState` 廃止 |
| R-08 | 済 | `IClipboardService`（DI）、`ITpmSealer` は単一インスタンス登録。`FeatureFlags` は保留機能の切り替え用として残す |
| R-09 | 未 | `ISlotProvider` は未実施（FIDO2 は保留機能のため後回し） |
| R-10 | 済 | 本体形式 v2（タグ付きフィールド、知らない項目を保持）、v1 は自動移行 |
| R-11 | 一部 | 失敗時の文言は `UserMessages` に集約。画面固有の文言の resx 化は未実施 |
| R-12 | 済 | `Themes/Colors.xaml`・`Themes/Controls.xaml` |
| R-13 | 済 | Application・ViewModel・CLI のテスト 85 件（Linux） |
| R-14 | 済 | `ErrorCode`・`OpResult`・`UnlockResult` |
| R-15 | 済 | 最終使用日は日付が変わったときだけ、バックアップを回さず counter も進めずに記録 |
| R-16 | 済 | Vault ごとの名前付き Mutex、保存前の counter 照合（別の場所で更新されていれば上書きしない） |
| R-17 | 済 | 構造化ログ（docs/10）、全テストでのカナリア検査 |
| R-18 | 済 | CLI `pwm`、「鍵と設定」の診断情報とログフォルダを開くボタン |
| R-19 | 済 | 未処理例外を記録 → ロック → 案内 |
| R-20 | 済 | `DevOptions`（キャプチャ許可は Debug ビルドのみ） |

構造検討書の作成後に分かった不具合（デバッグ結果書 §5 の B-05〜B-19）も修正した。主なもの:
ロック後に復旧コードの表示が残る、2 台目の PC で TPM を登録すると 1 台目の TPM スロットが消える、
解錠しただけで counter が進み別の PC で保存できなくなる、取り込みでパスワードの変わった項目を捨てていた。

### 6.1 実装後の構成

```
PwManager.App (WPF)            Views（XAML・表示専用のコードビハインド）、WPF 版のダイアログ・クリップボード、多重起動の防止
  └─→ PwManager.Presentation   ViewModel（CommunityToolkit.Mvvm）、ポート（IDialogService・IShellService・ISecretInput）、依存の組み立て
        └─→ PwManager.Application  VaultSession、ユースケース、ポリシー、ポート、結果型、ログ、診断、ファイルの実装
              └─→ PwManager.Core   暗号・形式・スロット
PwManager.Cli (pwm) ─→ Application（Windows 版は Platform.Windows も）
PwManager.Platform.Windows ─→ Core（ITpmSealer の実装）
```

計画（§3.1）との違い: ViewModel を App ではなく `PwManager.Presentation`（net10.0）に置いた（Linux の CI でテストするため）。
ファイル・ログの実装（`FileVaultStore`・`JsonlFileLoggerProvider` など）は OS に依存しないため Platform.Windows ではなく Application に置いた。

