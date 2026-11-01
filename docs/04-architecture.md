# 04. アーキテクチャ

## 1. コンポーネント構成

### 1.1 現行（2026-10-25、リファクタリング R0〜R6 の後）

```
PwManager.App (WPF, net10.0-windows)   Views（表示専用）、WPF 版ダイアログ・クリップボード、多重起動の防止、起動
  └─→ PwManager.Presentation (net10.0)  ViewModel（CommunityToolkit.Mvvm）、UI のポート、依存の組み立て（DI）
        └─→ PwManager.Application (net10.0)  VaultSession・ユースケース・ポリシー・ポート・結果型・ログ・診断
              └─→ PwManager.Core (net10.0)   Crypto / Vault 形式 / Slots / Generation / Import / Storage
PwManager.Cli (pwm)               診断（net10.0 / net10.0-windows）
PwManager.Platform.Windows        WindowsTpmSealer（ITpmSealer の実装）
```

| プロジェクト | 主な型 |
|---|---|
| Core / Crypto | `CryptoPrimitives`（XChaCha20-Poly1305・Argon2id・HKDF・HMAC）、`SecretBuffer`、`KdfParameters` |
| Core / Vault | `PasswordVault`（`PrepareSave`/`CommitSave`、`VaultCheckpoint`）、`KeySlot`、`VaultFile`、`EntrySerializer`（v2）、`SlotPolicy` |
| Core / Slots | `RecoverySlot`＋`RecoveryCode`、`KeyfileSlot`＋`Keyfile`、`TpmSlot`＋`ITpmSealer`（`IsSealedOnThisDevice`）、`Fido2Slot`（保留） |
| Application | `VaultSession`、`UnlockService`・`SetupService`・`SlotService`・`EntryService`・`ImportService`・`SettingsService`、`CredentialPolicy`・`UnlockBackoffPolicy`・`AutoLockMonitor`、ポート（`IVaultStore`・`IKeyfileStore`・`ISettingsStore`・`IClipboardService`）と実装（`FileVaultStore` ほか）、`ErrorCode`・`UserMessages`、`Log`・`JsonlFileLoggerProvider`、`VaultDiagnostics` |
| Presentation | `ShellViewModel`・`SetupViewModel`・`UnlockViewModel`・`VaultViewModel`（`EntryEditorViewModel`・`GeneratorViewModel`）・`KeysViewModel`、`IDialogService`・`IShellService`・`SecretInput`、`Composition` |
| App | `MainWindow`、`Views/*`、`Controls/Secret`（PasswordBox を SecretInput につなぐ）・`RevealablePasswordBox`、`Services/WpfDialogService`・`WpfClipboardService`・`SingleInstance`・`QrPrinter` |
| Platform.Windows | `WindowsTpmSealer` |

### 1.2 方針

構造検討書 [09](09-structure-review.md) §3・§6 のとおり。

- **Core と Application は OS 非依存**、Linux CI で単体テストする。
- プロセスは 1 つ。**ネットワーク API を参照しない**。
- ログは秘密を記録しない構造化ログ（[10](10-debug-logging.md)）。

## 2. Vault ファイルフォーマット（ファイル形式 v1・本体形式 v2、実装済み）

バイナリ（リトルエンディアン）。実装: `src/PwManager.Core/Vault/VaultFile.cs`。

```
"PWMV" | u16 version=1 | i32 headerLen | header | headerMac[32] | bodyNonce[24] | i32 bodyLen | body
header = vaultId[16] | created i64 | modified i64 | counter u64 | i32 slotCount | slot*
slot   = id[16] | type u8 | label str | salt bytes | kdf(m i32, t i32, p i32) | typeParams bytes
         | nonce bytes | wrapped bytes(48) | created i64 | lastVerified i64 (0 = 未使用)
headerMac = HMAC-SHA256( HKDF(VMK,"pwm/header/v1"), header )
body      = XChaCha20-Poly1305( HKDF(VMK,"pwm/entries/v1"), bodyNonce, aad = header, entries )
entries   = i32 formatVersion=2 | i32 count | entry*      … v2（タグ付きフィールド）
entry     = i32 fieldCount | (u16 tag | i32 len | bytes)*   tag: 1 id, 2 title, 3 user, 4 password, 5 url, 6 notes, 7 tag（複数可）, 8 created, 9 modified
```

設計ポイント:
- ヘッダは平文だが **AAD と MAC で改ざん検知**。スロットの追加/削除にファイル全体の再暗号化は不要。
- `wrapped` の AAD に `vaultId ‖ slotId ‖ type` を含め、スロットを別 Vault へ移植する攻撃を防ぐ。
- 解錠前は未認証のヘッダを読むため、KDF パラメータ・長さに上限を設けて DoS を防ぐ。
- 書き込みは **一時ファイル → Flush(ディスクまで) → 置き換え**。直前 5 世代を `.bak1`〜`.bak5` で保持。
- 本体は丸ごと 1 AEAD。

【実装済み R-10】本体は v2（タグ付きフィールド）。知らないタグは保持して書き戻す（前方互換）。v1（固定順）は読み込み時に変換し、次の保存で v2 になる。
- counter は「内容の版」。項目・スロットの変更で +1。最終使用日の記録など内容の変わらない保存では進めない（別の場所での更新の検知に使うため）。
- 保存前にディスク上の counter を照合し、開いた後に別の場所で更新されていれば上書きしない（R-16）。

## 3. 解錠フロー（Slot A: TPM + PIN の例）

```
User         App              Core                Platform.Tpm         TPM
                                                  （HMAC セッション＋応答暗号化）
 │ 起動       │                 │                      │                 │
 │──────────▶│ ヘッダ読込       │                      │                 │
 │           │───────────────▶│ slots 列挙            │                 │
 │ PIN/顔    │                 │──Unlock(slotA)─────▶│ TPM2_Unseal     │
 │──────────▶│                 │                      │──(PIN/Hello)──▶│
 │           │                 │                      │◀─tpm_secret────│
 │           │                 │ KEK=HKDF(tpm_secret‖Argon2id(PIN))     │
 │           │                 │ VMK=AEAD-Open(slotA)                   │
 │           │                 │ header_mac 検証 → body 復号             │
 │           │◀──── entries ───│ tpm_secret/KEK をゼロ化                 │
```

## 4. 実行時セキュリティ対策

| 項目 | 実装 |
|---|---|
| メモリ | `SecretBuffer`: `GC.AllocateArray(pinned:true)` + `VirtualLock` + `CryptographicOperations.ZeroMemory`。パスワード入力は `PasswordBox.SecurePassword` 相当から直接バイト化 |
| 自動ロック | 無操作 N 分、`SessionSwitch`（ロック/ユーザー切替）、`PowerModeChanged`（スリープ）で VMK 破棄。処理中は完了後にロック（R-02） |
| クリップボード | コピー時に `ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory=0` / `CanUploadToCloudClipboard=0` を付与。20秒後に「自分が置いた内容のままなら」消去 |
| 画面 | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`（Debug ビルドのみ開発用に解除可） |
| 多重起動・外部変更 | Vault ごとの単一インスタンス、保存前に counter を照合（R-16） |
| ログ | 秘密を記録しない構造化ログ（[10](10-debug-logging.md)、R-17） |
| プロセス（未実装） | DEP/ASLR/CFG 有効、`SetProcessMitigationPolicy` で動的コード禁止等（.NET JIT と両立する範囲で） |
| ページファイル/ダンプ（未実装） | `SetErrorMode` + WER 除外、クラッシュダンプに秘密を残さない設定 |
| 配布（未実装） | 自己完結（self-contained）単一 exe、コード署名、再現可能ビルドを目標 |

## 5. 拡張ポイント（将来）

- **ブラウザ連携**: Native Messaging ホスト（stdio、ポート待受なし）。拡張 ID 固定・都度ユーザー確認。
- **同期**: Vault ファイルを OneDrive 等に置くだけ（流出しても安全）。`counter` と `modified` で競合検知し、エントリ単位の 3-way マージ。
- **TOTP**: エントリに seed を保存し表示。
