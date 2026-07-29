using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAI;
using OpenAI.Chat;


partial class Program
{
    static async Task Main(string[] args)
    {   
        IConfiguration configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .AddEnvironmentVariables()
            .Build();

        string inputFilePath = args.Length > 0 ? args[0] : "LegacyCode1.cs";
        string legacyCode = File.ReadAllText(inputFilePath);

        // 1. Parse code into AST
        SyntaxTree tree = CSharpSyntaxTree.ParseText(legacyCode);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

        // 2. Find the first method matching any known anti-pattern
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
        var match = FindAntiPattern(methods);

        if (match == null)
        {
            Console.WriteLine("No anti-patterns found.");
            return;
        }

        MethodDeclarationSyntax targetMethod = match.Value.Method;
        AntiPatternRule rule = match.Value.Rule;

        Console.WriteLine($"=== FOUND TARGET BLOCK ({rule.Description}) ===");
        Console.WriteLine(targetMethod.ToFullString());

        string apiKey = configuration["OpenAI:ApiKey"]
            ?? throw new InvalidOperationException("OpenAI API key not found.");

        ChatClient chatClient = new(model: "gpt-4o", apiKey: apiKey);

        // 3. Define Structured Output Schema
        string schemaJson = @"{
          ""type"": ""object"",
          ""properties"": {
            ""ModernizedCode"": { ""type"": ""string"", ""description"": ""The fully refactored C# method"" },
            ""Explanation"": { ""type"": ""string"", ""description"": ""Why this change was made"" },
            ""Severity"": { ""type"": ""string"", ""enum"": [""Low"", ""Medium"", ""High""] }
          },
          ""required"": [""ModernizedCode"", ""Explanation"", ""Severity""],
          ""additionalProperties"": false
        }";

        ChatCompletionOptions options = new()
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "ModernizationResult",
                jsonSchema: BinaryData.FromString(schemaJson),
                jsonSchemaIsStrict: true
            ),
            Temperature = 0.1f
        };

        // Ground the model in current, accurate API docs via the Context7 MCP server,
        // instead of relying solely on the model's (possibly outdated) training data.
        string? context7Docs = await GetContext7DocsAsync(
            configuration,
            libraryName: ".NET",
            query: rule.Context7Query);

        string groundingSection = context7Docs is { Length: > 0 }
            ? $"\n\nUse the following up-to-date reference documentation (via Context7) to ensure the APIs you use are accurate:\n{context7Docs}"
            : string.Empty;

        string systemPrompt = $@"You are an expert C# modernization engine.
        {rule.SystemPromptRules}
        Return ONLY the single refactored method in the ModernizedCode field: no using directives, no namespace, no wrapping class. If the fix requires a type only available via an additional using directive (e.g. StringBuilder, List<T>), use its fully-qualified name (e.g. System.Text.StringBuilder) instead of adding a using directive.
        Rule: Do NOT rename the method or change its parameter list, even if convention would suggest an 'Async' suffix. You only see this one method in isolation and cannot see or update its callers elsewhere in the codebase, so renaming it would break the build." + groundingSection;

        string userPrompt = $"Refactor this method:\n\n{targetMethod.ToFullString()}";

        List<ChatMessage> messages = new()
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        Console.WriteLine("Sending request to OpenAI...");
        ChatCompletion completion = await chatClient.CompleteChatAsync(messages, options);

        // 4. Deserialize Response
        string jsonResponse = completion.Content[0].Text;
        var result = JsonSerializer.Deserialize<ModernizationResult>(jsonResponse);

        if (result == null || string.IsNullOrWhiteSpace(result.ModernizedCode))
        {
            Console.WriteLine("Error: Failed to parse LLM response.");
            return;
        }

        Console.WriteLine($"\n[Severity: {result.Severity}]");
        Console.WriteLine($"Explanation: {result.Explanation}\n");

        // 5. Replace Node in Roslyn Syntax Tree
        var newMethodSyntax = SyntaxFactory.ParseMemberDeclaration(result.ModernizedCode);

        if (newMethodSyntax == null)
        {
            Console.WriteLine("Error: Could not parse the modernized code as a valid member declaration. Raw response:");
            Console.WriteLine(result.ModernizedCode);
            return;
        }

        {
            // Guard: the LLM only ever sees this one method in isolation, so it has no way to
            // know about (or safely update) callers elsewhere in the codebase. If it renamed the
            // method anyway (e.g. adding an 'Async' suffix), force the original name back on so
            // existing call sites keep compiling. (Changing sync -> async Task is still safe for
            // callers that don't use the return value: the call becomes a discarded Task, which
            // compiles with only a CS4014 warning, not an error.)
            if (newMethodSyntax is MethodDeclarationSyntax newMethod && newMethod.Identifier.Text != targetMethod.Identifier.Text)
            {
                Console.WriteLine($"Warning: model renamed '{targetMethod.Identifier.Text}' to '{newMethod.Identifier.Text}'; reverting to the original name to keep callers working.");
                newMethodSyntax = newMethod.WithIdentifier(targetMethod.Identifier);
            }

            // Preserve leading/trailing indentation and comments from the target method
            newMethodSyntax = newMethodSyntax.WithTriviaFrom(targetMethod);

            // Replace old method node with modernized method node
            CompilationUnitSyntax newRoot = root.ReplaceNode(targetMethod, newMethodSyntax);

            // Ensure 'using System.Threading.Tasks;' is present at the top of the file
            // (System.Threading alone does not provide the Task type used by async methods).
            bool hasTasksUsing = root.Usings.Any(u => u.Name?.ToString() == "System.Threading.Tasks");
            if (!hasTasksUsing)
            {
                var tasksUsing = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(" System.Threading.Tasks"))
                                            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
                newRoot = newRoot.AddUsings(tasksUsing);
            }

            // If the method's signature became async (e.g. void -> Task, or T -> Task<T> to
            // properly fix the anti-pattern), every call site within this file needs `await`,
            // and every method containing such a call site needs to become async itself -
            // cascading all the way up to Main if necessary. Since the LLM only ever saw the
            // one target method, it has no way to make these changes itself.
            if (newMethodSyntax is MethodDeclarationSyntax modernizedMethod
                && modernizedMethod.Modifiers.Any(SyntaxKind.AsyncKeyword))
            {
                newRoot = PropagateAsyncToCallers(newRoot, targetMethod.Identifier.Text);
            }

            // Save modernized file
            string outputFilePath = Path.Combine(
                Path.GetDirectoryName(inputFilePath) ?? "",
                Path.GetFileNameWithoutExtension(inputFilePath) + ".Modernized.cs"
            );

            File.WriteAllText(outputFilePath, newRoot.ToFullString());

            Console.WriteLine($"=== SUCCESS ===");
            Console.WriteLine($"Updated code saved to: {outputFilePath}");

            // --- STAGE 3: AUTOMATED COMPILATION & SELF-HEALING LOOP ---
            Console.WriteLine("\n=== STAGE 3: VERIFYING COMPILATION ===");
            bool compiled = (await VerifyBuildAsync(outputFilePath)).IsSuccess;

            if (!compiled)
            {
                Console.WriteLine("⚠️ Compilation failed. Initiating Self-Healing Loop (Pass 1/1)...");

                // Feed the compiler error back to OpenAI to get a corrected method
                string repairPrompt = $"The modernized code failed to compile. Please fix the code based on these compiler errors:\n\n{(await VerifyBuildAsync(outputFilePath)).ErrorOutput}";
                
                messages.Add(new UserChatMessage(repairPrompt));
                ChatCompletion repairCompletion = await chatClient.CompleteChatAsync(messages, options);
                
                var repairResult = JsonSerializer.Deserialize<ModernizationResult>(repairCompletion.Content[0].Text);
                if (repairResult != null && !string.IsNullOrWhiteSpace(repairResult.ModernizedCode))
                {
                    var repairedSyntax = SyntaxFactory.ParseMemberDeclaration(repairResult.ModernizedCode);
                    if (repairedSyntax != null)
                    {
                        newRoot = root.ReplaceNode(targetMethod, repairedSyntax.WithTriviaFrom(targetMethod));
                        File.WriteAllText(outputFilePath, newRoot.ToFullString());
                        
                        Console.WriteLine("Re-verifying build after repair...");
                        compiled = (await VerifyBuildAsync(outputFilePath)).IsSuccess;
                    }
                }
            }

            if (compiled)
            {
                Console.WriteLine("✅ SUCCESS: Modernized code compiled cleanly!");
            }
            else
            {
                Console.WriteLine("❌ FAILED: Code requires manual developer intervention.");
            }
        }
    }
}

public record ModernizationResult(
    string ModernizedCode,
    string Explanation,
    string Severity
);

/// <summary>
/// Describes one recognizable legacy anti-pattern: how to detect it in a method,
/// and how to guide the OpenAI model + Context7 lookup toward the right fix.
/// </summary>
record AntiPatternRule(
    string Description,
    Func<MethodDeclarationSyntax, bool> Matches,
    string SystemPromptRules,
    string Context7Query);

partial class Program
{
    static readonly AntiPatternRule[] AntiPatternRules =
    [
        new AntiPatternRule(
            Description: "Blocking Thread.Sleep",
            Matches: m => m.DescendantNodes()
                           .OfType<InvocationExpressionSyntax>()
                           .Any(inv => inv.Expression.ToString().EndsWith("Thread.Sleep")),
            SystemPromptRules: """
                Refactor synchronous, blocking legacy code into modern asynchronous .NET 8 code.
                Rule 1: Replace Thread.Sleep with await Task.Delay.
                Rule 2: Update the method signature to async Task.
                """,
            Context7Query: "Replace Thread.Sleep with await Task.Delay in an async C# method"),

        new AntiPatternRule(
            Description: "Blocking .Result / .Wait() on a Task",
            Matches: m => m.DescendantNodes()
                           .OfType<MemberAccessExpressionSyntax>()
                           .Any(mae => mae.Name.Identifier.Text == "Result")
                       || m.DescendantNodes()
                           .OfType<InvocationExpressionSyntax>()
                           .Any(inv => inv.Expression is MemberAccessExpressionSyntax mae
                                       && mae.Name.Identifier.Text == "Wait"
                                       && inv.ArgumentList.Arguments.Count == 0),
            SystemPromptRules: """
                Refactor code that blocks on asynchronous calls into properly async code.
                Rule 1: Replace blocking `.Result` / `.Wait()` calls on Task/Task<T> with `await`.
                Rule 2: Update the method signature to async Task (or async Task<T> if it returns a value).
                """,
            Context7Query: "Avoid blocking on async code with .Result and .Wait(); use await instead in C#"),

        new AntiPatternRule(
            Description: "Non-generic collections (ArrayList/Hashtable)",
            Matches: m => m.DescendantNodes()
                           .OfType<ObjectCreationExpressionSyntax>()
                           .Any(oce => oce.Type is IdentifierNameSyntax id
                                       && (id.Identifier.Text == "ArrayList" || id.Identifier.Text == "Hashtable")),
            SystemPromptRules: """
                Refactor legacy, non-type-safe collection usage into modern generic collections.
                Rule 1: Replace ArrayList with List<T> and Hashtable with Dictionary<TKey, TValue>, inferring the right type arguments.
                Rule 2: Replace string concatenation inside loops with a StringBuilder.
                """,
            Context7Query: "Migrate ArrayList and Hashtable to generic List<T> and Dictionary<TKey,TValue> in C#"),
    ];

    static (MethodDeclarationSyntax Method, AntiPatternRule Rule)? FindAntiPattern(IEnumerable<MethodDeclarationSyntax> methods)
    {
        // Materialize once since each rule scans the same method list.
        var methodList = methods as IReadOnlyList<MethodDeclarationSyntax> ?? methods.ToList();

        foreach (var rule in AntiPatternRules)
        {
            var method = methodList.FirstOrDefault(rule.Matches);
            if (method != null)
            {
                return (method, rule);
            }
        }

        return null;
    }

    /// <summary>
    /// After the target method's signature becomes async, walks every call site of it within
    /// the same file and makes the whole call chain consistent: wraps each call in `await`, and
    /// promotes the enclosing method to `async` (adjusting its return type: void -> Task,
    /// T -> Task&lt;T&gt;) if it wasn't already async. This repeats breadth-first for the newly
    /// asyncified callers' own callers, cascading all the way up to Main if needed, since that's
    /// the only way to keep everything inside this single file compiling and properly awaited.
    /// </summary>
    static CompilationUnitSyntax PropagateAsyncToCallers(CompilationUnitSyntax root, string asyncifiedMethodName)
    {
        CompilationUnitSyntax currentRoot = root;
        var pending = new Queue<string>();
        var seen = new HashSet<string> { asyncifiedMethodName };
        pending.Enqueue(asyncifiedMethodName);

        while (pending.Count > 0)
        {
            string methodName = pending.Dequeue();

            while (true)
            {
                InvocationExpressionSyntax? invocation = currentRoot.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .FirstOrDefault(inv =>
                        GetInvokedMemberName(inv) == methodName &&
                        inv.Parent is not AwaitExpressionSyntax);

                if (invocation == null)
                {
                    break;
                }

                MethodDeclarationSyntax? enclosingMethod = invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                if (enclosingMethod == null)
                {
                    // Can't safely asyncify a call made outside of any method body (e.g. a field
                    // initializer). Leave it as-is rather than risk producing invalid code.
                    Console.WriteLine($"Warning: found a call to '{methodName}' outside of a method body; leaving it un-awaited.");
                    break;
                }

                var awaitExpression = SyntaxFactory.AwaitExpression(invocation.WithoutTrivia())
                    .WithAwaitKeyword(SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                    .WithTriviaFrom(invocation);

                MethodDeclarationSyntax updatedMethod = enclosingMethod.ReplaceNode(invocation, awaitExpression);
                bool alreadyAsync = updatedMethod.Modifiers.Any(SyntaxKind.AsyncKeyword);

                if (!alreadyAsync)
                {
                    updatedMethod = updatedMethod
                        .WithModifiers(updatedMethod.Modifiers.Add(
                            SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
                        .WithReturnType(GetAsyncReturnType(updatedMethod.ReturnType).WithTriviaFrom(updatedMethod.ReturnType));
                }

                currentRoot = currentRoot.ReplaceNode(enclosingMethod, updatedMethod);

                if (!alreadyAsync)
                {
                    string callerName = updatedMethod.Identifier.Text;
                    Console.WriteLine($"  -> propagating async up to caller '{callerName}'");
                    if (seen.Add(callerName))
                    {
                        pending.Enqueue(callerName);
                    }
                }
            }
        }

        return currentRoot;
    }

    static string? GetInvokedMemberName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
        _ => null
    };

    /// <summary>
    /// void -> Task, T -> Task&lt;T&gt;. Already-awaitable return types are left untouched.
    /// </summary>
    static TypeSyntax GetAsyncReturnType(TypeSyntax returnType)
    {
        string text = returnType.ToString().Trim();

        if (text == "void")
        {
            return SyntaxFactory.ParseTypeName("Task");
        }

        if (text == "Task" || text.StartsWith("Task<") ||
            text == "System.Threading.Tasks.Task" || text.StartsWith("System.Threading.Tasks.Task<"))
        {
            return returnType;
        }

        return SyntaxFactory.ParseTypeName($"Task<{text}>");
    }

    /// <summary>
    /// Runs dotnet build on the specified file and returns the result and any compiler errors.
    /// </summary>
    static async Task<(bool IsSuccess, string ErrorOutput)> VerifyBuildAsync(string filePath)
    {
        var buildProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build {filePath}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });

        if (buildProcess == null) 
        {
            return (false, "Failed to start the dotnet build process.");
        }

        await buildProcess.WaitForExitAsync();

        if (buildProcess.ExitCode == 0)
        {
            return (true, string.Empty);
        }
        else
        {
            // Note: 'dotnet build' often sends its error output to StandardOutput instead of StandardError
            string stdout = await buildProcess.StandardOutput.ReadToEndAsync();
            string stderr = await buildProcess.StandardError.ReadToEndAsync();
            return (false, stdout + "\n" + stderr);
        }
    }

    /// <summary>
    /// Queries the Context7 MCP server (https://context7.com) for up-to-date library
    /// documentation, so the OpenAI model is grounded in real, current APIs rather than
    /// relying purely on training data. Returns null if Context7 is unavailable so the
    /// caller can gracefully continue without grounding.
    /// </summary>
    static async Task<string?> GetContext7DocsAsync(IConfiguration configuration, string libraryName, string query)
    {
        try
        {
            var environmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables();

            // Optional: set via `dotnet user-secrets set "Context7:ApiKey" "ctx7sk-..."` for higher rate limits.
            string? apiKey = configuration["Context7:ApiKey"];
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                environmentVariables["CONTEXT7_API_KEY"] = apiKey;
            }

            var clientTransport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "Context7",
                Command = "npx",
                Arguments = ["-y", "@upstash/context7-mcp"],
                EnvironmentVariables = environmentVariables
            });

            await using var mcpClient = await McpClient.CreateAsync(clientTransport);

            CallToolResult resolveResult = await mcpClient.CallToolAsync("resolve-library-id", new Dictionary<string, object?>
            {
                ["libraryName"] = libraryName,
                ["query"] = query
            });

            string resolvedText = GetTextContent(resolveResult);
            Match match = Regex.Match(resolvedText, @"Context7-compatible library ID:\s*(\S+)");
            if (!match.Success)
            {
                Console.Error.WriteLine("Context7: could not resolve a library ID; continuing without grounding.");
                return null;
            }

            CallToolResult docsResult = await mcpClient.CallToolAsync("query-docs", new Dictionary<string, object?>
            {
                ["libraryId"] = match.Groups[1].Value,
                ["query"] = query
            });

            return GetTextContent(docsResult);
        }
        catch (Exception ex)
        {
            // Context7 is a nice-to-have grounding source, not a hard dependency.
            // If npx/node isn't installed, or the server can't be reached, keep going without it.
            Console.Error.WriteLine($"Context7 lookup skipped: {ex.Message}");
            return null;
        }
    }

    static string GetTextContent(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}