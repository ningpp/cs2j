using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;

namespace CSharpToJava.Tests;

public class ToStringFormatTests
{
    [Fact]
    public void ByteToStringX2_GeneratesMathHelperFormatNumeric()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(byte b)
    {
        return b.ToString(""X2"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.valueOf(b)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteToStringNoArgs_GeneratesStringValueOf()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(byte b)
    {
        return b.ToString();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("String.valueOf(b)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MathHelper.formatNumeric", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IntToStringX2_GeneratesMathHelperFormatNumeric()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(int value)
    {
        return value.ToString(""X2"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IntToStringD8_GeneratesMathHelperFormatNumeric()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(int value)
    {
        return value.ToString(""D8"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleToStringF2_GeneratesMathHelperFormatNumeric()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(double value)
    {
        return value.ToString(""F2"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleToStringCustomHashFormat_RunsAgainstCompatRuntime()
    {
        var result = Convert("""
using System.Globalization;

public class Sample
{
    public static string Format(double value)
    {
        return value.ToString("#.##########", CultureInfo.InvariantCulture);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("MathHelper.formatNumeric(\"#.##########\", value)", code, StringComparison.Ordinal);

        using var temp = new TempDir();
        var samplePath = Path.Combine(temp.Path, "Sample.java");
        var runnerPath = Path.Combine(temp.Path, "Runner.java");
        var outDir = Path.Combine(temp.Path, "classes");
        var dotnetSystemDir = Path.Combine(temp.Path, "dotnet", "system");
        var dotnetSystemStubPath = Path.Combine(dotnetSystemDir, "Placeholder.java");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(dotnetSystemDir);

        File.WriteAllText(samplePath, code);
        File.WriteAllText(runnerPath, """
public class Runner {
    public static void main(String[] args) {
        System.out.print(Sample.format(1.23456789d));
    }
}
""");
        File.WriteAllText(dotnetSystemStubPath, "package dotnet.system; public final class Placeholder { }");

        var repoRoot = FindRepoRoot();
        var compatProject = Path.Combine(repoRoot, "java", "csharptojava-compat", "pom.xml");
        var compatClasses = Path.Combine(repoRoot, "java", "csharptojava-compat", "target", "classes");
        var mvn = RunProcess(FindRequiredExecutable("mvn"), $"-q -f \"{compatProject}\" -DskipTests package");
        Assert.True(mvn.ExitCode == 0, mvn.Output);

        var classPath = string.Join(Path.PathSeparator, compatClasses, outDir);
        var sources = string.Join(" ", new[] { dotnetSystemStubPath, samplePath, runnerPath }.Select(path => $"\"{path}\""));
        var javac = RunProcess(FindRequiredExecutable("javac"), $"-cp \"{compatClasses}\" -d \"{outDir}\" {sources}");
        Assert.True(javac.ExitCode == 0, javac.Output);

        var java = RunProcess(FindRequiredExecutable("java"), $"-cp \"{classPath}\" Runner");
        Assert.True(java.ExitCode == 0, java.Output);
        Assert.Equal("1.23456789", java.Output.Trim());
    }

    [Fact]
    public void LongToStringX16_GeneratesMathHelperFormatNumeric()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(long value)
    {
        return value.ToString(""X16"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SByteToStringX2_GeneratesMaskedFormatNumeric()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(sbyte b)
    {
        return b.ToString(""X2"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UShortToStringX4_GeneratesMaskedFormatNumeric()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(ushort value)
    {
        return value.ToString(""X4"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("& 0xFFFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteToStringWithProvider_StripsProvider()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(byte b)
    {
        return b.ToString(""X2"", System.Globalization.CultureInfo.InvariantCulture);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CultureInfo", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DateTimeToStringRoundtrip_UsesCompatToStringFormat()
    {
        var result = Convert(@"
using System;

public class Sample
{
    public string Format(DateTime value)
    {
        return value.ToString(""o"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("value.toString(\"o\")", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("value.format(DateTimeFormatter.ofPattern(\"o\"))", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IntToStringWithProviderOnly_GeneratesStringValueOf()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(int value)
    {
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("String.valueOf(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MathHelper.formatNumeric", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteToStringX2_UriEncodingScenario()
    {
        // Simulates the real-world URI encoding scenario where byte.ToString("X2")
        // is used to produce hex strings for percent-encoding
        var result = Convert(@"
public class Sample
{
    public string UriEncode(byte[] bytes)
    {
        string result = """";
        foreach (byte b in bytes)
        {
            result += ""%"" + b.ToString(""X2"");
        }
        return result;
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
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

    private static string FindRequiredExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), name + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException($"{name} is required for this test.");
    }

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
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
