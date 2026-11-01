Secrypt {VERSION} 配布パッケージ
================================================================

Windows 用のローカル・パスワードマネージャです。このフォルダだけで
ほかの PC にインストールできます（ビルド・.NET のインストールは不要）。

■ 動作要件
  ・Windows 11、または Windows 10 バージョン 2004（ビルド 19041）以降
  ・64 ビット（x64）
  ・TPM 2.0 推奨（無くても鍵ファイルと復旧コードで使えます）
  ・管理者権限は不要

■ インストール
  1. このフォルダ（ZIP の場合は「すべて展開」したもの）を PC にコピーする
  2. setup.cmd をダブルクリックする
       デスクトップにもショートカット:  setup.cmd -DesktopShortcut
       診断ツール pwm を PATH に追加:   setup.cmd -AddToPath
  3. スタートメニューの「Secrypt」から起動し、初期設定を行う
     （USB メモリ 1 本と、紙とペンを用意してください）

  インストール先: %LOCALAPPDATA%\Programs\Secrypt
  データの保存先: %LOCALAPPDATA%\NewPWManageTool（vault.pwmv・設定・ログ）

  ※ 署名のないアプリのため、初回起動時に「Windows によって PC が保護されました」
     と表示されることがあります。配布元が確かな場合は「詳細情報」→「実行」。

■ 更新
  新しい版の配布フォルダで setup.cmd を実行します。データはそのまま使えます。

■ アンインストール
  setup.cmd -Uninstall
  （またはインストール先の uninstall.cmd）
  パスワードのデータは削除しません。

■ 別の PC からデータを移すとき
  元の PC の %LOCALAPPDATA%\NewPWManageTool\vault.pwmv を新しい PC の同じ場所へコピーし、
  鍵ファイル（USB メモリ）か復旧コードで開いてから、「鍵と設定」→
  「＋ この PC の TPM を登録」で新しい PC 用の PIN を設定してください。

■ 同梱物
  setup.cmd / setup.ps1   インストール・アンインストール
  app\                    アプリ本体（Secrypt.exe）と診断ツール（app\cli\pwm.exe）
  manual.html             操作手順書（画面写真つき。ブラウザで開く）
  SHA256SUMS.txt          ファイルの改ざん確認用ハッシュ
  LICENSE.txt             ライセンス（MIT）
  THIRD-PARTY-NOTICES.md  同梱しているライブラリ・フォントのライセンス表記
  app\Fonts\*-OFL.txt     ロゴ画面の書体（Oxanium・Share Tech Mono）のライセンス（SIL OFL 1.1）

詳しい使い方は manual.html を参照してください。
