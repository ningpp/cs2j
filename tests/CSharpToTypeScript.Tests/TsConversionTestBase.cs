using System.Diagnostics;
using System.IO;
using System.Reflection;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToTypeScript.Tests;

public abstract class TsConversionTestBase
{
    protected ConversionResult Convert(string csharpSource)
    {
        var basePath = AppContext.BaseDirectory;
        var typeMappingPath = Path.Combine(basePath, "config", "typescript", "TypeMappings.json");
        var memberMappingPath = Path.Combine(basePath, "config", "typescript", "MemberMappings.json");

        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpSource,
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                TargetLanguage = TargetLanguage.TypeScript,
                TypeScriptMappingConfigPath = File.Exists(typeMappingPath) ? typeMappingPath : null,
                TypeScriptMemberMappingPath = File.Exists(memberMappingPath) ? memberMappingPath : null,
            },
        });
    }

    protected static string StripExports(string code)
    {
        return code.Replace("export ", "");
    }

    protected void AssertDiagnostic(ConversionResult r, string contains)
    {
        Assert.True(r.Diagnostics.Any(d => d.Message.Contains(contains, StringComparison.Ordinal)),
            $"Expected diagnostic containing '{contains}'. Got: {string.Join("; ", r.Diagnostics.Select(d => d.Message))}");
    }

    /// <summary>
    /// L3: 会话级共享临时工程（spec §5.2：预生成 package.json + tsconfig.json，
    /// typescript 仅安装一次），写入待验证 .ts 后调用 tsc --noEmit。无 Node 时跳过。
    /// </summary>
    protected void AssertCompiles(string tsCode, params string[] additionalFiles)
    {
        var nodePath = FindNodeExecutable();
        if (nodePath == null)
        {
            throw new SkipException("Node.js not available — L3 tsc check skipped.");
        }

        var workDir = TsCompileFixture.GetWorkDir(nodePath);   // 懒初始化：首次调用时 npm install typescript
        var probePath = Path.Combine(workDir, "probe_" + Guid.NewGuid().ToString("N") + ".ts");
        File.WriteAllText(probePath, tsCode);
        for (int i = 0; i < additionalFiles.Length; i += 2)
        {
            File.WriteAllText(Path.Combine(workDir, additionalFiles[i]), additionalFiles[i + 1]);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = nodePath,
                Arguments = $"node_modules/typescript/bin/tsc --noEmit -p tsconfig.json",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();   // 先读完输出再等待，避免管道缓冲区满导致死锁
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(60000);
            Assert.True(proc.ExitCode == 0,
                $"tsc failed (exit {proc.ExitCode}):\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}\n--- code ---\n{tsCode}");
        }
        finally
        {
            try { File.Delete(probePath); } catch { }
        }
    }

    /// <summary>
    /// 会话级共享 tsc 工程（避免每个测试重复 npm install，spec §5.2）。
    /// </summary>
    private static class TsCompileFixture
    {
        private static readonly object Lock = new();
        private static string? _workDir;

        public static string GetWorkDir(string nodePath)
        {
            lock (Lock)
            {
                if (_workDir != null) return _workDir;
                var dir = Path.Combine(Path.GetTempPath(), "cs2j_ts_compile_shared");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "package.json"), "{\"type\":\"module\"}");
                File.WriteAllText(Path.Combine(dir, "tsconfig.json"),
                    "{\"compilerOptions\":{\"strict\":true,\"target\":\"ES2022\",\"module\":\"NodeNext\",\"moduleResolution\":\"NodeNext\",\"noEmit\":true,\"useDefineForClassFields\":false},\"include\":[\"*.ts\"]}");
                if (!Directory.Exists(Path.Combine(dir, "node_modules", "typescript")))
                {
                    var npmCmd = OperatingSystem.IsWindows()
                        ? Path.Combine(Path.GetDirectoryName(nodePath)!, "npm.cmd")
                        : "npm";
                    var installPsi = new ProcessStartInfo
                    {
                        FileName = npmCmd,
                        Arguments = "install typescript --no-save",
                        WorkingDirectory = dir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };
                    var install = Process.Start(installPsi)!;
                    install.StandardOutput.ReadToEnd();
                    install.StandardError.ReadToEnd();
                    install.WaitForExit(120000);
                    Assert.True(install.ExitCode == 0, $"npm install typescript failed (exit {install.ExitCode})");
                }
                _workDir = dir;
                return _workDir;
            }
        }
    }

    private static string? FindNodeExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv == null) return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
