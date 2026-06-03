using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CSharpToJava.Tests;

public class LabelGotoStatementTests
{
    [Fact]
    public void Label_ForLoop()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: for", result.GeneratedCode);
    }

    [Fact]
    public void Label_WhileLoop()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: while (true) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: while", result.GeneratedCode);
    }

    [Fact]
    public void Label_DoWhileLoop()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: do { } while (true);
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: do", result.GeneratedCode);
    }

    [Fact]
    public void Label_ForeachLoop()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(List<int> list) {
        outer: foreach (var x in list) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: for", result.GeneratedCode);
    }

    [Fact]
    public void Label_Block()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: { int x = 1; }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("target: {", result.GeneratedCode);
    }

    [Fact]
    public void Label_SimpleStatement_WrappedInBlock()
    {
        var result = Convert(@"
class Test {
    void M() {
        int x = 0;
        target: x = 1;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("target: { x = 1; }", result.GeneratedCode);
    }

    [Fact]
    public void Label_NestedLabels()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) {
            inner: for (int j = 0; j < 10; j++) { }
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: for", result.GeneratedCode);
        Assert.Contains("inner: for", result.GeneratedCode);
    }

    [Fact]
    public void Goto_LoopLabel_Continue()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) {
            if (i == 5) goto outer;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("continue outer;", result.GeneratedCode);
    }

    [Fact]
    public void Goto_BlockLabel_Break()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: {
            if (true) goto target;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("break target;", result.GeneratedCode);
    }

    [Fact]
    public void Goto_OtherLabel_Break()
    {
        var result = Convert(@"
class Test {
    void M() {
        int x = 0;
        target: x = 1;
        if (true) goto target;
    }
}");
        Assert.True(result.Success);
        // Cross-scope goto now uses state machine
        Assert.Contains("__gotoState", result.GeneratedCode);
        Assert.Contains("__gotoLoop", result.GeneratedCode);
    }

    [Fact]
    public void GotoCase_Supported()
    {
        var result = Convert(@"
class Test {
    void M(int x) {
        switch (x) {
            case 1:
                goto case 2;
            case 2:
                break;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("continue _switch", result.GeneratedCode);
        Assert.DoesNotContain("/* TODO: goto case - unsupported */", result.GeneratedCode);
    }

    [Fact]
    public void GotoDefault_Supported()
    {
        var result = Convert(@"
class Test {
    void M(int x) {
        switch (x) {
            case 1:
                goto default;
            default:
                break;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("continue _switch", result.GeneratedCode);
        Assert.DoesNotContain("/* TODO: goto default - unsupported */", result.GeneratedCode);
    }

    [Fact]
    public void Goto_CrossScope_Unsupported()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: { }
        goto target;
    }
}");
        Assert.True(result.Success);
        // Cross-scope goto now uses state machine
        Assert.Contains("__gotoState", result.GeneratedCode);
        Assert.Contains("__gotoLoop", result.GeneratedCode);
    }

    [Fact]
    public void Goto_UnknownLabel_Unsupported()
    {
        var result = Convert(@"
class Test {
    void M() {
        goto nonexistent;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("/* TODO: goto nonexistent - label not found in scope */", result.GeneratedCode);
    }

    [Fact]
    public void Break_Label_Preserved()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) {
            for (int j = 0; j < 10; j++) {
                if (j == 5) break;
            }
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: for", result.GeneratedCode);
    }

    [Fact]
    public void Goto_WhileLoop_Continue()
    {
        var result = Convert(@"
class Test {
    void M() {
        restart: while (true) {
            if (System.DateTime.Now.Ticks > 0) goto restart;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("continue restart;", result.GeneratedCode);
    }

    [Fact]
    public void Label_Registry_ClearedBetweenMethods()
    {
        var result = Convert(@"
class Test {
    void M1() {
        outer: for (int i = 0; i < 10; i++) { }
    }
    void M2() {
        goto outer;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("/* TODO: goto outer - label not found in scope */", result.GeneratedCode);
    }

    [Fact]
    public void Goto_LabelInsideIf_WithinScope()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: for (int i = 0; i < 10; i++) {
            if (i == 3) {
                goto target;
            }
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("continue target;", result.GeneratedCode);
    }

    [Fact]
    public void CrossScopeGoto_ForwardJump_SkipsStatementsBeforeTarget()
    {
        var result = Convert(@"
class Test {
    static int M(boolean flag) {
        int x = 0;
        if (flag) goto target;
        x = 1;
        target: x = x + 2;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(true) + \",\" + Test.m(false));");

        Assert.Equal("2,3", output);
    }

    [Fact]
    public void CrossScopeGoto_BackwardJump_ContinuesFromTargetWithoutRestartingMethod()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int init = 0;
        int x = 0;
        target: x = x + 1;
        if (x < 3) goto target;
        return init * 10 + x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m());");

        Assert.Equal("3", output);
    }

    [Fact]
    public void CrossScopeGoto_MultipleLabels_RunExpectedPath()
    {
        var result = Convert(@"
class Test {
    static int M(int mode) {
        int x = 0;
        if (mode == 1) goto first;
        if (mode == 2) goto second;
        x = 10;
        goto end;
        first: x = x + 1;
        goto end;
        second: x = x + 2;
        end: return x + 100;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(0) + \",\" + Test.m(1) + \",\" + Test.m(2));");

        Assert.Equal("110,101,102", output);
    }

    [Fact]
    public void CrossScopeGoto_TargetInsideNestedBlock_RunExpectedPath()
    {
        var result = Convert(@"
class Test {
    static int M(boolean jump) {
        int x = 0;
        if (jump) goto inside;
        x = 10;
        {
            inside: x = x + 3;
        }
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(true) + \",\" + Test.m(false));");

        Assert.Equal("3,13", output);
    }

    [Fact]
    public void GotoCase_ExecutesTargetCase()
    {
        var result = Convert(@"
class Test {
    static int M(int x) {
        int result = 0;
        switch (x) {
            case 1:
                result = result + 10;
                goto case 2;
            case 2:
                result = result + 20;
                break;
            default:
                result = result + 30;
                break;
        }
        return result;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(1) + \",\" + Test.m(2) + \",\" + Test.m(3));");

        Assert.Equal("30,20,30", output);
    }

    [Fact]
    public void GotoDefault_ExecutesDefaultCase()
    {
        var result = Convert(@"
class Test {
    static int M(int x) {
        int result = 0;
        switch (x) {
            case 1:
                result = result + 10;
                goto default;
            default:
                result = result + 30;
                break;
        }
        return result;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(1) + \",\" + Test.m(2));");

        Assert.Equal("40,30", output);
    }

    [Fact]
    public void GotoCase_NonAdjacentTarget_ExecutesTargetCase()
    {
        var result = Convert(@"
class Test {
    static int M(int x) {
        int result = 0;
        switch (x) {
            case 1:
                result = result + 10;
                goto case 3;
            case 2:
                result = result + 20;
                break;
            case 3:
                result = result + 30;
                break;
            default:
                result = result + 40;
                break;
        }
        return result;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(1) + \",\" + Test.m(2) + \",\" + Test.m(3) + \",\" + Test.m(4));");

        Assert.Equal("40,20,30,40", output);
    }

    [Fact]
    public void GotoDefault_NonAdjacentTarget_ExecutesDefaultCase()
    {
        var result = Convert(@"
class Test {
    static int M(int x) {
        int result = 0;
        switch (x) {
            case 1:
                result = result + 10;
                goto default;
            case 2:
                result = result + 20;
                break;
            default:
                result = result + 40;
                break;
        }
        return result;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(1) + \",\" + Test.m(2) + \",\" + Test.m(3));");

        Assert.Equal("50,20,40", output);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }

    private static string CompileAndRun(string javaCode, string mainBody)
    {
        var javac = FindExecutable("javac");
        var java = FindExecutable("java");
        if (javac == null || java == null)
        {
            throw new InvalidOperationException("javac/java are required for goto semantic tests.");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "cs2j-goto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var testCode = Regex.Replace(javaCode, @"\bpublic\s+class\s+Test\b", "class Test");
            File.WriteAllText(Path.Combine(tempDir, "Test.java"), testCode);
            File.WriteAllText(Path.Combine(tempDir, "Runner.java"), $@"
public class Runner {{
    public static void main(String[] args) {{
        {mainBody}
    }}
}}
");

            RunProcess(javac, "Test.java Runner.java", tempDir);
            return RunProcess(java, "Runner", tempDir).Trim();
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; test result should not depend on temp deletion.
            }
        }
    }

    private static string? FindExecutable(string name)
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

        return null;
    }

    private static string RunProcess(string fileName, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} exited with {process.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }

        return stdout;
    }
}
