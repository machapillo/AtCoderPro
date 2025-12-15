# AtCoder Automated Solver (C# Edition)

This tool automates solving AtCoder problems using C# and OpenAI.
It features **Local Verification**: generated solutions are compiled and tested against sample inputs before submission.

## Prerequisites

- **.NET 8 SDK**
- **OpenAI API Key**
- **AtCoder Account**

## Setup

1.  **Configure Credentials**:
    - Copy `.env.example` to `.env` in the project root.
    - Set your `ATCODER_USERNAME`, `ATCODER_PASSWORD`, and `OPENAI_API_KEY`.

2.  **Install Playwright Browsers**:
    (First run only)
    ```powershell
    cd AtCoderBot
    dotnet build
    powershell bin/Debug/net8.0/playwright.ps1 install
    ```

## Usage

Run the bot:

```powershell
dotnet run --project AtCoderBot
```

1.  Enter the Contest URL (e.g. `https://atcoder.jp/contests/ahc058`).
2.  The bot will:
    - Log in.
    - Fetch tasks.
    - For each task:
        - Scrape problem & samples.
        - Generate C# code (via OpenAI).
        - **Verify**: Run valid inputs locally using `TempSolution`.
        - **Submit**: If local tests pass, submit to AtCoder.

## Notes
- Code generation uses `gpt-4o`.
- Browser runs in **Visible Mode** so you can watch progress.
