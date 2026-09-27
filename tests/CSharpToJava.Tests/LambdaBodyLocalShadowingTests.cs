using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for C#-allows / Java-forbids variable shadowing across lambda boundaries.
///
/// C# permits a local declared inside a lambda body to shadow a variable visible
/// from the enclosing scope (method parameter, method local, or enclosing lambda
/// parameter/local). Java forbids the shadowing, so javac reports:
///   "variable X is already defined in method ..."
///
/// The converter must rename the inner declaration and every reference that binds
/// to it, while leaving references to the outer variable untouched.
/// </summary>
public class LambdaBodyLocalShadowingTests
{
    private readonly ITestOutputHelper _out;

    public LambdaBodyLocalShadowingTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void LambdaBodyLocal_NoConflict_IsNotRenamed()
    {
        var result = Convert("""
using System;

class Test
{
    static void M(int options)
    {
        Action a = () =>
        {
            var s = "x";
            var count = 3;
            Console.WriteLine(s + count);
        };
        Console.WriteLine(options);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("var s = \"x\";", code);
        Assert.Contains("var count = 3;", code);
        Assert.DoesNotContain("s_1", code);
        Assert.DoesNotContain("count_1", code);
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
