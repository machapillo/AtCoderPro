using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using OpenAI.Chat;
using DotNetEnv;

namespace AtCoderWeb.Services
{
    public class AtCoderService
    {
        public event Action<string>? OnLog;
        private IPlaywright? _playwright;
        private IBrowser? _browser;
        private IBrowserContext? _context;
        private IPage? _page;
        private bool _isRunning;

        // Configuration
        public string? AtCoderUser { get; set; }
        public string? AtCoderPass { get; set; }
        public string? OpenAiKey { get; set; }

        public AtCoderService()
        {
            // Try load default env
             try {
                string root = FindProjectRoot();
                Env.Load(Path.Combine(root, ".env"));
                AtCoderUser = Environment.GetEnvironmentVariable("ATCODER_USERNAME");
                AtCoderPass = Environment.GetEnvironmentVariable("ATCODER_PASSWORD");
                OpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            } catch { }
        }

        private void Log(string message)
        {
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        public async Task StartAsync(string contestUrl)
        {
            if (_isRunning) return;
            _isRunning = true;
            Log("Starting Service...");

            try
            {
                if (string.IsNullOrEmpty(AtCoderUser) || string.IsNullOrEmpty(AtCoderPass))
                {
                    Log("Error: Credentials missing.");
                    return;
                }

                _playwright = await Playwright.CreateAsync();
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
                _context = await _browser.NewContextAsync(new BrowserNewContextOptions { Locale = "ja-JP" });
                _page = await _context.NewPageAsync();

                if (!await Login()) return;

                var tasks = await GetTasks(contestUrl);
                foreach (var task in tasks)
                {
                    if (!_isRunning) break;
                    await ProcessTask(task.Url, task.Name);
                }
                
                Log("All tasks processed.");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
            }
            finally
            {
                _isRunning = false;
                await StopAsync();
            }
        }

        public async Task StopAsync()
        {
            _isRunning = false;
            Log("Stopping...");
            if (_browser != null) await _browser.CloseAsync();
            _playwright?.Dispose();
            _browser = null;
            _playwright = null;
        }

        private async Task<bool> Login()
        {
            Log("Logging in...");
            await _page!.GotoAsync("https://atcoder.jp/login");
            await _page.FillAsync("#username", AtCoderUser!);
            await _page.FillAsync("#password", AtCoderPass!);
            await _page.ClickAsync("#submit");
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            if (_page.Url.Contains("/login"))
            {
                Log("Login failed.");
                return false;
            }
            Log("Login successful.");
            return true;
        }

        private async Task<List<(string Url, string Name)>> GetTasks(string contestUrl)
        {
            var tasksUrl = contestUrl.TrimEnd('/') + "/tasks";
            Log($"Fetching tasks from {tasksUrl}...");
            await _page!.GotoAsync(tasksUrl);

            var tasks = new List<(string, string)>();
            var links = await _page.QuerySelectorAllAsync("table tbody tr td.text-center a");
            var seen = new HashSet<string>();

            foreach (var link in links)
            {
                var href = await link.GetAttributeAsync("href");
                if (!string.IsNullOrEmpty(href) && href.Contains("/tasks/") && !seen.Contains(href))
                {
                    seen.Add(href);
                    var text = await link.InnerTextAsync();
                    tasks.Add(("https://atcoder.jp" + href, text));
                }
            }
            Log($"Found {tasks.Count} tasks.");
            return tasks;
        }

        private async Task ProcessTask(string taskUrl, string taskName)
        {
            Log($"Processing {taskName}...");
            await _page!.GotoAsync(taskUrl);

            var statementEl = await _page.QuerySelectorAsync("#task-statement");
            if (statementEl == null)
            {
                Log("Task statement not found.");
                return;
            }
            string problemText = await statementEl.InnerTextAsync();
            var samples = await ExtractSamples(_page);

            Log($"Generate C# Code (Samples: {samples.Count})...");
            string code = await GenerateSolution(problemText);

            bool passed = true;
            if (samples.Count > 0)
            {
                passed = await VerifySolution(code, samples);
            }
            else
            {
                Log("No samples. Skipping verification.");
            }

            if (passed)
            {
                await SubmitSolution(code);
            }
            else
            {
                Log("Verification failed. Skipping submission.");
            }
        }

        private async Task<List<(string Input, string Output)>> ExtractSamples(IPage page)
        {
            var list = new List<(string, string)>();
            var headings = await page.QuerySelectorAllAsync("h3");
            var inputs = new Dictionary<string, string>();
            var outputs = new Dictionary<string, string>();

            foreach (var h3 in headings)
            {
                var title = (await h3.InnerTextAsync()).Trim();
                bool isInput = title.Contains("Input") || title.Contains("入力例");
                bool isOutput = title.Contains("Output") || title.Contains("出力例");

                if (isInput || isOutput)
                {
                    var section = await h3.QuerySelectorAsync("xpath=.."); 
                    var pre = await section?.QuerySelectorAsync("pre");
                    if (pre != null)
                    {
                        var content = await pre.InnerTextAsync();
                        var match = Regex.Match(title, @"\d+");
                        string id = match.Success ? match.Value : "1";

                        if (isInput) inputs[id] = content;
                        else outputs[id] = content;
                    }
                }
            }

            foreach (var key in inputs.Keys)
            {
                if (outputs.ContainsKey(key))
                {
                    list.Add((inputs[key], outputs[key]));
                }
            }
            return list;
        }

        private async Task<string> GenerateSolution(string problemText)
        {
            if (string.IsNullOrEmpty(OpenAiKey))
            {
                Log("Mocking generation.");
                return "using System; class Program { static void Main() { Console.WriteLine(\"Mock\"); } }";
            }

            ChatClient client = new(model: "gpt-4o", apiKey: OpenAiKey);
            string prompt = $@"
You are an expert competitive programmer.
Generate a complete C# solution for the following AtCoder problem.
Output ONLY the code block. No explanation.
Problem: {problemText[..Math.Min(problemText.Length, 4000)]}...";

            ChatCompletion completion = await client.CompleteChatAsync(prompt);
            string code = completion.Content[0].Text;
            return code.Replace("```csharp", "").Replace("```cs", "").Replace("```", "").Trim();
        }

        private async Task<bool> VerifySolution(string code, List<(string Input, string Output)> samples)
        {
            Log("Verifying locally...");
            string root = FindProjectRoot();
            // We assume a sibling "TempSolution" folder exists or create one?
            // "TempSolution" was at root of dev/AtCoderPro. 
            // root here might be AtCoderWeb? We need to go up one level.
            string solutionDir = Path.GetFullPath(Path.Combine(root, "..", "TempSolution"));
            if (!Directory.Exists(solutionDir))
            {
                Log($"TempSolution not found at {solutionDir}");
                return false; 
            }

            string programPath = Path.Combine(solutionDir, "Program.cs");
            await File.WriteAllTextAsync(programPath, code);

            var build = RunProcess("dotnet", "build", solutionDir);
            if (build.ExitCode != 0)
            {
                Log("Compilation Failed.");
                return false;
            }

            bool allPassed = true;
            foreach (var sample in samples)
            {
                 var result = RunProcess("dotnet", "run --no-build", solutionDir, sample.Input);
                 string actual = result.Output.Trim().Replace("\r\n", "\n");
                 string expected = sample.Output.Trim().Replace("\r\n", "\n");

                 if (actual == expected) Log("  Pass");
                 else 
                 {
                     Log("  FAIL");
                     allPassed = false;
                 }
            }
            return allPassed;
        }

        private (int ExitCode, string Output) RunProcess(string filename, string args, string workDir, string? input = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = filename,
                Arguments = args,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = input != null,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = new Process { StartInfo = psi };
            StringBuilder output = new StringBuilder();
            p.OutputDataReceived += (s, e) => { if(e.Data != null) output.AppendLine(e.Data); };
            p.ErrorDataReceived += (s, e) => { if(e.Data != null) output.AppendLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (input != null) { p.StandardInput.Write(input); p.StandardInput.Close(); }
            p.WaitForExit();
            return (p.ExitCode, output.ToString());
        }

        private async Task SubmitSolution(string code)
        {
            Log("Submitting...");
            // Language
             var select2 = await _page!.QuerySelectorAsync(".select2-container");
            if (select2 != null)
            {
                await select2.ClickAsync();
                await _page.Keyboard.TypeAsync("C#");
                await _page.Keyboard.PressAsync("Enter");
                await Task.Delay(500);
            }

            // Code
            try { await _page.FillAsync("textarea[name='sourceCode']", code); }
            catch 
            {
                var editor = await _page.QuerySelectorAsync(".monaco-editor");
                if (editor != null)
                {
                     await _page.EvaluateAsync("code => { if(window.monaco) monaco.editor.getModels()[0].setValue(code); }", code);
                }
            }

            // Submit
            var submitBtn = await _page.QuerySelectorAsync("#submit");
            if (submitBtn != null)
            {
                await submitBtn.ClickAsync();
                Log("Submitted!");
                await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }
        }

        static string FindProjectRoot()
        {
            var dir = Directory.GetCurrentDirectory();
            for (int i = 0; i < 5; i++)
            {
                if (File.Exists(Path.Combine(dir, ".env"))) return dir;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return Directory.GetCurrentDirectory();
        }
    }
}
