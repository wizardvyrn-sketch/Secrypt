# 07. McEliece 暗号（rsa_bench 実装）の適用可否検討

対象: 以前作成した `rsa_bench/McEliece/McEliece.cs`（C#）および `bench/mceliece.py`（Python）。
Goppa 符号 + Patterson 復号による教科書型 McEliece、プリセット toy (m=8, n=255, t=16) / small (m=10, n=1023, t=50)。

## 1. 結論（要約）

| 問い | 結論 |
|---|---|
| Vault の暗号化そのものに McEliece が必要か | **不要**。本設計の Vault は共通鍵暗号（XChaCha20-Poly1305, 256bit）と Argon2id/HKDF のみで構成され、量子計算機に対しても十分な強度（Grover で実効 128bit）がある |
| 公開鍵暗号（KEM）を使うと得をする場面はあるか | **ある**。「復旧スロットを紙のコードなしで更新する」用途（後述 §3） |
| 既存の自作実装をそのまま組み込めるか | **不可**。学習用実装として正しく動くが、実運用の暗号としては欠陥がある（§4） |
| 採用する場合の方針 | 自作実装ではなく **検証済みライブラリ**（BouncyCastle の Classic McEliece、または .NET 10 標準の ML-KEM）を使う。自作実装は相互検証や学習用の位置づけにとどめる |
| 優先度 | **Phase 3（拡張）**。MVP には入れない |

## 2. そもそも量子耐性の観点で弱い箇所はどこか

McEliece を検討するきっかけとして、設計全体を「量子計算機が実用化された後に、今盗まれたファイルを解く（Harvest Now, Decrypt Later）」観点で見直した。

| 要素 | 方式 | 量子耐性 |
|---|---|---|
| Vault 本体 | XChaCha20-Poly1305（256bit） | ○ |
| パスワード/PIN の鍵化 | Argon2id + HKDF | ○ |
| keyfile / recovery スロット | 共通鍵のみ | ○ |
| fido2 スロット | hmac-secret（共通鍵 HMAC） | ○ |
| **tpm スロット（当初案1）** | TPM の **RSA 鍵**で `tpm_secret` を暗号化し、その暗号文をヘッダに保存 | **✕** 量子計算機で RSA が破られると、ヘッダの暗号文から `tpm_secret` が取り出され、残りは PIN（短い）の総当たりだけになる |

**→ 本当に直すべきなのは McEliece の追加ではなく、TPM スロットの方式。**
TPM の **シール（sealed data object）** を使えば、秘密は TPM 内部の対称鍵で保護されるため公開鍵暗号に依存しない。
これを TPM スロットの第一候補に変更する（[ADR-0004](adr/0004-tpm-seal-instead-of-rsa-wrap.md)）。
なお TPM は McEliece の秘密鍵を内部で扱えないため、McEliece で TPM スロットを置き換えることはできない。

## 3. 公開鍵暗号が役に立つ場面：復旧スロットの「書き込み専用」化

現行設計では、VMK をローテーション（作り直し）すると、復旧スロットも作り直す必要がある＝**紙の復旧コードを毎回入力**するか、**新しい紙を作り直す**必要がある。

KEM を使うと次のようにできる:

```
復旧コード（紙）──seed──▶ KEM 鍵ペアを決定的に生成
                             ├ 公開鍵 → Vault ヘッダに保存
                             └ 秘密鍵 → 保存しない（必要時に紙から再生成）

VMK ローテーション時:
  新 VMK を「ヘッダ内の公開鍵」で暗号化して復旧スロットを更新   ← 紙は不要
復旧時:
  紙のコード → 秘密鍵を再生成 → 復旧スロットを復号 → VMK
```

- 利点: キー紛失・USB 紛失時の VMK ローテーションで、紙の復旧コードを取り出す・作り直す手間がなくなる。暗号化エクスポート（バックアップ）を復旧鍵宛てに自動作成することもできる。
- 量子耐性のある KEM が必要（ここで RSA/ECDH を使うと §2 と同じ問題になる）→ **ここが McEliece / ML-KEM の出番**。
- Classic McEliece は公開鍵が大きい（最小パラメータ mceliece348864 で約 261KB、高強度版は約 1MB）が、ローカルファイルなので許容範囲。
  暗号文は約 100〜200 バイトと小さい。安全性の実績（1978 年から破られていない）は最も長い。
- ML-KEM（FIPS 203）は鍵が小さく（約 1.2KB）高速、.NET 10 の標準 API（`System.Security.Cryptography.MLKem`）で使える（Windows 側の CNG 対応が前提）。
- 推奨は **ハイブリッド**: 復旧スロットの KEK = HKDF( 共通鍵の復旧コード由来秘密 ‖ KEM 共有秘密 )。KEM 側に未知の欠陥があっても、従来の安全性を下回らない。

## 4. 既存実装（McEliece.cs）をそのまま使えない理由

コードレビュー結果。学習・ベンチマーク用途としては問題ないが、秘密を守る用途には以下が致命的。

| # | 問題 | 箇所 | 影響 |
|---|---|---|---|
| 1 | **誤りベクトルの位置と置換 P に `System.Random` を使用** | `RandomPermutation()`, `RandomChoice()` | `System.Random` は暗号用乱数ではなく予測可能。誤りベクトル e が推測されると暗号文から平文がそのまま復元される。置換 P も秘密鍵の一部 |
| 2 | **パラメータが安全基準を満たさない** | toy: n=255, t=16 / small: n=1023, t=50 | 標準化提案の Classic McEliece 最小は m=12, n=3488, t=64（NIST レベル1, 128bit 相当）。small はそれを大きく下回る |
| 3 | **教科書型（CPA）McEliece で KEM 化されていない** | `EncryptBatch` / `DecryptBatch` | 選択暗号文攻撃・暗号文改変（malleability）に弱い。Classic McEliece は Niederreiter 形式＋ハッシュによる KEM 構成（CCA 安全）を採る |
| 4 | **復号が定数時間でない** | `Patterson.Decode`, 多項式演算・Chien 探索 | 処理時間の差から秘密鍵（Goppa 多項式・サポート）が漏れるタイミング攻撃の余地 |
| 5 | **秘密鍵のメモリ管理** | `int[]`/`byte[,]` を GC 管理下で保持 | ゼロ化・ピン留めがなく、本設計のメモリ方針（SecretBuffer）と合わない |
| 6 | **鍵のシリアライズ・決定的鍵生成（seed から生成）がない** | — | §3 の用途には seed からの決定的鍵生成が必須 |
| 7 | 難読化（Obfuscar）は安全性に寄与しない | `obfuscar.xml` | 暗号の安全性は鍵の秘密性のみに依存させるべき（ケルクホフスの原理）。本プロジェクトでは難読化は採用しない。配布物はむしろ公開・検証可能性を優先 |

### 既存実装の活かし方

- **相互検証用のテストオラクル**としては価値がある: 採用ライブラリ（BouncyCastle の CMCE）と同じ小パラメータで、
  Goppa 符号・Patterson 復号の中間値を突き合わせることで、仕組みの理解とライブラリ利用の誤り検出に使える。
- GF(2^m)/Goppa の知見は、Classic McEliece のパラメータ選定・性能評価（鍵生成時間、ヘッダサイズ）の見積もりに使える。

## 5. 採用する場合の実装方針（Phase 3）

| 項目 | 方針 |
|---|---|
| ライブラリ | 第一候補: **ML-KEM-768**（.NET 10 標準 `MLKem`）。Classic McEliece を選ぶ場合は **BouncyCastle.Cryptography**（`Org.BouncyCastle.Pqc.Crypto.Cmce`、mceliece348864 以上） |
| 構成 | ハイブリッド（共通鍵の復旧秘密 ＋ KEM 共有秘密を HKDF で結合） |
| 鍵生成 | 紙の復旧コード → HKDF → 32B seed → 決定的に鍵ペア生成 |
| フォーマット | スロット種別 `recovery_kem` を追加（`kem_alg`, `public_key`, `kem_ciphertext`, `wrapped_vmk`） |
| 選定の観点 | ML-KEM: 小さく速い・標準 API・Windows の OS 対応に依存 / Classic McEliece: 実績が最長・公開鍵が大きい・外部ライブラリ依存 |
| PoC | 鍵生成時間、ヘッダサイズ、seed からの再現性、OS 対応状況（ML-KEM の CNG 可用性）を計測して決定 |

## 6. 参考

- rsa_bench `WORK_LOG.md`（実装経緯、バグ修正、難読化検証）
- [ML-KEM and ML-DSA Post-Quantum Cryptography in Windows（strathweb）](https://strathweb.com/2025/08/ml-kem-and-ml-dsa-post-quantum-cryptography-in-windows/)
- [Bouncy Castle C#](https://bouncycastle.org/csharp/) / [BouncyCastle.Cryptography (NuGet)](https://nuget.org/packages/BouncyCastle.Cryptography)
- [Bouncy Castle CMCE パッケージ（Java 版ドキュメント）](https://downloads.bouncycastle.org/java/docs/bcprov-jdk18on-javadoc/org/bouncycastle/pqc/crypto/cmce/package-summary.html)
