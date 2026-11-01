# ADR-0004: TPM スロットは RSA 暗号化ではなく TPM シール（対称保護）を第一候補にする

- Status: **Accepted**（2026-10-25、PoC 全項目成功: [docs/poc/2026-10-25-tpm-seal.md](../poc/2026-10-25-tpm-seal.md)）
- Date: 2026-10-25

## Context
当初案では TPM 上の RSA 鍵で `tpm_secret` を暗号化し、暗号文を Vault ヘッダに保存していた。
将来、量子計算機で RSA が破られると、流出済みのヘッダから `tpm_secret` が取り出され、
残る防御は PIN（短い）だけになる（Harvest Now, Decrypt Later）。McEliece 検討（docs/07）の過程で判明。

## Decision
- 第一候補: TPM2 の **sealed data object**（`TPM2_Create` で `tpm_secret` を封印、authValue に PIN 由来値を設定）。
  封印データは親鍵の内部シードから導出した対称鍵で保護され、公開鍵暗号に依存しない。
  実装は TBS（TPM Base Services）経由で、Microsoft の TSS.MSR（TSS.Net）等を利用。
- 代替: TBS 直接利用が難しい場合は NCrypt（PCP）の RSA 案にフォールバックし、残余リスクとして記録する。
- いずれの場合も KEK には PIN の Argon2id 値を併用する。

## Consequences
- ＋ Vault の全スロットが共通鍵暗号のみで保護され、量子耐性の観点で弱点がなくなる。
- ＋ PIN の総当たりは TPM のアンチハンマリングで止まる点は変わらない。
- − NCrypt より低レベルな TPM コマンドを扱うため実装が難しい。→ PoC で一般ユーザー権限・Windows 既定 SRK での動作を確認済み。
- 本実装では HMAC セッション＋パラメータ暗号化を使う（パスワードセッションは PIN 由来値が平文でバスを流れるため）。
- − Windows Hello PIN との UI 統合は自前になる。
