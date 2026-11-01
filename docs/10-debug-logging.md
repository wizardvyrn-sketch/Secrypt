# 10. デバッグ・ログ仕様

構造検討書の指摘 R-17〜R-20 を受けた仕様。
目的は「利用者の PC で起きた問題（開けない・保存できない・TPM エラー）を、**秘密を一切残さずに**後から追えるようにする」こと。

## 1. 基本方針

1. **秘密はログに書かない**（§3）。迷ったら書かない。
2. ログは「何が・いつ・どの結果で起きたか」を、決まった項目（構造化）で残す。自由文に利用者データを混ぜない。
3. 1 つの操作（解錠・保存・スロット追加など）に操作 ID を振り、開始から結果までを追えるようにする。
4. 外部送信はしない（テレメトリなし）。ログはローカルにのみ置く。

## 2. 出力

| 項目 | 仕様 |
|---|---|
| ライブラリ | `Microsoft.Extensions.Logging`（抽象）＋自前の JSONL ファイル出力（依存を増やさない） |
| 場所 | `%LOCALAPPDATA%\NewPWManageTool\logs\pwm-YYYYMMDD.jsonl` |
| 形式 | 1 行 1 JSON: `{"t":"2026-10-25T17:33:01.123+09:00","lv":"Info","ev":2101,"name":"UnlockSucceeded","op":"7f3a…","fields":{…}}` |
| 保持 | 14 日分。1 ファイル 5 MB を超えたら連番で分割 |
| レベル | 既定 `Information`。設定画面の「詳細ログ」で `Debug` |
| 書き込み | 失敗してもアプリは止めない（ログは補助） |

## 3. 記録してよいもの・いけないもの

| 区分 | 例 | 扱い |
|---|---|---|
| **秘密** | パスワード、PIN、復旧コード、キーファイルの中身・テキスト、KEK / VMK、TPM authValue、クリップボードの内容 | **絶対に記録しない** |
| **利用者データ** | 項目のタイトル・ユーザー名・URL・メモ・タグ、取り込んだ CSV の内容 | **記録しない**（件数のみ可） |
| 識別子・数値 | Vault ID、スロット ID・種別、項目数、保存回数（counter）、所要時間、KDF パラメータ、TPM の応答コード | 記録する |
| パス | Vault・キーファイルの場所 | ユーザー名部分を伏せて記録（`%USERPROFILE%\…`、D-4） |
| 例外 | 種類・メッセージ・スタック | 記録する。ただしメッセージは §3 の秘密・利用者データを含まないよう、アプリの例外は文言を固定にする |

### 3.1 仕組みでの担保

- ログ出力は **イベントごとの型付きメソッド**（`LoggerMessage` のソース生成）だけを使い、任意の文字列を渡す API を App から使わない。
- 秘密を表す型（`SecretBuffer`、`Keyfile`、`RecoveryCode`）は `ToString()` が固定文字列（`"***"`）を返す。
- **カナリアテスト**: テストで既知のパスワード・PIN・復旧コード・鍵ファイルのテキスト・項目のパスワードを使って一連の操作を行い、出力ログと設定ファイルにそれらが含まれないことを確認する（CI で常時実行）。実装: Application.Tests の `TestWorld` が、**すべてのテスト**の終了時に検査する（検査が働くこと自体もテストしている）。

## 4. イベント一覧

実装: `src/PwManager.Application/Logging/Log.cs`（2026-10-25 時点。初版から追加したものに ＊）。理由コードは `ErrorCode` の名前。

| 範囲 | 区分 | イベント（ID / 名前 / 項目） |
|---|---|---|
| 1xxx | アプリ | 1001 AppStarted（版、OS、.NET）/ 1002 AppExiting / 1003 SecondInstanceBlocked / 1004 VaultLocation＊（伏せ字のパス、有無）/ 1090 UnhandledException（発生元、種類、例外） |
| 2xxx | 解錠・ロック | 2001 UnlockStarted（方式）/ 2101 UnlockSucceeded（方式、スロット ID、所要時間、counter）/ 2201 UnlockFailed（方式、理由: WrongPin / WrongKey / WrongRecoveryCode / RecoveryCodeMalformed / Keyfile* / Tpm* / VaultFormat / VaultTampered など、所要時間、連続失敗数）/ 2202 UnlockError＊ / 2301 Locked（理由: Manual / Idle / SessionLock / Suspend / Exit / Error）/ 2302 AutoLockDeferred（理由、実行中の操作名）/ 2303 SlotUseRecorded＊（スロット ID） |
| 3xxx | スロット・初期設定 | 3001 SlotEnrolled（種別、スロット ID）/ 3002 SlotRemoved / 3003 SlotReplaced（旧 ID、新 ID）/ 3101 SlotPolicyRejected（操作名）/ 3201 SetupCompleted＊（Vault ID、スロット数）/ 3202 SetupAbandoned＊（手順）/ 3203 KeyfileCleanupFailed＊ |
| 4xxx | 項目 | 4001 EntryCreated / 4002 EntryUpdated / 4003 EntryDeleted（いずれも項目 ID のみ）/ 4101 UnsavedChangesDiscarded（項目 ID、理由） |
| 5xxx | 保存 | 5001 SaveCompleted（操作名、counter、サイズ、所要時間、バックアップを回したか）/ 5101 SaveFailed（操作名、例外の種類、元に戻したか）/ 5102 SaveConflict（操作名、開いたときの counter）/ 5201 BackupRotated（Debug）/ 5202 BackupsPurged（操作名、削除数、削除できなかった数。解錠方法の削除・復旧コードの作り直し時） |
| 6xxx | TPM | 6001 TpmAvailable / 6101 TpmUnavailable（理由）/ 6102 TpmSlotsOnThisDevice＊（TPM スロット数、この PC のもの）。製造元・版と 6201 TpmCommandFailed は未実装（`ITpmSealer` が情報を返さないため） |
| 7xxx | 取り込み | 7001 ImportPreviewed（行数、取り込む件数、登録済みと同じ、CSV 内の重複、スキップ）/ 7002 ImportCommitted（件数）/ 7003 ImportSourceDeleted（成否）/ 7101 ImportFailed＊（理由） |
| 8xxx | 設定 | 8001 SettingsChanged（項目名・新しい値。数値の設定のみ）/ 8101 SettingsLoadFailed＊ / 8102 SettingsSaveFailed＊（例外の種類） |

操作 ID（op）: 解錠・保存などの 1 操作（`VaultSession.RunExclusiveAsync`）の間のログに同じ 8 桁の ID が付く。
文字列の値・例外のメッセージとスタックは、書く前にパスのユーザー名を伏せる（`PathRedactor.RedactText`）。

## 5. 未処理例外（R-19）

1. ログに `UnhandledException`（Critical）を記録する。
2. 解錠中ならロックする（Vault をゼロ化）。
3. 「予期しないエラーが発生しました。ロックしました。」と表示し、「ログフォルダを開く」「続ける」「終了」を選べるようにする。
4. 背景スレッドの例外（`TaskScheduler.UnobservedTaskException`、`AppDomain.UnhandledException`）も同様に扱う。

## 6. 診断ツール `pwm`（R-18）

秘密を入力せずに使える診断用 CLI（`src/PwManager.Cli`、Application 層を利用）。

| コマンド | 出力 |
|---|---|
| `pwm diag vault [path]` | 形式バージョン、Vault ID、保存回数、作成・更新日時、スロット一覧（種別・ラベル・登録日・最終使用日・KDF パラメータ）、ファイルサイズ、構造の健全性（ヘッダ読み込み可否）。※ 鍵がないため改ざん検知（MAC）は確認できない旨を表示 |
| `pwm diag tpm` | TPM の利用可否、製造元・版、SRK の有無と種類、DA カウンタ・上限・減少間隔 |
| `pwm backups [path]` | バックアップ世代の一覧（日時・保存回数・サイズ） |
| `pwm bench argon2` | 現在の KDF パラメータでの所要時間（パラメータ見直し用） |
| `pwm logs [--days N]` | 直近ログの要約（エラー・警告の件数と種類） |

アプリの「鍵と設定」画面にも **診断情報**（版、TPM 状態、Vault の保存回数、ログフォルダを開くボタン）を置く。

## 7. 開発用設定（R-20）

| 設定 | 用途 | Release での扱い |
|---|---|---|
| キャプチャ許可（現 `PWM_ALLOW_CAPTURE`） | 開発中のスクリーンショット | **無効**（コンパイル時に除外。テストで確認） |
| テスト用 Vault パス（起動引数） | 実データを汚さない確認 | 有効（引数を付けたときだけ） |
| 設定の保存先の切り替え（例 `PWM_DATA`） | 試行時に本番の設定・ログを汚さない（FocusCoach の `FOCUSCOACH_DATA` と同じ考え方） | 有効 |
| テストエラー（`PWM_ALLOW_TEST_ERROR=1` ＋ Ctrl+Shift+F12） | 未処理例外の処理（R-19）を実機で確認する | **無効**（Debug ビルドのみコンパイル） |

> 2026-10-25 の実機確認では、テスト用 Vault を引数で分けたが、設定ファイル（最後に使った鍵ファイルの場所）は本番の場所に書かれた（確認後に削除済み）。`PWM_DATA` で設定・ログも分けられるようにする。
