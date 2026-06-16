using System.Diagnostics;

namespace CSharpToJava.Tests;

public class SystemPrivateXmlRuntimeResourceTests
{
    [Fact]
    public void XmlCharTypeBin_IsAvailableAsManifestResource()
    {
        using var temp = new TempDir();
        var outDir = Path.Combine(temp.Path, "classes");
        var xmlPackageDir = Path.Combine(temp.Path, "dotnet", "xml");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(xmlPackageDir);

        var xmlWriterPath = Path.Combine(xmlPackageDir, "XmlWriter.java");
        var runnerPath = Path.Combine(temp.Path, "Runner.java");
        File.WriteAllText(xmlWriterPath, "package dotnet.xml; public final class XmlWriter { }");
        File.WriteAllText(runnerPath, """
import dotnet.xml.XmlWriter;
import io.github.ningpp.compat.AssemblyCompat;
import io.github.ningpp.compat.UnmanagedMemoryStream;

public class Runner {
    public static void main(String[] args) {
        UnmanagedMemoryStream stream = AssemblyCompat.getManifestResourceStream(XmlWriter.class, "XmlCharType.bin");
        System.out.print(stream.getLength());
    }
}
""");

        var repoRoot = FindRepoRoot();
        var compatProject = Path.Combine(repoRoot, "java", "csharptojava-compat", "pom.xml");
        var compatClasses = Path.Combine(repoRoot, "java", "csharptojava-compat", "target", "classes");
        var mvn = RunProcess(FindRequiredExecutable("mvn"), $"-q -f \"{compatProject}\" -DskipTests package");
        Assert.True(mvn.ExitCode == 0, mvn.Output);

        var sources = string.Join(" ", new[] { xmlWriterPath, runnerPath }.Select(path => $"\"{path}\""));
        var javac = RunProcess(
            FindRequiredExecutable("javac"),
            $"-cp \"{compatClasses}\" -d \"{outDir}\" {sources}");
        Assert.True(javac.ExitCode == 0, javac.Output);

        var classPath = string.Join(Path.PathSeparator, compatClasses, outDir);
        var java = RunProcess(FindRequiredExecutable("java"), $"-cp \"{classPath}\" Runner");
        Assert.True(java.ExitCode == 0, java.Output);
        Assert.Equal("65536", java.Output.Trim());
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CSharpToJavaConverter.slnx"))
                && Directory.Exists(Path.Combine(dir, "java", "csharptojava-compat")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }

    private static string FindRequiredExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), name + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException($"{name} is required for this test.");
    }

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2j-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
