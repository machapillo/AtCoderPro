# AtCoder Web Bot (Blazor Server)

Web UIを備えたAtCoder自動解答ツールです。
実行ログのリアルタイム表示、C#コードの生成（GPT-4）、ローカルテスト検証、自動提出機能を提供します。

## 前提条件
- .NET 8 SDK
- AtCoderアカウント
- OpenAI API Key

## セットアップ
1.  **リポジトリの準備**:
    ```bash
    git clone <your-repo-url>
    git init # (新規作成の場合)
    ```
2.  **環境変数**:
    - `.env.example` をコピーして `.env` を作成します（必須ではありません）。
    - `OPENAI_API_KEY` を設定してください。
    - アカウント情報はWeb画面から入力可能です。

3.  **ブラウザのインストール**:
    ```bash
    cd AtCoderWeb
    powershell bin/Debug/net8.0/playwright.ps1 install
    ```

## 実行方法
```bash
cd AtCoderWeb
dotnet run
```
コンソールに表示されるURL（例: `http://localhost:5xxx`）にアクセスしてください。

## コンソール版からの移行について
ロジックは `AtCoderWeb/Services/AtCoderService.cs` に移行されました。
元のコンソールアプリは参照用として残されていますが、メインプロジェクトは `AtCoderWeb` です。
