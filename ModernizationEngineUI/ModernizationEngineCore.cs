using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

/// <summary>
/// The structured result the OpenAI model returns for a single modernization request.
/// </summary>
public record ModernizationResult(
    string ModernizedCode,
    string Explanation,
    string Severity
);

/// <summary>
/// Describes one recognizable legacy anti-pattern: how to detect it in a method,
/// and how to guide the OpenAI model + Context7 lookup toward the right fix.
/// </summary>
public record AntiPatternRule(
    string Description,
    Func<MethodDeclarationSyntax, bool> Matches,
    string SystemPromptRules,
    string Context7Query);

/// <summary>
/// Core, UI-agnostic modernization pipeline logic: anti-pattern detection, async call-chain
/// propagation, Context7-grounded documentation lookup, and compilation verification. Kept
/// separate from Program.cs so the Blazor UI can call into this pipeline without depending on
/// hosting/orchestration code.
/// </summary>
public static class ModernizationEngineCore
{
    public static readonly AntiPatternRule[] AntiPatternRules =
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

    public static (MethodDeclarationSyntax Method, AntiPatternRule Rule)? FindAntiPattern(IEnumerable<MethodDeclarationSyntax> methods)
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
    public static CompilationUnitSyntax PropagateAsyncToCallers(CompilationUnitSyntax root, string asyncifiedMethodName)
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
    /// Verifies that the given file compiles. Rather than relying on .NET's "file-based
    /// programs" support (which only has implicit SDK/BCL references, not NuGet packages),
    /// this copies the file into a throwaway directory alongside a minimal .csproj and runs a
    /// real `dotnet build` there - so it works the same way regardless of the caller's own
    /// working directory (e.g. a console app's cwd vs. a Blazor app's hosting directory).
    /// </summary>
    public static async Task<(bool IsSuccess, string ErrorOutput)> VerifyBuildAsync(string filePath)
    {
        string sourceCode = await File.ReadAllTextAsync(filePath);
        string tempDir = Path.Combine(Path.GetTempPath(), "ModernizationVerify_" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempDir);

            const string csprojContent = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """;

            await File.WriteAllTextAsync(Path.Combine(tempDir, "VerifyBuild.csproj"), csprojContent);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Program.cs"), sourceCode);

            var buildProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "build",
                WorkingDirectory = tempDir,
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
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; a leftover temp folder isn't worth failing the request over.
            }
        }
    }

    /// <summary>
    /// Queries the Context7 MCP server (https://context7.com) for up-to-date library
    /// documentation, so the OpenAI model is grounded in real, current APIs rather than
    /// relying purely on training data. Returns null if Context7 is unavailable so the
    /// caller can gracefully continue without grounding.
    /// </summary>
    public static async Task<string?> GetContext7DocsAsync(IConfiguration configuration, string libraryName, string query)
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
