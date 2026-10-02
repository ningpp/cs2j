using System.Text;
using CSharpToJava.Core.ClosureLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// M1 SDK hit-rate audit (design §9 milestone M1).
/// Env CS2J_CLOSURE_AUDIT_ROOT: audit that root; project groups are csproj
/// directories (fallback: the root itself), each compiled as one CSharpCompilation
/// with trusted-platform references. Without the env var a deterministic temp
/// smoke tree is audited — this test NEVER skips.
/// In env mode a markdown report is written to docs/superpowers/specs/
/// 2026-10-02-c32-sdk-capture-hitrate.md (execution artifact; not pre-created).
/// </summary>
public sealed class ClosureLiftSdkAuditHarness
{
    private const string RootEnv = "CS2J_CLOSURE_AUDIT_ROOT";
    private const string ReportFileName = "2026-10-02-c32-sdk-capture-hitrate.md";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);
    // M2 起门面默认执行真实改写；本 harness 的契约是审计画像（逐字不改名），
    // 故显式 AuditOnly（M1 时改写能力尚不存在，该位曾被忽略）。
    private static readonly ClosureLiftOptions AuditOptions = new(Strict: false, AuditOnly: true);

    private readonly ITestOutputHelper _output;

    public ClosureLiftSdkAuditHarness(ITestOutputHelper output) => _output = output;

    [Fact]
    public void AuditOnly_NeverChangesFiles_AndReportsHitRate()
    {
        var envRoot = Environment.GetEnvironmentVariable(RootEnv);
        string auditRoot;
        bool envMode;
        string? tempRoot = null;

        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            auditRoot = Path.GetFullPath(envRoot);
            Assert.True(Directory.Exists(auditRoot), $"{RootEnv} does not exist: {auditRoot}");
            envMode = true;
        }
        else
        {
            tempRoot = Directory.CreateDirectory(Path.Combine(
                Path.GetTempPath(), "cs2j-closure-audit-" + Guid.NewGuid().ToString("N"))).FullName;
            SeedSmokeTree(tempRoot);
            auditRoot = tempRoot;
            envMode = false;
        }

        try
        {
            var bag = new AuditBag();
            foreach (var projectRoot in FindProjectRoots(auditRoot))
                AuditProject(projectRoot, bag);

            _output.WriteLine(bag.Summary());

            // M1 hard red line, in every mode.
            Assert.Equal(0, bag.FilesChanged);
            Assert.True(bag.FilesScanned > 0, "no .cs files found under audit root");

            if (envMode)
            {
                var reportPath = WriteReport(auditRoot, bag);
                _output.WriteLine($"report: {reportPath}");
            }
            else
            {
                Assert.Equal(4, bag.FilesScanned);
                Assert.Equal(2, bag.FilesClean);
                Assert.Equal(2, bag.RowsByCode.GetValueOrDefault("CS2J3207"));
                Assert.True(bag.RowsByReason.ContainsKey(TriggerReason.ExternalWrite));
                Assert.True(bag.RowsByReason.ContainsKey(TriggerReason.ForeachIteration));
            }
        }
        finally
        {
            if (tempRoot != null && Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void AuditProject(string projectRoot, AuditBag bag)
    {
        var files = EnumerateCsFiles(projectRoot)
            .Where(f => bag.SeenFiles.Add(Path.GetFullPath(f)))
            .ToList();
        if (files.Count == 0)
            return;

        var trees = files
            .Select(f => CSharpSyntaxTree.ParseText(
                File.ReadAllText(f), ParseOptions, path: f))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "closure-audit-" + Guid.NewGuid().ToString("N"),
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new ClosureLiftPreprocessor();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var content = tree.ToString();
            var relative = Path.GetRelativePath(projectRoot, tree.FilePath);

            var result = preprocessor.Preprocess(content, model, AuditOptions);
            bag.FilesScanned++;
            bag.FilesChanged += result.Changed ? 1 : 0;
            bag.FilesClean += result.Statistics.FilesSkippedClean;
            bag.FilesTransformed += result.Statistics.FilesTransformed;
            foreach (var kvp in result.Statistics.SkippedByReason)
                bag.RowsByCode[kvp.Key] = bag.RowsByCode.GetValueOrDefault(kvp.Key) + kvp.Value;
            foreach (var kvp in result.Statistics.CapturesByReason)
                bag.RowsByReason[kvp.Key] = bag.RowsByReason.GetValueOrDefault(kvp.Key) + kvp.Value;

            foreach (var d in result.Diagnostics)
            {
                bag.RowsByCode[d.Code] = bag.RowsByCode.GetValueOrDefault(d.Code) + 1;
                if (d.Reason.HasValue)
                    bag.RowsByReason[d.Reason.Value] = bag.RowsByReason.GetValueOrDefault(d.Reason.Value) + 1;
                bag.Rows.Add((relative, d.Code, d.Severity.ToString(), d.Line, d.Message));
            }

            // M1 contract, per file.
            Assert.False(result.Changed, $"audit rewrote {tree.FilePath}");
            Assert.Equal(content, result.OutputCode);
        }
    }

    private static List<string> FindProjectRoots(string root)
    {
        var csprojDirs = Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Select(d => Path.GetFullPath(Path.GetDirectoryName(d)!))
            .Distinct(StringComparer.Ordinal)
            .OrderList();
        return csprojDirs.Count == 0
            ? new List<string> { Path.GetFullPath(root) }
            : csprojDirs;
    }

    private static List<string> EnumerateCsFiles(string dir)
        => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(seg =>
                seg.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                seg.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .OrderList();

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                    yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static void SeedSmokeTree(string root)
    {
        File.WriteAllText(Path.Combine(root, "Counter.cs"), @"
class SampleCounter
{
    void Run()
    {
        int c = 0;
        System.Action a = () => System.Console.WriteLine(c);
        c++;
    }
}");
        File.WriteAllText(Path.Combine(root, "ForeachCase.cs"), @"
class SampleForeach
{
    void Run()
    {
        foreach (var item in new[] { 1, 2, 3 })
        {
            System.Action a = () => System.Console.WriteLine(item);
        }
    }
}");
        File.WriteAllText(Path.Combine(root, "ReadOnly.cs"), @"
class SampleReadOnly
{
    int Run()
    {
        var x = 41;
        System.Func<int> f = () => x + 1;
        return f();
    }
}");
        File.WriteAllText(Path.Combine(root, "Plain.cs"), "class SamplePlain { int Run() => 1; }");
    }

    private static string WriteReport(string root, AuditBag bag)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# C32 M1 SDK capture hit-rate audit");
        sb.AppendLine();
        sb.AppendLine($"- Root: `{root}`");
        sb.AppendLine($"- Generated (UTC): {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine("- Mode: AuditOnly (M1; output byte-identical, nothing rewritten)");
        sb.AppendLine("- Options: Strict=false; references=TRUSTED_PLATFORM_ASSEMBLIES; compile group = csproj dir");
        sb.AppendLine();
        sb.AppendLine("## Totals");
        sb.AppendLine();
        sb.AppendLine("| Metric | Count |");
        sb.AppendLine("| --- | ---: |");
        sb.AppendLine($"| .cs files scanned | {bag.FilesScanned} |");
        sb.AppendLine($"| files clean (no liftable candidate) | {bag.FilesClean} |");
        sb.AppendLine($"| files changed (must be 0 in M1) | {bag.FilesChanged} |");
        sb.AppendLine($"| CS2J3207 audit rows (liftable symbols) | {bag.RowsByCode.GetValueOrDefault("CS2J3207")} |");
        sb.AppendLine();
        sb.AppendLine("## Rows by code");
        sb.AppendLine();
        sb.AppendLine("| Code | Count |");
        sb.AppendLine("| --- | ---: |");
        foreach (var kvp in bag.RowsByCode.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.AppendLine($"| {kvp.Key} | {kvp.Value} |");
        sb.AppendLine();
        sb.AppendLine("## Rows by trigger reason (CS2J3207)");
        sb.AppendLine();
        sb.AppendLine("| Reason | Count |");
        sb.AppendLine("| --- | ---: |");
        foreach (var kvp in bag.RowsByReason.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal))
            sb.AppendLine($"| {kvp.Key} | {kvp.Value} |");
        sb.AppendLine();
        sb.AppendLine("## First 200 rows");
        sb.AppendLine();
        foreach (var (relative, code, severity, line, message) in bag.Rows.Take(200))
            sb.AppendLine($"- `{relative}:{line}` [{severity}] {code}: {message}");
        if (bag.Rows.Count > 200)
            sb.AppendLine($"- … {bag.Rows.Count - 200} more rows omitted");
        sb.AppendLine();
        sb.AppendLine("## Methodology caveats");
        sb.AppendLine();
        sb.AppendLine("- Cross-project symbols are not linked; ErrorType symbols may surface as CS2J3202 noise.");
        sb.AppendLine("- Generators, #line-mapped files and MSBuild items are not modeled.");
        sb.AppendLine("- Hit rate = CS2J3207 rows per scanned file; the file is an M1 planning input, not a quality verdict.");
        sb.AppendLine();

        var repoRoot = FindRepoRoot();
        var reportPath = Path.Combine(
            repoRoot, "docs", "superpowers", "specs", ReportFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, sb.ToString());
        return reportPath;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CSharpToJavaConverter.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("repo root (CSharpToJavaConverter.slnx) not found.");
    }

    private sealed class AuditBag
    {
        public int FilesScanned;
        public int FilesChanged;
        public int FilesClean;
        public int FilesTransformed;
        public HashSet<string> SeenFiles = new(StringComparer.Ordinal);
        public Dictionary<string, int> RowsByCode = new(StringComparer.Ordinal);
        public Dictionary<TriggerReason, int> RowsByReason = new();
        public List<(string Relative, string Code, string Severity, int Line, string Message)> Rows = new();

        public string Summary()
            => $"[closure-lift] scanned={FilesScanned}, clean={FilesClean}, changed={FilesChanged}, " +
               $"auditRows={RowsByCode.GetValueOrDefault("CS2J3207")}, blocked=" +
               $"{RowsByCode.Where(kv => kv.Key is not "CS2J3207").Sum(kv => kv.Value)}";
    }
}

internal static class OrderListExtensions
{
    public static List<string> OrderList(this IEnumerable<string> source)
        => source.OrderBy(p => p, StringComparer.Ordinal).ToList();
}
