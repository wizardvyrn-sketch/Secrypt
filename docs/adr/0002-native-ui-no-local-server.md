# ADR-0002: ネイティブ UI（WPF）を採用し、ローカル HTTP サーバ型 Web UI を採用しない

- Status: Proposed
- Date: 2026-10-25

## Context
Web UI は実装しやすいが、ローカルポート待受・ブラウザ拡張・CSRF/DNS リバインディング等の攻撃面が生じる。

## Decision
C# / WPF によるネイティブ単一プロセスアプリとする。待受ポート・IPC・プラグイン機構を持たない。

## Consequences
- ＋ Web 由来の攻撃クラスを構造的に排除。
- ＋ TPM / Windows Hello / WebAuthn / クリップボード制御を直接利用できる。
- − Windows 専用（要件上は問題なし）。
- − 将来のブラウザ連携は Native Messaging（stdio）で別途設計が必要。
