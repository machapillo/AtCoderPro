using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using OpenAI.Chat;
using DotNetEnv;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AtCoderWeb.Services
{
    public class AtCoderTask
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public bool IsSelected { get; set; } = true;
        public string Status { get; set; } = "Idle";
    }

    public class AtCoderService
    {
        public event Action<string>? OnLog;
        public event Action<string, string>? OnProblemTextReceived;
        public event Action<string, string>? OnSolutionGenerated;
        
        public List<AtCoderTask> FetchedTasks { get; private set; } = new();
        

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
            var msg = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Console.WriteLine(msg); // Output to console for debugging
            OnLog?.Invoke(msg);
        }

        public async Task FetchTasksAsync(string contestUrl)
        {
            if (_isRunning) return;
            _isRunning = true;
            Log("Fetching Tasks...");

            if (contestUrl.Contains("safelinks.protection.outlook.com") && contestUrl.Contains("url="))
            {
                try { contestUrl = System.Web.HttpUtility.ParseQueryString(new Uri(contestUrl).Query)["url"] ?? contestUrl; Log($"Cleaned URL: {contestUrl}"); } catch {}
            }

            try
            {
                if (_playwright == null) await ConnectBrowser();
                if (!await Login()) return;

                FetchedTasks.Clear();
                var tasks = await GetTasks(contestUrl);
                foreach (var t in tasks) 
                {
                    var at = new AtCoderTask { Url = t.Url, Name = t.Name };
                    FetchedTasks.Add(at);
                    
                    // Optimization: Pre-fetch text
                    Log($"Loading problem for {at.Name}...");
                     try 
                     {
                         await _page.GotoAsync(at.Url);
                         var text = await ExtractProblemText();
                         at.Status = "Loaded";
                         OnProblemTextReceived?.Invoke(at.Name, text);
                     }
                     catch (Exception ex)
                     {
                         Log($"Failed to load text for {at.Name}: {ex.Message}");
                     }
                }
                
                Log($"Fetched {FetchedTasks.Count} tasks. Please select tasks and click 'Generate'.");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
            }
            finally
            {
                _isRunning = false;
            }
        }

        public async Task RunSelectedTasksAsync()
        {
            if (_isRunning) return;
            _isRunning = true;
            Log("Starting code generation for selected tasks...");

            try
            {
                if (_playwright == null) await ConnectBrowser();

                foreach (var task in FetchedTasks.Where(t => t.IsSelected))
                {
                    if (!_isRunning) break;
                    task.Status = "Processing...";
                    Log($"Processing {task.Name}...");
                    try 
                    {
                        await ProcessTask(task.Url, task.Name);
                        task.Status = "Done";
                    }
                    catch (Exception ex)
                    {
                        task.Status = "Error";
                        Log($"Error on {task.Name}: {ex.Message}");
                    }
                }
                Log("Processing completed.");
            }
            catch (Exception ex) 
            {
                Log($"Error: {ex.Message}");
            }
            finally
            {
                _isRunning = false;
            }
        }

        private async Task ConnectBrowser()
        {
            if (_playwright != null) return;
            
            _playwright = await Playwright.CreateAsync();
            try
            {
                Log("Connecting to existing Chrome (Port 9222)...");
                _browser = await _playwright.Chromium.ConnectOverCDPAsync("http://localhost:9222");
                _context = _browser.Contexts.FirstOrDefault();
                if (_context == null) _context = await _browser.NewContextAsync();
                
                var dashboardPage = _context.Pages.FirstOrDefault(p => p.Url.Contains("localhost:5108"));
                _page = await _context.NewPageAsync(); 
                Log("Opened new tab for bot operations.");

                if (dashboardPage != null)
                {
                    await dashboardPage.BringToFrontAsync();
                    Log("Restored focus to dashboard.");
                }
                Log("Connected to Chrome successfully.");
            }
            catch (Exception)
            {
                Log("Connection failed! Check Chrome debugger port 9222.");
                throw;
            }
        }

        public async Task StopAsync()
        {
            _isRunning = false;
            Log("Stopping...");
            if (_browser != null) await _browser.CloseAsync();
            _playwright?.Dispose();
            _context = null;
            _browser = null;
            _playwright = null;
        }

        private async Task<bool> Login()
        {
            Log("Checking login status...");
            // Try accessing a protected page
            await _page!.GotoAsync("https://atcoder.jp/settings");
            try 
            { 
                await _page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5000 }); 
            } 
            catch { }

            if (!_page.Url.Contains("/login"))
            {
                Log("Already logged in! (Accessed settings)");
                return true;
            }

            Log("Navigate to Login page...");
            await _page!.GotoAsync("https://atcoder.jp/login");
            
            Log(">>> PLEASE LOG IN MANUALLY <<<");
            Log("Waiting for you to log in (5 minutes max)...");

            // Wait for URL to NOT contain "/login"
            for (int i = 0; i < 60; i++) // 60 * 5s = 300s (5 mins)
            {
                await Task.Delay(5000);
                if (!_page.Url.Contains("/login"))
                {
                    Log("Login detected! Proceeding...");
                    return true;
                }
            }

            Log("Login timed out.");
            return false;
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

        private async Task<string> ExtractProblemText()
        {
            var statementEl = await _page.QuerySelectorAsync("#task-statement");
            if (statementEl == null) return "<p>Problem statement not found.</p>";
            
            // Return HTML to preserve formatting (MathJax, tables, etc.)
            // We need to keep styles or at least structure.
            // But for Gemini prompt we need TEXT.
            // So we need TWO things: HTML for display, Text for AI.
            // For now, let's return HTML and handle text conversion in GenerateSolution if needed.
            // Wait, GenerateSolution expects text.
            // Let's modify OnProblemTextReceived to send HTML for UI, but keep using text for AI logic?
            // Refactoring: OnProblemTextReceived(name, htmlString)
            // But ProcessTask uses this return value for GenerateSolution prompt.
            
            // Let's grab HTML. The UI will render it as Raw HTML.
            // And for Gemini, we will extract text separately or convert HTML to text.
            return await statementEl.InnerHTMLAsync();
        }

        private async Task ProcessTask(string taskUrl, string taskName)
        {
            Log($"Processing {taskName}...");
            await _page!.GotoAsync(taskUrl);
            
            // Extracted text is now HTML.
            string problemHtml = await ExtractProblemText();
            // OnProblemTextReceived?.Invoke(taskName, problemHtml); // Already invoked in Fetch

            // Convert HTML to Text for AI prompt (Simple strip tags or re-fetch text)
            // Re-fetching text is safer for AI context.
            var statementEl = await _page.QuerySelectorAsync("#task-statement");
            string problemTextForAi = statementEl != null ? await statementEl.InnerTextAsync() : "";
            
            var samples = await ExtractSamples(_page);

            Log($"Generate C# Code (Samples: {samples.Count})...");
            string code = await GenerateSolution(problemTextForAi);
            OnSolutionGenerated?.Invoke(taskName, code);

            bool passed = true;
            if (samples.Count > 0)
            {
                passed = await VerifySolution(code, samples, taskUrl);
            }
            else
            {
                Log("No samples. Skipping verification.");
            }

            if (passed)
            {
                // await SubmitSolution(code);
                Log("Verification passed. Copy code from UI to submit.");
            }
            else
            {
                Log("Verification failed. Copy code from UI to submit (if desired).");
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
            // Check for Gemini API Key
            try 
            {
                Env.TraversePath().Load();
                string? geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
                if (!string.IsNullOrWhiteSpace(geminiKey))
                {
                    Log("Gemini API Key found. Generating solution using Gemini...");
                    try 
                    {
                        return await GenerateSolutionWithGemini(problemText, geminiKey);
                    }
                    catch (Exception ex)
                    {
                        Log($"Gemini API Error: {ex.Message}. Falling back to manual mode.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading .env file: {ex.Message}. Using manual mode.");
            }

            // Fallback to File Watch (Collaboration Mode)
            string root = FindProjectRoot();
            string solutionDir = Path.GetFullPath(Path.Combine(root, "..", "TempSolution"));
            if (!Directory.Exists(solutionDir)) Directory.CreateDirectory(solutionDir);

            string problemFile = Path.Combine(solutionDir, "problem.txt");
            string programFile = Path.Combine(solutionDir, "Program.cs");

            // 1. Save problem to file
            Log($"Saving problem to {problemFile}...");
            await File.WriteAllTextAsync(problemFile, problemText);

            // 2. Wait for user/AI to update Program.cs
            Log(">>> WAITING FOR SOLUTION <<<");
            Log("Please ask the AI Assistant to generate the code now.");
            Log($"Waiting for update in {programFile}...");

            var initialLastWrite = File.Exists(programFile) ? File.GetLastWriteTime(programFile) : DateTime.MinValue;

            // Wait loop (max 10 mins)
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(5000);
                if (File.Exists(programFile))
                {
                    var currentLastWrite = File.GetLastWriteTime(programFile);
                    if (currentLastWrite > initialLastWrite)
                    {
                        Log("Solution file updated! Reading...");
                        return await File.ReadAllTextAsync(programFile);
                    }
                }
            }

            Log("Timed out waiting for solution.");
            return "";
        }

        private async Task<string> GenerateSolutionWithGemini(string problemText, string apiKey)
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30); 
            
            var exhaustedModels = new List<string>();
            string modelName = "gemini-1.5-flash"; 
            string baseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

            // Prepare Request
            var prompt = $@"
You are an expert competitive programmer. 
Solve the following AtCoder problem in C#.
- Read input from Standard Input (Console.ReadLine).
- Write output to Standard Output (Console.WriteLine).
- Use efficient algorithms.
- Wrap the code in ```csharp ... ``` blocks.
- Do NOT include any explanation, only the code.

Problem Statement:
{problemText}
";
            var requestBody = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
            // var json = JsonSerializer.Serialize(requestBody); // Serialize inside loop to ensure fresh content if needed? No, consistent.
            
            HttpResponseMessage? response = null;
            bool modelDiscovered = false;

            for (int i = 0; i < 8; i++) // Increased attempts to allow for model switching
            {
                // Ensure we don't reuse an exhausted model if it was just set as default
                if (exhaustedModels.Contains(modelName))
                {
                     modelName = await DiscoverBestModel(client, apiKey, exhaustedModels);
                }

                var url = $"{baseUrl}/{modelName}:generateContent?key={apiKey}";
                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                Log($"Requesting Gemini ({modelName})... Attempt {i + 1}");
                
                try 
                {
                    response = await client.PostAsync(url, content);
                }
                catch (Exception ex)
                {
                    Log($"Request error: {ex.Message}. Retrying...");
                    await Task.Delay(2000);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var errorMsg = await response.Content.ReadAsStringAsync();
                    double waitSeconds = 5; 
                    
                    if (errorMsg.Contains("PerDay") || errorMsg.Contains("limit: 20") || errorMsg.Contains("Quota exceeded"))
                    {
                        Log($"Gemini 429. DAILY QUOTA EXCEEDED for {modelName}. Switching model...");
                        exhaustedModels.Add(modelName);
                        // Force switch immediately
                        modelName = await DiscoverBestModel(client, apiKey, exhaustedModels);
                        Log($"Switched to {modelName}. Retrying immediately...");
                        await Task.Delay(2000);
                        continue;
                    }

                    var match = Regex.Match(errorMsg, @"retry in\s+([\d\.]+)\s*s");
                    if (match.Success && double.TryParse(match.Groups[1].Value, out double parsedVal))
                    {
                        waitSeconds = parsedVal + 2; 
                    }
                    if (waitSeconds > 60) waitSeconds = 60; 

                    Log($"Gemini 429. Waiting {waitSeconds:F1}s...");
                    await Task.Delay((int)(waitSeconds * 1000));
                    continue;
                }
                
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    Log($"Model {modelName} not found (404). Switching...");
                    exhaustedModels.Add(modelName);
                    modelName = await DiscoverBestModel(client, apiKey, exhaustedModels);
                    await Task.Delay(1000);
                    continue;
                }

                break;
            }

            if (response == null) throw new Exception("Request failed.");
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Log($"Gemini API Error: {response.StatusCode} - {errorBody}");
                throw new HttpRequestException($"Gemini API failed: {response.StatusCode}");
            }

            Log("Response received from Gemini. Parsing...");
            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            
            try 
            {
                // ... extraction logic ...
                var candidates = doc.RootElement.GetProperty("candidates");
                if (candidates.GetArrayLength() == 0) return "// Error: No candidates returned.";
                
                var text = candidates[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();

                if (string.IsNullOrEmpty(text)) return "// Error: Empty response from Gemini";

                var match = Regex.Match(text, @"```csharp(.*?)```", RegexOptions.Singleline);
                if (match.Success) return match.Groups[1].Value.Trim();
                return text;
            }
            catch (Exception ex)
            {
                Log($"Failed to parse Gemini response: {ex.Message}");
                return "// Error parsing response";
            }
        }

        private async Task<string> DiscoverBestModel(HttpClient client, string apiKey, List<string>? exhausted = null)
        {
            try
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}";
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                
                var models = doc.RootElement.GetProperty("models");
                var availableList = new List<string>();

                foreach (var model in models.EnumerateArray())
                {
                    var name = model.GetProperty("name").GetString();
                    bool canGenerate = false;
                    if (model.TryGetProperty("supportedGenerationMethods", out var methods))
                    {
                        foreach (var m in methods.EnumerateArray())
                        {
                            if (m.GetString() == "generateContent") canGenerate = true;
                        }
                    }
                    if (name != null && canGenerate) 
                    {
                        if (name.StartsWith("models/")) name = name.Replace("models/", "");
                        if (exhausted == null || !exhausted.Contains(name))
                        {
                            availableList.Add(name);
                        }
                    }
                }

                Log($"Available models (filtered): {string.Join(", ", availableList)}");

                var priorities = new[] { 
                    "gemini-2.0-flash-lite-preview", // Try lite preview first
                    "gemini-flash-latest",
                    "gemini-pro-latest",
                    "gemini-1.5-flash",
                    "gemini-pro"
                };
                
                foreach (var p in priorities)
                {
                    var match = availableList.FirstOrDefault(m => m.Contains(p));
                    if (match != null) return match;
                }
                
                return availableList.FirstOrDefault(m => m.Contains("gemini")) ?? "gemini-pro";
            }
            catch(Exception ex) 
            {
                 Log($"Discovery error: {ex.Message}");
                 return "gemini-pro";
            }
        }

        private async Task<bool> VerifySolution(string code, List<(string Input, string Output)> samples, string taskUrl)
        {
            Log("Verifying locally...");
            string root = FindProjectRoot();
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
                Log(build.Output); // Show compiler errors
                return false;
            }

            bool isHeuristic = taskUrl.Contains("/ahc");
            if (isHeuristic) 
            {
                Log("Heuristic contest (AHC) detected. Skipping automated verification.");
                return true;
            }

            bool allPassed = true;
            foreach (var sample in samples)
            {
                 var result = RunProcess("dotnet", "run --no-build", solutionDir, sample.Input);
                 if (result.ExitCode != 0)
                 {
                     Log($"  Runtime Error on sample");
                     allPassed = false;
                     continue;
                 }

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
