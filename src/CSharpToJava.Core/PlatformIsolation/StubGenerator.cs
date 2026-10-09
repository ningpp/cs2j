using CSharpToJava.Core.Context;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using CoreDiagnosticSeverity = CSharpToJava.Core.Context.DiagnosticSeverity;

namespace CSharpToJava.Core.PlatformIsolation;

/// <summary>跨文件生成产物（相对路径 + 文本；设计 §3.4/§4.2）。</summary>
public sealed class GeneratedFile
{
    public required string RelativePath { get; init; }
    public required string Content { get; init; }
}

/// <summary>单文件改写结果。Changed=false 表示字节复制（保留原文）。</summary>
public sealed class StubRewriteResult
{
    public required bool Changed { get; init; }
    public required string OutputCode { get; init; }
    public required IReadOnlyList<PlatformIsolationDiagnostic> Diagnostics { get; init; }
    public required bool ProbeRedirected { get; init; }
}

/// <summary>
/// C42 M2 StubGenerator（设计 §4.2）。对单个文件的 apply 动作：
/// <list type="bullet">
/// <item>OS 探针 8 身份调用点重定向至 <c>Cs2j.Platform.Cs2jPlatformProbe</c>（PI011）；</item>
/// <item>OS guard 4 属性擦除（含 AssemblyInfo 登记+擦除，PI012）；</item>
/// <item>平台服务类型 PNSE 成员桩 + D5 签名移植（PI010）；</item>
/// <item>改写后语法校验失败 → PI040 且原文保留。</item>
/// </list>
/// 互操作族（3201/3202/3203）永不在此发桩（D8）；Reject/Facade 由门面处理。
/// </summary>
public static class StubGenerator
{
    public const string ProbeNamespace = "Cs2j.Platform";
    public const string ProbeClassName = "Cs2jPlatformProbe";
    public const string ProbeShimFileName = "Cs2jPlatformProbe.g.cs";

    private static readonly string[] MethodProbeNames = ["IsWindows", "IsLinux", "IsMacOS", "IsOSPlatform"];

    public static StubRewriteResult Rewrite(
        string filePath,
        string sourceText,
        SemanticModel? semanticModel,
        IsolationPlan plan,
        bool isAssemblyInfo)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (semanticModel == null)
        {
            // 无语义模型：无法安全裁决探针/成员桩，显式降级（D10）而不是猜测。
            return new StubRewriteResult
            {
                Changed = false,
                OutputCode = sourceText,
                Diagnostics =
                [
                    new PlatformIsolationDiagnostic(
                        PlatformIsolationDiagnosticCodes.CrossFilePlanSkipped,
                        CoreDiagnosticSeverity.Warning,
                        $"platform-isolation skipped apply for '{filePath}' (no semantic model).",
                        filePath),
                ],
                ProbeRedirected = false,
            };
        }

        var actions = plan.ForFile(filePath).ToList();
        if (actions.Count == 0)
        {
            return NoChange(sourceText);
        }

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var diagnostics = new List<PlatformIsolationDiagnostic>();
        var probeInvocations = new HashSet<SyntaxNode>();
        var probeProperties = new HashSet<SyntaxNode>();
        var probeNames = new Dictionary<SyntaxNode, string>();
        var stripAttributes = new HashSet<SyntaxNode>();
        var stubMembers = new Dictionary<SyntaxNode, MemberDeclarationSyntax>();

        foreach (var action in actions)
        {
            var site = action.Site;
            var node = FindNode(root, site);
            switch (action.Action)
            {
                case PlatformIsolationAction.ProbeRedirect when node != null:
                    RegisterProbe(node, site, probeInvocations, probeProperties, probeNames, diagnostics, filePath);
                    break;

                case PlatformIsolationAction.StripAttribute when node != null:
                    RegisterStrip(node, site, stripAttributes, diagnostics, filePath);
                    break;

                case PlatformIsolationAction.ThrowStub when node != null:
                    RegisterThrowStub(node, site, semanticModel, stubMembers, diagnostics, filePath);
                    break;
            }
        }

        if (probeInvocations.Count == 0 && probeProperties.Count == 0
            && stripAttributes.Count == 0 && stubMembers.Count == 0)
        {
            return new StubRewriteResult
            {
                Changed = false,
                OutputCode = sourceText,
                Diagnostics = diagnostics,
                ProbeRedirected = false,
            };
        }

        var rewriter = new IsolationRewriter(
            probeInvocations, probeProperties, probeNames, stripAttributes, stubMembers);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;
        var output = newRoot.ToFullString();

        // 三候选语法校验（仿 JK/E06 invalid-syntax 保护）：非法则原文保留。
        var reparsed = CSharpSyntaxTree.ParseText(output, semanticModel.SyntaxTree.Options as CSharpParseOptions);
        var parseError = reparsed.GetDiagnostics()
            .FirstOrDefault(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        if (parseError != null)
        {
            diagnostics.Add(new PlatformIsolationDiagnostic(
                PlatformIsolationDiagnosticCodes.GeneratedSyntaxInvalid,
                CoreDiagnosticSeverity.Error,
                $"platform-isolation rewrite of '{filePath}' produced invalid C#; original preserved. " +
                $"First error: {parseError}",
                filePath));
            return new StubRewriteResult
            {
                Changed = false,
                OutputCode = sourceText,
                Diagnostics = diagnostics,
                ProbeRedirected = false,
            };
        }

        _ = isAssemblyInfo;
        return new StubRewriteResult
        {
            Changed = !string.Equals(output, sourceText, StringComparison.Ordinal),
            OutputCode = output,
            Diagnostics = diagnostics,
            ProbeRedirected = probeInvocations.Count > 0 || probeProperties.Count > 0,
        };
    }

    /// <summary>探针 shim 单例内容（仅 BCL，自身零平台身份）。</summary>
    public static string BuildProbeShim()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// C42 platform-isolation probe shim (Cs2jPlatformProbeMarker=Cs2j.Generation.PlatformStubAttribute).");
        sb.AppendLine("// Conservative neutral values; no platform identity (BCL only).");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("namespace " + ProbeNamespace);
        sb.AppendLine("{");
        sb.AppendLine("    internal static class " + ProbeClassName);
        sb.AppendLine("    {");
        sb.AppendLine("        public static bool IsWindows() => false;");
        sb.AppendLine("        public static bool IsLinux() => false;");
        sb.AppendLine("        public static bool IsMacOS() => false;");
        sb.AppendLine("        public static bool IsOSPlatform(System.Runtime.InteropServices.OSPlatform osPlatform) => false;");
        sb.AppendLine("        public static string OSDescription() => \"Unknown\";");
        sb.AppendLine("        public static System.Runtime.InteropServices.Architecture OSArchitecture() => System.Runtime.InteropServices.Architecture.X64;");
        sb.AppendLine("        public static System.Runtime.InteropServices.Architecture ProcessArchitecture() => System.Runtime.InteropServices.Architecture.X64;");
        sb.AppendLine("        public static System.OperatingSystem OSVersion() => new System.OperatingSystem(System.PlatformID.Other, new System.Version(0, 0));");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static StubRewriteResult NoChange(string sourceText)
        => new()
        {
            Changed = false,
            OutputCode = sourceText,
            Diagnostics = [],
            ProbeRedirected = false,
        };

    private static SyntaxNode? FindNode(CompilationUnitSyntax root, BoundarySite site)
    {
        if (site.SpanStart < 0 || site.SpanEnd <= site.SpanStart || site.SpanEnd > root.FullSpan.End)
        {
            return null;
        }

        return root.FindNode(new TextSpan(site.SpanStart, site.SpanEnd - site.SpanStart), getInnermostNodeForTie: true);
    }

    private static void RegisterProbe(
        SyntaxNode node,
        BoundarySite site,
        HashSet<SyntaxNode> probeInvocations,
        HashSet<SyntaxNode> probeProperties,
        Dictionary<SyntaxNode, string> probeNames,
        List<PlatformIsolationDiagnostic> diagnostics,
        string filePath)
    {
        var name = LastSegment(site.Identity);
        string replacement;
        if (node is InvocationExpressionSyntax)
        {
            probeInvocations.Add(node);
            replacement = $"Cs2j.Platform.{ProbeClassName}.{name}(...)";
        }
        else if (node is MemberAccessExpressionSyntax memberAccess)
        {
            // 已在父 invocation 站点处理的探针（如 OperatingSystem.IsWindows 的成员访问）跳过。
            if (memberAccess.Parent is InvocationExpressionSyntax)
            {
                return;
            }

            probeProperties.Add(node);
            replacement = $"Cs2j.Platform.{ProbeClassName}.{name}()";
        }
        else
        {
            return;
        }

        probeNames[node] = name;
        var original = site.Identity;
        diagnostics.Add(new PlatformIsolationDiagnostic(
            PlatformIsolationDiagnosticCodes.ProbeRedirected,
            CoreDiagnosticSeverity.Info,
            $"Reroute OS probe '{original}' at {filePath}({site.Line + 1},{site.Column}) -> {replacement}; " +
            $"conservative value '{ConservativeValueFor(name)}'",
            filePath));
    }

    private static void RegisterStrip(
        SyntaxNode node,
        BoundarySite site,
        HashSet<SyntaxNode> stripAttributes,
        List<PlatformIsolationDiagnostic> diagnostics,
        string filePath)
    {
        var attribute = node as AttributeSyntax ?? node.AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
        if (attribute == null)
        {
            return;
        }

        stripAttributes.Add(attribute);
        diagnostics.Add(new PlatformIsolationDiagnostic(
            PlatformIsolationDiagnosticCodes.GuardAttributeStripped,
            CoreDiagnosticSeverity.Info,
            $"Strip OS guard attribute '{site.Identity}' at {filePath}({site.Line + 1},{site.Column}); Java ignores it.",
            filePath));
    }

    private static void RegisterThrowStub(
        SyntaxNode node,
        BoundarySite site,
        SemanticModel model,
        Dictionary<SyntaxNode, MemberDeclarationSyntax> stubMembers,
        List<PlatformIsolationDiagnostic> diagnostics,
        string filePath)
    {
        var member = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (member is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax))
        {
            return;
        }

        if (stubMembers.ContainsKey(member))
        {
            return;
        }

        var message = $"CS2J platform stub: {site.Identity} at {filePath}:{site.Line + 1}; isolated by platform-isolation";
        var signatureChanges = new List<string>();
        var replacement = BuildThrowStub(member, message, model, signatureChanges);
        if (replacement == null)
        {
            // 无法发桩（abstract/interface/无体）→ 保持原样，交给 residual 闸门裁决。
            return;
        }

        stubMembers[member] = replacement;
        var detail = signatureChanges.Count == 0 ? "no signature change" : string.Join("; ", signatureChanges);
        diagnostics.Add(new PlatformIsolationDiagnostic(
            PlatformIsolationDiagnosticCodes.ThrowStubGenerated,
            CoreDiagnosticSeverity.Info,
            $"PNSE throw stub for '{site.Identity}' at {filePath}({site.Line + 1},{site.Column}); {detail}",
            filePath));
    }

    private static MemberDeclarationSyntax? BuildThrowStub(
        MemberDeclarationSyntax member,
        string message,
        SemanticModel model,
        List<string> signatureChanges)
    {
        var literal = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(message, quote: true);
        var throwStatement = SyntaxFactory.ParseStatement(
            $"throw new System.PlatformNotSupportedException({literal});");

        switch (member)
        {
            case MethodDeclarationSyntax method when method.Body != null || method.ExpressionBody != null:
            {
                var ported = (MethodDeclarationSyntax)PortSignature(method, model, signatureChanges);
                return ported
                    .WithBody(SyntaxFactory.Block(throwStatement))
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default);
            }

            case ConstructorDeclarationSyntax ctor when ctor.Body != null || ctor.ExpressionBody != null:
            {
                var ported = (ConstructorDeclarationSyntax)PortSignature(ctor, model, signatureChanges);
                return ported
                    .WithBody(SyntaxFactory.Block(throwStatement))
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default);
            }

            case PropertyDeclarationSyntax property when property.AccessorList != null:
            {
                var ported = (PropertyDeclarationSyntax)PortSignature(property, model, signatureChanges);
                var accessors = ported.AccessorList!.Accessors
                    .Select(a => a.WithBody(SyntaxFactory.Block(throwStatement)).WithExpressionBody(null).WithSemicolonToken(default));
                return ported.WithAccessorList(ported.AccessorList.WithAccessors(SyntaxFactory.List(accessors)));
            }

            case PropertyDeclarationSyntax property when property.ExpressionBody != null:
            {
                var ported = (PropertyDeclarationSyntax)PortSignature(property, model, signatureChanges);
                return ported.WithExpressionBody(null).WithAccessorList(
                    SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithBody(SyntaxFactory.Block(throwStatement)))));
            }

            case FieldDeclarationSyntax field:
            {
                // 字段无法抛错：仅做类型移植（D5）。
                var ported = (FieldDeclarationSyntax)PortSignature(field, model, signatureChanges);
                return ported;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// D5 签名移植：平台类型（9 类型前缀 / 8 成员前缀）→ 可移植句柄 <c>object</c>；
    /// 记录每处变更。禁止保留平台类型参数。M3 起供 FacadeGenerator 复用
    /// （端口成员同一套 D5 规则，单一事实源）。
    /// </summary>
    internal static MemberDeclarationSyntax PortSignature(
        MemberDeclarationSyntax member,
        SemanticModel model,
        List<string> signatureChanges)
    {
        TypeSyntax Port(TypeSyntax type)
        {
            if (IsPlatformType(type, model))
            {
                signatureChanges.Add($"{type} -> object");
                return SyntaxFactory.ParseTypeName("object").WithTriviaFrom(type);
            }

            return type;
        }

        ParameterListSyntax PortParameters(ParameterListSyntax list)
        {
            var ported = list.Parameters
                .Select(p => p.WithType(Port(p.Type)))
                .ToList();
            return list.WithParameters(
                SyntaxFactory.SeparatedList(ported, list.Parameters.GetSeparators()));
        }

        switch (member)
        {
            case MethodDeclarationSyntax method:
                return method
                    .WithParameterList(PortParameters(method.ParameterList))
                    .WithReturnType(method.ReturnType is { } rt ? Port(rt) : method.ReturnType);

            case ConstructorDeclarationSyntax ctor:
                return ctor.WithParameterList(PortParameters(ctor.ParameterList));

            case PropertyDeclarationSyntax property:
                return property.WithType(Port(property.Type));

            case FieldDeclarationSyntax field:
                return field.WithDeclaration(field.Declaration.WithType(Port(field.Declaration.Type)));

            default:
                return member;
        }
    }

    private static bool IsPlatformType(TypeSyntax type, SemanticModel model)
    {
        var identity = model.GetTypeInfo(type).Type?.ToDisplayString();
        if (string.IsNullOrEmpty(identity))
        {
            identity = type.ToString();
        }

        return BoundaryTables.IsPlatformTypeIdentity(identity) || BoundaryTables.IsPlatformMemberIdentity(identity);
    }

    private static string LastSegment(string identity)
    {
        var index = identity.LastIndexOf('.');
        return index >= 0 ? identity[(index + 1)..] : identity;
    }

    private static string ConservativeValueFor(string name) => name switch
    {
        "IsWindows" or "IsLinux" or "IsMacOS" or "IsOSPlatform" => "false",
        "OSDescription" => "\"Unknown\"",
        "OSArchitecture" or "ProcessArchitecture" => "Architecture.X64",
        "OSVersion" => "OperatingSystem(PlatformID.Other, 0.0)",
        _ => "neutral",
    };

    /// <summary>单文件内的隔离重写器（按原始节点集合匹配，避免顺序失效）。</summary>
    private sealed class IsolationRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<SyntaxNode> _probeInvocations;
        private readonly HashSet<SyntaxNode> _probeProperties;
        private readonly Dictionary<SyntaxNode, string> _probeNames;
        private readonly HashSet<SyntaxNode> _stripAttributes;
        private readonly Dictionary<SyntaxNode, MemberDeclarationSyntax> _stubMembers;

        public IsolationRewriter(
            HashSet<SyntaxNode> probeInvocations,
            HashSet<SyntaxNode> probeProperties,
            Dictionary<SyntaxNode, string> probeNames,
            HashSet<SyntaxNode> stripAttributes,
            Dictionary<SyntaxNode, MemberDeclarationSyntax> stubMembers)
        {
            _probeInvocations = probeInvocations;
            _probeProperties = probeProperties;
            _probeNames = probeNames;
            _stripAttributes = stripAttributes;
            _stubMembers = stubMembers;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (_probeInvocations.Contains(node))
            {
                var args = node.ArgumentList.Arguments.ToString();
                return SyntaxFactory.ParseExpression(
                    $"Cs2j.Platform.{ProbeClassName}.{_probeNames[node]}({args})");
            }

            return base.VisitInvocationExpression(node);
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (_probeProperties.Contains(node))
            {
                return SyntaxFactory.ParseExpression(
                    $"Cs2j.Platform.{ProbeClassName}.{_probeNames[node]}()");
            }

            return base.VisitMemberAccessExpression(node);
        }

        public override SyntaxNode? VisitAttributeList(AttributeListSyntax node)
        {
            if (!node.Attributes.Any(_stripAttributes.Contains))
            {
                return base.VisitAttributeList(node);
            }

            var kept = node.Attributes.Where(a => !_stripAttributes.Contains(a)).ToList();
            return kept.Count == 0 ? null : node.WithAttributes(SyntaxFactory.SeparatedList(kept));
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
            => _stubMembers.TryGetValue(node, out var replacement) ? replacement : base.VisitMethodDeclaration(node);

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
            => _stubMembers.TryGetValue(node, out var replacement) ? replacement : base.VisitConstructorDeclaration(node);

        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
            => _stubMembers.TryGetValue(node, out var replacement) ? replacement : base.VisitPropertyDeclaration(node);

        public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
            => _stubMembers.TryGetValue(node, out var replacement) ? replacement : base.VisitFieldDeclaration(node);
    }
}