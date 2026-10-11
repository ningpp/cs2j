using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for the generic <c>preprocess</c> verb: catalog
/// listing, CS2J9002 rejection of unknown pass ids, and explicit single-pass
/// selection isolated from the default chain.
/// </summary>
public class PreprocessVerbCLITests
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

    private static string CreateTempWorkspace(string csprojName, string csharpSource, string? langVersion = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-preprocess-verb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var csproj = langVersion is null
            ? """
              <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                  <TargetFramework>net10.0</TargetFramework>
                  <Nullable>disable</Nullable>
                </PropertyGroup>
              </Project>
              """
            : $"""
              <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                  <TargetFramework>net10.0</TargetFramework>
                  <Nullable>disable</Nullable>
                  <LangVersion>{langVersion}</LangVersion>
                </PropertyGroup>
              </Project>
              """;
        File.WriteAllText(Path.Combine(tempDir, csprojName), csproj);
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
        // PassStubCatalog.Stubs 已为空（最后一个 Stub dynamic-desugar 随 C25 M1 翻
        // Implemented）；列表不再可能出现 Stub 状态，改断言"全部 Implemented"。
        Assert.DoesNotContain("Stub", stdout);
        Assert.Contains("Implemented", stdout);
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

    // 注：EnabledStub_FailsWith_CS2J9001 样本测试已随最后一个 Stub（dynamic-desugar，
    // C25 M1）翻 Implemented 而移除——PassStubCatalog.Stubs 为空，CS2J9001 机制保留，
    // 再有 Stub 注册时补回同样本（与 CSharpToJava.Tests/PassStubCatalogTests 同步）。

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

    // ───────────────────────── Wave-0 接线（2026-10-04）：C24 默认开 + kill switch、C50 CLI 开关 ─────────────────────────

    [Fact]
    public async Task ExpressionDesugarPass_C24InitializerDesugar_IsOnByDefault()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", """
namespace InitSample;

public class Poco
{
    public int Value { get; set; }
    public string? Name { get; set; }
}

public static class Factory
{
    public static Poco Make() => new Poco { Value = 42, Name = "x" };
}
""");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "expression-desugar",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr);

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-expression-desugar-src", "Class1.cs"));
            // Wave-0: C24 wired default-on — the object initializer must be
            // statement-lowered; initializer-block syntax cannot survive.
            Assert.DoesNotContain("new Poco {", rewritten);
            Assert.Contains("new Poco()", rewritten);
            Assert.Contains("42", rewritten);
            Assert.Contains("expression-desugar:", stdout);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExpressionDesugarPass_NoInitializerDesugar_KeepsInitializerSyntax()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", """
namespace InitSample;

public class Poco
{
    public int Value { get; set; }
}

public static class Factory
{
    public static Poco Make() => new Poco { Value = 42 };
}
""");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "expression-desugar",
                "--no-initializer-desugar",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr);

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-expression-desugar-src", "Class1.cs"));
            // Kill switch: red-line byte-identical behavior, initializer preserved.
            Assert.Contains("new Poco { Value = 42 }", rewritten);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task MemberDesugarPass_EnableFieldKeywordRule_NormalizesFieldKeywordFromCli()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", """
namespace FkSample;

public class Poco
{
    public int Value { get => field + 1; set => field = value; }
}
""", langVersion: "preview");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "member-desugar",
                "--enable-field-keyword-rule",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr);

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-member-desugar-src", "Class1.cs"));
            // C50 reached via the CLI flag (no JSON overrides file): the field
            // contextual keyword must not survive and the property must be methodized.
            var codeOnly = string.Join('\n',
                rewritten.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Assert.DoesNotMatch(new Regex(@"=>\s*field\b"), codeOnly);
            Assert.Contains("get_Value", rewritten);
            Assert.Contains("set_Value", rewritten);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExtensionMethodsPass_MaterializeExtensionBlocks_NormalizesBlockFromCli()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", """
namespace EmWiring;

public static class Exts
{
    extension(int x)
    {
        public bool IsEven() => x % 2 == 0;
    }
}

public class Runner
{
    public bool Check(int n) => n.IsEven();
}
""", langVersion: "preview");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            // member-desugar rides along: the EMD-PROP/EMD-OP CS2J1208 gate
            // requires its consumption chain on the enabled pass set.
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "extension-methods,member-desugar",
                "--materialize-extension-blocks",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr);

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-extension-methods-src", "Class1.cs"));
            // C49 reached explicitly through the CLI flag (the rule is
            // default-on; the flag stays for positive coverage, while
            // --no-materialize-extension-blocks keeps CS2J1206): the block
            // is gone, the member is a classic this-parameter static method
            // and the receiver call site is an explicit global:: host call.
            Assert.DoesNotMatch(new Regex(@"\bextension\s*\("), rewritten);
            Assert.Matches(new Regex(@"bool\s+IsEven\s*\(\s*this\s+int\s+x\s*\)"), rewritten);
            Assert.Matches(new Regex(@"global::EmWiring\.Exts\.IsEven\(\s*n\s*\)"), rewritten);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
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

    [SlowE2EFact(120.20)]
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
            var svsTreeCreated = Directory.Exists(
                Path.Combine(destDir, ".cs2j-struct-value-semantics-src"));
            Assert.True(svsTreeCreated,
                "struct-value-semantics is default-on and must run in the default chain");

            // 第二条链的目标是源根下的另一个嵌套目录；若保留第一条链的 out，
            // 后续 pass 的源文件枚举会把 out 内已注入的 Cs2jPrelude 等当作源
            // 文件拷进新中间树，dotnet build 即 CS0101 重复定义。第一条链的
            // 断言已在上面取证，删除其输出保持第二条链源树干净。
            Directory.Delete(destDir, recursive: true);

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

    [SlowE2EFact(152.34)]
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

            // 同 DefaultChain_Creates_StructValueSemantics：首链嵌套输出会污染
            // 第二条链的源枚举（注入的支撑工程被重复 glob），取证后删除。
            Directory.Delete(destDir, recursive: true);

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
    public async Task ListPasses_ShowsConcurrencyNormalize_ImplementedAndOnByDefault()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        var line = stdout.Split('\n').First(l => l.Contains("concurrency-normalize"));
        Assert.Contains("Implemented", line);
        Assert.Contains(" on ", line); // all-on policy，默认开
        Assert.DoesNotContain(" off ", line);
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
