using CSharpToJava.Core.Context;
using System.Diagnostics;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class FlagsEnumToStringTests
{
    private static ConversionResult Convert(string src, string file = "Sample.cs")
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = file,
            Options = new ConversionOptions(),
        });
    }

    private static void WriteTopLevelTypes(string generatedCode, string dir)
    {
        int i = 0;
        while (i < generatedCode.Length)
        {
            var m = System.Text.RegularExpressions.Regex.Match(generatedCode.Substring(i),
                @"(?m)^public\s+(?:static\s+|final\s+|abstract\s+|sealed\s+)*(class|enum|interface|record|@interface)\s+([A-Za-z_$][A-Za-z0-9_$]*)");
            if (!m.Success) break;
            int start = i + m.Index;
            string name = m.Groups[2].Value;
            int brace = generatedCode.IndexOf('{', start);
            if (brace < 0) break;
            int depth = 0;
            int end = -1;
            for (int j = brace; j < generatedCode.Length; j++)
            {
                char c = generatedCode[j];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { end = j + 1; break; }
                }
            }
            if (end < 0) break;
            File.WriteAllText(Path.Combine(dir, name + ".java"), generatedCode.Substring(start, end - start));
            i = end;
        }
    }

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
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
