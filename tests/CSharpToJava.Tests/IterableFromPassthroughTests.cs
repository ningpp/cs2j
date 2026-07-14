using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace CSharpToJava.Tests;

public class IterableFromPassthroughTests
{
    /// <summary>
    /// When a C# IEnumerable<T> property returns a List<T> directly, the Java
    /// converter wraps the return in CSharpGenericIterable.from(). The calling
    /// code uses "as List<T>" which becomes "instanceof CSharpList". If from()
    /// wraps the list in an anonymous class, the instanceof check fails and
    /// mutations go to a copy instead of the original list.
    /// This test verifies that from() returns the original CSharpList so that
    /// instanceof checks succeed and mutations affect the original list.
    /// </summary>
    [Fact]
    public void CSharpGenericIterableFrom_ReturnsOriginalWhenAlreadyCSharpGenericIterable()
    {
        // First, verify the converter generates CSharpGenericIterable.from() wrapping
        var result = Convert("""
using System.Collections.Generic;

class Container {
    private List<string> items = new List<string>();

    public IEnumerable<string> Items {
        get {
            if (items == null)
                items = new List<string>();
            return items;
        }
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // Verify the converter generated the CSharpGenericIterable.from() wrapping
        Assert.Contains("CSharpGenericIterable.from", code);

        // Now verify the runtime behavior: from() must return the original CSharpList
        // so that instanceof CSharpList succeeds in calling code
        using var temp = new TempDir();
        var outDir = Path.Combine(temp.Path, "classes");
        Directory.CreateDirectory(outDir);

        var repoRoot = FindRepoRoot();
        var compatSrcRoot = Path.Combine(repoRoot, "java", "csharptojava-compat", "src", "main", "java");
        Assert.True(Directory.Exists(compatSrcRoot), $"Missing compat source root: {compatSrcRoot}");

        var testPath = Path.Combine(temp.Path, "IterableFromPassthroughTest.java");
        File.WriteAllText(testPath, """
import io.github.ningpp.compat.CSharpGenericIterable;
import io.github.ningpp.compat.CSharpList;

public class IterableFromPassthroughTest {
    public static void main(String[] args) {
        // Create a CSharpList (which implements CSharpGenericIterable)
        CSharpList<String> original = new CSharpList<String>();
        original.add("first");

        // Call from() — should return the original, not a wrapper
        CSharpGenericIterable<String> result = CSharpGenericIterable.from(original);

        // Verify the result is still a CSharpList (instanceof check must pass)
        if (!(result instanceof CSharpList)) {
            throw new RuntimeException("FAIL: CSharpGenericIterable.from() did not return a CSharpList");
        }

        // Verify mutations affect the original list
        result.add("second");
        if (original.size() != 2) {
            throw new RuntimeException("FAIL: original list not modified, size=" + original.size());
        }
        if (!"second".equals(original.get(1))) {
            throw new RuntimeException("FAIL: second element not in original list");
        }

        System.out.println("PASS: CSharpGenericIterable.from() returns original CSharpList");
    }
}
""");

        var javac = RunProcess("javac", $"-d \"{outDir}\" -sourcepath \"{compatSrcRoot}\" \"{testPath}\"");
        Assert.True(javac.ExitCode == 0, javac.Output);

        var java = RunProcess("java", $"-cp \"{outDir}\" IterableFromPassthroughTest");
        Assert.True(java.ExitCode == 0, $"Test failed: {java.Output}");
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
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
        using var proc = Process.Start(psi)!;
        var sb = new StringBuilder();
        sb.Append(proc.StandardOutput.ReadToEnd());
        sb.Append(proc.StandardError.ReadToEnd());
        proc.WaitForExit();
        return (proc.ExitCode, sb.ToString());
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir() => Directory.CreateDirectory(Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2j_" + Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
