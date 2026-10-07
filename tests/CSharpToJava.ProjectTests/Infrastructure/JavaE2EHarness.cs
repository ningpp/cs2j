using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Shared "real Java" gate for slow end-to-end tests (see <see cref="SlowE2EGate"/>):
/// flatten generated Java files into a temp javac unit, compile with a JDK 28
/// javac against the compat <c>target/classes</c>, then run a main class (or a
/// test-authored JVM harness) and gate on runtime output instead of text
/// snapshots. Extracted from EqualityConcatProjectTests for C31/C40 and later
/// numeric-oracle gates.
/// </summary>
internal static class JavaE2EHarness
{
    /// <summary>Compat compiled classes: five levels up from the test bin dir to the worktree root.</summary>
    public static string CompatClasses =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "java", "csharptojava-compat", "target", "classes"));

    public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>
    /// Compiles <paramref name="javaEntries"/> (file name + source text) plus
    /// any <paramref name="extraSourceFiles"/> already on disk. Package dirs are
    /// reconstructed by javac under the returned classes directory. Throws an
    /// xUnit assertion exception with the compiler output on failure.
    /// </summary>
    public static string Compile(
        string workRoot,
        IEnumerable<(string FileName, string Code)> javaEntries,
        IReadOnlyList<string>? extraSourceFiles = null)
    {
        var javaSrc = Path.Combine(workRoot, "src");
        var javaOut = Path.Combine(workRoot, "classes");
        Directory.CreateDirectory(javaSrc);
        Directory.CreateDirectory(javaOut);

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var javaFiles = new List<string>();
        foreach (var (fileName, code) in javaEntries)
        {
            var name = Path.GetFileName(fileName);
            if (!usedNames.Add(name))
            {
                name = javaFiles.Count.ToString("D2", CultureInfo.InvariantCulture) + "-" + name;
            }

            var path = Path.Combine(javaSrc, name);
            File.WriteAllText(path, code);
            javaFiles.Add(path);
        }

        if (extraSourceFiles is { Count: > 0 })
        {
            javaFiles.AddRange(extraSourceFiles);
        }

        var javac = FindJdkTool("javac");
        var javacArgs = new List<string>
        {
            // Compat classpath artifacts are JDK 28 preview class files: javac
            // must itself run with preview on the matching release to read them.
            "--enable-preview",
            "--release", "28",
            "-encoding", "UTF-8",
            "-cp", CompatClasses,
            "-d", javaOut,
        };
        javacArgs.AddRange(javaFiles);

        var compiled = RunProcess(javac, javacArgs, 300_000);
        if (compiled.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException(
                "javac failed:\n" + compiled.StdOut + "\n" + compiled.StdErr);
        }

        return javaOut;
    }

    /// <summary>
    /// Runs <paramref name="mainClass"/> with the compiled classes + compat on
    /// the classpath. <paramref name="extraJavaArgs"/> are inserted before the
    /// classpath (e.g. <c>--enable-native-access=ALL-UNNAMED</c>).
    /// </summary>
    public static ProcessResult RunMain(
        string classesDir,
        string mainClass,
        IReadOnlyList<string>? extraJavaArgs = null)
    {
        var java = FindJdkTool("java");
        var args = new List<string>
        {
            "--enable-preview",
            "-Djava.awt.headless=true",
        };
        if (extraJavaArgs is { Count: > 0 })
        {
            args.AddRange(extraJavaArgs);
        }

        args.AddRange(new[]
        {
            "-cp", classesDir + Path.PathSeparator + CompatClasses,
            mainClass,
        });
        return RunProcess(java, args, 300_000);
    }

    /// <summary>
    /// Finds the first class declaring a main method in a Java source; returns
    /// its fully qualified (package-prefixed) name, or null.
    /// </summary>
    public static string? FindMainClass(string javaSource)
    {
        var mainMatch = Regex.Match(javaSource,
            @"static\s+(?:void|int)\s+main\s*\(", RegexOptions.Compiled);
        if (!mainMatch.Success)
        {
            return null;
        }

        var packageMatch = Regex.Match(javaSource,
            @"package\s+([\w.]+)\s*;", RegexOptions.Compiled);
        var package = packageMatch.Success ? packageMatch.Groups[1].Value + "." : "";

        var classMatches = Regex.Matches(javaSource,
            @"(?:class|record|enum)\s+(\w+)", RegexOptions.Compiled);
        foreach (Match classMatch in classMatches)
        {
            if (classMatch.Index < mainMatch.Index)
            {
                return package + classMatch.Groups[1].Value;
            }
        }

        return null;
    }

    public static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 3 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception) when (attempt < 2)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// Locates a JDK tool: prefers JAVA_HOME, then any jdk-28* install (compat
    /// classes are built for release 28), then PATH.
    /// </summary>
    public static string FindJdkTool(string tool)
    {
        var exe = tool + ".exe";
        var roots = new List<string>
        {
            @"D:\Java",
            @"C:\Program Files\Java",
            @"C:\Program Files\Eclipse Adoptium",
        };
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
        {
            roots.Insert(0, javaHome);
        }

        // Pass 1: JAVA_HOME or a jdk-28* install directly.
        foreach (var root in roots.Distinct())
        {
            if (!Directory.Exists(root)) continue;
            var direct = Path.Combine(root, "bin", exe);
            if (File.Exists(direct)) return direct;
            foreach (var dir in Directory.GetDirectories(root, "jdk-28*"))
            {
                var candidate = Path.Combine(dir, "bin", exe);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // Pass 2: any other install, newest first.
        foreach (var root in roots.Distinct())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d))
            {
                var candidate = Path.Combine(dir, "bin", exe);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // Pass 3: PATH.
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }

        throw new Xunit.Sdk.XunitException(
            "Could not locate " + exe + "; need a JDK 28 to run Java e2e assertions.");
    }

    public static ProcessResult RunProcess(
        string executable, IReadOnlyList<string> arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process == null)
        {
            throw new Xunit.Sdk.XunitException("failed to start " + executable);
        }

        // Drain stderr concurrently with stdout: sequential ReadToEnd deadlocks
        // when a child fills the stderr (~64KB) pipe buffer while we block on stdout.
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new Xunit.Sdk.XunitException(
                executable + " timed out after " + timeoutMs + "ms");
        }

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }
}
