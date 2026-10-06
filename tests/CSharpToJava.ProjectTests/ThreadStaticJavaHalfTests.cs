using System.Diagnostics;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.ThreadStaticRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C09 §5.4/§8 gate 4 Java-half smoke: representative <c>[ThreadStatic]</c>
/// shapes (uninitialized value-type counter, explicit ThreadLocal factory,
/// read/write/compound sites, <c>IsValueCreated</c>/<c>Dispose</c>) are run
/// through the production ThreadStaticPreprocessor and then the
/// <see cref="ConversionPipeline"/>. The generated Java must compile under
/// <c>javac</c> (when a JDK is present) together with the compat
/// <c>Cs2jThreadLocal</c> support type, and a runtime harness pins the
/// per-thread isolation semantics that <c>[ThreadStatic]</c> promises.
/// </summary>
public class ThreadStaticJavaHalfTests
{
    private const string SampleFileName = "C09ThreadLocalBox.cs";

    private const string SampleSource = """
        using System;
        using System.Threading;

        namespace C09Smoke;

        public struct Vec
        {
            public int X;
            public int Y;
        }

        public sealed class C09ThreadLocalBox
        {
            // No inline initializer: C# keeps the parameterless holder (first
            // touch yields default(int)); the converter must synthesize a
            // per-thread () -> 0 factory because java.lang.ThreadLocal defaults
            // to null, which would NPE on unbox.
            [ThreadStatic] private static int plain;

            // A ThreadLocal field that users wrote directly is untouched by the pass.
            private static readonly ThreadLocal<int> initialized = new ThreadLocal<int>(() => 42);

            // Reference-type thread-static: per-thread default stays null, so the
            // holder stays parameterless after conversion (no synthetic factory).
            [ThreadStatic] private static string label;

            // Struct T thread-static: the converter factory supplies default(Vec).
            [ThreadStatic] private static Vec origin;

            public static int Bump() => ++plain;
            public static int PlainValue() => plain;
            public static int InitializedValue() => initialized.Value;
            public static bool IsCreated() => initialized.IsValueCreated;
            public static void ResetInitialized() { initialized.Dispose(); }

            public static string GetLabel() => label;
            public static void SetLabel(string value) => label = value;
            public static Vec GetOrigin() => origin;
            public static void SetOrigin(Vec value) => origin = value;
        }
        """;

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

    private static string RepoCompatRoot
    {
        get
        {
            var fromBaseDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "java", "csharptojava-compat", "src", "main", "java");
            var candidates = new[]
            {
                fromBaseDir,
                @"d:\code\cs2j\java\csharptojava-compat\src\main\java",
            };
            return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
        }
    }

    private static (string Preprocessed, ConversionResult Java) RunPipeline()
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
            "c09-java-smoke",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);

        var preprocessor = new ThreadStaticPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ThreadStaticOptions { Strict = true });
        var rewritten = preprocessor.RewriteFile(model);
        Assert.True(rewritten.Changed, "C09 must rewrite the ThreadStatic sample");
        Assert.DoesNotContain(rewritten.Diagnostics, d => d.Severity == ThreadStaticSeverity.Error);
        // 预处理产物保持规范 C#：无初始化器值类型字段用无参 holder（C# 首访 default(int)），
        // Java 侧的每线程默认工厂由转换器在对象创建点补。
        Assert.Contains("new global::System.Threading.ThreadLocal<int>()", rewritten.OutputCode);
        Assert.DoesNotContain("ThreadStaticAttribute", rewritten.OutputCode);
        Assert.DoesNotContain("[ThreadStatic]", rewritten.OutputCode);

        var java = new ConversionPipeline().Convert(new ConversionRequest
        {
            SourceCode = rewritten.OutputCode,
            FileName = SampleFileName,
            Options = new ConversionOptions { TypeMappingConfigPath = ConfigPath },
        });
        return (rewritten.OutputCode, java);
    }

    [Fact]
    public void PreprocessedThreadStatic_ConvertsToRunnableCompatHolder()
    {
        var (preprocessed, result) = RunPipeline();

        Assert.True(result.Success,
            "Java conversion must succeed." + Environment.NewLine
            + "--- Diagnostics ---" + Environment.NewLine
            + string.Join(Environment.NewLine, result.Diagnostics) + Environment.NewLine
            + "--- C# after C09 ---" + Environment.NewLine + preprocessed + Environment.NewLine
            + "--- Generated Java ---" + Environment.NewLine + result.GeneratedCode);

        var code = result.GeneratedCode;
        Assert.Contains("io.github.ningpp.compat.Cs2jThreadLocal", code);
        Assert.Contains("Cs2jThreadLocal<", code);
        Assert.Contains("getValue()", code);
        Assert.Contains("isValueCreated()", code);
        Assert.Contains("dispose()", code);
        // The default(int) factory must survive the Java translation.
        Assert.Contains("() -> 0", code);
        // Reference-type holder stays parameterless (its default IS null); the
        // struct holder gets a default-instance factory.
        Assert.Contains("Cs2jThreadLocal<String>()", code);
        Assert.Contains("() -> new Vec()", code);
        // The raw java.lang.ThreadLocal must not leak past the compat type, and
        // property sites must use the bean accessors (not the inherited get()).
        Assert.DoesNotContain("new ThreadLocal", code);
        Assert.DoesNotContain(".get()", code);

        if (TryLocateJavaTool("javac", out var javac)
            && TryLocateJavaTool("java", out var java))
        {
            var (compileExit, compileOutput, workDir) = CompileJava(javac, code);
            try
            {
                Assert.True(compileExit == 0,
                    $"javac ({javac}) exited {compileExit}:\n{compileOutput}\n--- Generated Java ---\n{code}");

                var (runExit, runOutput) = RunJava(java, workDir);
                Assert.True(runExit == 0,
                    $"java ({java}) thread-isolation harness exited {runExit}:\n{runOutput}");
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); }
                catch (IOException) { /* temp cleanup best-effort */ }
            }
        }
    }

    private const string JavaHarnessSource = """
        package C09Smoke;

        public final class C09ThreadLocalHarness {
            public static void main(String[] args) throws Exception {
                if (C09ThreadLocalBox.isCreated())
                    throw new AssertionError("factory must not run before first touch");
                if (C09ThreadLocalBox.initializedValue() != 42)
                    throw new AssertionError("factory value on main thread");
                if (!C09ThreadLocalBox.isCreated())
                    throw new AssertionError("IsValueCreated after first access");

                if (C09ThreadLocalBox.bump() != 1 || C09ThreadLocalBox.bump() != 2)
                    throw new AssertionError("per-thread counter starts at default(int)=0");
                if (C09ThreadLocalBox.plainValue() != 2)
                    throw new AssertionError("plain read after increments");

                // Reference-type thread-static defaults to null per thread.
                if (C09ThreadLocalBox.getLabel() != null)
                    throw new AssertionError("reference-type slot must start null");
                C09ThreadLocalBox.setLabel("main");
                // Struct T thread-static defaults to an all-zero instance per thread.
                Vec mainVec = new Vec();
                mainVec.X = 5;
                mainVec.Y = 6;
                C09ThreadLocalBox.setOrigin(mainVec);

                Object[] seen = new Object[5];
                Thread t = new Thread(() -> {
                    seen[0] = C09ThreadLocalBox.plainValue();
                    seen[1] = C09ThreadLocalBox.bump();
                    seen[2] = C09ThreadLocalBox.initializedValue();
                    seen[3] = C09ThreadLocalBox.getLabel();
                    Vec w = C09ThreadLocalBox.getOrigin();
                    seen[4] = (w.X == 0 && w.Y == 0) ? "vec-zero" : "vec-bad";
                    w.X = 7;
                    w.Y = 8;
                    C09ThreadLocalBox.setOrigin(w);
                    C09ThreadLocalBox.setLabel("worker");
                });
                t.start();
                t.join();
                if (seen[0] == null || (Integer) seen[0] != 0
                    || seen[1] == null || (Integer) seen[1] != 1
                    || seen[2] == null || (Integer) seen[2] != 42)
                    throw new AssertionError("worker thread must get independent value slots: "
                        + seen[0] + "," + seen[1] + "," + seen[2]);
                if (seen[3] != null)
                    throw new AssertionError("worker reference slot must start null, got " + seen[3]);
                if (!"vec-zero".equals(seen[4]))
                    throw new AssertionError("worker struct slot must start default(Vec)");
                if (C09ThreadLocalBox.plainValue() != 2)
                    throw new AssertionError("worker writes must not leak to the main thread");
                if (!"main".equals(C09ThreadLocalBox.getLabel()))
                    throw new AssertionError("worker reference write leaked");
                Vec afterJoin = C09ThreadLocalBox.getOrigin();
                if (afterJoin.X != 5 || afterJoin.Y != 6)
                    throw new AssertionError("worker struct write leaked");

                C09ThreadLocalBox.resetInitialized();
                if (C09ThreadLocalBox.initializedValue() != 42)
                    throw new AssertionError("factory must rerun for the slot after dispose");
            }
        }
        """;

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
            candidates = [.. candidates, Path.Combine(envHome, "bin", exe)];

        toolPath = candidates.FirstOrDefault(File.Exists) ?? string.Empty;
        return toolPath.Length > 0;
    }

    /// <summary>
    /// Splits one converted compilation unit into (typeName, source) pairs,
    /// keeping the shared package/import header. Brace counting skips string,
    /// character and comment contents.
    /// </summary>
    private static IReadOnlyList<(string Name, string Source)> SplitTopLevelTypes(string java)
    {
        var declStart = new System.Text.RegularExpressions.Regex(
            @"(?m)^((?:public|protected|private|final|abstract|static|sealed|non-sealed|strictfp)\s+)+(class|interface|enum|record)\s+(?<name>[A-Za-z_]\w*)");
        var first = declStart.Match(java);
        if (!first.Success)
        {
            return [];
        }

        var header = java[..first.Index];
        var results = new List<(string, string)>();
        var pos = first.Index;
        while (pos < java.Length)
        {
            var match = declStart.Match(java, pos);
            if (!match.Success)
            {
                break;
            }

            var openBrace = java.IndexOf('{', match.Index + match.Length);
            if (openBrace < 0)
            {
                break;
            }

            var depth = 0;
            var i = openBrace;
            var inLineComment = false;
            var inBlockComment = false;
            var inString = false;
            var inChar = false;
            for (; i < java.Length; i++)
            {
                var c = java[i];
                var next = i + 1 < java.Length ? java[i + 1] : '\0';
                if (inLineComment)
                {
                    if (c == '\n')
                    {
                        inLineComment = false;
                    }

                    continue;
                }

                if (inBlockComment)
                {
                    if (c == '*' && next == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }

                    continue;
                }

                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (inChar)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '\'')
                    {
                        inChar = false;
                    }

                    continue;
                }

                if (c == '/' && next == '/')
                {
                    inLineComment = true;
                    i++;
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '\'')
                {
                    inChar = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        i++;
                        break;
                    }
                }
            }

            var end = i;
            // Enums/records may carry a trailing semicolon after the brace.
            if (end < java.Length && java[end] == ';')
            {
                end++;
            }

            results.Add((match.Groups["name"].Value, header + java[match.Index..end]));
            pos = end;
        }

        return results;
    }

    private static (int ExitCode, string Output, string WorkDir) CompileJava(
        string javac, string generatedJava)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "cs2j-c09-javac-" + Guid.NewGuid().ToString("N"));
        var packageDir = Path.Combine(workDir, "C09Smoke");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "C09ThreadLocalHarness.java"), JavaHarnessSource);

        // Single-file conversion keeps every top-level type in one .java; the
        // project pipeline emits one file per type. Java forbids multiple public
        // top-level types per file, so mirror the project layout here.
        var generatedFiles = new List<string>();
        foreach (var (typeName, typeSource) in SplitTopLevelTypes(generatedJava))
        {
            var path = Path.Combine(packageDir, typeName + ".java");
            File.WriteAllText(path, typeSource);
            generatedFiles.Add(path);
        }

        var compatSource = Path.Combine(RepoCompatRoot,
            "io", "github", "ningpp", "compat", "Cs2jThreadLocal.java");
        // The converter annotates non-public members accessed cross-type.
        var compatAnnotation = Path.Combine(RepoCompatRoot,
            "io", "github", "ningpp", "compat", "CompatNonPublicProperty.java");
        var sourceArgs = string.Join(" ", generatedFiles
            .Append(Path.Combine(packageDir, "C09ThreadLocalHarness.java"))
            .Append(compatSource)
            .Append(compatAnnotation)
            .Where(File.Exists)
            .Select(p => $"\"{p}\""));
        var psi = new ProcessStartInfo
        {
            FileName = javac,
            Arguments = $"-d \"{workDir}\" {sourceArgs}",
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
            Arguments = "-cp . C09Smoke.C09ThreadLocalHarness",
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
