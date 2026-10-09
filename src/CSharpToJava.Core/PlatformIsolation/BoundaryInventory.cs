using CSharpToJava.Core.Analysis;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CoreDiagnosticSeverity = CSharpToJava.Core.Context.DiagnosticSeverity;

namespace CSharpToJava.Core.PlatformIsolation;

/// <summary>平台边界站点族（设计 §4 文件表 / §2.1 逐族策略表）。</summary>
public enum BoundaryFamily
{
    UiNamespace,
    PlatformType,
    PlatformMember,
    OsGuardAttribute,
    NativeAttribute,
    NativeType,
    ExternMethod,
}

/// <summary>
/// 站点豁免分类（设计 §4 文件表）。M1 仅 <see cref="CompatMapping"/> 与
/// <see cref="TestProbeWarning"/> 在现状语义下实际产出；AssemblyInfo /
/// NativeMethods / Generated 为 M2「识别并登记」预留的冻结枚举值，
/// M1 复刻现状仍整文件跳过。
/// </summary>
public enum BoundaryExemption
{
    None,
    AssemblyInfo,
    NativeMethods,
    CompatMapping,
    TestProbeWarning,
    Generated,
}

/// <summary>站点默认档（设计 §2.1；受配置 policy 覆盖）。</summary>
public enum PlatformIsolationDefaultAction
{
    Reject,
    Stub,
    Facade,
}

/// <summary>
/// 站点最终动作（设计 §4.6 枚举；与 Statistics 统计字段一一对应）。
/// </summary>
public enum PlatformIsolationAction
{
    None,
    Reject,
    ProbeRedirect,
    ThrowStub,
    StripAttribute,
    Facade,
    Suppressed,
}

/// <summary>身份解析置信度（设计 §4.6 / D10）。</summary>
public enum BoundaryConfidence
{
    Symbol,
    SyntaxFallback,
}

/// <summary>
/// 统一平台边界站点记录（设计 §4 文件表）。字节稳定排序键为
/// (file, line, column, code)（设计 §4.6）。
/// </summary>
public sealed class BoundarySite
{
    public required string File { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public required int SpanStart { get; init; }
    public required int SpanEnd { get; init; }
    public required BoundaryFamily Family { get; init; }
    public required string Code { get; init; }
    public required string Category { get; init; }
    public required string Message { get; init; }
    public required string Identity { get; init; }
    public required string SyntaxKind { get; init; }
    public required CoreDiagnosticSeverity Severity { get; init; }
    public required PlatformIsolationDefaultAction DefaultAction { get; init; }
    public required BoundaryExemption Exemption { get; init; }
    public required BoundaryConfidence Confidence { get; init; }
}

/// <summary>
/// 整树一次的平台边界清单（设计 §4.1）。直接调用现有三 analyzer
/// （UnsupportedDomainAnalyzer / PlatformBoundaryAnalyzer /
/// NativeInteropAnalyzer，同程序集 internal 直调），复刻两个 CheckPass 的
/// 现状语义：compat 前缀豁免、测试工程 6 探针 Warning 降级、
/// <c>*.AssemblyInfo.cs</c> 跳过 CS2J3101、<c>NativeMethods.cs</c> 文件豁免。
/// M1 不改码、不改文案、不改豁免（表与身份匹配的抽取留给 M2 BoundaryTables）。
/// </summary>
public sealed class BoundaryInventory
{
    public required IReadOnlyList<BoundarySite> Sites { get; init; }
}

/// <summary>
/// 清单构建器。UI 命名空间扫描以 <c>compatNamespacePrefixes: null</c> 调 analyzer
/// 拿到全部 6 前缀站点，再按传入的 compat 前缀（TypeMappingRegistry 显式命名空间
/// 映射）自行分类豁免——与 CheckPass「传前缀令 analyzer 跳过」等价，但保留
/// CompatMapping 站点台账（设计 §2.1 / §7.1）。
/// </summary>
internal static class BoundaryInventoryBuilder
{
    public static BoundaryInventory Build(
        Compilation compilation,
        bool isTestProject = false,
        IReadOnlyCollection<string>? compatNamespacePrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        var sites = new List<BoundarySite>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var filePath = syntaxTree.FilePath ?? string.Empty;
            var root = syntaxTree.GetRoot();
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var confidence = semanticModel != null ? BoundaryConfidence.Symbol : BoundaryConfidence.SyntaxFallback;

            // UI 命名空间：传 null 前缀取全量，再自行分类 compat 豁免。
            foreach (var diagnostic in UnsupportedDomainAnalyzer.AnalyzeSyntaxTree(syntaxTree, compatNamespacePrefixes: null))
            {
                var identity = ExtractQuotedIdentity(diagnostic.Message);
                var isCompat = HasCompatNamespacePrefix(identity, compatNamespacePrefixes);
                AddSite(
                    sites,
                    seen,
                    syntaxTree,
                    root,
                    diagnostic,
                    filePath,
                    family: BoundaryFamily.UiNamespace,
                    exemption: isCompat ? BoundaryExemption.CompatMapping : BoundaryExemption.None,
                    defaultAction: PlatformIsolationDefaultAction.Reject,
                    confidence);
            }

            // 平台服务 / OS 探针。M2：AssemblyInfo 文件由「跳过」改为「登记」
            // （Exemption=AssemblyInfo，供 apply 档擦除），CheckPass 的现状跳过不变。
            var isAssemblyInfo = BoundaryAnalysisHelpers.IsGeneratedAssemblyInfoFile(filePath);
            foreach (var diagnostic in PlatformBoundaryAnalyzer.AnalyzeSyntaxTree(
                         syntaxTree,
                         semanticModel,
                         shouldDowngradeTestPlatformProbes: isTestProject))
            {
                var identity = ExtractQuotedIdentity(diagnostic.Message);
                var family = ClassifyPlatformFamily(diagnostic.Code, diagnostic.Message, identity);
                var exemption = isAssemblyInfo
                    ? BoundaryExemption.AssemblyInfo
                    : diagnostic.Severity == CoreDiagnosticSeverity.Warning
                        ? BoundaryExemption.TestProbeWarning
                        : BoundaryExemption.None;
                AddSite(
                    sites,
                    seen,
                    syntaxTree,
                    root,
                    diagnostic,
                    filePath,
                    family,
                    exemption,
                    DefaultActionFor(family),
                    confidence);
            }

            // 原生互操作。M2：NativeMethods.cs 文件豁免不再伪装成跳过，登记为
            // Exemption=NativeMethods 的 Reject 站点（D8/§7.4 不允许文件名逃生口）。
            // M3：C06 unsafe-ffm 已归一并带 InteropFacade 标记的声明，其 3201/3203
            // 与 UF060/UF070 重复报，按共享谓词抑制（设计 §4.5；只读消费 C06 谓词）。
            var isNativeMethods = BoundaryAnalysisHelpers.IsPInvokeContainerFile(filePath);
            foreach (var diagnostic in NativeInteropAnalyzer.AnalyzeSyntaxTree(syntaxTree, semanticModel))
            {
                if (diagnostic.Code is "CS2J3201" or "CS2J3203"
                    && semanticModel != null
                    && IsOnNormalizedInteropFacade(root, semanticModel, diagnostic))
                {
                    continue;
                }

                var family = diagnostic.Code switch
                {
                    "CS2J3201" => BoundaryFamily.NativeAttribute,
                    "CS2J3203" => BoundaryFamily.ExternMethod,
                    _ => BoundaryFamily.NativeType,
                };
                AddSite(
                    sites,
                    seen,
                    syntaxTree,
                    root,
                    diagnostic,
                    filePath,
                    family,
                    isNativeMethods ? BoundaryExemption.NativeMethods : BoundaryExemption.None,
                    DefaultActionFor(family),
                    confidence);
            }
        }

        sites.Sort(CompareSites);
        return new BoundaryInventory { Sites = sites };
    }

    private static void AddSite(
        List<BoundarySite> sites,
        HashSet<string> seen,
        SyntaxTree syntaxTree,
        SyntaxNode root,
        DiagnosticMessage diagnostic,
        string filePath,
        BoundaryFamily family,
        BoundaryExemption exemption,
        PlatformIsolationDefaultAction defaultAction,
        BoundaryConfidence confidence)
    {
        var lineSpan = diagnostic.Location?.GetLineSpan();
        var line = lineSpan?.StartLinePosition.Line ?? -1;
        var column = lineSpan?.StartLinePosition.Character ?? -1;
        var category = diagnostic.Category ?? string.Empty;
        var key = $"{diagnostic.Code}|{category}|{filePath}|{line}|{column}|{diagnostic.Message}";
        if (!seen.Add(key))
        {
            return;
        }

        var span = diagnostic.Location?.SourceSpan ?? default;
        var syntaxKind = ResolveSyntaxKind(root, span);

        sites.Add(new BoundarySite
        {
            File = filePath,
            Line = line,
            Column = column,
            SpanStart = span.Start,
            SpanEnd = span.End,
            Family = family,
            Code = diagnostic.Code ?? string.Empty,
            Category = category,
            Message = diagnostic.Message,
            Identity = ExtractQuotedIdentity(diagnostic.Message),
            SyntaxKind = syntaxKind,
            Severity = diagnostic.Severity,
            DefaultAction = defaultAction,
            Exemption = exemption,
            Confidence = confidence,
        });
    }

    private static int CompareSites(BoundarySite left, BoundarySite right)
    {
        var byFile = string.CompareOrdinal(left.File, right.File);
        if (byFile != 0) return byFile;
        var byLine = left.Line.CompareTo(right.Line);
        if (byLine != 0) return byLine;
        var byColumn = left.Column.CompareTo(right.Column);
        if (byColumn != 0) return byColumn;
        return string.CompareOrdinal(left.Code, right.Code);
    }

    private static string ResolveSyntaxKind(SyntaxNode root, Microsoft.CodeAnalysis.Text.TextSpan span)
    {
        if (span.Length <= 0 || span.End > root.FullSpan.End)
        {
            return string.Empty;
        }

        var node = root.FindNode(span, getInnermostNodeForTie: true);
        return node?.Kind().ToString() ?? string.Empty;
    }

    /// <summary>
    /// 3201/3203 站点是否落在 C06 已归一的受控 interop facade 上（M3 抑制）。
    /// 3203 的位置是方法标识符、3201 的位置是特性节点：先按节点直接判（谓词认
    /// 方法声明/调用/标识符），再上卷到包含它的方法声明，用方法符号上的
    /// InteropFacade 标记终判（谓词同源 C06 InteropBoundaryFacts，不改 C06）。
    /// </summary>
    private static bool IsOnNormalizedInteropFacade(
        SyntaxNode root,
        SemanticModel semanticModel,
        DiagnosticMessage diagnostic)
    {
        var span = diagnostic.Location?.SourceSpan;
        if (span is not { IsEmpty: false } sourceSpan || sourceSpan.End > root.FullSpan.End)
        {
            return false;
        }

        var node = root.FindNode(sourceSpan, getInnermostNodeForTie: true);
        if (InteropBoundaryFacts.IsNormalizedInteropFacade(semanticModel, node))
        {
            return true;
        }

        var method = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return method != null
               && semanticModel.GetDeclaredSymbol(method) is { } methodSymbol
               && InteropBoundaryFacts.IsNormalizedInteropFacadeMethod(methodSymbol);
    }

    /// <summary>从冻结消息原文取单引号内身份串。</summary>
    private static string ExtractQuotedIdentity(string message)
    {
        var start = message.IndexOf('\'');
        if (start < 0)
        {
            return string.Empty;
        }

        var end = message.IndexOf('\'', start + 1);
        return end < 0 ? string.Empty : message.Substring(start + 1, end - start - 1);
    }

    private static BoundaryFamily ClassifyPlatformFamily(string? code, string message, string identity)
    {
        if (code == "CS2J3101")
        {
            return BoundaryFamily.OsGuardAttribute;
        }

        // CS2J3102：消息原文区分 API / type；API 中命中 8 探针前缀者归 PlatformMember
        // （表驱动，单一真相 BoundaryTables，C42 M2 抽表）。
        if (message.Contains("platform-specific type", StringComparison.Ordinal))
        {
            return BoundaryFamily.PlatformType;
        }

        return BoundaryTables.IsPlatformMemberIdentity(identity)
            ? BoundaryFamily.PlatformMember
            : BoundaryFamily.PlatformType;
    }

    private static PlatformIsolationDefaultAction DefaultActionFor(BoundaryFamily family) => family switch
    {
        BoundaryFamily.UiNamespace => PlatformIsolationDefaultAction.Reject,
        BoundaryFamily.PlatformType => PlatformIsolationDefaultAction.Reject,
        BoundaryFamily.PlatformMember => PlatformIsolationDefaultAction.Stub,
        BoundaryFamily.OsGuardAttribute => PlatformIsolationDefaultAction.Stub,
        BoundaryFamily.NativeAttribute => PlatformIsolationDefaultAction.Reject,
        BoundaryFamily.NativeType => PlatformIsolationDefaultAction.Reject,
        BoundaryFamily.ExternMethod => PlatformIsolationDefaultAction.Reject,
        _ => PlatformIsolationDefaultAction.Reject,
    };

    private static bool HasCompatNamespacePrefix(string name, IReadOnlyCollection<string>? prefixes)
    {
        if (string.IsNullOrEmpty(name) || prefixes == null || prefixes.Count == 0)
        {
            return false;
        }

        foreach (var prefix in prefixes)
        {
            if (name.Equals(prefix, StringComparison.Ordinal)
                || name.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}