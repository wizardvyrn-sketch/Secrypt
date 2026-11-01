# 05. 技術スタック検討

## 1. UI 方式の比較（セキュリティ観点）

| 方式 | 主なリスク | 評価 |
|---|---|---|
| **ローカル HTTP サーバ + ブラウザ** | 他ローカルプロセス/他ユーザーからのポート接続、CSRF・DNS リバインディング、ブラウザ拡張による DOM 読み取り、認証トークン管理が必要 | ✕ 不採用 |
| Electron | Chromium + Node の巨大な攻撃面、更新追従負担、`nodeIntegration` 等の設定ミスリスク | ✕ 不採用 |
| Tauri (Rust + WebView2) | ポート待受なし・IPC はプロセス内。だが WebView（JS/HTML）面が残り、XSS→IPC コマンド呼び出しの経路設計が必要 | △ 次点 |
| **WPF / WinUI 3（ネイティブ）** | Web 面なし。入力は OS のテキストボックス、IME 対応も素直 | ◎ **採用** |
| egui / iced（Rust ネイティブ GUI） | Web 面なしだが日本語 IME・アクセシビリティが弱め | △ |

「Web UI でも可」という条件だが、**ネイティブ UI の方が攻撃面を構造的に減らせ、Windows 固有 API（TPM/Hello/WebAuthn/クリップボード制御/キャプチャ除外）との相性も良い**ため、ネイティブを選ぶ。

## 2. 言語・ランタイム候補

| 観点 | **C# / .NET 10 + WPF** | Rust (+ Tauri or egui) | C++ / Win32 |
|---|---|---|---|
| Windows API 利用 | ◎ WinRT/P-Invoke 容易（Hello, NCrypt, WebAuthn） | ○ `windows` crate で可能 | ◎ |
| メモリ安全 | ○（GC 下で秘密のコピー管理に注意が必要） | ◎（`zeroize`, 所有権） | ✕ |
| 秘密のゼロ化 | △ pinned 配列＋明示ゼロ化で運用カバー | ◎ | ○ |
| UI 開発速度・日本語 IME | ◎ | △〜○ | △ |
| 暗号ライブラリ | NSec (libsodium) / 標準 `AesGcm`, `HKDF` | `argon2`, `chacha20poly1305`, `hkdf`（RustCrypto） | libsodium |
| 配布 | self-contained 単一 exe（~70MB） | 単一 exe（数 MB） | 小 |
| AI 支援開発のしやすさ | ◎ | ○ | △ |

### 選定: **C# / .NET 10 (LTS) + WPF**

理由:
1. 中核となる TPM（NCrypt）・Windows Hello（`Windows.Security.Credentials`）・WebAuthn（`webauthn.dll`）を最短で叩ける。
2. ネイティブ UI で Web 攻撃面ゼロ、日本語入力も問題なし。
3. GC による秘密コピーの懸念は「`string` を使わない」「pinned バッファ」「Core 層への集約」で実用上抑えられる。

**代替プラン**: 将来メモリ安全性をさらに高めたい場合、Core（暗号・フォーマット）だけ Rust で書いて C ABI 経由で呼ぶ構成に移行可能なように、Core の API 境界を細く保つ。

※ .NET 8 はサポート期限（2026年11月）が近いため、最新 LTS の .NET 10 を採用する。

## 3. ライブラリ・API

| 用途 | 採用 | 備考 |
|---|---|---|
| AEAD | XChaCha20-Poly1305（NSec / libsodium） | 192bit nonce でランダム nonce 衝突を気にしなくてよい |
| パスワード KDF | Argon2id（libsodium） | 既定: m=256MiB, t=3, p=1（PIN 用は軽量設定） |
| 鍵導出 | HKDF-SHA256（`System.Security.Cryptography.HKDF`） | |
| 乱数 | `RandomNumberGenerator`（BCryptGenRandom） | |
| TPM | 第一候補: TBS + TSS.MSR（TPM2 シール）／フォールバック: NCrypt + `MS_PLATFORM_CRYPTO_PROVIDER` | ADR-0004。シールは公開鍵暗号に依存しない |
| 耐量子 KEM（Phase 3） | .NET 10 `MLKem` または BouncyCastle.Cryptography（Classic McEliece） | docs/07。自作 McEliece 実装は使わない |
| Windows Hello | `KeyCredentialManager`（WinRT） | 生体 UX 用途 |
| FIDO2 | `webauthn.dll`（P/Invoke、`hmac-secret` / PRF） | 参考実装: Yubico python-fido2 の Windows クライアント、Chromium `webauthn_api.cc` |
| シリアライズ | 独自バイナリ（`BinaryWriter`、長さ付き） | 依存追加なし。本体は v2 で TLV 化予定（R-10） |
| UI | WPF（現行はコードビハインド）→ CommunityToolkit.Mvvm へ移行（R4） | ADR-0006 |
| DI | Microsoft.Extensions.DependencyInjection（R2 で導入） | ADR-0006 |
| ログ | Microsoft.Extensions.Logging ＋ 自前 JSONL 出力（R1 で導入） | ADR-0007、docs/10 |
| QR コード | QRCoder 1.6.0 | キーファイル予備の印刷 |
| TPM | Microsoft.TSS 2.1.1（TSS.MSR） | WindowsTpmSealer |
| テスト | xUnit 2.9。実機 TPM のテストは `Category=Hardware`（CI では除外）。SharpFuzz（パーサのファジング）は予定 | |
| 静的解析 | .NET Analyzers、`dotnet list package --vulnerable`、CodeQL（GitHub Actions） | |

## 4. 依存方針

- 暗号は **libsodium（NSec）＋ .NET 標準のみ**。自作暗号はしない（組み合わせのみ）。
- NuGet パッケージはロックファイル（`packages.lock.json`）でハッシュ固定。
- ネットワーク系パッケージを一切参照しないことを CI でチェック。

## 5. 開発環境

- Visual Studio 2022 / VS Code + C# Dev Kit
- GitHub Actions（windows-latest）: ビルド、テスト、CodeQL
- テスト用ハードウェア: TPM 2.0 搭載 PC、USB メモリ。YubiKey 5 系（FIDO2 + hmac-secret 対応）は購入後（保留機能の有効化時）
