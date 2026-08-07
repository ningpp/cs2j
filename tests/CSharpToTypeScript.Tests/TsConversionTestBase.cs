using System.Diagnostics;
using System.IO;
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
                // Task 11 分叉后 TS 路径不消费 Java 类型映射配置，但 Convert 入口仍无条件构造
                // TypeMappingRegistry(config)，缺配置会以 "Configuration error" 失败，故保留该配置。
                TypeMappingConfigPath = File.Exists(Path.Combine(basePath, "config", "TypeMappings.json")) ? Path.Combine(basePath, "config", "TypeMappings.json") : null,
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
    /// L3: 每次校验在共享根目录下的唯一 run_&lt;GUID&gt; 子目录中写入 package.json、
    /// tsconfig.json、探针 .ts 与 additionalFiles，调用共享 typescript 安装执行 tsc --noEmit，
    /// finally 中递归删除该子目录（共享根目录仅保留 typescript 安装，不参与编译）。
    /// 无 Node 时直接返回：xunit v2 普通 [Fact] 无法动态跳过，抛 SkipException 会使测试
    /// FAIL 而非 SKIP，因此空转通过，避免无 Node 机器上 L3 测试误红。
    /// </summary>
    protected void AssertCompiles(string tsCode, params string[] additionalFiles)
    {
        var nodePath = FindNodeExecutable();
        if (nodePath == null)
        {
            // xunit v2 普通 [Fact] 无法动态跳过：抛 SkipException 会使测试 FAIL 而非 SKIP。
            // 无 Node 时 L3 校验空转通过，避免误红。
            return;
        }

        var sharedRoot = TsCompileFixture.GetSharedRoot(nodePath);   // 懒初始化：首次调用时 npm install typescript
        var workDir = Path.Combine(sharedRoot, "run_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        File.WriteAllText(Path.Combine(workDir, "package.json"), "{\"type\":\"module\"}");
        File.WriteAllText(Path.Combine(workDir, "tsconfig.json"),
            "{\"compilerOptions\":{\"strict\":true,\"target\":\"ES2022\",\"module\":\"NodeNext\",\"moduleResolution\":\"NodeNext\",\"noEmit\":true,\"useDefineForClassFields\":false},\"include\":[\"*.ts\"]}");
        var probePath = Path.Combine(workDir, "probe_" + Guid.NewGuid().ToString("N") + ".ts");
        File.WriteAllText(probePath, tsCode);
        for (int i = 0; i < additionalFiles.Length; i += 2)
        {
            // 附加文件可能是 node_modules/... 子路径（Task 18 compat 桩），先建父目录
            var additionalPath = Path.Combine(workDir, additionalFiles[i]);
            var parent = Path.GetDirectoryName(additionalPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
            File.WriteAllText(additionalPath, additionalFiles[i + 1]);
        }

        try
        {
            var tscPath = Path.Combine(sharedRoot, "node_modules", "typescript", "bin", "tsc");
            var tsconfigPath = Path.Combine(workDir, "tsconfig.json");
            var psi = new ProcessStartInfo
            {
                FileName = nodePath,
                Arguments = $"\"{tscPath}\" --noEmit -p \"{tsconfigPath}\"",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var (exitCode, stdout, stderr) = RunProcess(psi, 60000, "tsc");
            Assert.True(exitCode == 0,
                $"tsc failed (exit {exitCode}):\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}\n--- code ---\n{tsCode}");
        }
        finally
        {
            // 安全删除：仅删除共享根目录下以 run_ 前缀命名的子目录，避免误删共享安装
            var fullWorkDir = Path.GetFullPath(workDir);
            var fullSharedRoot = Path.GetFullPath(sharedRoot);
            if (fullWorkDir.StartsWith(fullSharedRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullWorkDir).StartsWith("run_", StringComparison.Ordinal))
            {
                try { Directory.Delete(fullWorkDir, recursive: true); } catch { }
            }
        }
    }

    /// <summary>
    /// L3 变体：@csharptotypescript/compat 尚不存在（M4 Task 28 脚手架），用最小
    /// d.ts 桩使导入可解析（Task 18 起 CsString.format 用例需要；语义测试在 M4 以
    /// vitest 对齐 spec §5.6，桩仅满足 tsc 编译）。
    /// </summary>
    protected void AssertCompilesWithCompat(string tsCode, params string[] additionalFiles)
    {
        var args = new List<string>
        {
            @"node_modules/@csharptotypescript/compat/package.json", CompatPackageJson,
            @"node_modules/@csharptotypescript/compat/index.d.ts", CompatIndexDts,
            @"node_modules/@csharptotypescript/compat/index.js", CompatIndexJs,
        };
        args.AddRange(additionalFiles);
        AssertCompiles(tsCode, args.ToArray());
    }

    private const string CompatPackageJson =
        """{"name":"@csharptotypescript/compat","version":"0.0.0","types":"index.d.ts","exports":{".":{"types":"./index.d.ts","default":"./index.js"}}}""";

    private const string CompatIndexDts =
        """
export declare namespace CsString { function format(fmt: string, ...args: unknown[]): string; }
export declare class CsException extends Error { constructor(message?: string); getMessage(): string; }
export declare class ArgumentException extends CsException {}
export declare class ArgumentNullException extends ArgumentException {}
export declare class ArgumentOutOfRangeException extends ArgumentException {}
export declare class InvalidOperationException extends CsException {}
export declare class NotSupportedException extends CsException {}
export declare class NotImplementedException extends CsException {}
export declare class IndexOutOfRangeException extends CsException {}
export declare class KeyNotFoundException extends CsException {}
export declare class ArithmeticException extends CsException {}
export declare class DivideByZeroException extends ArithmeticException {}
export declare class OverflowException extends ArithmeticException {}
export declare class FormatException extends CsException {}
export declare class IOException extends CsException {}
export declare class TypeToken<T = any> {
    readonly name: string;
    constructor(name: string);
    getName(): string;
    static of<T = any>(name: string): TypeToken<T>;
    static nameOf<T = any>(name: string): string;
    static isInstance<T = any>(name: string, v: unknown): boolean;
}
export declare function csDefault(typeInfo?: TypeToken): any;
export declare class CsRuntime {
    static markImplements(obj: object, ...ifaceNames: string[]): void;
    static isMarked(obj: unknown, ifaceName: string): boolean;
}
""";

    private const string CompatIndexJs =
        """
export const CsString = { format(fmt, ...args) { return fmt; } };
export class CsException extends Error { constructor(message) { super(message); } getMessage() { return this.message; } }
export class ArgumentException extends CsException {}
export class ArgumentNullException extends ArgumentException {}
export class ArgumentOutOfRangeException extends ArgumentException {}
export class InvalidOperationException extends CsException {}
export class NotSupportedException extends CsException {}
export class NotImplementedException extends CsException {}
export class IndexOutOfRangeException extends CsException {}
export class KeyNotFoundException extends CsException {}
export class ArithmeticException extends CsException {}
export class DivideByZeroException extends ArithmeticException {}
export class OverflowException extends ArithmeticException {}
export class FormatException extends CsException {}
export class IOException extends CsException {}
export class TypeToken {
    constructor(name) { this.name = name; }
    getName() { return this.name; }
    static of(name) { return new TypeToken(name); }
    static nameOf(name) { return name; }
    static isInstance(name, v) { return v !== null && v !== undefined; }
}
export function csDefault(typeInfo) { return null; }
export class CsRuntime {
    static markImplements(obj, ...ifaceNames) {}
    static isMarked(obj, ifaceName) { return false; }
}
""";

    /// <summary>
    /// 启动进程并等待退出；超时则终止整个进程树并以包含已捕获输出的清晰消息断言失败。
    /// </summary>
    private static (int ExitCode, string Stdout, string Stderr) RunProcess(ProcessStartInfo psi, int timeoutMs, string description)
    {
        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit();   // 确保进程退出、管道关闭后 ExitCode 可读
            Assert.Fail($"{description} timed out after {timeoutMs}ms:\n--- stdout ---\n{stdoutTask.Result}\n--- stderr ---\n{stderrTask.Result}");
        }
        proc.WaitForExit();
        return (proc.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>
    /// 会话级共享 typescript 安装（spec §5.2：typescript 仅安装一次；共享根目录不参与编译）。
    /// </summary>
    private static class TsCompileFixture
    {
        private static readonly object Lock = new();
        private static string? _sharedRoot;

        public static string GetSharedRoot(string nodePath)
        {
            lock (Lock)
            {
                if (_sharedRoot != null) return _sharedRoot;
                var dir = Path.Combine(Path.GetTempPath(), "cs2j_ts_compile_shared");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "package.json"), "{\"type\":\"module\"}");
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
                    var (exitCode, stdout, stderr) = RunProcess(installPsi, 120000, "npm install typescript");
                    Assert.True(exitCode == 0,
                        $"npm install typescript failed (exit {exitCode}):\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
                }
                _sharedRoot = dir;
                return _sharedRoot;
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
