using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for the C10 attribute-normalize pass:
/// catalog listing with default-on status (flipped 2026-10-06 after the
/// §8 gates), opt-out via --disable-pass, explicit --pass selection,
/// enable/disable conflict, the loose-directory self-containment gate
/// (CS2J1390), and the --strict escalation matrix (CS2J1302 warning -> error).
/// </summary>
public class AttributeNormalizeCLITests
{
    private static string CliPath => TestCliPaths.LocateCli();

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{CliPath}\" {string.Join(' ', args.Select(a => $"\"{a}\""))}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private const string ProjectFile =
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>disable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private static string CreateWorkspace(params (string File, string Content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "cs2j-an-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (file, content) in files)
            File.WriteAllText(Path.Combine(dir, file), content);
        return dir;
    }

    private static string SelfContainedProjectSource =>
        """
        using System.Runtime.CompilerServices;
        public class Api
        {
            public void Log(string msg, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerArgumentExpression("msg")] string expr = "") {}
        }
        public class Runner
        {
            public void Go() { new Api().Log("hi"); }
        }
        """;

    // ───────────────────────── list-passes ─────────────────────────

    [Fact]
    public async Task ListPasses_ShowsAttributeNormalize_Implemented_DefaultOn_WithC10Catalog()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        var line = stdout.Split('\n').First(l => l.Contains("attribute-normalize"));
        Assert.Contains("Implemented", line);
        Assert.Contains(" on ", line);
        Assert.DoesNotContain(" off ", line);
        Assert.Contains("rules=C10", line);
    }

    // ───────────────────────── default on / opt-out ─────────────────────────

    [SlowE2EFact(171.9)]
    public async Task DefaultChain_Creates_AttributeNormalizeIntermediate()
    {
        var sourceDir = CreateWorkspace(("Lib.csproj", ProjectFile), ("Class1.cs", SelfContainedProjectSource));
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", sourceDir, "-d", destDir);

            Assert.True(exit == 0, $"default chain failed:\n{stderr}\n{stdout}");
            var intermediate = Path.Combine(destDir, ".cs2j-attribute-normalize-src");
            Assert.True(Directory.Exists(intermediate),
                "attribute-normalize flipped default-on (2026-10-06) and must run in the default chain");

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(intermediate, "Class1.cs"));
            Assert.DoesNotContain("CallerMemberName", rewritten);
            Assert.Contains("\"Go\"", rewritten);
            Assert.Contains("attribute-normalize:", stdout);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [SlowE2EFact(159.7)]
    public async Task DisablePass_OptsOutOf_AttributeNormalizeIntermediate()
    {
        // 独立工作区：与默认链用例分开，避免上一轮预处理支撑文件残留导致
        // extension-methods 支撑类重复定义（CS0101/CS0111）。
        var sourceDir = CreateWorkspace(("Lib.csproj", ProjectFile), ("Class1.cs", SelfContainedProjectSource));
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--disable-pass", "attribute-normalize",
                "-s", sourceDir, "-d", destDir);

            Assert.True(exit == 0, $"--disable-pass run failed:\n{stderr}");
            Assert.False(Directory.Exists(
                Path.Combine(destDir, ".cs2j-attribute-normalize-src")),
                "--disable-pass attribute-normalize must keep the pass out of the chain");
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [SlowE2EFact(83.8)]
    public async Task EnablePass_AddsAttributeNormalize_ToDefaultChain()
    {
        var sourceDir = CreateWorkspace(("Lib.csproj", ProjectFile), ("Class1.cs", SelfContainedProjectSource));
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--enable-pass", "attribute-normalize",
                "-s", sourceDir, "-d", destDir);

            Assert.True(exit == 0, $"--enable-pass run failed:\n{stderr}\n{stdout}");
            var intermediate = Path.Combine(destDir, ".cs2j-attribute-normalize-src");
            Assert.True(Directory.Exists(intermediate),
                "attribute-normalize must run when explicitly enabled");

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(intermediate, "Class1.cs"));
            Assert.DoesNotContain("CallerMemberName", rewritten);
            Assert.Contains("\"Go\"", rewritten);
            Assert.Contains("attribute-normalize:", stdout);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    // ───────────────────────── explicit --pass ─────────────────────────

    [Fact]
    public async Task ExplicitPassSelection_FillsCallerSites_InIsolatedTree()
    {
        var sourceDir = CreateWorkspace(("Lib.csproj", ProjectFile), ("Class1.cs", SelfContainedProjectSource));
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "attribute-normalize",
                "-s", sourceDir, "-d", destDir);

            Assert.True(exit == 0, $"--pass run failed:\n{stderr}\n{stdout}");
            Assert.Equal(string.Empty, stderr);

            var intermediateDirs = Directory
                .GetDirectories(destDir, ".cs2j-*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray();
            Assert.Single(intermediateDirs);
            Assert.Equal(".cs2j-attribute-normalize-src", intermediateDirs[0]);

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-attribute-normalize-src", "Class1.cs"));
            Assert.DoesNotContain("CallerMemberName", rewritten);
            Assert.DoesNotContain("CallerFilePath", rewritten);
            Assert.DoesNotContain("CallerLineNumber", rewritten);
            Assert.DoesNotContain("CallerArgumentExpression", rewritten);
            Assert.Contains("\"Go\"", rewritten);
            Assert.Contains("\"hi\"", rewritten);
            Assert.Contains("attribute-normalize:", stdout);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    // ───────────────────────── --disable-pass conflict ─────────────────────────

    [Fact]
    public async Task EnableAndDisableSamePass_FailsWith_CS2J9002_AndCreatesNoDirectories()
    {
        var sourceDir = CreateWorkspace(("Lib.csproj", ProjectFile), ("Class1.cs", SelfContainedProjectSource));
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess",
                "--enable-pass", "attribute-normalize",
                "--disable-pass", "attribute-normalize",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(1, exit);
            Assert.Contains("CS2J9002", stderr);
            Assert.Contains("attribute-normalize", stderr);
            Assert.False(Directory.Exists(destDir),
                "the conflict must be reported before any output directory is created");
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    // ───────────────────────── loose-directory CS2J1390 gate ─────────────────────────

    [Fact]
    public async Task LooseMode_CrossFilePlan_Warns_CS2J1390_AndLeavesInputsUnchanged()
    {
        var loggerCs =
            """
            using System.Runtime.CompilerServices;
            public class Logger
            {
                public static void M([CallerMemberName] string member = "") {}
            }
            """;
        var programCs =
            """
            public class Runner
            {
                public void Go() { Logger.M(); }
            }
            """;
        var sourceDir = CreateWorkspace(("Logger.cs", loggerCs), ("Program.cs", programCs));
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "attribute-normalize",
                "-s", sourceDir, "-d", destDir);

            // Warning severity: the pass still exits zero.
            Assert.Equal(0, exit);
            Assert.Contains("CS2J1390", stderr);
            Assert.Contains("warning", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("caller:", stderr);

            var intermediate = Path.Combine(destDir, ".cs2j-attribute-normalize-src");
            var rewrittenLogger = await File.ReadAllTextAsync(
                Path.Combine(intermediate, "Logger.cs"));
            var rewrittenProgram = await File.ReadAllTextAsync(
                Path.Combine(intermediate, "Program.cs"));
            Assert.Contains("CallerMemberName", rewrittenLogger);
            Assert.DoesNotContain("\"Go\"", rewrittenProgram);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task LooseMode_SingleFilePlan_AppliesWithout_CS2J1390()
    {
        var source =
            """
            using System.Runtime.CompilerServices;
            public class Api
            {
                public void Log([CallerMemberName] string member = "") {}
            }
            public class Runner
            {
                public void Go() { new Api().Log(); }
            }
            """;
        var sourceDir = CreateWorkspace(("File.cs", source));
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "attribute-normalize",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            Assert.DoesNotContain("CS2J1390", stderr);

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-attribute-normalize-src", "File.cs"));
            Assert.DoesNotContain("CallerMemberName", rewritten);
            Assert.Contains("\"Go\"", rewritten);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    // ───────────────────────── --strict escalation (CS2J1302 dynamic) ─────────────────────────

    private const string DynamicSiteSource =
        """
        using System.Runtime.CompilerServices;
        public class Api
        {
            public static void Log(string msg, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) {}
        }
        public class Runner
        {
            public void Go()
            {
                dynamic d = "x";
                Api.Log(d);
            }
        }
        """;

    [Fact]
    public async Task DynamicSite_Warns_CS2J1302_ByDefault()
    {
        var sourceDir = CreateWorkspace(("Lib.csproj", ProjectFile), ("Class1.cs", DynamicSiteSource));
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "attribute-normalize",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            Assert.Contains("CS2J1302", stderr);
            Assert.Contains("warning", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task DynamicSite_Strict_Escalates_CS2J1302_ToError_AndFails()
    {
        var sourceDir = CreateWorkspace(("Lib.csproj", ProjectFile), ("Class1.cs", DynamicSiteSource));
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "attribute-normalize", "--strict",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(1, exit);
            Assert.Contains("CS2J1302", stderr);
            Assert.Contains("error", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }
}
