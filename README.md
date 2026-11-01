# Secrypt

**English** | [日本語](#日本語)

Secrypt is a user name / password manager for Windows PCs. It keeps your passwords in an encrypted file on your own PC and is designed to work **independently of any web browser** — your passwords are not tied to a browser account or a browser's built-in password store, and no browser extension or local web server is involved.

- Day to day, open it with a short **PIN protected by the PC's TPM** (the TPM itself limits PIN guessing).
- Backup ways in: a **key file on a USB drive + password**, and a **recovery code on paper**.
- Leaking the encrypted file alone, or a password alone, is not enough to open it.
- Import passwords exported from Chrome / Edge (CSV), generate strong passwords, auto-lock, auto-clear the clipboard.
- The screen can be switched between **English and Japanese** on the first screen.

### Requirements

| | Recommended | Also works (outside the recommendation) |
|---|---|---|
| OS | Windows 11 (x64) | Windows 10 version 2004 (build 19041) or later, x64 |
| TPM | TPM 2.0 enabled — lets you open with a PIN | Without a usable TPM: open with the key file + password, or the recovery code |
| Removable media | A USB drive for the key file (plus a printed QR code as a spare) | The key file can be kept on an internal drive (weaker protection) |
| Rights | Standard user (no administrator rights needed) | — |
| Runtime | None — the distributed package is self-contained (.NET included) | Building from source needs the .NET 10 SDK |

Not supported: macOS / Linux / mobile, browser auto-fill, sync between multiple PCs.

### Install

Download the package, extract the ZIP, and run `setup.cmd`. Start **Secrypt** from the Start menu. Have a USB drive, paper and a pen ready for the initial setup. See `manual.html` in the package for details (Japanese).

To build from source: `scripts\install.cmd` (checks requirements, installs the .NET 10 SDK via winget if needed, builds and installs).

### About this software

- **The code of this software was written entirely by generative AI.** It has not been independently security-audited. Use it at your own discretion and keep the recovery code safe.
- **Original author:** Wizardvyrn (wizardvyrn@gmail.com)
- **No support is provided by the author.** You are free to copy, modify and use it, provided you follow the [license](LICENSE) (MIT).
- Third-party components and fonts are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

### Known limitations

Details and how to report a problem: [SECURITY.md](SECURITY.md).

- **Removing an unlock method (a lost key file, an old recovery code) or changing the PIN does not change the vault's master key.** Anyone holding a copy of the vault file made *before* the removal, plus the removed method's secret, can still open it — and the master key it yields also opens the current file. Secrypt deletes its own automatic backups on this PC when you do this, but **cannot delete copies elsewhere** (cloud sync, File History, external backups). Master-key rotation is not implemented yet.
- Not independently audited. A PC that is already compromised (malware, keyloggers) is out of scope.
- The distributed `Secrypt.exe` is not code-signed; Windows SmartScreen may warn on first launch. Compare the files with `SHA256SUMS.txt`.
- Print the backup QR code on paper — saving it as a PDF leaves a plain-text copy of the key file on the PC.

---

## 日本語

Secrypt は、Windows PC で使うユーザー名／パスワードの管理ツールです。パスワードは自分の PC 内の暗号化したファイルに保存し、**ブラウザに依存しない管理**を目指しています。ブラウザのアカウントや、ブラウザ内蔵のパスワード保存には頼らず、ブラウザ拡張機能やローカルの Web サーバーも使いません。

- 普段は **PC の TPM で守られた短い PIN** で開きます（PIN の総当たりは TPM 自体が回数制限します）。
- 予備の開け方として、**USB メモリの鍵ファイル＋パスワード**と、**紙の復旧コード**を用意します。
- 暗号化したファイルだけ、またはパスワードだけが漏れても、開くことはできません。
- Chrome / Edge から書き出したパスワード（CSV）の取り込み、強いパスワードの生成、自動ロック、クリップボードの自動消去に対応しています。
- 最初の画面で、表示を **日本語／英語** に切り替えられます。

### 動作条件

| | 推奨 | 推奨外でも動作する条件 |
|---|---|---|
| OS | Windows 11（x64） | Windows 10 バージョン 2004（ビルド 19041）以降、x64 |
| TPM | TPM 2.0 が有効（PIN で開けます） | TPM が使えない場合は、鍵ファイル＋パスワード、または復旧コードで開きます |
| 取り外せるメディア | 鍵ファイル用の USB メモリ（予備として QR コードの印刷も） | 鍵ファイルを PC 内のドライブに置くこともできます（守りは弱くなります） |
| 権限 | 一般ユーザー（管理者権限は不要） | — |
| ランタイム | 不要（配布パッケージは .NET を含む自己完結形式） | ソースからビルドする場合は .NET 10 SDK が必要 |

対応しないもの: macOS / Linux / スマートフォン、ブラウザへの自動入力、複数 PC 間の同期。

### インストール

配布パッケージの ZIP を展開し、`setup.cmd` を実行します。スタートメニューの **Secrypt** から起動してください。初期設定では USB メモリ 1 本と、紙とペンを用意してください。詳しくはパッケージ内の `manual.html`（操作手順書）を参照してください。

ソースからビルドする場合: `scripts\install.cmd`（動作要件の確認、必要なら winget で .NET 10 SDK を導入、ビルド、インストール）。

### このソフトについて

- **このソフトのコードは、すべて生成 AI で作成しています。** 第三者によるセキュリティ監査は受けていません。ご自身の判断で利用し、復旧コードは大切に保管してください。
- **オリジナル制作者:** Wizardvyrn（wizardvyrn@gmail.com）
- **制作者によるサポートは行っていません。** 自由にコピー・改変して使って構いませんが、[ライセンス](LICENSE)（MIT）に従って利用してください。
- 同梱しているライブラリとフォントのライセンスは [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) にあります。

### 既知の制限（Known limitations）

詳しい内容と、問題の報告のしかたは [SECURITY.md](SECURITY.md) にあります。

- **解錠方法の削除（失くした鍵ファイル、古い復旧コード）や PIN の変更をしても、保管ファイルの主鍵は変わりません。** 削除前の保管ファイルのコピーと、削除した方法の秘密がそろうと、そのコピーを開けられます。そこで得られる主鍵は、今の保管ファイルも開けてしまいます。この PC 内の自動バックアップは Secrypt が削除しますが、**アプリの外にあるコピー**（クラウド同期、ファイル履歴、外付けディスクへのバックアップ）は削除できません。主鍵の作り直しはまだ実装していません。
- 第三者による監査は受けていません。すでに侵害された PC（マルウェア、キーロガー）は対象外です。
- 配布している `Secrypt.exe` にはコード署名がなく、初回起動時に Windows SmartScreen の警告が出ることがあります。`SHA256SUMS.txt` と照合してください。
- 予備の QR コードは紙に印刷してください。PDF で保存すると、鍵ファイルの平文のコピーが PC に残ります。

---

## For developers / 開発者向け

| 層 | 内容 |
|---|---|
| `src/PwManager.Core/` | 暗号・保管ファイル形式・解錠方法（キースロット）・生成・取り込み（OS 非依存） |
| `src/PwManager.Application/` | 業務フロー（解錠・初期設定・項目・解錠方法・取り込み）、状態、ログ、診断（OS 非依存） |
| `src/PwManager.Presentation/` | 画面の ViewModel（WPF 非依存）と依存の組み立て |
| `src/PwManager.Platform.Windows/` | TPM シール（TSS.MSR） |
| `src/PwManager.App/` | WPF アプリ |
| `src/PwManager.Cli/` | 診断用 CLI `pwm`（日本語のみ） |
| `tests/` | xUnit（実機 TPM のテストは `Category=Hardware`） |

```powershell
dotnet test PwManager.slnx --filter "Category!=Hardware"   # テスト
dotnet run --project src\PwManager.App -c Release            # アプリ起動
scripts\publish.cmd                                          # 配布フォルダと ZIP を作る（publish\Secrypt-<版>-win-x64）
```

データの保存先: `%LOCALAPPDATA%\NewPWManageTool\`（`vault.pwmv`・`settings.json`・`logs\`）。環境変数 `PWM_DATA` で切り替えられます。ログにパスワードや項目の内容は記録しません。

設計文書（日本語）: [docs/](docs/) — 01 構想・要件、02 脅威モデル、03 鍵管理設計、04 アーキテクチャ、05 技術スタック、06 ロードマップ、07 McEliece 適用検討、08 画面仕様、09 構造検討書、10 デバッグ・ログ仕様、[adr/](docs/adr/)。
