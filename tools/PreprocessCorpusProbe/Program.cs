// Reusable corpus preprocessing probe (P3 M3/M4 evidence entry point).
//
//   scan --root D:\csharp-sdk --profile c23 --out probe-out\c23 [options]
//
// In-process profiles drive production Core preprocessors over
// msbuild-evaluated compile items (no intermediate tree is written, corpora
// stay untouched). The c27-c50 profile shells out to the built CLI because
// member-desugar owns a whole-tree compile/rollback loop with internal entry
// points. Outputs: compact JSON + markdown summary in --out.
//
// Numbers are MEASURED. Evaluation failures and missing references are
// recorded per project as "unavailable"; nothing is estimated.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintUsage();
                return args.Length == 0 ? 2 : 0;
            }

            return args[0] switch
            {
                "scan" => RunScan(args.Skip(1).ToArray()),
                "list" => RunList(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("probe failed: " + ex);
            return 1;
        }
    }

    private static int RunList()
    {
        foreach (var p in ProbeReport.AllProfiles())
            Console.WriteLine($"{p.Id,-18} [{(p.InProcess ? "in-proc" : "cli   ")}] {p.Description}");
        return 0;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"unknown verb: {verb}");
        PrintUsage();
        return 2;
    }

    private static int RunScan(string[] args)
    {
        string? root = null;
        string? profileId = null;
        string? outDir = null;
        string? samplesCsv = null;
        string? projectsCsv = null;
        string? entry = null;
        string? cliDll = null;
        string? note = null;
        string? corpus = null;
        int? limit = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root": root = args[++i]; break;
                case "--profile": profileId = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                case "--samples": samplesCsv = args[++i]; break;
                case "--projects": projectsCsv = args[++i]; break;
                case "--limit": limit = int.Parse(args[++i]); break;
                case "--entry": entry = args[++i]; break;
                case "--cli": cliDll = args[++i]; break;
                case "--corpus": corpus = args[++i]; break;
                case "--note": note = args[++i]; break;
                default: throw new InvalidOperationException("unknown option: " + args[i]);
            }
        }

        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(profileId)
            || string.IsNullOrWhiteSpace(outDir))
        {
            Console.Error.WriteLine("scan requires --root, --profile, --out");
            return 2;
        }

        var profile = ProbeReport.AllProfiles().FirstOrDefault(p => p.Id == profileId)
            ?? throw new InvalidOperationException($"unknown profile '{profileId}' (try 'list')");
        var samples = SplitCsv(samplesCsv);
        var projectFilters = SplitCsv(projectsCsv);
        corpus ??= new DirectoryInfo(root).Name;

        Console.WriteLine($"[probe] profile={profile.Id} corpus={corpus} root={root}");

        ProbeRun run;
        if (profile is CliDrivenProfile cliProfile)
        {
            run = CliDriver.Run(
                cliProfile, root!, corpus!, note, entry,
                cliDll ?? FindDefaultCliDll(), Path.GetFullPath(outDir));
        }
        else
        {
            var builds = MsBuildProject.Enumerate(root!, samples, projectFilters, limit);
            run = RunInProcess(profile, builds, root!, corpus!, note);
        }

        Directory.CreateDirectory(outDir);
        var jsonPath = Path.Combine(outDir, $"{profile.Id}.json");
        var mdPath = Path.Combine(outDir, $"{profile.Id}.md");
        File.WriteAllText(jsonPath, ProbeReport.BuildJson(run), new UTF8Encoding(false));
        File.WriteAllText(mdPath, ProbeReport.BuildMarkdown(run), new UTF8Encoding(false));

        Console.WriteLine($"[probe] json: {jsonPath}");
        Console.WriteLine($"[probe] md:   {mdPath}");
        Console.WriteLine(ProbeReport.Headline(run));
        return 0;
    }

    private static ProbeRun RunInProcess(
        ProbeProfile profile,
        List<ProjectBuild> builds,
        string root,
        string corpus,
        string? note)
    {
        var run = new ProbeRun(profile.Id, profile.Family, profile.Description, root, corpus, note);

        foreach (var build in builds)
        {
            var row = new ProjectRow(build.Unit);
            run.Projects.Add(row);

            if (build.UnavailableReason is not null)
            {
                row.UnavailableReason = build.UnavailableReason;
                run.ProjectsUnavailable++;
                continue;
            }

            var compilation = CSharpCompilation.Create(
                "probe-" + Sanitize(build.Unit),
                build.Trees,
                build.References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: true,
                    nullableContextOptions: build.Nullable,
                    specificDiagnosticOptions: build.Suppressed));

            row.Files = build.Trees.Count;
            row.MissingCompileItems = build.MissingCompileItems;

            // Whole-compilation families (member-desugar) share one analyzer across
            // the project's files and flush totals/diagnostics after the file loop.
            var session = profile.CreateSession();

            foreach (var tree in build.Trees)
            {
                SemanticModel model;
                try
                {
                    model = compilation.GetSemanticModel(tree);
                    foreach (var d in model.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
                    {
                        row.FilesWithErrors++;
                        row.CompileErrors++;
                        row.ErrorCodes[d.Id] = row.ErrorCodes.GetValueOrDefault(d.Id) + 1;
                    }
                }
                catch (Exception ex)
                {
                    row.UnavailableReason = "semantic model failure: " + ex.Message;
                    run.ProjectsUnavailable++;
                    break;
                }

                string source;
                try
                {
                    source = File.ReadAllText(tree.FilePath);
                }
                catch
                {
                    continue;
                }

                FileOutcome outcome;
                try
                {
                    outcome = session != null
                        ? session.Process(source, model, tree.FilePath)
                        : profile.RunFile(source, model, tree.FilePath);
                }
                catch (Exception ex)
                {
                    row.DriverErrors++;
                    if (Environment.GetEnvironmentVariable("PROBE_DEBUG") == "1")
                        Console.Error.WriteLine($"[driver-error] {tree.FilePath}: {ex}");
                    run.Diagnostic("driver-error", "Error",
                        $"{Path.GetFileName(tree.FilePath)}: {ex.GetType().Name}: {ex.Message}",
                        build.Unit);
                    continue;
                }

                foreach (var (key, value) in outcome.Metrics)
                    row.Metrics[key] = row.Metrics.GetValueOrDefault(key) + value;
                foreach (var diag in outcome.Diagnostics)
                    run.Diagnostic(diag.Code, diag.Severity, diag.Message, build.Unit,
                        Path.GetFileName(tree.FilePath), diag.Line);
                if (outcome.Changed)
                    row.FilesTransformed++;
            }

            session?.Flush(row, run, build.Unit);
        }

        Finalize(run);
        return run;
    }

    internal static void Finalize(ProbeRun run)
    {
        foreach (var row in run.Projects)
            foreach (var (key, value) in row.Metrics)
                run.Totals[key] = run.Totals.GetValueOrDefault(key) + value;
    }

    internal static string Sanitize(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private static IReadOnlyList<string>? SplitCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? null
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FindDefaultCliDll()
    {
        // tools/PreprocessCorpusProbe/bin/<cfg>/<tfm> → repo root.
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName, "src", "CSharpToJava.CLI", "bin", "Release", "net10.0",
                "CSharpToJava.CLI.dll");
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException(
            "CSharpToJava.CLI.dll (Release) not found; build the solution or pass --cli <path>.");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            PreprocessCorpusProbe — reusable sdk/corpus preprocessing profiler

              list
              scan --root D:\csharp-sdk --profile <id> --out probe-out\<name>
                   [--samples json,uri] [--projects substring,...] [--limit N]
                   [--entry path\to.sln] [--cli path\to\CSharpToJava.CLI.dll]
                   [--corpus name] [--note "口径备注"]

            c27-c50 uses the CLI driver and needs --entry (a .sln/.csproj under
            the read-only corpus); intermediate trees go under --out\cli.
            """);
    }
}

internal static class CliDriver
{
    private static readonly Regex MsBuildDiag = new(
        @"(?:^|[\]])\s*(?<file>[^\[\]\r\n]*?\.(?:cs|csproj))?(?:\((?<line>\d+),(?<col>\d+)\))?:\s*" +
        @"(?<sev>error|warning|info)\s+(?<code>CS2J\d+)\s*:\s*(?<msg>.*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ProbeRun Run(
        CliDrivenProfile profile,
        string root,
        string corpus,
        string? note,
        string? entry,
        string cliDll,
        string outDir)
    {
        entry ??= Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f.Contains("without", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"no --entry and no .sln directly under {root}");
        entry = Path.GetFullPath(entry);

        var stageDir = Path.Combine(outDir, "cli", profile.Id);
        Directory.CreateDirectory(stageDir);

        var psi = new ProcessStartInfo("dotnet",
            $"\"{cliDll}\" preprocess -s \"{entry}\" -d \"{stageDir}\" " +
            $"--pass {profile.PassId} --no-verify-build --no-verify-tests " +
            $"--no-local-function-strict -v")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        Console.WriteLine($"[probe] cli: {psi.FileName} {psi.Arguments}");
        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        // Whole-tree member-desugar over a large P2P graph can exceed the default
        // 10-minute budget (multi-stage per-file reparse + whole-tree fallback
        // iterations); triage runs may raise it via PROBE_CLI_TIMEOUT_MS.
        var timeoutMs = 600_000;
        var timeoutEnv = Environment.GetEnvironmentVariable("PROBE_CLI_TIMEOUT_MS");
        if (!string.IsNullOrWhiteSpace(timeoutEnv) && int.TryParse(timeoutEnv, out var configured))
            timeoutMs = configured;
        process.WaitForExit(timeoutMs);
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"CLI profile run timed out ({timeoutMs / 1000}s)");
        }

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;
        File.WriteAllText(Path.Combine(stageDir, "stdout.log"), stdout, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(stageDir, "stderr.log"), stderr, new UTF8Encoding(false));

        var run = new ProbeRun(profile.Id, profile.Family, profile.Description, root, corpus,
            (note ?? "") + $" | entry={Path.GetRelativePath(root, entry)}; cli exit={process.ExitCode}");
        var row = new ProjectRow(Path.GetRelativePath(root, entry).Replace('\\', '/'))
        {
            Note = $"CLI exit {process.ExitCode}",
        };
        run.Projects.Add(row);

        var summaryLine = stdout.Split('\n').FirstOrDefault(l => l.StartsWith(profile.SummaryPrefix));
        if (summaryLine is not null)
        {
            row.Metrics = ParseSummary(profile, summaryLine);
            run.RawSummaryLines.Add(summaryLine.Trim());
        }
        else
        {
            // Some adapters write the summary to stderr when the chain fails late.
            summaryLine = stderr.Split('\n').FirstOrDefault(l => l.StartsWith(profile.SummaryPrefix));
            if (summaryLine is not null)
            {
                row.Metrics = ParseSummary(profile, summaryLine);
                run.RawSummaryLines.Add(summaryLine.Trim());
            }
        }

        foreach (var line in stdout.Split('\n').Concat(stderr.Split('\n')))
        {
            var match = MsBuildDiag.Match(line);
            if (!match.Success)
                continue;
            var sev = match.Groups["sev"].Value.ToLowerInvariant() switch
            {
                "error" => "Error",
                "warning" => "Warning",
                _ => "Info",
            };
            var file = match.Groups["file"].Success ? match.Groups["file"].Value.Trim() : "";
            var lineNo = match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value) : 0;
            var topDir = file.Split('/', '\\').FirstOrDefault() ?? "";
            run.Diagnostic(
                match.Groups["code"].Value,
                sev,
                match.Groups["msg"].Value.Trim(),
                string.IsNullOrEmpty(topDir) ? row.Unit : topDir,
                Path.GetFileName(file),
                lineNo);
        }

        if (process.ExitCode != 0)
            row.UnavailableReason = $"CLI exit {process.ExitCode} (counts kept; see stderr.log)";

        Program.Finalize(run);
        return run;
    }

    /// <summary>
    /// Turns "prefix: 3 a, 4 b | seg: 5 c, 6 d" into {prefix.a=3, seg.c=5, …}
    /// using the first two significant words of each clause as the metric slug.
    /// </summary>
    private static Dictionary<string, long> ParseSummary(CliDrivenProfile profile, string summary)
    {
        var metrics = new Dictionary<string, long>(StringComparer.Ordinal);
        var body = summary[(summary.IndexOf(':') + 1)..];
        var segments = body.Split('|');
        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim();
            var prefix = profile.SegmentPrefix(segment);
            foreach (var clause in segment.Split(','))
            {
                var match = Regex.Match(clause.Trim(), @"^(\d+)\s+(.+)$");
                if (!match.Success)
                    continue;
                var value = long.Parse(match.Groups[1].Value);
                var words = Regex.Matches(match.Groups[2].Value, @"[a-z][a-z-]*")
                    .Select(m => m.Value)
                    .Where(w => w is not ("y" or "ies") and not ("site" or "sites")
                        and not ("s") and not ("es"))
                    .Take(3)
                    .ToArray();
                var key = prefix + "." + string.Join('-', words);
                metrics[key] = metrics.GetValueOrDefault(key) + value;
            }
        }

        return metrics;
    }
}

internal abstract class CliDrivenProfile : ProbeProfile
{
    public abstract string PassId { get; }
    public abstract string SummaryPrefix { get; }
    public override bool InProcess => false;
    public override FileOutcome RunFile(string source, SemanticModel model, string filePath)
        => throw new NotSupportedException();

    public virtual string SegmentPrefix(string segment)
    {
        var head = segment.Split(':', ' ').FirstOrDefault(w => w.Length > 0) ?? "x";
        var known = new[] { "decimal-ops", "increment-targets", "nullable-lift", "field-keyword", "records" };
        foreach (var k in known)
            if (segment.StartsWith(k, StringComparison.Ordinal))
                return k;
        return "pass";
    }
}

internal sealed class MemberDesugarCliProfile : CliDrivenProfile
{
    public override string Id => "c27-c50";
    public override string Family => "C27/C50";
    public override string Description =>
        "member-desugar via CLI: record with/init/required (C27) + field keyword (C50), non-strict";
    public override string PassId => "member-desugar";
    public override string SummaryPrefix => "member-desugar:";
}

internal sealed class ProbeRun
{
    // Compact by default; triage runs can raise via PROBE_SAMPLE_CAP so every
    // diagnostic site (e.g. CS2J0510 handler list) lands in the JSON.
    private static readonly int SampleCap =
        int.TryParse(Environment.GetEnvironmentVariable("PROBE_SAMPLE_CAP"), out var cap) && cap > 0
            ? cap
            : 10;

    public ProbeRun(string profileId, string family, string description,
        string root, string corpus, string? note)
    {
        ProfileId = profileId;
        Family = family;
        Description = description;
        Root = root;
        Corpus = corpus;
        Note = note;
    }

    public string ProfileId { get; }
    public string Family { get; }
    public string Description { get; }
    public string Root { get; }
    public string Corpus { get; }
    public string? Note { get; }
    public string GeneratedAt { get; } = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
    public List<ProjectRow> Projects { get; } = new();
    public int ProjectsUnavailable { get; set; }
    public Dictionary<string, long> Totals { get; } = new(StringComparer.Ordinal);
    public List<string> RawSummaryLines { get; } = new();

    public List<DiagAggregate> Diagnostics { get; } = new();
    private readonly Dictionary<string, DiagAggregate> _diagByCode = new(StringComparer.Ordinal);

    public DiagAggregate Diagnostic(string code, string severity, string message,
        string unit, string file = "", int line = 0)
    {
        if (!_diagByCode.TryGetValue(code, out var agg))
        {
            agg = new DiagAggregate(code, severity);
            _diagByCode[code] = agg;
            Diagnostics.Add(agg);
        }

        agg.Count++;
        agg.ByUnit[unit] = agg.ByUnit.GetValueOrDefault(unit) + 1;
        if (agg.Samples.Count < SampleCap)
        {
            var trimmed = message.Replace('\r', ' ').Replace('\n', ' ');
            if (trimmed.Length > 180)
                trimmed = trimmed[..180] + "…";
            var location = string.IsNullOrEmpty(file) ? unit : $"{unit}/{file}" + (line > 0 ? $":{line}" : "");
            if (!agg.Samples.Any(s => s.Location == location && s.Message == trimmed))
                agg.Samples.Add(new DiagSample(location, trimmed));
        }

        return agg;
    }
}

internal sealed class DiagAggregate
{
    public DiagAggregate(string code, string severity)
    {
        Code = code;
        Severity = severity;
    }

    public string Code { get; }
    public string Severity { get; }
    public int Count { get; set; }
    public Dictionary<string, int> ByUnit { get; } = new(StringComparer.Ordinal);
    public List<DiagSample> Samples { get; } = new();
}

internal sealed record DiagSample(string Location, string Message);

internal sealed class ProjectRow
{
    public ProjectRow(string unit) => Unit = unit;
    public string Unit { get; }
    public int Files { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesWithErrors { get; set; }
    public int CompileErrors { get; set; }
    public int MissingCompileItems { get; set; }
    public int DriverErrors { get; set; }
    public string? UnavailableReason { get; set; }
    public string? Note { get; set; }
    public Dictionary<string, long> Metrics { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ErrorCodes { get; } = new(StringComparer.Ordinal);
}
