using System.Diagnostics;
using System.Text;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for <c>big-literal-resource</c>: the pass runs second
/// in the locked chain, moves oversized literal payloads to
/// <c>Cs2jResources/*</c>, registers them as <c>EmbeddedResource</c> items with a
/// stable <c>LogicalName</c>, injects the <c>Cs2jPrelude</c> loader project and is
/// idempotent on a second run.
/// </summary>
public sealed class BigLiteralResourceCLITests
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

    private static string CreateProject(Action<string> writeSources)
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-blr-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "Lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        writeSources(tempDir);
        return tempDir;
    }

    private static string ByteArraySource(int count)
    {
        var builder = new StringBuilder();
        builder.AppendLine("public static class Demo");
        builder.AppendLine("{");
        builder.AppendLine("    public static readonly byte[] Data = new byte[]");
        builder.AppendLine("    {");
        for (var index = 0; index < count; index++)
        {
            builder.Append("        0x").Append((index % 256).ToString("X2"));
            builder.AppendLine(index == count - 1 ? string.Empty : ",");
        }

        builder.AppendLine("    };");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static async Task<string> RunPassAsync(string sourceDir, string destDir)
    {
        var (exit, stdout, stderr) = await RunCliAsync(
            "preprocess", "--pass", "big-literal-resource",
            "-s", sourceDir, "-d", destDir,
            "--no-verify-build", "--no-verify-tests");
        Assert.Equal(0, exit);
        Assert.DoesNotContain("CS2J9001", stderr);
        return stdout + stderr;
    }

    private static string IntermediateRoot(string destDir)
        => Path.Combine(destDir, ".cs2j-big-literal-resource-src");

    [Fact]
    public async Task IsolatedPass_moves_the_byte_array_into_a_resource_and_registers_it()
    {
        const int count = 6000;
        var sourceDir = CreateProject(dir => File.WriteAllText(
            Path.Combine(dir, "DemoBigBytes.cs"), ByteArraySource(count)));
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var log = await RunPassAsync(sourceDir, destDir);
            Assert.Contains("big-literal-resource: 1 transformed", log, StringComparison.Ordinal);

            var intermediate = IntermediateRoot(destDir);
            var rewritten = await File.ReadAllTextAsync(Path.Combine(intermediate, "DemoBigBytes.cs"));
            Assert.Contains("global::Cs2jPrelude.Cs2jResource.LoadBytes(", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("0x00,", rewritten, StringComparison.Ordinal);
            Assert.True(
                rewritten.Length < count * 4,
                $"expected the rewritten file to shrink below {count * 4} chars, got {rewritten.Length}");

            var payloads = Directory.GetFiles(
                Path.Combine(intermediate, "Cs2jResources"), "*.bin");
            var payload = Assert.Single(payloads);
            var bytes = await File.ReadAllBytesAsync(payload);
            Assert.Equal(count, bytes.Length);
            for (var index = 0; index < count; index++)
            {
                Assert.Equal((byte)(index % 256), bytes[index]);
            }

            var project = await File.ReadAllTextAsync(Path.Combine(intermediate, "Lib.csproj"));
            Assert.Contains("<EmbeddedResource Include=\"Cs2jResources\\", project, StringComparison.Ordinal);
            Assert.Contains("<LogicalName>cs2j/Demo.Data.", project, StringComparison.Ordinal);
            Assert.Contains("<ProjectReference Include=\"Cs2jPrelude\\Cs2jPrelude.csproj\"", project, StringComparison.Ordinal);

            Assert.True(
                File.Exists(Path.Combine(intermediate, "Cs2jPrelude", "Cs2jResource.cs")),
                "the Cs2jPrelude loader source must be released into the tree");
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task IsolatedPass_externalizes_long_strings_as_txt_payloads()
    {
        var text = new string('q', 5000);
        var sourceDir = CreateProject(dir => File.WriteAllText(
            Path.Combine(dir, "DemoBigStrings.cs"),
            $"public static class Demo\n{{\n    public static readonly string Sql = \"{text}\";\n}}\n"));
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            await RunPassAsync(sourceDir, destDir);

            var intermediate = IntermediateRoot(destDir);
            var rewritten = await File.ReadAllTextAsync(Path.Combine(intermediate, "DemoBigStrings.cs"));
            Assert.Contains("Cs2jResource.LoadString(", rewritten, StringComparison.Ordinal);

            var payload = Assert.Single(
                Directory.GetFiles(Path.Combine(intermediate, "Cs2jResources"), "*.txt"));
            Assert.Equal(text, await File.ReadAllTextAsync(payload));
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task Second_run_is_clean_and_never_duplicates_the_resource_item()
    {
        var sourceDir = CreateProject(dir => File.WriteAllText(
            Path.Combine(dir, "DemoBigBytes.cs"), ByteArraySource(5000)));
        var firstOut = Path.Combine(sourceDir, "out1");
        var secondOut = Path.Combine(sourceDir, "out2");

        try
        {
            await RunPassAsync(sourceDir, firstOut);

            // Feed the pass its own output: nothing qualifies any more.
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "big-literal-resource",
                "-s", IntermediateRoot(firstOut), "-d", secondOut,
                "--no-verify-build", "--no-verify-tests");
            Assert.Equal(0, exit);
            Assert.Contains(
                "big-literal-resource: 0 transformed",
                stdout + stderr,
                StringComparison.Ordinal);

            var project = await File.ReadAllTextAsync(
                Path.Combine(IntermediateRoot(secondOut), "Lib.csproj"));
            var occurrences = project.Split("<EmbeddedResource Include=").Length - 1;
            Assert.Equal(1, occurrences);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ListPasses_places_the_pass_immediately_after_conditional()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");
        Assert.Equal(0, exit);

        var rows = stdout
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        Assert.StartsWith("conditional ", rows[0], StringComparison.Ordinal);
        Assert.StartsWith("big-literal-resource ", rows[1], StringComparison.Ordinal);
        Assert.Contains(" on ", rows[1], StringComparison.Ordinal);
        Assert.Contains("conditional", rows[1], StringComparison.Ordinal);
    }
}
