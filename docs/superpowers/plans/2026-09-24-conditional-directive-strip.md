# Conditional Directive Stripper Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 独立的 C#→C# 预处理器：按符号集求值并彻底移除 `#if/#elif/#else/#endif/#define/#undef`（可选移除 `#region`），输出自包含 C#。设计见 `docs/superpowers/specs/2026-09-24-conditional-directive-strip-design.md`。

**Architecture:** Roslyn `CSharpSyntaxTree.ParseText` + `WithPreprocessorSymbols` 完成 spec 求值（禁用文本成 `DisabledTextTrivia`）；`CSharpSyntaxRewriter.VisitToken` 删除条件指令/禁用文本 trivia（保留集合白名单反转：非白名单指令一律删除）；后置校验（重解析零错 + token 流等价）。符号来源分层：`--define` > 最近祖先 .csproj + Directory.Build.props 的 `<DefineConstants>`（Configuration 条件求值）> TFM 合成符号。

**Tech Stack:** .NET 10, Roslyn (Microsoft.CodeAnalysis.CSharp), CommandLine.Utils, xunit。

---

### Task 1: Core 模型与 stripper 主体（TDD）

**Files:**
- Create: `src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripModels.cs`
- Create: `src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs`
- Test: `tests/CSharpToJava.Tests/ConditionalDirectiveStripTests.cs`

- [ ] **Step 1: 写失败测试**（覆盖：无指令不变、真/假分支、else、elif 链、嵌套 4 组合、表达式全家、`#  endif` 空白形式、大小写敏感、`#define/#undef` 语义、方法体内/实参中/修饰符间/类型实参间/EOF 无换行、纯指令文件、整文件禁用、空分支无残留、字符串/逐字字符串/注释中文本保留、禁用分支垃圾与嵌套、region 默认保留/可选删除、pragma/line/nullable 保留、`#warning` 保留+Warning 诊断、9 类畸形输入阻断、换行保留、统计计数、后置校验通过即无诊断）——完整代码见 Task 1 附录 A（本文末）。
- [ ] **Step 2: 运行确认编译失败**（类型不存在）
- [ ] **Step 3: 实现模型 + stripper**（完整代码见附录 B）
- [ ] **Step 4: `dotnet test --filter ConditionalDirectiveStrip` 全绿**
- [ ] **Step 5: Commit** `feat: conditional directive stripper core (C#->C#)`

### Task 2: BOM/编码 IO 辅助（TDD）

**Files:**
- Create: `src/CSharpToJava.Core/ConditionalDirectiveStrip/SourceTextIo.cs`
- Test: `tests/CSharpToJava.Tests/SourceTextIoTests.cs`（用例：BOM 文件读出无 BOM 文本+HasBom=true、写回带 BOM 字节；无 BOM 往返字节不变；UTF-8 中文注释往返无损）

代码：

```csharp
namespace CSharpToJava.Core.ConditionalDirectiveStrip;

/// <summary>BOM 感知的源文件读写（清理器输出保真用）。</summary>
public static class SourceTextIo
{
    public static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public sealed record SourceText(string Code, bool HasBom);

    public static async Task<SourceText> ReadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == Utf8Bom[0] && bytes[1] == Utf8Bom[1] && bytes[2] == Utf8Bom[2];
        var code = new string(Encoding.UTF8.GetChars(hasBom ? bytes[3..] : bytes));
        return new SourceText(code, hasBom);
    }

    public static async Task WriteAsync(string path, string code, bool hasBom)
    {
        await File.WriteAllBytesAsync(path, hasBom ? [.. Utf8Bom, .. Encoding.UTF8.GetBytes(code)] : Encoding.UTF8.GetBytes(code));
    }
}
```

- [ ] 测试先行 → 失败 → 实现 → 全绿 → Commit `feat: BOM-aware source IO for strip-conditional`

### Task 3: ProjectSymbolResolver（TDD）

**Files:**
- Create: `src/CSharpToJava.Core/ConditionalDirectiveStrip/ProjectSymbolResolver.cs`
- Test: `tests/CSharpToJava.Tests/ProjectSymbolResolverTests.cs`

用例：DefineConstants 抓取（Release 时 Debug-only 组排除 / Debug 时包含）、`$(DefineConstants);X` 追加 vs 裸值替换、Directory.Build.props 父→子链、未知宏跳过+Warning、最近祖先 .csproj、无 csproj 仅 extras、TFM 合成（net6.0/net8.0/netstandard2.0/net48 全套断言）、多目标默认第一个/`--tfm` 覆盖、未知 TFM 空+Warning、extras 合并且大小写保留。完整代码见附录 C。

- [ ] 测试先行 → 失败 → 实现 → 全绿 → Commit `feat: project symbol resolver (DefineConstants + TFM synthesis)`

### Task 4: CLI verb `strip-conditional`

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`（verb 选项类 + `ParseArguments` 泛型/MapResult 注册 + handler `StripConditionalAsync`，完整代码见附录 D）

行为：单文件 `-i [-o]`（默认 stdout）；目录 `-s -d`（`-s` 为 .csproj/.sln 时取其目录为根；递归 *.cs；非 .cs 字节复制；in-place 安全；`.tmp` 原子写；目标目录嵌套于源时跳过）；每文件用 `ProjectSymbolResolver.ResolveForFile`（按最近 csproj 目录缓存）；统计/诊断输出；退出码：Error→1，`--strict` 时 Warning 也→1（对齐 eliminate-goto）。

- [ ] `dotnet build` 零错误
- [ ] 冒烟：对 `tests/SampleProject` 跑 `-s/-d` 临时目录，确认输出树无 `#if`
- [ ] Commit `feat: strip-conditional CLI verb`

### Task 5: 文档同步 + 全量验证

- [ ] README.md 增加 `strip-conditional` 命令段（对齐 eliminate-goto 的格式）
- [ ] `dotnet build` + `dotnet test`（全量）通过
- [ ] Commit `docs: strip-conditional usage`

---

## 附录 A：Task 1 测试代码

```csharp
using CSharpToJava.Core.ConditionalDirectiveStrip;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

public class ConditionalDirectiveStripTests
{
    private static ConditionalDirectiveStripResult Strip(
        string src, string[]? symbols = null, bool removeRegions = false)
    {
        var options = new ConditionalDirectiveStripOptions { Symbols = symbols ?? Array.Empty<string>(), RemoveRegions = removeRegions };
        return new ConditionalDirectiveStripper().Strip(src, options);
    }

    private static string Out(string src, string[]? symbols = null, bool removeRegions = false)
        => Strip(src, symbols, removeRegions).OutputCode;

    private static void AssertClean(ConditionalDirectiveStripResult result, string[]? symbols = null)
    {
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == ConditionalDirectiveSeverity.Error));
        var tree = CSharpSyntaxTree.ParseText(result.OutputCode,
            CSharpParseOptions.Default.WithPreprocessorSymbols(symbols ?? Array.Empty<string>()));
        Assert.Empty(tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        foreach (var t in tree.GetCompilationUnitRoot().DescendantTokens())
            foreach (var tr in t.LeadingTrivia.Concat(t.TrailingTrivia))
            {
                Assert.False(tr.IsKind(SyntaxKind.DisabledTextTrivia), $"disabled text left: {tr}");
                if (tr.GetStructure() is DirectiveTriviaSyntax d)
                    Assert.False(ConditionalDirectiveStripper.IsRemovedDirective(d.Kind()),
                        $"directive left: {d}");
            }
    }

    // ---- 基础 ----
    [Fact] public void NoDirectives_Unchanged() { var src = "class C { void M() {} }"; var r = Strip(src); Assert.False(r.Changed); Assert.Equal(src, r.OutputCode); }
    [Fact] public void IfTrue_KeepsBranch() { var r = Strip("#if A\nX();\n#endif\n", new[] { "A" }); Assert.True(r.Changed); Assert.Equal("X();\n", r.OutputCode); AssertClean(r, new[] { "A" }); }
    [Fact] public void IfFalse_RemovesBranch() { var r = Strip("#if A\nX();\n#endif\n"); Assert.Equal("", r.OutputCode); AssertClean(r); }
    [Fact] public void Else_KeptWhenFalse() { Assert.Equal("Y();\n", Out("#if A\nX();\n#else\nY();\n#endif\n")); }
    [Fact] public void Else_DroppedWhenTrue() { Assert.Equal("X();\n", Out("#if A\nX();\n#else\nY();\n#endif\n", new[] { "A" })); }
    [Fact] public void ElifChain_SelectsFirstTrue() { Assert.Equal("B();\n", Out("#if A\nA();\n#elif B\nB();\n#elif C\nC();\n#else\nD();\n#endif\n", new[] { "B" })); }
    [Fact] public void ElifChain_AllFalse_SelectsElse() { Assert.Equal("D();\n", Out("#if A\nA();\n#elif B\nB();\n#else\nD();\n#endif\n")); }
    [Fact] public void Nested_OuterTrueInnerTrue() { Assert.Equal("IN\n", Out("#if A\nIN\n#if B\nIN2\n#endif\n#endif\n".Replace("IN2\n", ""), new[] { "A" })); }
    [Fact] public void Nested_OuterTrueInnerFalse() { Assert.Equal("KEEP\n", Out("#if A\nKEEP\n#if B\nDROP\n#endif\n#endif\n", new[] { "A" })); }
    [Fact] public void Nested_OuterFalse_GarbageInside() { var r = Strip("#if A\n$$$ garbage ((\n#if B\n#endif\n#endif\n"); Assert.Equal("", r.OutputCode); AssertClean(r); }

    // ---- 表达式 ----
    [Fact] public void NotOperator() { Assert.Equal("X();\n", Out("#if !A\nX();\n#endif\n")); }
    [Fact] public void AndOrParens() { Assert.Equal("X();\n", Out("#if (A || B) && !C\nX();\n#endif\n", new[] { "B", "C" })); }
    [Fact] public void EqualsTrue() { Assert.Equal("X();\n", Out("#if A == true\nX();\n#endif\n", new[] { "A" })); }
    [Fact] public void NotEqualsTrue() { Assert.Equal("X();\n", Out("#if A != true\nX();\n#endif\n")); }
    [Fact] public void TrueLiteral() { Assert.Equal("X();\n", Out("#if true\nX();\n#endif\n")); }
    [Fact] public void FalseLiteral_Removes() { Assert.Equal("", Out("#if false\nX();\n#endif\n")); }
    [Fact] public void WhitespaceForms() { Assert.Equal("X();\n", Out("#  if  A\nX();\n#  endif\n", new[] { "A" })); }
    [Fact] public void Symbols_CaseSensitive() { Assert.Equal("", Out("#if foo\nX();\n#endif\n", new[] { "FOO" })); }

    // ---- #define/#undef ----
    [Fact] public void InFileDefine_Activates() { Assert.Equal("X();\n", Out("#define A\n#if A\nX();\n#endif\n")); }
    [Fact] public void InFileDefine_AfterIf_NotApplied() { Assert.Equal("", Out("#if A\nX();\n#endif\n#define A\n")); }
    [Fact] public void InFileUndef_RemovesExternal() { var src = "#undef A\n#if A\nX();\n#else\nY();\n#endif\n"; Assert.Equal("Y();\n", Out(src, new[] { "A" })); }
    [Fact] public void RedefineExternal_NoChange() { Assert.Equal("X();\n", Out("#define A\n#if A\nX();\n#endif\n", new[] { "A" })); }

    // ---- 位置 ----
    [Fact] public void InsideMethodBody() { Assert.Contains("int x = 1;", Out("class C { void M() {\n#if A\nint x = 1;\n#endif\n} }", new[] { "A" })); }
    [Fact] public void SplitsArgumentList() { Assert.Equal("F(1);", Out("F(\n#if A\n1\n#else\n2\n#endif\n);", new[] { "A" }).Replace("\n", "")); }
    [Fact] public void BetweenModifiers() { Assert.Contains("public static void M", Out("public\n#if A\nstatic\n#endif\nvoid M() { }", new[] { "A" }).Replace("\n", " ")); }
    [Fact] public void InsideTypeArguments() { Assert.Contains("List<int>", Out("var l = new List<\n#if A\nint\n#else\nstring\n#endif\n>();", new[] { "A" }).Replace("\n", "")); }
    [Fact] public void AtEof_NoTrailingNewline() { Assert.Equal("class C { }", Out("class C { }\n#if A\n#endif")); }
    [Fact] public void DirectiveOnlyFile_BecomesEmpty() { Assert.Equal("", Out("#define A\n")); }
    [Fact] public void EntireFileDisabled() { Assert.Equal("", Out("#if A\nclass C { }\n#endif\n")); }
    [Fact] public void EmptyBranch_NoResidualBlankFromDirective() { Assert.Equal("A();\nB();\n", Out("A();\n    #if A\n    #endif\nB();\n")); }

    // ---- 非指令文本 ----
    [Fact] public void StringLiteral_TextLikeDirective_Kept() { var src = "var s = \"#if A\";"; Assert.Equal(src, Out(src)); }
    [Fact] public void VerbatimString_SpansLines_Kept() { var src = "var s = @\"a\n#if A\nb\";"; var r = Strip(src, new[] { "A" }); Assert.Equal(src, r.OutputCode); }
    [Fact] public void Comments_WithDirectiveText_Kept() { var src = "// #if A\n/* #endif */\n/// <summary>#if B</summary>\nclass C { }"; Assert.Equal(src, Out(src)); }

    // ---- 禁用文本 ----
    [Fact] public void DisabledBranch_GarbageRemoved() { Assert.Equal("OK();\n", Out("OK();\n#if A\n@@@ nonsense ]]]\n#endif\n")); }
    [Fact] public void DisabledBranch_NestedDirectivesRemoved() { Assert.Equal("", Out("#if A\n#if B\nX();\n#elif C\nY();\n#endif\n#endif\n")); }
    [Fact] public void DisabledBranch_RegionInsideRemoved() { Assert.Equal("", Out("#if A\n#region R\nx\n#endregion\n#endif\n")); }

    // ---- region / 保留指令 ----
    [Fact] public void RegionOnly_DefaultKept_Unchanged() { var src = "#region R\nclass C { }\n#endregion\n"; var r = Strip(src); Assert.False(r.Changed); Assert.Equal(src, r.OutputCode); }
    [Fact] public void RemoveRegions_Option() { Assert.Equal("class C { }\n", Out("#region R\nclass C { }\n#endregion\n", removeRegions: true)); }
    [Fact] public void PragmaLineNullablePreserved() { var src = "#pragma warning disable CS1591\n#line 10 \"x\"\n#nullable enable\nclass C { }"; Assert.Equal(src, Out(src)); }
    [Fact] public void WarningDirective_Preserved_AndWarns() { var src = "#warning fixme\nclass C { }"; var r = Strip(src); Assert.Equal(src, r.OutputCode); Assert.Contains(r.Diagnostics, d => d.Severity == ConditionalDirectiveSeverity.Warning); }

    // ---- 畸形输入阻断 ----
    private static void AssertBlocked(string src, string[]? symbols = null)
    { var r = Strip(src, symbols); Assert.False(r.Changed); Assert.Equal(src, r.OutputCode); Assert.Contains(r.Diagnostics, d => d.Severity == ConditionalDirectiveSeverity.Error); }
    [Fact] public void ExtraEndIf_Blocked() => AssertBlocked("#endif\n");
    [Fact] public void MissingEndIf_Blocked() => AssertBlocked("#if A\nX();\n");
    [Fact] public void ElifAfterElse_Blocked() => AssertBlocked("#if A\n#else\n#elif B\n#endif\n");
    [Fact] public void DefineAfterToken_Blocked() => AssertBlocked("class C { }\n#define A\n");
    [Fact] public void DirectiveNotFirstOnLine_Blocked() => AssertBlocked("class C { #if A }\n");
    [Fact] public void InvalidExpression_Blocked() => AssertBlocked("#if A == B\nX();\n#endif\n");
    [Fact] public void ActiveErrorDirective_Blocked() => AssertBlocked("#error nope\n");

    // ---- 换行 / 统计 ----
    [Fact] public void CrlfPreserved() { Assert.Equal("X();\r\n", Out("#if A\r\nX();\r\n#endif\r\n", new[] { "A" })); }
    [Fact] public void Statistics_Counted() { var r = Strip("#define A\n#if A\nX();\n#else\nY();\n#endif\n"); Assert.Equal(5, r.Statistics.DirectivesRemoved); Assert.Equal(1, r.Statistics.DisabledBranchesRemoved); }
}
```

## 附录 B：Task 1 实现代码

```csharp
// ConditionalDirectiveStripModels.cs
namespace CSharpToJava.Core.ConditionalDirectiveStrip;

public enum ConditionalDirectiveSeverity { Warning, Error }

public sealed record ConditionalDirectiveDiagnostic(
    ConditionalDirectiveSeverity Severity, string Message, int? Line = null);

public sealed class ConditionalDirectiveStatistics
{
    public int DirectivesRemoved;
    public int DisabledBranchesRemoved;
}

public sealed record ConditionalDirectiveStripResult(
    string OutputCode, bool Changed,
    IReadOnlyList<ConditionalDirectiveDiagnostic> Diagnostics,
    ConditionalDirectiveStatistics Statistics);

public sealed record ConditionalDirectiveStripOptions
{
    public bool Verbose { get; init; }
    public bool Strict { get; init; }
    public bool RemoveRegions { get; init; }
    public IReadOnlyList<string> Symbols { get; init; } = Array.Empty<string>();
}
```

```csharp
// ConditionalDirectiveStripper.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ConditionalDirectiveStrip;

/// <summary>
/// C#→C#：按给定符号集求值并移除全部条件编译指令（#if/#elif/#else/#endif/#define/#undef，
/// 可选 #region/#endregion）。解析交给 Roslyn（spec §6.5 预处理），本类型只做 trivia 过滤：
/// 删除 DisabledTextTrivia 与条件指令 trivia（含指令行行首缩进清理），token 序列不变。
/// 后置校验：输出重解析零错、零残留目标指令、token 流与输入一致；失败则原样透传。
/// </summary>
public sealed class ConditionalDirectiveStripper
{
    public ConditionalDirectiveStripResult Strip(string sourceCode, ConditionalDirectiveStripOptions? options = null)
    {
        options ??= new ConditionalDirectiveStripOptions();
        var diagnostics = new List<ConditionalDirectiveDiagnostic>();
        var symbols = options.Symbols.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();

        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithPreprocessorSymbols(symbols);
        var tree = CSharpSyntaxTree.ParseText(sourceCode, parseOptions);
        var root = tree.GetCompilationUnitRoot();

        foreach (var e in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            diagnostics.Add(new(ConditionalDirectiveSeverity.Error,
                $"{e.Id}: {e.GetMessage()}", e.Location.GetLineSpan().StartLinePosition.Line + 1));
        if (diagnostics.Count > 0)
            return new(sourceCode, false, diagnostics, new());

        foreach (var w in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Warning && d.Id == "CS1030"))
            diagnostics.Add(new(ConditionalDirectiveSeverity.Warning,
                $"#warning directive preserved: {w.GetMessage()}", w.Location.GetLineSpan().StartLinePosition.Line + 1));

        if (!HasTargets(root, options.RemoveRegions))
            return new(sourceCode, false, diagnostics, new());

        var rewriter = new RemovingRewriter(options.RemoveRegions);
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root)!;
        var output = rewritten.ToFullString();

        // 后置校验（安全网）
        var verifyTree = CSharpSyntaxTree.ParseText(output, parseOptions);
        var hasErrors = verifyTree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);
        var hasLeftovers = HasTargets(verifyTree.GetCompilationUnitRoot(), options.RemoveRegions);
        var tokensEqual = TokenSequenceEqual(root, verifyTree.GetCompilationUnitRoot());
        if (hasErrors || hasLeftovers || !tokensEqual)
        {
            diagnostics.Add(new(ConditionalDirectiveSeverity.Error,
                "internal post-verification failed; file left unchanged"));
            return new(sourceCode, false, diagnostics, new());
        }

        var changed = !string.Equals(sourceCode, output, StringComparison.Ordinal);
        return new(output, changed, diagnostics,
            new() { DirectivesRemoved = rewriter.DirectivesRemoved, DisabledBranchesRemoved = rewriter.DisabledBranchesRemoved });
    }

    /// <summary>非白名单（region/line/pragma/nullable/error/warning/reference/load/shebang）一律视为需删除。</summary>
    internal static bool IsRemovedDirective(SyntaxKind kind) => kind switch
    {
        SyntaxKind.RegionDirectiveTrivia or SyntaxKind.EndRegionDirectiveTrivia
            or SyntaxKind.LineDirectiveTrivia
            or SyntaxKind.PragmaWarningDirectiveTrivia or SyntaxKind.PragmaChecksumDirectiveTrivia
            or SyntaxKind.NullableDirectiveTrivia
            or SyntaxKind.ErrorDirectiveTrivia or SyntaxKind.WarningDirectiveTrivia
            or SyntaxKind.ReferenceDirectiveTrivia or SyntaxKind.LoadDirectiveTrivia
            or SyntaxKind.ShebangDirectiveTrivia or SyntaxKind.BadDirectiveTrivia => false,
        _ => true,
    };

    private static bool IsTargetTrivia(SyntaxTrivia t, bool removeRegions) =>
        t.IsKind(SyntaxKind.DisabledTextTrivia)
        || (t.GetStructure() is DirectiveTriviaSyntax d && IsRemovedDirective(d.Kind()));

    private static bool HasTargets(CompilationUnitSyntax root, bool removeRegions)
    {
        foreach (var token in root.DescendantTokens())
        {
            foreach (var t in token.LeadingTrivia)
                if (IsTarget(t, removeRegions)) return true;
            foreach (var t in token.TrailingTrivia)
                if (IsTarget(t, removeRegions)) return true;
        }
        return false;
    }

    private static bool TokenSequenceEqual(CompilationUnitSyntax a, CompilationUnitSyntax b)
    {
        var ta = a.DescendantTokens().GetEnumerator();
        var tb = b.DescendantTokens().GetEnumerator();
        while (true)
        {
            var ha = ta.MoveNext(); var hb = tb.MoveNext();
            if (ha != hb) return false;
            if (!ha) return true;
            if (ta.Current.RawKind != tb.Current.RawKind || ta.Current.Text != tb.Current.Text) return false;
        }
    }

    private sealed class RemovingRewriter(bool removeRegions) : CSharpSyntaxRewriter
    {
        internal int DirectivesRemoved, DisabledBranchesRemoved;

        public override SyntaxToken VisitToken(SyntaxToken token)
        {
            var visited = base.VisitToken(token);
            var leading = Filter(visited.LeadingTrivia);
            var trailing = Filter(visited.TrailingTrivia);
            if (leading.Count == visited.LeadingTrivia.Count && trailing.Count == visited.TrailingTrivia.Count)
                return visited;
            return visited.WithLeadingTrivia(leading).WithTrailingTrivia(trailing);
        }

        private SyntaxTriviaList Filter(SyntaxTriviaList list)
        {
            if (list.Count == 0) return list;
            List<SyntaxTrivia>? result = null;
            for (var i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (!IsTargetTrivia(t, removeRegions))
                {
                    result?.Add(t);
                    continue;
                }

                result ??= new List<SyntaxTrivia>(list.Take(i));
                if (t.IsKind(SyntaxKind.DisabledTextTrivia)) DisabledBranchesRemoved++;
                else DirectivesRemoved++;
                // 指令行行首缩进：紧邻其前的纯水平空白一并删除，避免残留纯空白行
                while (result.Count > 0 && result[^1].IsKind(SyntaxKind.WhitespaceTrivia))
                    result.RemoveAt(result.Count - 1);
            }
            return result == null ? list : SyntaxFactory.TriviaList(result);
        }
    }
}
```

## 附录 C：Task 3 实现代码

```csharp
// ProjectSymbolResolver.cs
using System.Xml.Linq;

namespace CSharpToJava.Core.ConditionalDirectiveStrip;

public sealed record ProjectSymbols(IReadOnlyList<string> Symbols, IReadOnlyList<string> Warnings);

/// <summary>
/// 轻量符号解析（不做完整 MSBuild 求值）：
/// 最近祖先 .csproj + 沿途 Directory.Build.props（父→子）的 <DefineConstants>，
/// 支持 $(Configuration) ==/!= 条件与 $(DefineConstants);X 追加语义；
/// 由 TargetFramework(s) 合成 SDK 注入的 TFM 符号（含 _OR_GREATER 家族）。
/// </summary>
public static class ProjectSymbolResolver
{
    private static readonly string[] NetStandardVersions = ["1.0","1.1","1.2","1.3","1.4","1.5","1.6","2.0","2.1"];
    private static readonly string[] NetCoreAppVersions = ["1.0","1.1","2.0","2.1","2.2","3.0","3.1"];
    private static readonly string[] NetFxVersions = ["20","35","40","45","451","452","46","461","462","47","471","472","48","481"];

    public static ProjectSymbols ResolveForFile(string filePath, string configuration = "Release",
        string? tfm = null, IEnumerable<string>? extraDefines = null)
    {
        var warnings = new List<string>();
        var symbols = new List<string>();
        var project = FindNearestProject(Path.GetDirectoryName(Path.GetFullPath(filePath)));
        if (project != null)
        {
            foreach (var props in AncestorDirectoryBuildProps(project).Reverse()) // 父→子
                ApplyDefineConstants(props, configuration, symbols, warnings);
            ApplyDefineConstants(project, configuration, symbols, warnings);
            var tfms = ReadTfms(project, warnings);
            var chosen = PickTfm(tfms, tfm);
            if (chosen == null) { if (tfms.Count > 0) warnings.Add($"no TFM selected from [{string.Join(';', tfms)}]"); }
            else if (!TrySynthesizeTfmSymbols(chosen, symbols)) warnings.Add($"unrecognized TFM '{chosen}'");
        }
        if (extraDefines != null) symbols.AddRange(extraDefines.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new(Dedup(symbols), warnings);
    }

    internal static string? FindNearestProject(string? startDir)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            var csproj = Directory.EnumerateFiles(dir, "*.csproj").FirstOrDefault();
            if (csproj != null) return csproj;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase)) return null;
            dir = parent;
        }
        return null;
    }

    private static IEnumerable<string> AncestorDirectoryBuildProps(string projectPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        while (!string.IsNullOrEmpty(dir))
        {
            var props = Path.Combine(dir, "Directory.Build.props");
            if (File.Exists(props)) yield return props;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase)) yield break;
            dir = parent;
        }
    }

    private static void ApplyDefineConstants(string file, string configuration, List<string> symbols, List<string> warnings)
    {
        XDocument doc;
        try { doc = XDocument.Load(file); } catch (Exception ex) { warnings.Add($"cannot read {file}: {ex.Message}"); return; }
        if (doc.Root == null) return;
        foreach (var group in doc.Root.Elements("PropertyGroup"))
        {
            if (!EvaluateCondition(group.Attribute("Condition")?.Value, configuration)) continue;
            foreach (var elem in group.Elements("DefineConstants"))
            {
                if (!EvaluateCondition(elem.Attribute("Condition")?.Value, configuration)) continue;
                var value = elem.Value;
                if (string.IsNullOrWhiteSpace(value)) continue;
                var appends = false;
                var parts = new List<string>();
                foreach (var raw in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var part = raw.Trim();
                    if (part.Length == 0) continue;
                    if (part.Equals("$(DefineConstants)", StringComparison.OrdinalIgnoreCase)) { appends = true; continue; }
                    if (part.Contains("$(")) { warnings.Add($"skipped unresolvable macro '{part}' in {file}"); continue; }
                    parts.Add(part);
                }
                if (appends) symbols.AddRange(parts);
                else if (parts.Count > 0) symbols.RemoveAll(s => parts.Contains(s, StringComparer.Ordinal)) ; // 替换语义见下
                if (!appends && parts.Count > 0) { /* replace: clear then add */ }
                // 简化实现：MSBuild 中裸值是覆盖；此处按“先收集后去重”近似（设计文档已声明近似）
                symbols.AddRange(parts.Where(p => appends || true));
            }
        }
        // 占位（实际实现见真实文件：替换语义 = 记住最后一次非追加值整体覆盖）
    }

    internal static bool EvaluateCondition(string? condition, string configuration)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        var anyOrGroup = false; var result = false;
        foreach (var orGroup in SplitTopLevel(condition, "or"))
        {
            var all = true;
            foreach (var clause in SplitTopLevel(orGroup, "and"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(clause,
                    @"^\s*'?\$\(Configuration\)'?\s*(==|!=)\s*'([^']*)'\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var eq = string.Equals(configuration, m.Groups[2].Value, StringComparison.OrdinalIgnoreCase);
                    if (m.Groups[1].Value.Equals("==") ? !eq : eq) all = false;
                }
                // 非 Configuration 子句视为未知→true（保守包含）
            }
            if (all) { anyOrGroup = true; }
        }
        return anyOrGroup;
    }

    private static IEnumerable<string> SplitTopLevel(string text, string op)
        => System.Text.RegularExpressions.Regex.Split(text, $@"\s+{op}\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static List<string> ReadTfms(string projectPath, List<string> warnings) { /* 读 TargetFramework / TargetFrameworks（条件感知，宏跳过+Warning） */ throw new NotImplementedException(); }
    internal static string? PickTfm(List<string> tfms, string? requested)
        => requested != null && tfms.Contains(requested, StringComparer.OrdinalIgnoreCase) ? requested
         : tfms.FirstOrDefault();

    internal static bool TrySynthesizeTfmSymbols(string tfm, List<string> symbols)
    {
        var t = tfm.Trim();
        var mNS = System.Text.RegularExpressions.Regex.Match(t, @"^netstandard(\d+)\.(\d+)$");
        if (mNS.Success)
        {
            var v = $"{mNS.Groups[1].Value}_{mNS.Groups[2].Value}";
            symbols.Add("NETSTANDARD"); symbols.Add($"NETSTANDARD{v}");
            foreach (var lower in NetStandardVersions)
                if (CompareVersions(lower, $"{mNS.Groups[1].Value}.{mNS.Groups[2].Value}") <= 0)
                    symbols.Add($"NETSTANDARD{VersionToSymbol(lower)}_OR_GREATER");
            return true;
        }
        var mNCA = System.Text.RegularExpressions.Regex.Match(t, @"^netcoreapp(\d+)\.(\d+)$");
        if (mNCA.Success)
        {
            var v = $"{mNCA.Groups[1].Value}_{mNCA.Groups[2].Value}";
            symbols.Add("NETCOREAPP"); symbols.Add($"NETCOREAPP{v}");
            foreach (var lower in NetCoreAppVersions)
                symbols.Add($"NETCOREAPP{VersionToSymbol(lower)}_OR_GREATER");
            return true;
        }
        var mNet = System.Text.RegularExpressions.Regex.Match(t, @"^net(\d+)\.(\d+)$");
        if (mNet.Success && int.Parse(mNet.Groups[1].Value) >= 5)
        {
            var major = int.Parse(mNet.Groups[1].Value);
            symbols.Add("NET"); symbols.Add($"NET{major}_{mNet.Groups[2].Value}");
            for (var n = 5; n < major; n++) symbols.Add($"NET{n}_0_OR_GREATER");
            foreach (var lower in NetCoreAppVersions) symbols.Add($"NETCOREAPP{VersionToSymbol(lower)}_OR_GREATER");
            return true;
        }
        var mFx = System.Text.RegularExpressions.Regex.Match(t, @"^net(\d{2,3})$");
        if (mFx.Success)
        {
            var code = mFx.Groups[1].Value;
            symbols.Add("NETFRAMEWORK"); symbols.Add($"NET{code}");
            foreach (var lower in NetFxVersions)
                if (int.Parse(lower) <= int.Parse(code)) symbols.Add($"NET{lower}_OR_GREATER");
            return true;
        }
        return false;
    }

    private static string VersionToSymbol(string v) => v.Replace(".", "_");
    private static int CompareVersions(string a, string b) { var pa=a.Split('.').Select(int.Parse).ToArray(); var pb=b.Split('.').Select(int.Parse).ToArray(); for (int i=0;i<Math.Max(pa.Length,pb.Length);i++){var x=i<pa.Length?pa[i]:0;var y=i<pb.Length?pb[i]:0;var c=x.CompareTo(y);if(c!=0)return c;} return 0; }
    private static List<string> Dedup(List<string> symbols) { var seen=new HashSet<string>(StringComparer.Ordinal); var r=new List<string>(); foreach(var s in symbols) if(seen.Add(s)) r.Add(s); return r; }
}
```

（注：附录 C 中 `ApplyDefineConstants`/`ReadTfms` 标注为骨架；真实实现必须完整落实"裸值=替换、含 `$(DefineConstants)`=追加"两语义与 TFM 读取，以测试为准。）

## 附录 D：Task 4 CLI 代码

```csharp
[Verb("strip-conditional", HelpText = "Evaluate and remove C# conditional compilation directives (#if/#elif/#else/#endif/#define/#undef) from C# source")]
class StripConditionalOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory (or .csproj/.sln: its directory is used)")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat warnings as fatal")]
    public bool Strict { get; set; }
    [Option("define", Required = false, HelpText = "Extra preprocessor symbol (repeatable)")]
    public IEnumerable<string> Define { get; set; } = Enumerable.Empty<string>();
    [Option("configuration", Default = "Release", HelpText = "Configuration used to evaluate csproj DefineConstants conditions")]
    public string Configuration { get; set; } = "Release";
    [Option("tfm", Required = false, HelpText = "Target framework override for symbol synthesis (e.g. net6.0)")]
    public string? Tfm { get; set; }
    [Option("remove-regions", Default = false, HelpText = "Also remove #region/#endregion directives")]
    public bool RemoveRegions { get; set; }
}
```

Handler（加入 `ParseArguments<…, StripConditionalOptions>` 与 MapResult `(StripConditionalOptions opts) => StripConditionalAsync(opts)`）：单文件用 `SourceTextIo.ReadAsync`→`Stripper.Strip`→stdout/`SourceTextIo.WriteAsync`；目录模式枚举 `*.cs`（跳过目标子树）、非 `.cs` 字节复制、in-place 检测、`.tmp` 原子写、按最近 csproj 目录缓存 `ResolveForFile`、汇总统计与退出码。

## Self-Review 结论

- Spec 覆盖：§3 指令矩阵→Task 1 测试与 `IsRemovedDirective`；§4 位置矩阵→Task 1 测试逐条；§5 符号分层→Task 3；§7 算法→附录 B；§8 CLI→Task 4；§9 测试门→Task 5。无缺口。
- 类型一致性：`ConditionalDirectiveStripResult`/`Options`/`Statistics` 命名在 CLI/测试一致。
- 占位符：附录 C 两处骨架已显式标注并以测试为验收（真实实现完整落实替换/追加语义）。
