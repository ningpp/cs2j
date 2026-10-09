using CSharpToJava.Core.Context;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using CoreDiagnosticSeverity = CSharpToJava.Core.Context.DiagnosticSeverity;

namespace CSharpToJava.Core.PlatformIsolation;

/// <summary>Facade 单文件改写结果（设计 §4.3）。</summary>
public sealed class FacadeRewriteResult
{
    public required bool Changed { get; init; }
    public required string OutputCode { get; init; }
    public required IReadOnlyList<PlatformIsolationDiagnostic> Diagnostics { get; init; }
    public required IReadOnlyList<GeneratedFile> GeneratedFiles { get; init; }
}

/// <summary>
/// C42 M3 FacadeGenerator（设计 §4.3，D5/D8/R2）。对 Facade 动作命中的平台
/// 服务类型：
/// <list type="bullet">
/// <item>抽取可移植端口接口 <c>I&lt;Type&gt;Port</c>（成员签名经 D5 移植，平台类型换可移植句柄）；</item>
/// <item>原类型改 <c>partial</c>，持有可空端口字段，被隔离成员体整体委托端口；
/// 端口未供给时抛 PlatformNotSupportedException（受控失败，不冒充已移植）；</item>
/// <item>另发用户槽位 partial 文件：只含可编译 partial 壳与填写指引注释，
/// <b>绝不生成任何端口实现</b>（D8/R2 红线）。</item>
/// </list>
/// 互操作族（extern/DllImport 等）永不进入本生成器（计划层已锁 Reject）。
/// </summary>
public static class FacadeGenerator
{
    private const string FieldName = "_cs2jPort";
    private const string PropertyName = "Cs2jRequiredPort";
    private const string HookName = "ConfigureCs2jPort";

    /// <summary>
    /// 对单文件执行 Facade 改写。<paramref name="originalModel"/> 绑定动作表
    /// 所在的原树（站点 span 来自该树）；<paramref name="currentModel"/> 绑定
    /// 可能先经 Stub 通道改写后的当前树（探针/guard 改写只动其他成员，类型与
    /// 成员声明经符号显示名重定位，不依赖 span）。
    /// </summary>
    public static FacadeRewriteResult Rewrite(
        string filePath,
        string sourceText,
        SemanticModel currentModel,
        SemanticModel originalModel,
        IReadOnlyList<IsolationAction> facadeActions)
    {
        ArgumentNullException.ThrowIfNull(facadeActions);

        var empty = new FacadeRewriteResult
        {
            Changed = false,
            OutputCode = sourceText,
            Diagnostics = [],
            GeneratedFiles = [],
        };

        if (facadeActions.Count == 0)
        {
            return empty;
        }

        var originalRoot = (CompilationUnitSyntax)originalModel.SyntaxTree.GetRoot();
        var currentRoot = (CompilationUnitSyntax)currentModel.SyntaxTree.GetRoot();

        // 1) 原树：按 Facade 站点聚类出 (类型 -> 成员) 计划（符号显示名为键）。
        var plans = new Dictionary<string, TypePlan>();
        foreach (var action in facadeActions)
        {
            if (action.Site.Family != BoundaryFamily.PlatformType)
            {
                // D8 纵深防御：互操作族永不发 facade（计划层已拦截，双保险）。
                continue;
            }

            var site = action.Site;
            if (site.SpanStart < 0 || site.SpanEnd <= site.SpanStart
                || site.SpanEnd > originalRoot.FullSpan.End)
            {
                continue;
            }

            var siteNode = originalRoot.FindNode(
                new TextSpan(site.SpanStart, site.SpanEnd - site.SpanStart),
                getInnermostNodeForTie: true);

            var typeDecl = siteNode.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            var typeSymbol = typeDecl == null ? null : originalModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
            if (typeDecl == null || typeSymbol == null)
            {
                continue;
            }

            // 端口缝含字段与实现成员，接口无法承接；泛型类型需要同步泛型化接口：
            // M3 均不抽取（保留身份，交 residual 闸门显式裁决，不静默跳过）。
            if (typeDecl is InterfaceDeclarationSyntax
                || typeSymbol.TypeParameters.Length > 0)
            {
                continue;
            }

            MemberDeclarationSyntax? memberDecl = siteNode.AncestorsAndSelf()
                .OfType<MemberDeclarationSyntax>()
                .FirstOrDefault(n => n is MethodDeclarationSyntax
                    or PropertyDeclarationSyntax or FieldDeclarationSyntax);
            if (memberDecl == null || !IsFacadeableMember(memberDecl))
            {
                // 构造器/索引器/运算符/事件等：不抽取，保留身份由 PI031 闸门裁决。
                continue;
            }

            if (!plans.TryGetValue(typeSymbol.ToDisplayString(), out var plan))
            {
                plan = new TypePlan(typeSymbol);
                plans[typeSymbol.ToDisplayString()] = plan;
            }

            plan.AddMember(memberDecl, originalModel);
        }

        if (plans.Count == 0)
        {
            return empty;
        }

        // 2) 当前树：按符号显示名重定位类型与成员，构建替换成员 + 端口缝 + 生成文件。
        var diagnostics = new List<PlatformIsolationDiagnostic>();
        var generatedFiles = new List<GeneratedFile>();
        var memberReplacements = new Dictionary<SyntaxNode, MemberDeclarationSyntax>();
        var seamByType = new Dictionary<TypeDeclarationSyntax, List<MemberDeclarationSyntax>>();

        foreach (var typeDeclaration in currentRoot.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var currentTypeSymbol = currentModel.GetDeclaredSymbol(typeDeclaration) as INamedTypeSymbol;
            if (currentTypeSymbol == null
                || !plans.TryGetValue(currentTypeSymbol.ToDisplayString(), out var plan))
            {
                continue;
            }

            // 幂等（D12）：已含端口缝的类型不再重复生成。
            if (typeDeclaration.Members.Any(m => m is PropertyDeclarationSyntax p
                    && p.Identifier.Text == PropertyName))
            {
                continue;
            }

            var typeIsStatic = currentTypeSymbol.IsStatic
                || typeDeclaration.Modifiers.Any(SyntaxKind.StaticKeyword);
            var localReplacements = new Dictionary<SyntaxNode, MemberDeclarationSyntax>();
            var seamMembers = new List<MemberDeclarationSyntax>();
            var interfaceLines = new List<string>();
            var signatureChanges = new List<string>();
            var portedCount = 0;

            foreach (var currentMember in typeDeclaration.Members.ToList())
            {
                if (!plan.Matches(currentMember, currentModel))
                {
                    continue;
                }

                // M3 保守规则：实例类型的静态成员不抽取（单条缝无法同时供给
                // 静态/实例端口）；静态类型的成员恒为静态，走静态缝。
                if (!typeIsStatic && HasStaticModifier(currentMember))
                {
                    continue;
                }

                switch (currentMember)
                {
                    case MethodDeclarationSyntax method:
                    {
                        var ported = (MethodDeclarationSyntax)StubGenerator.PortSignature(
                            method, currentModel, signatureChanges);
                        var args = string.Join(", ",
                            ported.ParameterList.Parameters.Select(BuildCallArgument));
                        var replacement = ported
                            .WithBody(null)
                            .WithExpressionBody(ArrowExpressionClause(
                                ParseExpression($"{PropertyName}.{ported.Identifier.Text}({args})")))
                            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
                        localReplacements[currentMember] = replacement;
                        interfaceLines.Add(BuildInterfaceMethod(ported));
                        portedCount++;
                        break;
                    }

                    case PropertyDeclarationSyntax property:
                    {
                        var ported = (PropertyDeclarationSyntax)StubGenerator.PortSignature(
                            property, currentModel, signatureChanges);
                        var accessKinds = property.ExpressionBody != null
                            ? [SyntaxKind.GetAccessorDeclaration]
                            : property.AccessorList?.Accessors
                                .Select(a => a.Kind())
                                .Where(k => k is SyntaxKind.GetAccessorDeclaration
                                    or SyntaxKind.SetAccessorDeclaration
                                    or SyntaxKind.InitAccessorDeclaration)
                                .Distinct()
                                .ToList() ?? [SyntaxKind.GetAccessorDeclaration];

                        localReplacements[currentMember] =
                            BuildDelegatingProperty(ported, accessKinds);
                        interfaceLines.Add(BuildInterfaceProperty(ported, accessKinds));
                        portedCount++;
                        break;
                    }

                    case FieldDeclarationSyntax field:
                    {
                        // 字段不进端口接口：仅做 D5 类型移植。
                        var ported = (FieldDeclarationSyntax)StubGenerator.PortSignature(
                            field, currentModel, signatureChanges);
                        localReplacements[currentMember] = ported;
                        break;
                    }
                }
            }

            if (portedCount == 0)
            {
                // 成员均未能在当前树定位（如被前序通道改写）：不动，交 residual 闸门。
                continue;
            }

            // 计划成立后才把本类型的替换提交到全局表（避免纯字段/空计划半改写）。
            foreach (var pair in localReplacements)
            {
                memberReplacements.Add(pair.Key, pair.Value);
            }

            var seam = ParseSeamMembers(plan.TypeName, typeIsStatic);
            seamMembers.AddRange(seam);
            seamByType[typeDeclaration] = seamMembers;

            var portName = "I" + plan.TypeName + "Port";
            generatedFiles.Add(new GeneratedFile
            {
                RelativePath = BuildGeneratedRelativePath(plan.Namespace, portName + ".g.cs"),
                Content = BuildPortInterfaceFile(plan, portName, interfaceLines),
            });
            generatedFiles.Add(new GeneratedFile
            {
                RelativePath = BuildGeneratedRelativePath(plan.Namespace, plan.TypeName + ".PortSlot.g.cs"),
                Content = BuildUserSlotFile(plan, portName, currentTypeSymbol, typeDeclaration),
            });

            diagnostics.Add(new PlatformIsolationDiagnostic(
                PlatformIsolationDiagnosticCodes.FacadeGenerated,
                CoreDiagnosticSeverity.Info,
                $"Facade port generated for '{currentTypeSymbol.ToDisplayString()}': interface '{portName}' " +
                $"with {portedCount} delegating member(s); partial seam throws PlatformNotSupportedException " +
                $"until the user slot supplies the port; no port implementation generated (D8/R2)" +
                (signatureChanges.Count == 0 ? "." : $"; signature portability: {string.Join("; ", signatureChanges)}."),
                filePath));
        }

        if (memberReplacements.Count == 0)
        {
            return empty;
        }

        var rewriter = new FacadeRewriter(memberReplacements, seamByType);
        var rewrittenRoot = (CompilationUnitSyntax)rewriter.Visit(currentRoot)!;
        // 新增的 partial 修饰符、箭头表达式体与注入缝成员均为无 trivia 解析产物，
        // 序列化前统一格式化（与 DefaultParameterStripper 等同范式），避免
        // partial/class 等相邻 token 粘连导致 PI040。
        using var workspace = new AdhocWorkspace();
        rewrittenRoot = (CompilationUnitSyntax)Formatter.Format(rewrittenRoot, workspace);
        var output = EnsureNullableEnabled(rewrittenRoot.ToFullString());

        // 三候选语法校验（同 StubGenerator）：非法则原文保留，PI040 响亮失败。
        var reparsed = CSharpSyntaxTree.ParseText(
            output, currentModel.SyntaxTree.Options as CSharpParseOptions);
        var parseError = reparsed.GetDiagnostics()
            .FirstOrDefault(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        if (parseError != null)
        {
            diagnostics.Add(new PlatformIsolationDiagnostic(
                PlatformIsolationDiagnosticCodes.GeneratedSyntaxInvalid,
                CoreDiagnosticSeverity.Error,
                $"facade rewrite of '{filePath}' produced invalid C#; original preserved. " +
                $"First error: {parseError}",
                filePath));
            return new FacadeRewriteResult
            {
                Changed = false,
                OutputCode = sourceText,
                Diagnostics = diagnostics,
                GeneratedFiles = [],
            };
        }

        return new FacadeRewriteResult
        {
            Changed = !string.Equals(output, sourceText, StringComparison.Ordinal),
            OutputCode = output,
            Diagnostics = diagnostics,
            GeneratedFiles = generatedFiles,
        };
    }

    private static bool IsFacadeableMember(SyntaxNode member)
    {
        if (member is MethodDeclarationSyntax method)
        {
            // 泛型方法 M3 不抽取；ref/out/in 穿 ABI 的委托调用需要实参修饰符
            // 协同（C01 锁 ABI 面），M3 保守不抽取，交 residual 闸门显式裁决。
            if (method.TypeParameterList != null
                || method.ParameterList.Parameters.Any(HasRefKind))
            {
                return false;
            }

            return method.Modifiers.All(m => !m.IsKind(SyntaxKind.AbstractKeyword)
                && !m.IsKind(SyntaxKind.PartialKeyword));
        }

        if (member is PropertyDeclarationSyntax property)
        {
            return property.Modifiers.All(m => !m.IsKind(SyntaxKind.AbstractKeyword));
        }

        // 普通字段（含平台服务类型字段）做类型移植；事件字段跳过。
        return member is FieldDeclarationSyntax field
               && field.Modifiers.All(m => !m.IsKind(SyntaxKind.AbstractKeyword));
    }

    private static bool HasRefKind(ParameterSyntax parameter)
        => parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)
            || m.IsKind(SyntaxKind.OutKeyword)
            || m.IsKind(SyntaxKind.InKeyword));

    private static bool HasStaticModifier(MemberDeclarationSyntax member)
        => member.Modifiers.Any(SyntaxKind.StaticKeyword);

    /// <summary>构造端口委托调用实参：M3 已排除 ref/out/in，形参即标识符。</summary>
    private static string BuildCallArgument(ParameterSyntax parameter)
        => parameter.Identifier.Text;

    private static string BuildInterfaceMethod(MethodDeclarationSyntax ported)
    {
        var parameters = string.Join(", ", ported.ParameterList.Parameters.Select(p => p.ToString()));
        return $"        {ported.ReturnType} {ported.Identifier.Text}({parameters});";
    }

    private static string BuildInterfaceProperty(
        PropertyDeclarationSyntax ported,
        IReadOnlyCollection<SyntaxKind> accessKinds)
    {
        var accessors = string.Join(" ", accessKinds.Select(k => k switch
        {
            SyntaxKind.GetAccessorDeclaration => "get;",
            SyntaxKind.SetAccessorDeclaration => "set;",
            SyntaxKind.InitAccessorDeclaration => "init;",
            _ => "get;",
        }));
        return $"        {ported.Type} {ported.Identifier.Text} {{ {accessors} }}";
    }

    private static PropertyDeclarationSyntax BuildDelegatingProperty(
        PropertyDeclarationSyntax ported,
        IReadOnlyCollection<SyntaxKind> accessKinds)
    {
        var accessors = accessKinds.Select(kind =>
        {
            var accessor = AccessorDeclaration(kind);
            if (kind == SyntaxKind.GetAccessorDeclaration)
            {
                accessor = accessor.WithBody(Block(
                    ReturnStatement(ParseExpression($"{PropertyName}.{ported.Identifier.Text}"))));
            }
            else
            {
                accessor = accessor.WithBody(Block(
                    ParseStatement($"{PropertyName}.{ported.Identifier.Text} = value;")));
            }

            return accessor.WithSemicolonToken(default);
        });

        return ported
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(AccessorList(List(accessors)));
    }

    private static IEnumerable<MemberDeclarationSyntax> ParseSeamMembers(string typeName, bool isStatic)
    {
        var portType = "I" + typeName + "Port";
        var stat = isStatic ? "static " : string.Empty;
        var message = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
            $"CS2J facade port '{portType}' is not supplied: implement the port and fill " +
            $"'{HookName}' in the generated user slot (platform-isolation PI020).",
            quote: true);

        var declarations = new[]
        {
            $"private {stat}{portType}? {FieldName};",
            $$"""
private {{stat}}{{portType}} {{PropertyName}}
{
    get
    {
        var port = {{FieldName}};
        if (port == null)
        {
            {{HookName}}(ref port);
            {{FieldName}} = port;
        }
        if (port == null)
        {
            throw new System.PlatformNotSupportedException({{message}});
        }
        return port;
    }
}
""",
            // 无实现的经典 partial 方法不允许访问修饰符（C# 规则：未提供实现部时
            // 调用由编译器擦除），故不加 private；用户槽位补实现部时缺省即私有。
            $"partial void {HookName}(ref {portType}? port);",
        };

        foreach (var text in declarations)
        {
            var parsed = ParseMemberDeclaration(text)
                ?? throw new InvalidOperationException($"failed to parse facade seam member: {text}");
            // ParseMemberDeclaration 产物无任何 trivia；显式给换行+类成员缩进，
            // 避免 AddMembers 后多个缝成员被排在同一行（Formatter 保留既有换行）。
            yield return parsed.WithLeadingTrivia(
                SyntaxTriviaList.Create(EndOfLine(Environment.NewLine))
                    .Add(Whitespace("    ")));
        }
    }

    private static string BuildPortInterfaceFile(
        TypePlan plan,
        string portName,
        IReadOnlyList<string> memberLines)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("#nullable enable");
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine($"// C42 M3 facade port for '{plan.TypeFullName}' (design §4.3, D5).");
        sb.AppendLine("// Members use portable signatures only; platform types are replaced by");
        sb.AppendLine("// portable handles (see PI020 signature portability notes).");
        sb.AppendLine("// </auto-generated>");
        if (plan.Namespace.Length > 0)
        {
            sb.AppendLine("namespace " + plan.Namespace);
            sb.AppendLine("{");
        }
        sb.AppendLine("    internal interface " + portName);
        sb.AppendLine("    {");
        foreach (var line in memberLines)
        {
            sb.AppendLine(line);
        }
        sb.AppendLine("    }");
        if (plan.Namespace.Length > 0)
        {
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static string BuildUserSlotFile(
        TypePlan plan,
        string portName,
        INamedTypeSymbol typeSymbol,
        TypeDeclarationSyntax typeDeclaration)
    {
        var isStatic = typeSymbol.IsStatic
            || typeDeclaration.Modifiers.Any(SyntaxKind.StaticKeyword);
        var accessibility = typeDeclaration.Modifiers.Any(SyntaxKind.PublicKeyword)
            ? "public "
            : "internal ";
        var classKind = typeDeclaration switch
        {
            RecordDeclarationSyntax record when record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)
                => "record struct",
            RecordDeclarationSyntax => "record class",
            StructDeclarationSyntax => "struct",
            _ => "class",
        };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("#nullable enable");
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine($"// C42 M3 facade USER SLOT for '{plan.TypeFullName}' (design §4.3, D8/R2).");
        sb.AppendLine("//");
        sb.AppendLine("// This pass never emits a port implementation here. Implement '" + portName + "'");
        sb.AppendLine("// against portable APIs, then move this file out of generated/ and fill the");
        sb.AppendLine($"// partial hook '{HookName}'. Until a non-null port is supplied every isolated");
        sb.AppendLine("// member throws PlatformNotSupportedException at runtime.");
        sb.AppendLine("// </auto-generated>");
        if (plan.Namespace.Length > 0)
        {
            sb.AppendLine("namespace " + plan.Namespace);
            sb.AppendLine("{");
        }
        sb.AppendLine($"    {accessibility}{(isStatic ? "static " : "")}partial {classKind} {plan.TypeName}");
        sb.AppendLine("    {");
        sb.AppendLine($"        // TODO(platform-isolation): supply a portable '{portName}' implementation:");
        sb.AppendLine("//");
        sb.AppendLine($"        // partial void {HookName}(ref {portName}? port)");
        sb.AppendLine("//        {");
        sb.AppendLine("//            port = new Portable" + plan.TypeName + "Port();");
        sb.AppendLine("//        }");
        sb.AppendLine("    }");
        if (plan.Namespace.Length > 0)
        {
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static string BuildGeneratedRelativePath(string namespaceName, string fileName)
    {
        var folder = namespaceName.Replace('.', '/');
        return folder.Length == 0
            ? "generated/" + fileName
            : "generated/" + folder + "/" + fileName;
    }

    private static string EnsureNullableEnabled(string code)
        => code.StartsWith("#nullable", StringComparison.Ordinal) ? code : "#nullable enable" + Environment.NewLine + code;

    /// <summary>按原树符号显示名记录的单类型 Facade 计划。</summary>
    private sealed class TypePlan
    {
        public TypePlan(INamedTypeSymbol symbol)
        {
            TypeFullName = symbol.ToDisplayString();
            TypeName = symbol.Name;
            Namespace = symbol.ContainingNamespace?.IsGlobalNamespace == true
                ? string.Empty
                : symbol.ContainingNamespace.ToDisplayString();
        }

        private readonly HashSet<string> _methodAndPropertyKeys = [];
        private readonly HashSet<string> _fieldKeys = [];

        public string TypeFullName { get; }
        public string TypeName { get; }
        public string Namespace { get; }

        public void AddMember(MemberDeclarationSyntax member, SemanticModel originalModel)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                {
                    if (originalModel.GetDeclaredSymbol(method) is { } methodSymbol)
                    {
                        _methodAndPropertyKeys.Add(methodSymbol.ToDisplayString());
                    }
                    break;
                }
                case PropertyDeclarationSyntax property:
                {
                    if (originalModel.GetDeclaredSymbol(property) is { } propertySymbol)
                    {
                        _methodAndPropertyKeys.Add(propertySymbol.ToDisplayString());
                    }
                    break;
                }
                case FieldDeclarationSyntax field:
                {
                    foreach (var variable in field.Declaration.Variables)
                    {
                        if (originalModel.GetDeclaredSymbol(variable) is IFieldSymbol fieldSymbol)
                        {
                            _fieldKeys.Add(fieldSymbol.ToDisplayString());
                        }
                    }
                    break;
                }
            }
        }

        public bool Matches(MemberDeclarationSyntax member, SemanticModel currentModel)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    return currentModel.GetDeclaredSymbol(method) is { } methodSymbol
                           && _methodAndPropertyKeys.Contains(methodSymbol.ToDisplayString());
                case PropertyDeclarationSyntax property:
                    return currentModel.GetDeclaredSymbol(property) is { } propertySymbol
                           && _methodAndPropertyKeys.Contains(propertySymbol.ToDisplayString());
                case FieldDeclarationSyntax field:
                {
                    foreach (var variable in field.Declaration.Variables)
                    {
                        if (currentModel.GetDeclaredSymbol(variable) is IFieldSymbol fieldSymbol
                            && _fieldKeys.Contains(fieldSymbol.ToDisplayString()))
                        {
                            return true;
                        }
                    }
                    return false;
                }
                default:
                    return false;
            }
        }
    }

    /// <summary>成员替换 + 类型 partial 缝注入（缝在子成员访问之后追加）。</summary>
    private sealed class FacadeRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<SyntaxNode, MemberDeclarationSyntax> _memberReplacements;
        private readonly Dictionary<TypeDeclarationSyntax, List<MemberDeclarationSyntax>> _seamByType;

        public FacadeRewriter(
            Dictionary<SyntaxNode, MemberDeclarationSyntax> memberReplacements,
            Dictionary<TypeDeclarationSyntax, List<MemberDeclarationSyntax>> seamByType)
        {
            _memberReplacements = memberReplacements;
            _seamByType = seamByType;
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
            => _memberReplacements.TryGetValue(node, out var replacement)
                ? replacement
                : base.VisitMethodDeclaration(node);

        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
            => _memberReplacements.TryGetValue(node, out var replacement)
                ? replacement
                : base.VisitPropertyDeclaration(node);

        public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
            => _memberReplacements.TryGetValue(node, out var replacement)
                ? replacement
                : base.VisitFieldDeclaration(node);

        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
            => AttachSeam(node, base.VisitClassDeclaration(node) as TypeDeclarationSyntax);

        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
            => AttachSeam(node, base.VisitStructDeclaration(node) as TypeDeclarationSyntax);

        public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
            => AttachSeam(node, base.VisitRecordDeclaration(node) as TypeDeclarationSyntax);

        private SyntaxNode? AttachSeam(TypeDeclarationSyntax original, TypeDeclarationSyntax? visited)
        {
            if (visited == null || !_seamByType.TryGetValue(original, out var seam))
            {
                return visited;
            }

            var partialNode = visited.Modifiers.Any(SyntaxKind.PartialKeyword)
                ? visited
                : visited.WithModifiers(visited.Modifiers.Add(Token(SyntaxKind.PartialKeyword)));
            return partialNode.AddMembers(seam.ToArray());
        }
    }
}
