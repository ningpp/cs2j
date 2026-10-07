using System.Diagnostics;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C22 M2 §7.4 Java-half smoke: a representative sample spanning every lifted
/// shape family (binary/unary arithmetic, bool? three-valued logic, relational,
/// equality, GetValueOrDefault, lifted increment, lifted enum conversion) is
/// explicitized by the C22 preprocessor and then fed through the production
/// <see cref="ConversionPipeline"/>. The emitted Java must carry explicit null
/// guards (no BCL GetValueOrDefault surface, no implicit unboxing arithmetic on
/// the boxed representation) and, when a JDK is discoverable on this machine,
/// compile under <c>javac</c>.
/// </summary>
public class NumericBoundaryNullableJavaSmokeTests
{
    private const string SampleFileName = "C22JavaSmoke.cs";

    private const string SampleSource = """
        namespace C22Smoke;

        public sealed class C22JavaSmoke
        {
            public enum C22SmokeColor { Red, Green, Blue }

            public int? Add(int? a, int? b) => a + b;
            public int? Negate(int? a) => -a;
            public bool? And(bool? a, bool? b) => a & b;
            public bool? Or(bool? a, bool? b) => a | b;
            public bool? Not(bool? a) => !a;
            public bool Ge(int? a, int? b) => a >= b;
            public bool Eq(int? a, int? b) => a == b;
            public int Gov(int? a) => a.GetValueOrDefault();
            public int? Inc(int? start) { int? x = start; x++; return x; }
            public int? ColorToInt(C22SmokeColor? a) => (int?)a;
        }
        """;

    private static string CompatClasses =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "java", "csharptojava-compat", "target", "classes"));

    private static string ConfigPath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config", "TypeMappings.json"),
                @"d:\code\cs2j\config\TypeMappings.json",
            };
            return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }
    }

    private static (string CSharp, ConversionResult Java) RunPipeline()
    {
        var tree = CSharpSyntaxTree.ParseText(SampleSource);
        var references = new List<MetadataReference>();
        var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        foreach (var assembly in (trustedAssemblies ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(assembly))
            {
                references.Add(MetadataReference.CreateFromFile(assembly));
            }
        }

        var compilation = CSharpCompilation.Create(
            "c22-java-smoke",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);

        var options = new NumericBoundaryOptions
        {
            EnableNullable = true,
            EnableIncrementTargets = false,
            EnableDecimalOps = false,
            Strict = true,
        };
        var preprocessed = new NumericBoundaryPreprocessor().Rewrite(SampleSource, model, options);
        Assert.True(preprocessed.Changed, "C22 must explicitize the smoke sample");
        Assert.DoesNotContain(preprocessed.Diagnostics, d =>
            d.Severity is NumericBoundarySeverity.Error or NumericBoundarySeverity.Warning);
        Assert.DoesNotContain(preprocessed.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.NullableLiftResidual);

        var java = new ConversionPipeline().Convert(new ConversionRequest
        {
            SourceCode = preprocessed.OutputCode,
            FileName = SampleFileName,
            Options = new ConversionOptions { TypeMappingConfigPath = ConfigPath },
        });
        return (preprocessed.OutputCode, java);
    }

    [Fact]
    public void PreprocessedNullableSample_ConvertsToJava_WithExplicitNullGuards()
    {
        var (preprocessedCSharp, result) = RunPipeline();

        Assert.True(result.Success,
            "Java conversion must succeed." + Environment.NewLine
            + "--- Diagnostics ---" + Environment.NewLine
            + string.Join(Environment.NewLine, result.Diagnostics) + Environment.NewLine
            + "--- C# after C22 ---" + Environment.NewLine + preprocessedCSharp + Environment.NewLine
            + "--- Generated Java ---" + Environment.NewLine + result.GeneratedCode);

        var code = result.GeneratedCode;

        // The explicitized HasValue guards survive the Java translation as
        // reference null checks; .Value and lifted unbox sites route through
        // Nullable.value(...) so a null operand throws the same
        // IllegalStateException C# maps InvalidOperationException to (never a
        // raw NullPointerException from implicit unboxing).
        Assert.Contains("(a != null)", code);
        Assert.Contains("Nullable.value(a) + Nullable.value(b)", code);
        Assert.Contains("Nullable.value(a) >= Nullable.value(b)", code);
        Assert.Contains("Nullable.value(a) == Nullable.value(b)", code);
        Assert.Contains("!Nullable.value(a)", code);
        Assert.Contains("Nullable.value(a)", code);
        Assert.Contains(".ordinal()", code);
        Assert.Equal(0, CountOccurrences(code, "GetValueOrDefault"));
        Assert.DoesNotContain("getHasValue", code);
        Assert.DoesNotContain("?.", code);
        Assert.DoesNotContain("??", code);
        Assert.DoesNotContain("=>", code);

        // No implicit unboxing arithmetic or reference equality on the boxed
        // wrappers: operands must pass through the explicit Nullable guard.
        Assert.DoesNotContain("a + b", code);
        Assert.DoesNotContain("a >= b", code);
        Assert.DoesNotContain("a == b", code);

        // javac/java evidence when a JDK is present on this machine. The
        // harness executes the null/value matrix on the JVM, pinning null
        // propagation, false-absorption and default values with no NPE.
        if (TryLocateJavaTool("javac", out var javac)
            && TryLocateJavaTool("java", out var java))
        {
            Assert.True(Directory.Exists(CompatClasses),
                "compat target/classes missing; build csharptojava-compat first: " + CompatClasses);

            var (compileExit, compileOutput, workDir) = CompileJava(javac, code);
            try
            {
                Assert.True(compileExit == 0,
                    $"javac ({javac}) exited {compileExit}:\n{compileOutput}\n--- Generated Java ---\n{code}");

                var (runExit, runOutput) = RunJava(java, workDir);
                Assert.True(runExit == 0,
                    $"java ({java}) matrix harness exited {runExit}:\n{runOutput}");
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); }
                catch (IOException) { /* temp cleanup best-effort */ }
            }
        }
    }

    private const string JavaHarnessSource = """
        package C22Smoke;

        public final class C22JavaSmokeHarness {
            public static void main(String[] args) {
                C22JavaSmoke c = new C22JavaSmoke();

                if (c.add(null, 1) != null || c.add(2, null) != null)
                    throw new AssertionError("add null propagation");
                if (c.add(2, 3) != 5)
                    throw new AssertionError("add value path");
                if (c.negate(null) != null || c.negate(7) != -7)
                    throw new AssertionError("negate");

                if (c.ge(null, null) || c.ge(1, null) || !c.ge(2, 1))
                    throw new AssertionError("relational null-is-false grid");
                if (!c.eq(null, null) || c.eq(null, 1) || !c.eq(2, 2))
                    throw new AssertionError("equality double-null-true grid");

                if (c.and(false, null) != false)
                    throw new AssertionError("false & null must be false");
                if (c.and(null, null) != null || c.and(true, true) != true)
                    throw new AssertionError("bool? and grid");
                if (c.or(true, null) != true)
                    throw new AssertionError("true | null must be true");
                if (c.or(null, null) != null || c.or(false, false) != false)
                    throw new AssertionError("bool? or grid");
                if (c.not(null) != null || c.not(true) != false)
                    throw new AssertionError("lifted not");

                if (c.gov(null) != 0 || c.gov(42) != 42)
                    throw new AssertionError("GOV default/value paths");

                if (c.inc(null) != null || c.inc(41) != 42)
                    throw new AssertionError("lifted increment null/value paths");

                if (c.colorToInt(null) != null
                    || c.colorToInt(C22JavaSmoke.C22SmokeColor.Red) != 0)
                    throw new AssertionError("nullable enum conversion");
            }
        }
        """;

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static bool TryLocateJavaTool(string toolName, out string toolPath)
    {
        var exe = OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;
        string[] candidates =
        [
            Path.Combine(@"D:\Java\jdk-28-ea-11\bin", exe),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Java", "jdk-28", "bin", exe),
        ];
        var envHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(envHome))
        {
            candidates = [.. candidates, Path.Combine(envHome, "bin", exe)];
        }

        toolPath = candidates.FirstOrDefault(File.Exists) ?? string.Empty;
        return toolPath.Length > 0;
    }

    private static (int ExitCode, string Output, string WorkDir) CompileJava(
        string javac, string smokeJavaSource)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "cs2j-c22-javac-" + Guid.NewGuid().ToString("N"));
        var packageDir = Path.Combine(workDir, "C22Smoke");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "C22JavaSmoke.java"), smokeJavaSource);
        File.WriteAllText(Path.Combine(packageDir, "C22JavaSmokeHarness.java"), JavaHarnessSource);

        var psi = new ProcessStartInfo
        {
            FileName = javac,
            // Compat artifacts are JDK 28 preview class files; --release 28 +
            // --enable-preview are required to read them, and the compat
            // classes (Nullable guards) must be on the compile classpath.
            Arguments = $"--enable-preview --release 28 -cp \"{CompatClasses}\" -d \"{workDir}\" "
                + $"\"{Path.Combine(packageDir, "C22JavaSmoke.java")}\" "
                + $"\"{Path.Combine(packageDir, "C22JavaSmokeHarness.java")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        return (process.HasExited ? process.ExitCode : -1, stdout + stderr, workDir);
    }

    private static (int ExitCode, string Output) RunJava(string java, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = java,
            Arguments = "--enable-preview -cp \""
                + workDir + Path.PathSeparator + CompatClasses
                + "\" C22Smoke.C22JavaSmokeHarness",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        return (process.HasExited ? process.ExitCode : -1, stdout + stderr);
    }
}
