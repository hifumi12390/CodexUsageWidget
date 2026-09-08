# 調査・方式決定 — 2026-09-08

正規要件: [AI Shared Knowledge / Projects / Codex Usage Widget App](https://app.notion.com/p/3d59a871d266817ca8ebe2788f7b715b)

## 利用量取得方式の比較

| 方式 | 安定性・安全性・保守性 | 結論 |
| --- | --- | --- |
| Codex公式App Server `account/rateLimits/read` | 文書化されたJSON-RPC。認証管理を既存Codexへ委譲。ウィジェットは認証ファイルにアクセスしない。CLI側更新への追従は必要 | 採用。通常Windowsユーザー環境で実測成功 |
| Codex内のget_usage_limitsツール | 今回の調査用に取得成功。ただしCodexタスクのツール環境に依存し、独立Windowsアプリから汎用的に呼ぶ契約ではない | 調査の比較用のみ |
| サービスのHTTPエンドポイント直接呼び出し | アプリに認証読取・更新・エンドポイント追従の責務が増える。公式App Serverで取得可能なので必要性がない | 不採用。トークンの抽出も実施しない |
| セッションログ・ローカルDB読取 | 更新がCodex利用イベント依存。最新値や全項目の保証がなく内部形式へ依存 | 不採用 |
| UIスクレイピング | 表示・言語・レイアウトに依存し、ウィジェット更新のたびGUI操作が必要 | 不採用 |

Codex CLI 0.153.4で生成したschemaを`research/schema`に保存。公式文書と実測で、`rateLimitsByLimitId.codex`、primary/secondaryの`usedPercent`、`windowDurationMins`、`resetsAt`、`credits.balance`を確認。期間300分を5h、10080分をWeekとして判定し、primary/secondaryの固定順序には依存しない。複数枠マップがある場合はそれを優先し、codexがない場合は他の枠で代用しない。

4項目すべて実取得に成功。Creditsは0という実値を取得できた（未取得扱いではない）。残高はリセット券の枚数と異なる。固定の動的利用率は設計記録に保存しない。

サンドボックス内では認証不可、通常ユーザーとして同一プローブを実行すると成功。秘密情報の出力や認証設定の変更をせず切り分けた。通常利用のアプリは一般ユーザー権限（asInvoker）で起動する。

## Windows実装方式の比較

| 方式 | 判断 |
| --- | --- |
| C# / WPF / .NET 10 LTS | 採用。Windows標準ダイアログ、ウィンドウ移動・リサイズ、DWMと連携しやすい。ブラウザーを常駐させない。外部NuGetライブラリ不要 |
| WinUI 3 | モダンな背景効果は容易だが、この小型アプリにはWindows App SDK配布・初期化等の追加責務が増える |
| Electron | 作りやすいが、ブラウザーランタイムの常駐負荷を必要としない用途 |
| Tauri | 軽量だが、Rust/WebView2とUIの二層構成よりC#単独が保守しやすい |

最初の検証は既存.NET 9 SDKで行い、成果物はProject内の.NET 10.0.400 SDK / Runtime 10.0.11へ移行した。公式メタデータで.NET 10のサポート終了予定2028-11-14を確認。SDK ZIPはMicrosoft公式SHA512との一致を検証した。システムの.NET SDK選択設定は変更しない。

## 初回v1.0の実装判断（履歴）

- 5分更新。毎回専用App Serverを起動して取得後終了。待機中の子プロセス常駐を避ける。
- 失敗時10/20/30分へバックオフ、30秒タイムアウト、手動操作に10秒の連打制限。
- 未提供は取得不可。失敗時の前回値・時刻を明示し、推測や自動ゼロリセットをしない。
- 設定はLocalAppDataにJSON、ディスクflush後File.Replaceで置換。バックアップ復旧あり。
- 背景はアプリ管理PNGへコピーし、暗いオーバーレイで可読性を保つ。対応Windowsでは公式DWM Acrylic API、非対応時はグラデーション。
- 自動起動なし。通常ウィンドウ、タスクバーあり、単一起動、最大化なし、最前面は任意。
- ポータブル単一EXEとZIP。コード署名や自動更新サーバーは含めない。

出典: [OpenAI App Server](https://learn.chatgpt.com/docs/app-server)、[DWM API](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)、[.NET 10公式リリース情報](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json)

## v1.3.2までの判断更新

- v1.2からタスクバー非表示・トレイ常駐。ガラス非対応時は単色へフォールバックする。
- 利用量は15秒／30秒／1分／5分／15分から選択、初期5分。公式稼働情報は認証不要のStatus APIで別途1分／5分／15分、初期1分で取得する。失敗時は最大30分まで待機を延長する。
- 定期問い合わせによる準リアルタイム表示とし、サーバー側の即時反映やpushを保証しない。
- 履歴は起動中の直近90回の実観測のみ。未観測・通信失敗を正常扱いせず、90日間稼働率とは区別する。
- Codex・ChatGPTの名称を含むサービスとLogin・VS Code extensionに絞る。汎用APIをChatGPTの状態と推定せず、対象外は公式サイトへのリンクから確認する。
- 展開時の手動調整を情報更新による自動サイズ計算より優先し、折りたたみ時と展開時の高さを保存する。
