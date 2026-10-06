using System.Diagnostics;
using System.Text.RegularExpressions;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.AttributeNormalize;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C10 attribute-normalize project-level tests (design §3/§4): runs the real
/// CLI adapter over SampleAttributeNormalizeSolution and asserts the pass
/// fills Caller* arguments at every supported call context, removes the
/// declaration-side attributes, and leaves the DllImport tree untouched.
/// The adapter's own verification builds the intermediate tree in Release;
/// additionally we run the rewritten App, whose .NET half asserts the frozen
/// quadruples equal explicit constants.
/// </summary>
public class AttributeNormalizeProjectTests
{
    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SampleAttributeNormalizeSolution");

    private static AttributeNormalizePassAdapter CreateAdapter(AttributeNormalizeOptions? options = null)
        => new(
            options ?? new AttributeNormalizeOptions(),
            verbose: false,
            filterUnsupportedProjects: true,
            compatNamespacePrefixes: null);

    [Fact]
    public async Task Fixture_FillsCallerSites_RemovesAttributes_PreservesDllImport_WithExpectedStats()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleAttributeNormalizeSolution.sln");
        Assert.True(File.Exists(solutionPath), $"Fixture not found: {solutionPath}");

        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-an-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            var adapter = CreateAdapter();
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = solutionPath,
                DestinationRoot = destination,
                Force = true,
            });

            Assert.True(run.Success, string.Join("\n",
                adapter.Diagnostics
                    .Where(d => d.Severity == AttributeNormalizeSeverity.Error)
                    .Select(d => d.ToMsBuildString())));
            Assert.DoesNotContain(adapter.Diagnostics,
                d => d.Severity == AttributeNormalizeSeverity.Error);

            var stats = adapter.Statistics;

            // Three distinct targets: ICallerLog.Notify (interface),
            // CallerLog.Notify (class), ExternalLoggingApi.Emit.
            Assert.Equal(3, stats.CallerPlansDetected);
            Assert.Equal(3, stats.CallerPlansApplied);
            Assert.Equal(0, stats.CallerPlansSkipped);
            Assert.Equal(0, stats.PlansRolledBack);
            Assert.Equal(0, stats.ResidualCount);

            // 4 files carry rewrites; NativeMethods.cs plus the two M2 DTO
            // files are copied clean (inventory-only, tree bytes untouched).
            Assert.Equal(4, stats.FilesTransformed);
            Assert.Equal(3, stats.FilesSkippedClean);

            // 7 Notify sites (member+path+line+expr each) and 1 Emit site
            // (member only): member=8, path=7, line=7, expr=7.
            AssertSiteCount(stats, CallerInfoKind.CallerMemberName, 8);
            AssertSiteCount(stats, CallerInfoKind.CallerFilePath, 7);
            AssertSiteCount(stats, CallerInfoKind.CallerLineNumber, 7);
            AssertSiteCount(stats, CallerInfoKind.CallerArgumentExpression, 7);

            // 4 attributes on each Notify declaration + 1 on Emit.
            Assert.Equal(9, stats.CallerAttributesRemoved);

            var root = run.IntermediateRoot;

            // ── Whole intermediate tree: no Caller* attribute survives ──
            foreach (var csFile in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var text = await File.ReadAllTextAsync(csFile);
                Assert.DoesNotContain("CallerMemberName", text);
                Assert.DoesNotContain("CallerFilePath", text);
                Assert.DoesNotContain("CallerLineNumber", text);
                Assert.DoesNotContain("CallerArgumentExpression", text);
            }

            // ── Declaration defaults are preserved; signatures intact ──
            var callerLog = await File.ReadAllTextAsync(
                Path.Combine(root, "SampleAttributeNormalizeLib", "CallerLog.cs"));
            Assert.Contains("void Notify(", callerLog);
            Assert.Contains("string member = \"\"", callerLog);
            Assert.Contains("int line = 0", callerLog);

            // ── Program site: "$" member, literal line 5, path literal, expr text ──
            var program = await File.ReadAllTextAsync(
                Path.Combine(root, "SampleAttributeNormalizeApp", "Program.cs"));
            var compactProgram = StripWhitespace(program);
            Assert.Contains(StripWhitespace("log.Notify(\"hello from main\", \"$\","), compactProgram);
            Assert.Contains("\"hello from main\"", program);
            Assert.Matches(new Regex(@"""[^""\\]*(?:\\.[^""\\]*)*Program\.cs[^""]*"""), program);

            // ── Call contexts: constructor / property / indexer / lambda / local ──
            var callSites = await File.ReadAllTextAsync(
                Path.Combine(root, "SampleAttributeNormalizeLib", "CallSiteSamples.cs"));
            Assert.Contains("\".ctor\"", callSites);
            Assert.Contains("\"Property\"", callSites);
            Assert.Contains("\"Item\"", callSites);
            Assert.Contains("\"LambdaAndLocalFunction\"", callSites);
            Assert.Contains("\"Local\"", callSites);
            Assert.Contains("\"ctor site\"", callSites);
            Assert.Contains("\"property getter\"", callSites);
            Assert.Contains("\"indexer getter\"", callSites);
            Assert.Contains("\"lambda site\"", callSites);
            Assert.Contains("\"local function site\"", callSites);

            // ── Cross-class site + external-API shim site ──
            var crossClass = await File.ReadAllTextAsync(
                Path.Combine(root, "SampleAttributeNormalizeLib", "CrossClassCaller.cs"));
            Assert.Contains("\"CallAcross\"", crossClass);
            Assert.Contains("\"cross-class site\"", crossClass);

            // ── DllImport tree: byte-identical zero-change ──
            var originalNative = await File.ReadAllTextAsync(
                Path.Combine(FixtureDir, "SampleAttributeNormalizeLib", "NativeMethods.cs"));
            var rewrittenNative = await File.ReadAllTextAsync(
                Path.Combine(root, "SampleAttributeNormalizeLib", "NativeMethods.cs"));
            Assert.Equal(originalNative, rewrittenNative);
            Assert.Contains("DllImport(\"kernel32.dll\", SetLastError = true)", rewrittenNative);

            // ── Runtime proof: execute the rewritten App (.NET-half assertions) ──
            var appProject = Path.Combine(root, "SampleAttributeNormalizeApp", "SampleAttributeNormalizeApp.csproj");
            var (exitCode, output) = await RunDotNetCapturedAsync("run",
                $"--project \"{appProject}\" -c Release");
            Assert.True(exitCode == 0, $"rewritten App failed ({exitCode}):\n{output}");
            Assert.Contains("caller records verified", output);
            Assert.Contains("json roundtrip verified", output);
        }
        finally
        {
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Fixture_Inventories_Serialization_Families_And_Keeps_Attribute_Bytes_Untouched()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleAttributeNormalizeSolution.sln");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-an-inv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            var adapter = CreateAdapter();
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = solutionPath,
                DestinationRoot = destination,
                Force = true,
            });

            Assert.True(run.Success, string.Join("\n",
                adapter.Diagnostics
                    .Where(d => d.Severity == AttributeNormalizeSeverity.Error)
                    .Select(d => d.ToMsBuildString())));

            var stats = adapter.Statistics;

            // Family buckets: 9 caller, 4 STJ (3 bridged + 1 field-level gap),
            // 3 Newtonsoft (gaps), 1 custom (unknown), 1 interop.
            Assert.Equal(9, CountFamily(stats, AttributeFamily.CallerInfo));
            Assert.Equal(4, CountFamily(stats, AttributeFamily.JsonSystemTextJson));
            Assert.Equal(3, CountFamily(stats, AttributeFamily.JsonNewtonsoft));
            Assert.Equal(1, CountFamily(stats, AttributeFamily.Custom));
            Assert.Equal(1, CountFamily(stats, AttributeFamily.Interop));

            // STJ field-level JsonIgnore (1) + all three Newtonsoft annotations (3).
            Assert.Equal(4, stats.BridgeGaps);
            Assert.Equal(1, stats.UnknownAttributes);

            var cs1310 = adapter.Diagnostics
                .Where(d => d.Code == AttributeNormalizeDiagnosticCode.CS2J1310)
                .ToList();
            Assert.Equal(4, cs1310.Count);
            Assert.Contains(cs1310, d =>
                d.Message.Contains("Newtonsoft.Json.JsonPropertyAttribute"));
            Assert.Contains(cs1310, d =>
                d.Message.Contains("Newtonsoft.Json.JsonIgnoreAttribute"));
            Assert.Contains(cs1310, d =>
                d.Message.Contains("Newtonsoft.Json.JsonExtensionDataAttribute"));
            // Field-level STJ occurrence: target is the private field.
            var stjFieldGap = Assert.Single(cs1310.Where(d =>
                d.Message.Contains("System.Text.Json.Serialization.JsonIgnoreAttribute")));
            Assert.Contains("_fieldLevel", stjFieldGap.Message);
            Assert.Contains("Condition", stjFieldGap.Message);

            var cs1320 = Assert.Single(adapter.Diagnostics
                .Where(d => d.Code == AttributeNormalizeDiagnosticCode.CS2J1320));
            Assert.Contains("CustomMarkerAttribute", cs1320.Message);

            // ── Byte-level preservation: M2 must not rewrite JSON attributes ──
            var root = run.IntermediateRoot;
            foreach (var fileName in new[]
            {
                "SerializationDtos.cs",
                "NewtonsoftDtos.cs",
            })
            {
                var original = await File.ReadAllTextAsync(
                    Path.Combine(FixtureDir, "SampleAttributeNormalizeLib", fileName));
                var rewritten = await File.ReadAllTextAsync(
                    Path.Combine(root, "SampleAttributeNormalizeLib", fileName));
                Assert.Equal(original, rewritten);
            }
        }
        finally
        {
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
    }

    private static int CountFamily(AttributeNormalizeStatistics stats, AttributeFamily family)
        => stats.AttributesInventoried.TryGetValue(family, out var count) ? count : 0;

    private static void AssertSiteCount(AttributeNormalizeStatistics stats, CallerInfoKind kind, int expected)
    {
        var actual = stats.CallerSitesFilled.TryGetValue(kind, out var count) ? count : 0;
        Assert.Equal(expected, actual);
    }

    private static string StripWhitespace(string text)
        => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static async Task<(int ExitCode, string Output)> RunDotNetCapturedAsync(string verb, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{verb} {arguments} --nologo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var outputTask = process!.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = await outputTask;
        var error = await errorTask;
        return (process.ExitCode, output + (string.IsNullOrEmpty(error) ? string.Empty : "\n" + error));
    }
}
