using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for issue: lambdas capturing Java non-effectively-final variables.
///
/// C# closures may capture (1) for-each loop variables and (2) locals/parameters
/// reassigned before the lambda. Java requires captured variables to be effectively
/// final, so javac reports:
///   "local variables referenced from a lambda expression must be final or effectively final"
///
/// Expected Java output: the captured variable is hoisted into a one-element holder
/// array (String[] _input = { input }; / int[] _options = { options; }) emitted at the
/// method body start (parameters) or the loop body top (for-each variables), and all
/// references — including the reassignments themselves — read/write the holder element.
/// </summary>
public class LambdaCaptureEffectivelyFinalTests
{
    private readonly ITestOutputHelper _out;

    public LambdaCaptureEffectivelyFinalTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void LambdaCapturingReassignedParameter_AndForeachVariable_UsesHolders()
    {
        // Mirrors d:\csharp-RegularExpressions\tests\AttRegexTests.cs: a lambda inside
        // `foreach (int options in ...)` capturing the parameter `input` (reassigned at
        // method top: `input = ""`) and the for-each variable `options`.
        var result = Convert("""
using System;

class Test
{
    static void M(string input, string pattern)
    {
        if (input == "NULL") input = "";
        foreach (int options in new[] { 1, 2 })
        {
            Action a = () => Console.WriteLine(input + pattern + options);
            a();
        }
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The reassigned parameter must be hoisted into a holder at the body start,
        // and the reassignment must target the holder element.
        Assert.Contains("String[] _input = new String[] { input };", code);
        Assert.Contains("_input[0] = \"\"", code);
        // The for-each variable must be hoisted into a holder at the loop body top.
        Assert.Contains("int[] _options = new int[] { options };", code);
        // Lambda references must read the holder elements.
        Assert.Contains("_input[0] + pattern + _options[0]", code);

        // The generated file must compile with javac.
        using var temp = new TempDir();
        var samplePath = Path.Combine(temp.Path, "Test.java");
        var outDir = Path.Combine(temp.Path, "classes");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(samplePath, code);

        var javac = RunProcess("javac", $"-d \"{outDir}\" \"{samplePath}\"");
        Assert.True(javac.ExitCode == 0, javac.Output);
    }

    [Fact]
    public void ForeachVariableCapturedByNestedLambda_UsesHolder()
    {
        var result = Convert("""
using System;
using System.Collections.Generic;

class Test
{
    static void M(List<string> items)
    {
        foreach (string item in items)
        {
            Func<string> f = () => item + "!";
            Console.WriteLine(f());
        }
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("String[] _item = new String[] { item };", code);
        Assert.Contains("() -> _item[0] + \"!\"", code);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2j-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
