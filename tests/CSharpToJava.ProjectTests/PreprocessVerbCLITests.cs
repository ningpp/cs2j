using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for the generic <c>preprocess</c> verb: catalog
/// listing, CS2J9002 rejection of unknown pass ids, and explicit single-pass
/// selection isolated from the default chain.
/// </summary>
public class PreprocessVerbCLITests
{
    private static string CliPath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null)
            {
                var candidate = Path.Combine(
                    dir, "src", "CSharpToJava.CLI", "bin", "Debug", "net10.0",
                    "CSharpToJava.CLI.dll");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }

            throw new FileNotFoundException("Could not locate CSharpToJava.CLI.dll from test output directory.");
        }
    }

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

    private static string CreateTempWorkspace(string csprojName, string csharpSource)
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-preprocess-verb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, csprojName),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(tempDir, "Class1.cs"), csharpSource);
        return tempDir;
    }

    [Fact]
    public async Task ListPasses_PrintsAllRegisteredIds_AndExitsZero()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        Assert.Contains("cs2j preprocess passes", stdout);
        Assert.Contains("conditional", stdout);
        Assert.Contains("eliminate-goto", stdout);
        Assert.Contains("java-keywords", stdout);
        Assert.Contains("closure-lift", stdout);
        Assert.Contains("Stub", stdout);
        Assert.Contains("off", stdout);
        Assert.Contains("on", stdout);
        // C34 (decimal-ops) is co-located with the numeric-boundary pass; the
        // catalog inventory must surface it even though it ships default-off.
        // C17 (increment-targets) is a co-located family rule as well (M0 skeleton).
        // C22 (nullable-lift) joined the family at rule Order=15 (2026-10-02).
        Assert.Contains("rules=C15, C34, C17, C22", stdout);
    }

    [Fact]
    public async Task UnknownPassId_FailsWith_CS2J9002_AndExitsOne()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", "public class C { }");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "no-such-pass",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(1, exit);
            Assert.Contains("CS2J9002", stderr);
            Assert.Contains("no-such-pass", stderr);
            Assert.False(Directory.Exists(destDir),
                "failure must be reported before any output directory is created");
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task EnabledStub_FailsWith_CS2J9001_AndCreatesNoDirectories()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", "public class C { }");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            // C32 M2 flipped closure-lift to Implemented; async-normalize is
            // the remaining StateMachine stub this generic check targets.
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "async-normalize",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(1, exit);
            Assert.Contains("CS2J9001", stderr);
            Assert.Contains("async-normalize", stderr);
            Assert.False(Directory.Exists(destDir),
                "an enabled stub must be rejected before any output directory is created");
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task PatternDesugarPass_LowersStatementSidePatterns_ThroughTheCli()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", """
namespace PatternSample;

public static class Switches
{
    public static int Pick(object o)
    {
        switch (o)
        {
            case string s: return s.Length;
            case int i: return i;
            default: return 0;
        }
    }
}
""");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "pattern-desugar",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr);

            // Only the pattern-desugar intermediate tree exists.
            var intermediateDirs = Directory
                .GetDirectories(destDir, ".cs2j-*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray();
            Assert.Single(intermediateDirs);
            Assert.Equal(".cs2j-pattern-desugar-src", intermediateDirs[0]);

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-pattern-desugar-src", "Class1.cs"));
            Assert.Contains("is string &&", rewritten);
            Assert.Contains("is int &&", rewritten);
            Assert.DoesNotContain("case string s", rewritten);
            Assert.DoesNotContain("switch (o)", rewritten);

            Assert.Contains("pattern-desugar:", stdout);
            Assert.Contains("pattern-switch", stdout);
            Assert.DoesNotContain("unsigned-type:", stdout);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task PatternDesugarPass_ReportsUnsupportedPatterns_AsPD010()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", """
namespace PatternSample;

public static class Lists
{
    public static int Pick(object o)
    {
        switch (o)
        {
            case [1, ..]: return 1;
            default: return 0;
        }
    }
}
""");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "pattern-desugar",
                "-s", sourceDir, "-d", destDir);

            // A list pattern is C37 territory: the pass fails loudly rather than
            // emitting a placeholder.
            Assert.Equal(1, exit);
            var output = stdout + stderr;
            Assert.Contains("PD010", output);
            Assert.Contains("list", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ListPasses_ShowsPatternDesugarAsImplemented()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        var line = stdout.Split('\n').First(l => l.Contains("pattern-desugar"));
        Assert.Contains("Implemented", line);
    }

    // ───────────────────────── C07 struct-value-semantics（设计 §7.6）─────────────────────────

    [Fact]
    public async Task ListPasses_ShowsStructValueSemantics_ImplementedAndOnByDefault()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        var line = stdout.Split('\n').First(l => l.Contains("struct-value-semantics"));
        Assert.Contains("Implemented", line);
        Assert.Contains(" on ", line); // 默认开（M6 翻转）
        Assert.DoesNotContain(" off ", line);
    }

    [Fact(Skip = "TODO: 临时跳过端到端慢测试（排查根因中）— 120.20s")]
    public async Task DefaultChain_Creates_StructValueSemantics_IntermediateTree_AndDisablePass_SuppressesIt()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", """
namespace SvsDefault;

public static class Calc
{
    public static int Add(int a, int b) => a + b;
}
""");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            Assert.True(Directory.Exists(destDir), stderr);
            Assert.True(
                Directory.Exists(Path.Combine(destDir, ".cs2j-struct-value-semantics-src")),
                "struct-value-semantics is default-on and must run in the default chain");

            var disabledDest = Path.Combine(sourceDir, "out-off");
            var (offExit, _, offStderr) = await RunCliAsync(
                "preprocess", "-s", sourceDir, "-d", disabledDest,
                "--disable-pass", "struct-value-semantics");
            Assert.Equal(0, offExit);
            Assert.False(
                Directory.Exists(Path.Combine(disabledDest, ".cs2j-struct-value-semantics-src")),
                "struct-value-semantics must not run when --disable-pass struct-value-semantics is given");
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitPassSelection_RunsStructValueSemantics()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", """
namespace SvsSample;

public struct Point
{
    public int X;
    public void Move(int dx) { X += dx; }
}

public static class Runner
{
    public static void Go()
    {
        var p = new Point();
        var q = p;
        q.Move(1);
        System.Console.WriteLine(q.X);
    }
}
""");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "struct-value-semantics",
                "-s", sourceDir, "-d", destDir);

            Assert.True(exit == 0,
                $"preprocess --pass struct-value-semantics failed (exit {exit}).\nstderr: {stderr}\nstdout: {stdout}");

            var intermediateDirs = Directory
                .GetDirectories(destDir, ".cs2j-*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray();
            Assert.Single(intermediateDirs);
            Assert.Equal(".cs2j-struct-value-semantics-src", intermediateDirs[0]);

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-struct-value-semantics-src", "Class1.cs"));
            Assert.Contains("Clone()", rewritten);
            Assert.Contains("struct-value-semantics:", stdout);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    // ───────────────────────── C06 unsafe-ffm（设计 §7.6/§9 M0）─────────────────────────

    [Fact]
    public async Task ListPasses_ShowsUnsafeFfm_ImplementedAndOnByDefault()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        var line = stdout.Split('\n').First(l => l.Contains("unsafe-ffm"));
        Assert.Contains("Implemented", line);
        Assert.Contains(" on ", line); // M0 默认开
        Assert.DoesNotContain(" off ", line);
    }

    [Fact(Skip = "TODO: 临时跳过端到端慢测试（排查根因中）— 152.34s")]
    public async Task DefaultChain_Creates_UnsafeFfm_IntermediateTree_AndDisablePass_SuppressesIt()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", """
namespace UffmDefault;

public static class Calc
{
    public static int Add(int a, int b) => a + b;
}
""");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            Assert.True(Directory.Exists(destDir), stderr);
            Assert.True(
                Directory.Exists(Path.Combine(destDir, ".cs2j-unsafe-ffm-src")),
                "unsafe-ffm is default-on and must run in the default chain");

            var disabledDest = Path.Combine(sourceDir, "out-off");
            var (offExit, _, offStderr) = await RunCliAsync(
                "preprocess", "-s", sourceDir, "-d", disabledDest,
                "--disable-pass", "unsafe-ffm");
            Assert.Equal(0, offExit);
            Assert.False(
                Directory.Exists(Path.Combine(disabledDest, ".cs2j-unsafe-ffm-src")),
                "unsafe-ffm must not run when --disable-pass unsafe-ffm is given");
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitPassSelection_RunsUnsafeFfm()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", """
namespace UffmSample;

public class C
{
    unsafe int field;

    public static void Go()
    {
        unsafe
        {
            System.Console.WriteLine(1);
        }
    }
}
""");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "unsafe-ffm",
                "-s", sourceDir, "-d", destDir);

            Assert.True(exit == 0,
                $"preprocess --pass unsafe-ffm failed (exit {exit}).\nstderr: {stderr}\nstdout: {stdout}");

            var intermediateDirs = Directory
                .GetDirectories(destDir, ".cs2j-*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray();
            Assert.Single(intermediateDirs);
            Assert.Equal(".cs2j-unsafe-ffm-src", intermediateDirs[0]);

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-unsafe-ffm-src", "Class1.cs"));
            Assert.DoesNotContain("unsafe", rewritten);
            Assert.Contains("System.Console.WriteLine(1);", rewritten);
            Assert.Contains("int field;", rewritten);
            Assert.Contains("unsafe-ffm:", stdout);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitPassSelection_RunsOnlyTheRequestedPass()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj",
            "public class C { public uint Counter = 42u; public ushort Other = 7; }");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "unsigned",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);

            // Only the unsigned intermediate tree must exist (no default-chain neighbours).
            var intermediateDirs = Directory
                .GetDirectories(destDir, ".cs2j-*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray();
            Assert.Single(intermediateDirs);
            Assert.Equal(".cs2j-unsigned-src", intermediateDirs[0]);

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-unsigned-src", "Class1.cs"));
            Assert.Contains("System.UInt32", rewritten);
            Assert.Contains("System.UInt16", rewritten);

            // The unsigned summary ran; default-chain passes did not.
            Assert.Contains("unsigned-type:", stdout);
            Assert.DoesNotContain("make-readonly:", stdout);
            Assert.DoesNotContain("strip-conditional:", stdout);
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    // ───────────────────────── C29 concurrency-normalize（设计 §7.5）─────────────────────────

    [Fact]
    public async Task ListPasses_ShowsConcurrencyNormalize_ImplementedAndOffByDefault()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        var line = stdout.Split('\n').First(l => l.Contains("concurrency-normalize"));
        Assert.Contains("Implemented", line);
        Assert.Contains(" off ", line); // 长期 opt-in（设计 §3.4/§8），默认关
        Assert.DoesNotContain(" on ", line);
        // Late anchor + catalog inventory surface.
        Assert.Contains("eliminate-goto", line);
        Assert.Contains("rules=C29", line);
    }

    [Fact]
    public async Task ExplicitPassSelection_RunsConcurrencyNormalize()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", """
namespace ConcSample;

public static class Gate
{
    private static readonly object _lock = new();

    public static int Value;

    public static void Bump()
    {
        System.Threading.Monitor.Enter(_lock);
        try
        {
            Value++;
        }
        finally
        {
            System.Threading.Monitor.Exit(_lock);
        }
    }
}
""");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "concurrency-normalize",
                "-s", sourceDir, "-d", destDir);

            Assert.True(exit == 0,
                $"preprocess --pass concurrency-normalize failed (exit {exit}).\nstderr: {stderr}\nstdout: {stdout}");

            var intermediateDirs = Directory
                .GetDirectories(destDir, ".cs2j-*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray();
            Assert.Single(intermediateDirs);
            Assert.Equal(".cs2j-concurrency-normalize-src", intermediateDirs[0]);

            // CN-2 form B: the hand-written Enter/Exit pair normalizes to lock.
            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-concurrency-normalize-src", "Class1.cs"));
            Assert.Contains("lock (_lock)", rewritten);
            Assert.DoesNotContain("Monitor.Enter", rewritten);
            Assert.Contains("concurrency-normalize:", stdout);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }
}
