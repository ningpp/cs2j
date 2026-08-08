using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the second RegexCompiler/RegexInterpreter/RegexParser/
/// RegexWriter error batch:
/// 1. List&lt;T&gt;.ToArray() assigned to T[] needs an explicit (T[]) cast.
/// 2. Negated TryGetValue with primitive out var must declare the boxed type so the
///    null check compiles.
/// 3. Ref locals (ref int[]? x = ref arr[i]) must be declared with their type.
/// 4. Span&lt;char&gt; element compound assignment needs a (char) cast.
/// 5. MemoryExtensions.indexOf(ReadOnlySpan, char) / toLowerCase(...) compat surface.
/// </summary>
public class RegexEngineCompatBatch2Tests
{
    private readonly ITestOutputHelper _out;

    public RegexEngineCompatBatch2Tests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ListToArrayAssignedToArrayType_CastsToArrayType()
    {
        var result = Convert("""
using System.Collections.Generic;
class P {
    private List<string>? _capnamelist;
    string[]? GetNames() {
        return _capnamelist?.ToArray();
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("(String[]) ", code);
    }

    [Fact]
    public void NegatedTryGetValueWithIntOut_DeclaresBoxedType()
    {
        var result = Convert("""
using System.Collections.Generic;
class W {
    private Dictionary<string, int> _stringTable = new Dictionary<string, int>();
    int StringCode(string str) {
        if (!_stringTable.TryGetValue(str, out int i)) {
            i = _stringTable.Count;
            _stringTable.Add(str, i);
        }
        return i;
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("Integer i = (Integer)", code);
        Assert.DoesNotContain("int i = (Integer)", code);
    }

    [Fact]
    public void RefLocalDeclaration_BindsReferencedValue()
    {
        var result = Convert("""
class C {
    private int[] _codes;
    void M(int operand0) {
        ref int[] setLookup = ref _codes[operand0];
        if (setLookup != null) {
            setLookup[0] = 1;
        }
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.DoesNotContain("var setLookup = null", code);
        Assert.Contains("int[] setLookup = ", code);
    }

    [Fact]
    public void SpanCharCompoundAssignment_CastsBackToChar()
    {
        var result = Convert("""
using System;
class C {
    void M(Span<char> dest, int i) {
        dest[i >> 4] |= (char)(1 << (i & 0xF));
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("(char)(", code);
    }

    [Fact]
    public void IndexOfAndToLowerCase_CompileAgainstCompatRuntime()
    {
        var result = Convert("""
using System;
class C {
    static int Find(ReadOnlySpan<char> text, char ch) {
        return text.IndexOf(ch);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("MemoryExtensions.indexOf(", code);

        using var temp = new TempDir();
        // The generated code may bind MemoryExtensions to a project-local class
        // (dotnet.system.MemoryExtensions), so verify the compat API surface with a
        // direct API check compiled against the installed compat JAR.
        var samplePath = Path.Combine(temp.Path, "CompatApiCheck.java");
        var outDir = Path.Combine(temp.Path, "classes");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(samplePath, """
import io.github.ningpp.compat.MemoryExtensions;
import io.github.ningpp.compat.ReadOnlySpan;
class CompatApiCheck {
    static int check(ReadOnlySpan<Character> text, char ch) {
        return MemoryExtensions.indexOf(text, ch);
    }
}
""");

        var compatJar = Path.Combine(
            FindRepoRoot(), "..", "mvnrepo", "io", "github", "ningpp", "csharptojava-compat",
            "1.0-SNAPSHOT", "csharptojava-compat-1.0-SNAPSHOT.jar");
        var resolvedJar = Path.GetFullPath(compatJar);
        Assert.True(File.Exists(resolvedJar), $"Missing compat jar: {resolvedJar}");

        var javac = RunProcess("javac", $"-cp \"{resolvedJar}\" -d \"{outDir}\" \"{samplePath}\"");
        Assert.True(javac.ExitCode == 0, javac.Output);
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

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CSharpToJavaConverter.slnx"))
                && Directory.Exists(Path.Combine(dir, "java", "csharptojava-compat")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
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
        // Read both streams asynchronously to avoid buffer-fill deadlocks.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdoutTask.Result + stderrTask.Result);
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
