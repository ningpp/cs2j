using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for the foreach-source pass (C16): catalog
/// listing, default-on behavior, --pass/--enable-pass equivalence, FS-1
/// wrapping through the preprocess verb, and the M2 residual diagnostics /
/// FS-5 gate (CS2J5420/5421/5440) including strict escalation. These tests
/// execute the Debug CLI dll.
/// </summary>
public class ForeachSourceCLITests
{
    private const string IntermediateDir = ".cs2j-foreach-source-src";

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

    private static string CreateTempWorkspace(string csharpSource)
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-foreach-source-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "Lib.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(tempDir, "Class1.cs"), csharpSource);
        return tempDir;
    }

    [Fact]
    public async Task ListPasses_ShowsForeachSource_Implemented_On_C16()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        var line = stdout.Split('\n').First(l => l.Contains("foreach-source"));
        Assert.Contains("Implemented", line);
        Assert.Contains(" on ", line);
        Assert.DoesNotContain(" off ", line);
        // The list renders runAfter, not catalog item ids; the C16 mapping is
        // asserted at registry level in PassChainOrderTests.
    }

    [Fact(Skip = "TODO: 临时跳过端到端慢测试（排查根因中）— 153.33s")]
    public async Task DefaultChain_Creates_ForeachSource_IntermediateTree_AndDisablePass_SuppressesIt()
    {
        var sourceDir = CreateTempWorkspace("""
            public static class C {
                public static System.Collections.Generic.List<char> Run(string s) {
                    var r = new System.Collections.Generic.List<char>();
                    foreach (var c in s) { r.Add(c); }
                    return r;
                }
            }
            """);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", sourceDir, "-d", destDir);

            Assert.True(exit == 0, stderr);
            var treeDir = Path.Combine(destDir, IntermediateDir);
            Assert.True(
                Directory.Exists(treeDir),
                "foreach-source is default-on and must run in the default chain");

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(treeDir, "Class1.cs"));
            Assert.Contains("s.ToCharArray()", rewritten);
            Assert.Contains("foreach-source:", stdout);

            // The universal kill switch suppresses it again.
            var disabledDest = Path.Combine(sourceDir, "out-off");
            var (offExit, _, offStderr) = await RunCliAsync(
                "preprocess", "-s", sourceDir, "-d", disabledDest,
                "--disable-pass", "foreach-source");
            Assert.True(offExit == 0, offStderr);
            Assert.False(
                Directory.Exists(Path.Combine(disabledDest, IntermediateDir)),
                "foreach-source must not run when --disable-pass foreach-source is given");
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task PassSelection_RunsOnlyForeachSource_AndBuildsIntermediate()
    {
        var sourceDir = CreateStringSiteWorkspace();
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "foreach-source",
                "-s", sourceDir, "-d", destDir);

            Assert.True(exit == 0,
                $"preprocess --pass foreach-source failed (exit {exit}).\nstderr: {stderr}\nstdout: {stdout}");
            Assert.Equal(string.Empty, stderr);

            var dirs = Directory
                .GetDirectories(destDir, ".cs2j-*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray();
            Assert.Single(dirs);
            Assert.Equal(IntermediateDir, dirs[0]);

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, IntermediateDir, "Class1.cs"));
            Assert.Contains("s.ToCharArray()", rewritten);
            Assert.Contains("foreach (char __cs2jFsCh in s.ToCharArray())", rewritten);
            Assert.Contains("int code = (int)__cs2jFsCh;", rewritten);
            Assert.Contains("foreach-source:", stdout);
            Assert.DoesNotContain("CS2J9001", stdout + stderr);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact(Skip = "TODO: 临时跳过端到端慢测试（排查根因中）— 60.56s")]
    public async Task EnablePass_AddsForeachSource_OnTopOfTheDefaultChain()
    {
        // --enable-pass keeps the default-on chain and adds the requested pass
        // (unlike --pass which isolates). The foreach-source tree must exist
        // alongside default-chain neighbours and still contain the wrapping.
        var sourceDir = CreateStringSiteWorkspace();
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--enable-pass", "foreach-source",
                "-s", sourceDir, "-d", destDir);

            Assert.True(exit == 0,
                $"preprocess --enable-pass foreach-source failed (exit {exit}).\nstderr: {stderr}\nstdout: {stdout}");
            Assert.True(
                Directory.Exists(Path.Combine(destDir, IntermediateDir)),
                "foreach-source intermediate tree missing with --enable-pass");

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, IntermediateDir, "Class1.cs"));
            Assert.Contains("s.ToCharArray()", rewritten);
            Assert.Contains("foreach-source:", stdout);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    private static string CreateStringSiteWorkspace()
        => CreateTempWorkspace("""
            using System.Collections.Generic;
            public static class C {
                public static List<char> Run(string s) {
                    var r = new List<char>();
                    foreach (var c in s) { r.Add(c); }
                    foreach (int code in s) { r.Add((char)code); }
                    return r;
                }
            }
            """);

    [Fact]
    public async Task TupleResidual_FailsWith_CS2J5420_AndNoGenericExceptionCode()
    {
        // Node unchanged; the intermediate still compiles, the residual
        // error itself is what fails the chain (ruling 2).
        var sourceDir = CreateTempWorkspace("""
            using System.Collections.Generic;
            public static class C {
                public static int Run(List<(int x, int y)> rows) {
                    var sum = 0;
                    foreach (var (x, y) in rows) { sum += x + y; }
                    return sum;
                }
            }
            """);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "foreach-source",
                "--no-local-function-strict",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(1, exit);
            var output = stdout + stderr;
            Assert.Contains("CS2J5420", output);
            Assert.DoesNotContain("CS2J9001", output);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExtensionDeconstructResidual_Warns_GateWarns_AndPassesWhenNotStrict()
    {
        var sourceDir = CreateTempWorkspace("""
            using System.Collections.Generic;
            public class Box { public int A; public string B = ""; }
            public static class BoxExt {
                public static void Deconstruct(this Box b, out int a, out string s) { a = b.A; s = b.B; }
            }
            public static class C {
                public static int Run(List<Box> boxes) {
                    var sum = 0;
                    foreach (var (a, s) in boxes) { sum += a + s.Length; }
                    return sum;
                }
            }
            """);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "foreach-source",
                "--no-local-function-strict",
                "-s", sourceDir, "-d", destDir);

            Assert.True(exit == 0,
                $"expected relaxed gate to pass (exit 0).\nstderr: {stderr}\nstdout: {stdout}");
            var output = stdout + stderr;
            Assert.Contains("CS2J5421", output);
            Assert.Contains("CS2J5440", output);
            Assert.Contains("warning CS2J5421", output);
            Assert.Contains("warning CS2J5440", output);
            Assert.DoesNotContain("CS2J9001", output);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExtensionDeconstructResidual_Strict_ErrorsGate_AndFails()
    {
        var sourceDir = CreateTempWorkspace("""
            using System.Collections.Generic;
            public class Box { public int A; public string B = ""; }
            public static class BoxExt {
                public static void Deconstruct(this Box b, out int a, out string s) { a = b.A; s = b.B; }
            }
            public static class C {
                public static int Run(List<Box> boxes) {
                    var sum = 0;
                    foreach (var (a, s) in boxes) { sum += a + s.Length; }
                    return sum;
                }
            }
            """);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "foreach-source",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(1, exit);
            var output = stdout + stderr;
            Assert.Contains("error CS2J5421", output);
            Assert.Contains("error CS2J5440", output);
            Assert.DoesNotContain("CS2J9001", output);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }
}
