using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.EnumDeclaration;

/// <summary>
/// Phase 2b：整树改写被归一 enum 的使用点。分类口径必须与
/// <see cref="EnumDeclarationAnalyzer.UseSiteWalker"/> 一一对应——Analyzer 判可行
/// 的每一类站点这里都有落地改写；Analyzer 判 ED020 的 plan 不会进入 active 集。
/// </summary>
internal sealed class EnumUseSiteRewriter : CSharpSyntaxRewriter
{
    /// <summary>挂在“需要临时变量的判定表达式”上的标记种类（不写入文本，仅供后置插入）。</summary>
    internal const string TempMarkerKind = "Cs2jEnumSwitchTemp";

    private readonly SemanticModel _model;
    private readonly Dictionary<INamedTypeSymbol, EnumDeclarationPlan> _active;
    private readonly List<HoistedTempRequest> _pendingTemps = new();
    // 本次改写已发行的临时变量名。LookupSymbols 只看得到原始树，看不到尚未写回的
    // 合成声明；同作用域两个 switch/is 站点若都选 __cs2jEv 会产生 CS0128 重复局部。
    private readonly HashSet<string> _issuedTempNames = new();

    public EnumUseSiteRewriter(SemanticModel model, IEnumerable<EnumDeclarationPlan> activePlans)
    {
        _model = model;
        _active = new(SymbolEqualityComparer.Default);
        foreach (var p in activePlans)
            if (p.Feasible && p.Names != null) _active[p.Symbol] = p;
    }

    /// <summary>
    /// 本次 Visit 收集到的“值判定临时变量”请求；整树 Visit 结束后由
    /// <see cref="ApplyHoistedTemps"/> 统一把声明插入各自的语句锚点前。
    /// </summary>
    public IReadOnlyList<HoistedTempRequest> PendingTemps => _pendingTemps;

    // ── 基础工具 ──

    private EnumDeclarationPlan? PlanOf(ITypeSymbol? type)
    {
        var inner = EnumDeclarationSyntaxFacts.UnwrapNullableEnum(type);
        if (inner == null) return null;
        return _active.TryGetValue(inner, out var plan) ? plan : null;
    }

    /// <summary>仅精确（非 Nullable&lt;T&gt;）命中的活动 plan。</summary>
    private EnumDeclarationPlan? PlanExact(ITypeSymbol? type)
        => type is INamedTypeSymbol nt && _active.TryGetValue(nt, out var p) ? p : null;

    /// <summary>
    /// E04 形状判定：表达式是 (integral)enumExpr 强转，且 enumExpr 为活动 plan。
    /// </summary>
    private EnumDeclarationPlan? IntegralCastPlan(ExpressionSyntax? expr)
    {
        if (expr is not CastExpressionSyntax cast) return null;
        var castType = _model.GetTypeInfo(cast.Type).Type;
        if (castType?.SpecialType is not (SpecialType.System_Int32
            or SpecialType.System_UInt32 or SpecialType.System_Int64
            or SpecialType.System_UInt64 or SpecialType.System_Byte
            or SpecialType.System_SByte or SpecialType.System_Int16
            or SpecialType.System_UInt16))
            return null;
        return PlanExact(_model.GetTypeInfo(cast.Expression).Type);
    }

    private static bool IsAnyEnum(ITypeSymbol? type)
        => EnumDeclarationSyntaxFacts.UnwrapNullableEnum(type) != null;

    private static string TypeName(EnumDeclarationPlan plan)
        => "global::" + plan.EnumQualifiedName.Replace('+', '.');

    private static string U(EnumDeclarationPlan plan)
        => EnumDeclarationSyntaxFacts.GetTypeKeyword(plan.Underlying);

    private ExpressionSyntax DefaultOf(EnumDeclarationPlan plan)
        => Annotate(ParseExpression($"{TypeName(plan)}.{plan.Names!.DefaultInstance}"));

    private static ExpressionSyntax Annotate(ExpressionSyntax node)
        => node.WithAdditionalAnnotations(EnumDeclarationSyntaxFacts.RewriteAnnotation);

    private static StatementSyntax Annotate(StatementSyntax node)
        => node.WithAdditionalAnnotations(EnumDeclarationSyntaxFacts.RewriteAnnotation);

    private static ExpressionSyntax Paren(ExpressionSyntax e)
        => e is ParenthesizedExpressionSyntax ? e : ParenthesizedExpression(e);

    /// <summary>
    /// 节点文本嵌入 ParseExpression 合成表达式前剥掉整棵子树的 trivia：
    /// 操作数尾随的单行注释（//…）若原样拼接进合成文本，会把同一行后续合成的
    /// token（典型如闭合括号）整段注释掉，导致 CS1026/CS1002
    /// （XPathParser.IsStep：<c>lexKind == LexKind.Name // 注释</c>）。
    /// 剥掉的外层 trivia 由 <see cref="WithReplacementTrivia"/> 挂回合成结果。
    /// </summary>
    private static string Embed(SyntaxNode visited)
        => visited.WithoutTrivia().ToFullString();

    /// <summary>
    /// 把原表达式首尾 token 的 trivia（缩进、行尾注释、换行）挂到合成结果上，
    /// 使剥离 trivia 后嵌入的合成表达式保持原位注释不丢。
    /// </summary>
    private static T WithReplacementTrivia<T>(
        ExpressionSyntax original, T replacement) where T : ExpressionSyntax
        => replacement
            .WithLeadingTrivia(original.GetLeadingTrivia())
            .WithTrailingTrivia(original.GetTrailingTrivia());

    private string LiteralFor(EnumDeclarationPlan plan, object value)
        => EnumDeclarationSyntaxFacts.RenderIntegerLiteral(value, plan.Underlying);

    /// <summary>常量模式/标签表达式 → U 字面量（成员引用取 plan 内成员位值）。</summary>
    private bool TryPatternLiteral(ExpressionSyntax expression, EnumDeclarationPlan plan,
        out string literal)
    {
        literal = "0";
        var symbol = _model.GetSymbolInfo(expression).Symbol;
        if (symbol is IFieldSymbol f && SymbolEqualityComparer.Default.Equals(f.ContainingType, plan.Symbol))
        {
            var member = plan.Members.FirstOrDefault(m => m.Name == f.Name);
            if (member == null) return false;
            literal = LiteralFor(plan, member.ConstantValue);
            return true;
        }
        var constant = _model.GetConstantValue(expression);
        if (constant.HasValue && constant.Value != null)
        {
            literal = EnumDeclarationSyntaxFacts.RenderIntegerLiteral(constant.Value, plan.Underlying);
            return true;
        }
        return false;
    }

    // ── 1) 强转 ──

    public override SyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        // 归一目标 enum 内部（成员 EqualsValue 等）整体由 Synthesizer 用编译期常量重建，
        // 不做使用点改写；其余 enum 成员初值只能是整数常量，也无改写点。
        // 跳过子树同时保证 EnumDeclarationSyntax 节点引用身份不变（声明替换靠引用匹配）。
        return node;
    }

    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        // 语义判定必须在 base.Visit 前（新节点尚未进语义树）。
        var targetType = _model.GetTypeInfo(node.Type).Type;
        var sourceType = _model.GetTypeInfo(node.Expression).Type;
        var targetPlan = PlanOf(targetType);
        var sourcePlan = PlanOf(sourceType);

        var visited = (CastExpressionSyntax)base.VisitCastExpression(node)!;
        node = visited;

        if (sourceType?.TypeKind == TypeKind.Dynamic && targetPlan != null) return node;
        if (sourcePlan != null && targetType is INamedTypeSymbol tn
            && tn.ToDisplayString() is "System.IFormattable" or "System.IConvertible"
                or "System.Enum" or "System.ValueType")
        {
            return node;
        }

        if (targetPlan != null && sourcePlan != null)
        {
            // 双问题 enum 互转（同 plan 冗余转换也安全）。
            return WithReplacementTrivia(node, Annotate(ParseExpression(
                $"{TypeName(targetPlan)}.{targetPlan.Names!.FromValue}" +
                $"(({U(targetPlan)})({Embed(node.Expression)}.{sourcePlan.Names!.GetValue}()))")));
        }

        if (targetPlan != null
            && sourceType is { SpecialType: var st }
            && EnumDeclarationSyntaxFacts.IntegerSpecialTypes.Contains(st))
        {
            // 整数 → E：FromValue（D14）。
            return WithReplacementTrivia(node, Annotate(ParseExpression(
                $"{TypeName(targetPlan)}.{targetPlan.Names!.FromValue}" +
                $"(({U(targetPlan)})({Embed(node.Expression)}))")));
        }

        if (targetPlan != null && IsAnyEnum(sourceType) && sourcePlan == null)
        {
            // 普通（未改写）enum → class 形态：整数桥 (int)(object)。
            return WithReplacementTrivia(node, Annotate(ParseExpression(
                $"{TypeName(targetPlan)}.{targetPlan.Names!.FromValue}" +
                $"(({U(targetPlan)})(int)(object)({Embed(node.Expression)}))")));
        }

        if (targetPlan != null && sourceType is { SpecialType:
                SpecialType.System_Object or SpecialType.System_Enum
                or SpecialType.System_ValueType })
        {
            // 拆箱 (E)(object)：class 形态引用转换，零改写。
            return node;
        }

        if (sourcePlan != null && targetType != null
            && EnumDeclarationSyntaxFacts.IntegerSpecialTypes.Contains(targetType.SpecialType))
        {
            // E → 整数：GetValue 后按目标类型再转一层。
            var inner = Embed(node.Expression);
            var call = $"{inner}.{sourcePlan.Names!.GetValue}()";
            var targetText = Embed(node.Type);
            return WithReplacementTrivia(node, Annotate(ParseExpression(
                targetText == U(sourcePlan) ? call : $"({targetText})({call})")));
        }

        if (sourcePlan != null && IsAnyEnum(targetType) && targetPlan == null)
        {
            // class 形态 → 普通 enum：装箱桥。
            var targetText = Embed(node.Type);
            return WithReplacementTrivia(node, Annotate(ParseExpression(
                $"({targetText})(object)(int)({Embed(node.Expression)}" +
                $".{sourcePlan.Names!.GetValue}())")));
        }

        return node;
    }

    // ── 2) default(E) / default / new E() ──

    public override SyntaxNode? VisitDefaultExpression(DefaultExpressionSyntax node)
    {
        var plan = PlanOf(_model.GetTypeInfo(node).Type)
            ?? PlanOf(_model.GetTypeInfo(node).ConvertedType);
        return plan != null ? DefaultOf(plan) : base.VisitDefaultExpression(node);
    }

    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.DefaultLiteralExpression))
        {
            var plan = PlanOf(_model.GetTypeInfo(node).ConvertedType)
                ?? PlanOf(_model.GetTypeInfo(node).Type);
            if (plan != null) return WithReplacementTrivia(node, DefaultOf(plan));
        }
        if (node.IsKind(SyntaxKind.NumericLiteralExpression)
            && EnumDeclarationSyntaxFacts.IsZeroIntegralLiteral(node.Token.Value))
        {
            // 隐式常量表达式转换：字面量 0 可出现在任何 enum 类型位置，归一为
            // class 后 int→class 无隐式转换（CS0029），替换为合成默认实例。
            var plan = PlanOf(_model.GetTypeInfo(node).ConvertedType)
                ?? PlanOf(_model.GetTypeInfo(node).Type);
            if (plan != null) return WithReplacementTrivia(node, DefaultOf(plan));
        }
        return base.VisitLiteralExpression(node);
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var plan = PlanOf(_model.GetTypeInfo(node).Type);
        if (plan != null && node.ArgumentList?.Arguments.Count == 0)
            return DefaultOf(plan);
        return base.VisitObjectCreationExpression(node);
    }

    /// <summary>
    /// null/default 字面量实参的重载消歧：原代码中 <c>: this(null)</c> 只会绑定到
    /// 引用类型参数重载（enum 是值类型不接 null）；当另一个同位置参数是归一 enum
    /// 的重载在归一后也变成引用类型，null 就在两个重载间 CS0121 二义
    /// （XmlPreloadedResolver: (XmlKnownDtds) vs (XmlResolver)）。给实参补上原绑定
    /// 参数类型的显式 cast 即可恢复唯一解析。
    /// </summary>
    public override SyntaxNode? VisitArgument(ArgumentSyntax node)
    {
        var visited = (ArgumentSyntax)base.VisitArgument(node)!;
        if (!node.RefKindKeyword.IsKind(SyntaxKind.None)) return visited;
        if (node.Expression is not LiteralExpressionSyntax lit
            || (!lit.IsKind(SyntaxKind.NullLiteralExpression)
                && !lit.IsKind(SyntaxKind.DefaultLiteralExpression)))
            return visited;
        if (node.Parent is not BaseArgumentListSyntax argList)
            return visited;

        var info = argList.Parent switch
        {
            InvocationExpressionSyntax ie => _model.GetSymbolInfo(ie),
            ObjectCreationExpressionSyntax oc => _model.GetSymbolInfo(oc),
            ConstructorInitializerSyntax ci => _model.GetSymbolInfo(ci),
            _ => default,
        };
        if (info.Symbol is not IMethodSymbol chosen) return visited;

        var paramIndex = ResolveArgumentParameterIndex(argList, node, chosen);
        if (paramIndex < 0 || paramIndex >= chosen.Parameters.Length) return visited;
        var targetType = chosen.Parameters[paramIndex].Type;
        if (targetType.TypeKind == TypeKind.Dynamic
            || PlanOf(targetType) != null)
            return visited;

        if (!HasRivalOverloadWithActiveEnumParam(chosen, paramIndex)) return visited;

        var castType = targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return visited.WithExpression(WithReplacementTrivia(node.Expression,
            Annotate(ParseExpression($"({castType}){Embed(visited.Expression)}"))));
    }

    private static int ResolveArgumentParameterIndex(
        BaseArgumentListSyntax argList, ArgumentSyntax argument, IMethodSymbol method)
    {
        if (argument.NameColon?.Name is { } named)
        {
            for (var i = 0; i < method.Parameters.Length; i++)
                if (method.Parameters[i].Name == named.Identifier.Text) return i;
            return -1;
        }
        var positional = 0;
        foreach (var a in argList.Arguments)
        {
            if (a.NameColon != null) continue;
            if (a == argument) return positional;
            positional++;
        }
        return -1;
    }

    private bool HasRivalOverloadWithActiveEnumParam(IMethodSymbol chosen, int paramIndex)
    {
        foreach (var m in chosen.ContainingType.GetMembers()
                     .OfType<IMethodSymbol>()
                     .Where(m => m.MethodKind == chosen.MethodKind
                         && m.Name == chosen.Name
                         && m.Parameters.Length == chosen.Parameters.Length
                         && !SymbolEqualityComparer.Default.Equals(m, chosen)))
        {
            var rivalType = m.Parameters[paramIndex].Type;
            if (PlanOf(rivalType) != null
                && !SymbolEqualityComparer.Default.Equals(
                    rivalType, chosen.Parameters[paramIndex].Type))
                return true;
        }
        return false;
    }

    // ── 3) switch 语句降级为 if 链 ──

    public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
    {
        var plan = PlanOf(_model.GetTypeInfo(node.Expression).Type)
            ?? PlanOf(_model.GetTypeInfo(node.Expression).ConvertedType);
        if (plan == null)
            return base.VisitSwitchStatement(node);

        // Semantic decisions MUST be taken on the ORIGINAL tree before
        // visiting: case values/patterns that are themselves enum use sites
        // (e.g. case (E)(...): lowered by the enum pass) are replaced by
        // freshly parsed nodes during Visit, and those detached nodes cannot
        // be queried through _model ("Syntax node is not within syntax tree").
        var tmp = UniqueTemp(node, "__cs2jEv");
        var labelInfos = new List<SwitchLabelInfo>();
        foreach (var section in node.Sections)
        {
            var info = new SwitchLabelInfo();
            foreach (var label in section.Labels)
            {
                switch (label)
                {
                    case DefaultSwitchLabelSyntax:
                        info.IsDefault = true;
                        break;
                    case CaseSwitchLabelSyntax cs:
                        if (TryPatternLiteral(cs.Value, plan, out var lit1))
                            info.Preds.Add(ParseExpression($"{tmp} == {lit1}"));
                        break;
                    case CasePatternSwitchLabelSyntax cp:
                        if (cp.Pattern is DiscardPatternSyntax)
                            info.IsDefault = true;
                        else
                            info.Preds.Add(PatternPredicate(cp.Pattern, plan, tmp));
                        break;
                }
            }
            labelInfos.Add(info);
        }

        // 跨 case 段共享的局部变量同属 switch 块级作用域；降级成相互独立的
        // if 分支块后，只有真正被其他段引用的变量需要提升到合成包裹块。
        // 注意：所有变换都在 base.Visit 产出的 visited 树上做结构搬运，绝不能
        // 让 walker 访问新 Parse/WithoutTrivia 出来的游离节点（模型查询会抛
        // "Syntax node is not within syntax tree"）。
        var hoistedVariables = SwitchSectionScopeFacts.Analyze(node, _model).HoistedVariables;

        var visited = (SwitchStatementSyntax)base.VisitSwitchStatement(node)!;

        // 提升声明类型文本（var 已失去初始化器，必须替换为实际类型），在原树查询。
        string HoistTypeText(LocalDeclarationStatementSyntax original)
        {
            if (original.Declaration.Type is IdentifierNameSyntax { Identifier.Text: "var" })
            {
                var first = original.Declaration.Variables.First(hoistedVariables.Contains);
                var symbol = (ILocalSymbol?)_model.GetDeclaredSymbol(first);
                return symbol != null
                    ? symbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    : "object";
            }
            return Embed(original.Declaration.Type);
        }

        var hoistedDeclarations = new List<StatementSyntax>();
        var sectionBodies = new List<IReadOnlyList<StatementSyntax>>(visited.Sections.Count);

        for (var s = 0; s < visited.Sections.Count; s++)
        {
            var originalStatements = node.Sections[s].Statements;
            var visitedStatements = visited.Sections[s].Statements;
            var newStatements = new List<StatementSyntax>();

            for (var j = 0; j < visitedStatements.Count; j++)
            {
                var visitedStatement = visitedStatements[j];
                if (j < originalStatements.Count
                    && originalStatements[j] is LocalDeclarationStatementSyntax { Modifiers.Count: 0 } originalLocal
                    && visitedStatement is LocalDeclarationStatementSyntax visitedLocal
                    && originalLocal.Declaration.Variables.Any(hoistedVariables.Contains))
                {
                    var typeText = HoistTypeText(originalLocal);
                    var kept = new List<VariableDeclaratorSyntax>();
                    for (var k = 0; k < originalLocal.Declaration.Variables.Count; k++)
                    {
                        var originalVariable = originalLocal.Declaration.Variables[k];
                        var visitedVariable = visitedLocal.Declaration.Variables[k];
                        if (hoistedVariables.Contains(originalVariable))
                        {
                            hoistedDeclarations.Add(Annotate(
                                ParseStatement($"{typeText} {originalVariable.Identifier.Text};")));
                            if (visitedVariable.Initializer is { } initializer)
                            {
                                newStatements.Add(ExpressionStatement(
                                    AssignmentExpression(
                                        SyntaxKind.SimpleAssignmentExpression,
                                        IdentifierName(originalVariable.Identifier.Text),
                                        initializer.Value)));
                            }
                        }
                        else
                        {
                            kept.Add(visitedVariable);
                        }
                    }
                    if (kept.Count > 0)
                    {
                        newStatements.Add(visitedLocal.WithDeclaration(
                            visitedLocal.Declaration.WithVariables(SeparatedList(kept))));
                    }
                }
                else
                {
                    newStatements.Add(visitedStatement);
                }
            }

            sectionBodies.Add(newStatements);
        }

        var gov = Embed(visited.Expression);
        var govNullable = IsNullableReceiver(node.Expression);
        var tempDecl = ParseStatement(
            $"{(govNullable ? U(plan) + "?" : U(plan))} {tmp} = "
            + (govNullable ? $"{gov}?.{plan.Names!.GetValue}();"
                           : $"{gov}.{plan.Names!.GetValue}();"));

        var stmts = new List<StatementSyntax>();
        stmts.AddRange(hoistedDeclarations);
        stmts.Add(Annotate(tempDecl));
        IfStatementSyntax? chain = null;
        BlockSyntax? fallback = null;

        for (var s = 0; s < visited.Sections.Count; s++)
        {
            var section = visited.Sections[s];
            var info = labelInfos[s];

            ExpressionSyntax? whenClause = null;
            foreach (var label in section.Labels)
            {
                if (label is CasePatternSwitchLabelSyntax { WhenClause: { } w })
                    whenClause = w.Condition;
            }

            var predicate = info.Preds.Count == 0 ? ParseExpression("false")
                : info.Preds.Count == 1 ? info.Preds[0]
                : info.Preds.Aggregate((a, b) => BinaryExpression(SyntaxKind.LogicalOrExpression,
                    Paren(a), Paren(b)));
            if (whenClause != null)
                predicate = BinaryExpression(SyntaxKind.LogicalAndExpression,
                    Paren(predicate), Paren(whenClause.WithoutTrivia()));

            var body = StripTrailingBreak(sectionBodies[s]);
            if (info.IsDefault)
                fallback = body;
            else
                chain = chain == null
                    ? IfStatement(predicate, body)
                    : AddBranch(chain, IfStatement(predicate, body));
        }

        if (fallback != null && chain != null)
            chain = AttachFallback(chain, fallback);
        else if (fallback != null)
            stmts.Add(fallback);
        if (chain != null) stmts.Add(chain);
        return Annotate(Block(stmts));
    }

    /// <summary>Per-section semantic decisions captured before rewriting.</summary>
    private sealed class SwitchLabelInfo
    {
        public bool IsDefault;
        public readonly List<ExpressionSyntax> Preds = new();
    }

    /// <summary>把 default 段挂到 else-if 链最深层（不能覆盖已有的 else-if）。</summary>
    private static IfStatementSyntax AttachFallback(IfStatementSyntax chain, BlockSyntax fallback)
    {
        if (chain.Else == null) return chain.WithElse(ElseClause(fallback));
        if (chain.Else.Statement is IfStatementSyntax nested)
            return chain.WithElse(ElseClause(AttachFallback(nested, fallback)));
        return chain;
    }

    private static IfStatementSyntax AddBranch(IfStatementSyntax chain, IfStatementSyntax branch)
    {
        // else 与其嵌套 if 之间必须有空白，否则文本粘连成标识符 elseif。
        var spaced = branch.WithLeadingTrivia(SyntaxFactory.Space);
        if (chain.Else == null) return chain.WithElse(ElseClause(spaced));
        if (chain.Else.Statement is IfStatementSyntax nested)
            return chain.WithElse(ElseClause(AddBranch(nested, branch)));
        return chain;
    }

    private static BlockSyntax StripTrailingBreak(IReadOnlyList<StatementSyntax> statements)
    {
        var list = statements.ToList();
        if (list.Count == 1 && list[0] is BlockSyntax block)
        {
            var inner = block.Statements.ToList();
            if (inner.Count > 0 && inner[^1] is BreakStatementSyntax) inner.RemoveAt(inner.Count - 1);
            return Block(inner);
        }
        if (list.Count > 0 && list[^1] is BreakStatementSyntax) list.RemoveAt(list.Count - 1);
        return Block(list);
    }

    private string UniqueTemp(SyntaxNode anchor, string preferred)
    {
        var pos = anchor.SpanStart;
        var name = preferred;
        for (var i = 0; ; i++)
        {
            var candidate = i == 0 ? name : name + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_model.LookupSymbols(pos, name: candidate).Length == 0
                && _issuedTempNames.Add(candidate)) return candidate;
        }
    }

    /// <summary>原树接收者是否为 Nullable(E)（决定值访问是 . 还是 ?.）。</summary>
    private bool IsNullableReceiver(ExpressionSyntax receiver)
        => _model.GetTypeInfo(receiver).Type is INamedTypeSymbol nt
           && nt.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T;

    /// <summary>
    /// 找可承载“只求值一次”临时变量声明的锚点：最近语句或表达式体成员。
    /// lambda/匿名方法/查询表达式边界内不可外提（会改变求值时机），返回 null
    /// （退化为内联，字段/属性初始化器等同理）。
    /// </summary>
    private static SyntaxNode? FindHoistAnchor(SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is StatementSyntax or ArrowExpressionClauseSyntax)
                return current;
            if (current is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax
                or QueryExpressionSyntax)
                return null;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// 为 switch/is 判定准备值表达式文本。命中语句锚点时登记一次临时变量
    /// （<see cref="PendingTemps"/>）并返回变量名；无锚点时返回内联的
    /// <c>(recv.GetValue())</c>/<c>(recv?.GetValue())</c>。
    /// </summary>
    private string PrepareValueText(
        ExpressionSyntax originalReceiver, string receiverText,
        EnumDeclarationPlan plan, out SyntaxAnnotation? marker)
    {
        marker = null;
        var isNullable = IsNullableReceiver(originalReceiver);
        var accessor = isNullable
            ? $"{receiverText}?.{plan.Names!.GetValue}()"
            : $"{receiverText}.{plan.Names!.GetValue}()";

        if (FindHoistAnchor(originalReceiver) == null)
            return $"({accessor})";

        var name = UniqueTemp(originalReceiver, "__cs2jEv");
        marker = new SyntaxAnnotation(TempMarkerKind, name);
        _pendingTemps.Add(new HoistedTempRequest(
            marker, name, isNullable ? U(plan) + "?" : U(plan), accessor));
        return name;
    }

    /// <summary>
    /// 后置 Pass：把 <see cref="PendingTemps"/> 中的临时变量声明插入各自锚点前
    /// （复用 IndexRange 的单次求值提升机制：块前插入、嵌入式语句包块、
    /// 表达式体成员展开为块体）。标记仅用于定位，不写入源文本。
    /// </summary>
    internal static CompilationUnitSyntax ApplyHoistedTemps(
        CompilationUnitSyntax root, IReadOnlyList<HoistedTempRequest> requests)
    {
        if (requests.Count == 0) return root;

        var anchorEdits = new Dictionary<SyntaxNode, SyntaxNode>();
        var infoByAnnotation = new Dictionary<SyntaxAnnotation, (SyntaxNode Anchor, string Declaration)>();

        foreach (var request in requests)
        {
            var marked = root.GetAnnotatedNodes(request.Marker).FirstOrDefault();
            if (marked == null) continue;

            var anchor = marked.Parent;
            while (anchor != null
                   && anchor is not (StatementSyntax or ArrowExpressionClauseSyntax))
            {
                anchor = anchor.Parent;
            }
            if (anchor == null) continue;

            var anchorAnnotation = new SyntaxAnnotation();
            var annotatedAnchor = anchor.WithAdditionalAnnotations(anchorAnnotation);
            anchorEdits[anchor] = annotatedAnchor;
            infoByAnnotation[anchorAnnotation] = (
                annotatedAnchor,
                $"{request.Type} {request.Name} = {request.Initializer};");
        }

        if (anchorEdits.Count == 0) return root;

        var current = root.ReplaceNodes(anchorEdits.Keys, (oldNode, _) => anchorEdits[oldNode]);

        var pending = new List<IndexRange.TempLifter.PendingTemp>();
        var anchorAnnotations = new Dictionary<SyntaxNode, SyntaxAnnotation>();
        var order = 0;
        foreach (var (annotation, (_, declarationText)) in infoByAnnotation)
        {
            var anchor = current.GetAnnotatedNodes(annotation).FirstOrDefault();
            if (anchor == null) continue;
            anchorAnnotations[anchor] = annotation;
            pending.Add(new IndexRange.TempLifter.PendingTemp(
                ParseStatement(declarationText), anchor, order++));
        }

        return IndexRange.TempLifter.Apply(current, pending, anchorAnnotations);
    }

    /// <summary>switch/is 判定临时变量请求（声明由后置 Pass 插入）。</summary>
    internal sealed record HoistedTempRequest(
        SyntaxAnnotation Marker, string Name, string Type, string Initializer);

    // ── 4) switch 表达式降级为条件链 ──

    public override SyntaxNode? VisitSwitchExpression(SwitchExpressionSyntax node)
    {
        var plan = PlanOf(_model.GetTypeInfo(node.GoverningExpression).Type)
            ?? PlanOf(_model.GetTypeInfo(node.GoverningExpression).ConvertedType);
        if (plan == null)
            return base.VisitSwitchExpression(node);

        // Predicates depend on semantic info and must be captured from the
        // ORIGINAL arms before Visit replaces enum-use-site subnodes.
        var armInfos = new List<SwitchArmInfo>();
        foreach (var arm in node.Arms)
        {
            var info = new SwitchArmInfo();
            if (arm.Pattern is DiscardPatternSyntax)
            {
                info.IsDiscard = true;
            }
            else
            {
                info.Predicate = PatternPredicate(arm.Pattern, plan, GovPlaceholder);
            }
            armInfos.Add(info);
        }

        var visited = (SwitchExpressionSyntax)base.VisitSwitchExpression(node)!;

        var govText = Embed(visited.GoverningExpression);
        var govValue = PrepareValueText(
            node.GoverningExpression, govText, plan, out var tempMarker);
        ExpressionSyntax? fallback = null;
        for (var i = visited.Arms.Count - 1; i >= 0; i--)
        {
            var arm = visited.Arms[i];
            var info = armInfos[i];
            if (info.IsDiscard)
            {
                fallback = arm.Expression;
                continue;
            }

            // Re-root the precomputed predicate at the actual value text.
            var pred = ParseExpression(
                info.Predicate!.ToFullString().Replace(GovPlaceholder, govValue));
            if (arm.WhenClause != null)
                pred = BinaryExpression(SyntaxKind.LogicalAndExpression,
                    Paren(pred), Paren(arm.WhenClause.Condition.WithoutTrivia()));
            var innerFallback = fallback
                ?? ParseExpression("throw new System.Runtime.CompilerServices.SwitchExpressionException()");
            fallback = ConditionalExpression(Paren(pred),
                Paren(arm.Expression.WithoutTrivia()), Paren(innerFallback));
        }

        var result = Paren(fallback ?? ParseExpression("default"));
        if (tempMarker != null) result = result.WithAdditionalAnnotations(tempMarker);
        return Annotate(WithReplacementTrivia(node, result));
    }

    /// <summary>Per-arm semantic decision captured before rewriting.</summary>
    private sealed class SwitchArmInfo
    {
        public bool IsDiscard;
        public ExpressionSyntax? Predicate;
    }

    /// <summary>
    /// Distinct placeholder used while building switch-expression predicates
    /// before the rewritten governing-expression text is available; replaced
    /// 1:1 with the actual value expression afterwards.
    /// </summary>
    private const string GovPlaceholder = "__CS2J_ENUM_GOVERNING_VALUE__";

    /// <summary>Companion placeholder for the raw receiver in is-pattern predicates.</summary>
    private const string RecvPlaceholder = "__CS2J_ENUM_RECEIVER__";

    /// <summary>
    /// 模式 → 对 U? 值表达式的谓词。const 走 lifted ==（null 自动不匹配）；
    /// NullPattern 仅 switch 临时变量路径用到（is 路径在 <see cref="VisitIsPatternExpression"/>）。
    /// </summary>
    private ExpressionSyntax PatternPredicate(PatternSyntax pattern, EnumDeclarationPlan plan,
        string valueExpression)
    {
        switch (pattern)
        {
            case ParenthesizedPatternSyntax pp:
                return PatternPredicate(pp.Pattern, plan, valueExpression);
            case ConstantPatternSyntax cp when cp.Expression.IsKind(SyntaxKind.NullLiteralExpression):
                return ParseExpression($"(!{valueExpression}.HasValue)");
            case ConstantPatternSyntax cp:
                if (TryPatternLiteral(cp.Expression, plan, out var lit))
                    return ParseExpression($"{valueExpression} == {lit}");
                return ParseExpression("false");
            case DiscardPatternSyntax:
                return ParseExpression("true");
            case BinaryPatternSyntax bp when bp.IsKind(SyntaxKind.OrPattern):
                return BinaryExpression(SyntaxKind.LogicalOrExpression,
                    Paren(PatternPredicate(bp.Left, plan, valueExpression)),
                    Paren(PatternPredicate(bp.Right, plan, valueExpression)));
            case BinaryPatternSyntax bp when bp.IsKind(SyntaxKind.AndPattern):
                return BinaryExpression(SyntaxKind.LogicalAndExpression,
                    Paren(PatternPredicate(bp.Left, plan, valueExpression)),
                    Paren(PatternPredicate(bp.Right, plan, valueExpression)));
            case UnaryPatternSyntax up when up.IsKind(SyntaxKind.NotPattern):
                if (up.Pattern is ConstantPatternSyntax ncp
                    && ncp.Expression.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    return ParseExpression($"({valueExpression}.HasValue)");
                }
                return PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                    Paren(PatternPredicate(up.Pattern, plan, valueExpression)));
            default:
                // 声明/递归模式已被 Analyzer 判 ED020（plan 不会在 active 集）。
                return ParseExpression("false");
        }
    }

    // ── 5) is 模式 ──

    public override SyntaxNode? VisitIsPatternExpression(IsPatternExpressionSyntax node)
    {
        var plan = PlanOf(_model.GetTypeInfo(node.Expression).Type);
        if (plan == null)
            return base.VisitIsPatternExpression(node);

        // Predicate semantic decisions must be captured from the ORIGINAL
        // pattern before Visit rewrites enum use sites nested in it.
        var predicate = IsPatternPredicate(
            node.Pattern, plan, RecvPlaceholder, GovPlaceholder);

        var visited = (IsPatternExpressionSyntax)base.VisitIsPatternExpression(node)!;

        var recv = Embed(visited.Expression);
        var valueExpression = PrepareValueText(
            node.Expression, recv, plan, out var tempMarker);
        var rooted = ParseExpression(
            predicate.ToFullString()
                .Replace(RecvPlaceholder, recv)
                .Replace(GovPlaceholder, valueExpression));
        var result = Paren(rooted);
        if (tempMarker != null) result = result.WithAdditionalAnnotations(tempMarker);
        return Annotate(WithReplacementTrivia(node, result));
    }

    private ExpressionSyntax IsPatternPredicate(
        PatternSyntax pattern, EnumDeclarationPlan plan, string recv, string valueExpression)
    {
        switch (pattern)
        {
            case ParenthesizedPatternSyntax pp:
                return IsPatternPredicate(pp.Pattern, plan, recv, valueExpression);
            case ConstantPatternSyntax cp when cp.Expression.IsKind(SyntaxKind.NullLiteralExpression):
                return ParseExpression($"({recv} is null)");
            case ConstantPatternSyntax cp:
                if (TryPatternLiteral(cp.Expression, plan, out var lit))
                    return ParseExpression($"{valueExpression} == {lit}");
                return ParseExpression("false");
            case DiscardPatternSyntax:
                return ParseExpression("true");
            case BinaryPatternSyntax bp when bp.IsKind(SyntaxKind.OrPattern):
                return BinaryExpression(SyntaxKind.LogicalOrExpression,
                    Paren(IsPatternPredicate(bp.Left, plan, recv, valueExpression)),
                    Paren(IsPatternPredicate(bp.Right, plan, recv, valueExpression)));
            case BinaryPatternSyntax bp when bp.IsKind(SyntaxKind.AndPattern):
                return BinaryExpression(SyntaxKind.LogicalAndExpression,
                    Paren(IsPatternPredicate(bp.Left, plan, recv, valueExpression)),
                    Paren(IsPatternPredicate(bp.Right, plan, recv, valueExpression)));
            case UnaryPatternSyntax up when up.IsKind(SyntaxKind.NotPattern):
                if (up.Pattern is ConstantPatternSyntax ncp
                    && ncp.Expression.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    return ParseExpression($"({recv} is not null)");
                }
                return PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                    Paren(IsPatternPredicate(up.Pattern, plan, recv, valueExpression)));
            default:
                return ParseExpression("false");
        }
    }

    // ── 6) == / != / &lt; &gt; 比较 ──

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var kind = node.Kind();

        // 经典 is 表达式 p is E.M（BinaryExpression/IsExpression）→  lifted 值比较。
        if (kind == SyntaxKind.IsExpression)
        {
            var isPlan = PlanOf(_model.GetTypeInfo(node.Left).Type)
                ?? PlanOf(_model.GetTypeInfo(node.Left).ConvertedType);
            var field = _model.GetSymbolInfo(node.Right).Symbol as IFieldSymbol;
            var isVisited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            if (isPlan == null || field == null
                || !SymbolEqualityComparer.Default.Equals(field.ContainingType, isPlan.Symbol))
            {
                return isVisited;
            }
            var member = isPlan.Members.FirstOrDefault(m => m.Name == field.Name);
            if (member == null) return isVisited;
            var lit = LiteralFor(isPlan, member.ConstantValue);
            var isLeft = Embed(isVisited.Left);
            var valueText = PrepareValueText(
                node.Left, isLeft, isPlan, out var isTempMarker);
            var isResult = WithReplacementTrivia(node, Annotate(ParseExpression(
                $"({valueText} == {lit})")));
            if (isTempMarker != null)
                isResult = isResult.WithAdditionalAnnotations(isTempMarker);
            return isResult;
        }

        if (kind is SyntaxKind.BitwiseAndExpression
            && node.Right is BinaryExpressionSyntax innerEq
            && innerEq.IsKind(SyntaxKind.EqualsExpression)
            && IntegralCastPlan(node.Left) is { } hp
            && IntegralCastPlan(innerEq.Left) == hp
            && IntegralCastPlan(innerEq.Right) == hp)
        {
            // E04 (legacy shape) lowers x.HasFlag(y) to (int)x & (int)y == (int)y
            // without parens (== binds tighter than &); recover the intended
            // grouping and route the cast chain through GetValue().
            var hv = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            var heq = (BinaryExpressionSyntax)hv.Right;
            return WithReplacementTrivia(node, Annotate(ParseExpression(
                $"(({Embed(hv.Left)} & {Embed(heq.Left)}) " +
                $"== {Embed(heq.Right)})")));
        }

        if (kind is SyntaxKind.EqualsExpression
            && node.Left is ParenthesizedExpressionSyntax andParen
            && andParen.Expression is BinaryExpressionSyntax correctAnd
            && correctAnd.IsKind(SyntaxKind.BitwiseAndExpression)
            && IntegralCastPlan(node.Right) is { } hp2
            && IntegralCastPlan(correctAnd.Left) == hp2
            && IntegralCastPlan(correctAnd.Right) == hp2)
        {
            // E04 (fixed shape) lowers x.HasFlag(y) to (((U)x & (U)y) == (U)y).
            // Route the three cast sites through GetValue() while keeping the
            // now-correct precedence grouping.
            var eqVisited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            var eqAnd = (BinaryExpressionSyntax)
                ((ParenthesizedExpressionSyntax)eqVisited.Left).Expression;
            return WithReplacementTrivia(node, Annotate(ParseExpression(
                $"(({Embed(eqAnd.Left)} " +
                $"& {Embed(eqAnd.Right)}) " +
                $"== {Embed(eqVisited.Right)})")));
        }

        if (kind is SyntaxKind.BitwiseAndExpression or SyntaxKind.BitwiseOrExpression
            or SyntaxKind.ExclusiveOrExpression)
        {
            var lp = PlanExact(_model.GetTypeInfo(node.Left).Type);
            var rp = PlanExact(_model.GetTypeInfo(node.Right).Type);
            var bitVisited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            if (lp != null && lp == rp)
            {
                var bop = kind == SyntaxKind.BitwiseAndExpression ? "&"
                    : kind == SyntaxKind.BitwiseOrExpression ? "|" : "^";
                var bl = Embed(bitVisited.Left);
                var br = Embed(bitVisited.Right);
                return WithReplacementTrivia(node, Annotate(ParseExpression(
                    $"{TypeName(lp)}.{lp.Names!.FromValue}(({U(lp)})" +
                    $"({bl}.{lp.Names.GetValue}() {bop} {br}.{lp.Names.GetValue}()))")));
            }
            return bitVisited;
        }

        if (kind is SyntaxKind.AddExpression)
        {
            var lp = PlanExact(_model.GetTypeInfo(node.Left).Type);
            var rp = PlanExact(_model.GetTypeInfo(node.Right).Type);
            var addVisited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            if (lp != null && rp != null)
            {
                // 两个 enum 直接相加在 C# 中本就非法；出现在输出链（Console.WriteLine 等）
                // 的实际意图是字符串拼接，归一为 string.Concat(object, object)。
                return WithReplacementTrivia(node, Annotate(ParseExpression(
                    $"string.Concat((object){Embed(addVisited.Left)}, " +
                    $"(object){Embed(addVisited.Right)})")));
            }
            return addVisited;
        }

        if (kind is not (SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
            or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression))
        {
            return base.VisitBinaryExpression(node);
        }

        var plan = PlanOf(_model.GetTypeInfo(node.Left).Type)
            ?? PlanOf(_model.GetTypeInfo(node.Left).ConvertedType)
            ?? PlanOf(_model.GetTypeInfo(node.Right).Type)
            ?? PlanOf(_model.GetTypeInfo(node.Right).ConvertedType);
        var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
        if (plan == null) return visited;

        var left = Embed(visited.Left);
        var right = Embed(visited.Right);

        if (kind is SyntaxKind.EqualsExpression)
            return WithReplacementTrivia(node, Annotate(
                ParseExpression($"object.Equals({left}, {right})")));
        if (kind is SyntaxKind.NotEqualsExpression)
            return WithReplacementTrivia(node, Annotate(
                ParseExpression($"!object.Equals({left}, {right})")));

        var op = kind switch
        {
            SyntaxKind.LessThanExpression => "<",
            SyntaxKind.LessThanOrEqualExpression => "<=",
            SyntaxKind.GreaterThanExpression => ">",
            _ => ">=",
        };
        var compareTo = plan.Names!.Method(EnumOptionalMembers.CompareTo, "CompareTo");
        return WithReplacementTrivia(node, Annotate(ParseExpression(
            $"({left} is null ? false : {left}.{compareTo}({right}) {op} 0)")));
    }

    // ── 7) System.Enum 静态 API / GetValueOrDefault / Nullable 成员 ──

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (_model.GetSymbolInfo(node).Symbol is not IMethodSymbol method) return visited;

        if (method.ContainingType.ToDisplayString() == "System.Enum" && method.IsStatic)
        {
            var redirected = TryRedirectEnumApi(node, visited, method);
            if (redirected != null) return redirected;
            return visited;
        }

        if (method.Name == "GetValueOrDefault"
            && method.ContainingType.ConstructedFrom.SpecialType
                == SpecialType.System_Nullable_T)
        {
            var p = PlanOf(method.ReceiverType
                ?? method.ContainingType.TypeArguments.FirstOrDefault());
            if (p != null)
            {
                var recv = visited.Expression is MemberAccessExpressionSyntax ma0
                    ? Embed(ma0.Expression)
                    : Embed(node);
                return WithReplacementTrivia(node, Annotate(
                    ParseExpression($"({recv} ?? {DefaultText(p)})")));
            }
        }

        return visited;
    }

    private static string DefaultText(EnumDeclarationPlan plan)
        => $"{TypeName(plan)}.{plan.Names!.DefaultInstance}";

    private ExpressionSyntax? TryRedirectEnumApi(
        InvocationExpressionSyntax original, InvocationExpressionSyntax visited,
        IMethodSymbol method)
    {
        var args = original.ArgumentList.Arguments;
        EnumDeclarationPlan? plan = null;
        if (method.TypeArguments.Length > 0)
            plan = PlanOf(method.TypeArguments[0]);
        if (plan == null && args.Count > 0)
        {
            if (args[0].Expression is TypeOfExpressionSyntax tos)
                plan = PlanOf(_model.GetTypeInfo(tos.Type).Type);
        }
        if (plan == null) return null;

        var tn = TypeName(plan);
        var n = plan.Names!;
        // typeof(E) 形式才有可跳过的首参；泛型 Enum.M<E>(...) 首参就是业务参数。
        var skip = args.Count > 0 && args[0].Expression is TypeOfExpressionSyntax ? 1 : 0;
        ExpressionSyntax Call(string methodName, params int[] relativeIndices)
        {
            var mappedArgs = relativeIndices
                .Select(i => visited.ArgumentList.Arguments[i + skip]).ToList();
            var argText = string.Join(", ", mappedArgs.Select(Embed));
            var call = WithReplacementTrivia(original,
                Annotate(ParseExpression($"{tn}.{methodName}({argText})")));
            // 末参数尾随行注释（位于闭合括号之前）无法留在实参列表里，安全移位到
            // 合成调用之后；单行注释必须补换行，否则会吞掉后续 token。
            if (mappedArgs.Count > 0)
            {
                var moved = new List<SyntaxTrivia>();
                foreach (var trivia in mappedArgs[^1].GetTrailingTrivia())
                {
                    if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                        && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) continue;
                    moved.Add(trivia);
                    if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
                        moved.Add(EndOfLine("\n"));
                }
                if (moved.Count > 0)
                    call = call.WithTrailingTrivia(call.GetTrailingTrivia().AddRange(moved));
            }
            return call;
        }

        switch (method.Name)
        {
            case "GetValues":
                return ParseExpression($"{tn}.{n.Method(EnumOptionalMembers.GetValues, "GetValues")}()");
            case "GetNames":
                return ParseExpression($"{tn}.{n.Method(EnumOptionalMembers.GetNames, "GetNames")}()");
            case "GetName":
                return Call(n.Method(EnumOptionalMembers.GetName, "GetName"), 0);
            case "Parse":
                return Call(n.Method(EnumOptionalMembers.Parse, "Parse"),
                    Enumerable.Range(0, args.Count - skip).ToArray());
            case "TryParse":
                return Call(n.Method(EnumOptionalMembers.TryParse, "TryParse"),
                    Enumerable.Range(0, args.Count - skip).ToArray());
            case "IsDefined":
                return Call(n.Method(EnumOptionalMembers.IsDefinedValue, "IsDefined"), 0);
            case "Format":
                return Call(n.Method(EnumOptionalMembers.Format, "Format"), 0, 1);
            case "ToObject":
                return Call(n.Method(EnumOptionalMembers.ToObject, "ToObject"), 0);
            case "GetUnderlyingType":
                return Annotate(ParseExpression($"typeof({U(plan)})"));
            default:
                return null;
        }
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
        if (_model.GetSymbolInfo(node).Symbol is IPropertySymbol prop
            && prop.ContainingType.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
        {
            var inner = prop.ContainingType.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
            if (inner != null && _active.ContainsKey(inner))
            {
                var recv = Embed(visited.Expression);
                if (prop.Name == "HasValue")
                    return WithReplacementTrivia(node, Annotate(ParseExpression($"({recv} != null)")));
                if (prop.Name == "Value")
                    return WithReplacementTrivia(node, Annotate(ParseExpression($"({recv})")));
            }
        }
        return visited;
    }

    // ── 8) 插值格式槽 {e:G} → {e.ToString("G")} ──

    public override SyntaxNode? VisitInterpolation(InterpolationSyntax node)
    {
        var visited = (InterpolationSyntax)base.VisitInterpolation(node)!;
        if (node.FormatClause == null) return visited;
        var plan = PlanOf(_model.GetTypeInfo(node.Expression).Type);
        if (plan == null) return visited;

        var formatText = node.FormatClause.FormatStringToken.Text;
        return visited.WithExpression(Annotate(ParseExpression(
                $"{Embed(visited.Expression)}.ToString(" +
                $"\"{formatText.Replace("\"", "\\\"")}\")")))
            .WithFormatClause(null);
    }

    // ── 9) const 字段/局部降级（成员引用本身零改写） ──

    public override SyntaxNode? VisitArrayCreationExpression(ArrayCreationExpressionSyntax node)
    {
        // 仅一维、零初始化（无显式初始化器）的被改写 enum 数组需要处理：
        // 原生 new E[n] 的每个元素是零成员，类化引用类型后元素默认 null，
        // 首个读改写点（table[i] |= flag）即 NRE（UnicodeCharHelper 模式）。
        // 改写到合成类上的零填充工厂 E.__Cs2jNewArray(n)。
        var arrayType = _model.GetTypeInfo(node).Type as IArrayTypeSymbol;
        var plan = arrayType is { IsSZArray: true }
            ? PlanExact(arrayType.ElementType) : null;

        var visited = (ArrayCreationExpressionSyntax)base.VisitArrayCreationExpression(node)!;

        if (plan == null || visited.Initializer != null) return visited;
        if (visited.Type.RankSpecifiers.Count != 1) return visited;
        var rank = visited.Type.RankSpecifiers[0];
        if (rank.Sizes.Count != 1 || rank.Sizes[0] is OmittedArraySizeExpressionSyntax)
            return visited;

        return WithReplacementTrivia(node, Annotate(ParseExpression(
            $"{TypeName(plan)}.{plan.Names!.NewArray1D}({Embed(rank.Sizes[0])})")));
    }

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        var visited = (FieldDeclarationSyntax)base.VisitFieldDeclaration(node)!;
        if (node.Modifiers.Any(SyntaxKind.ConstKeyword))
        {
            if (!DeclarationReferencesActiveEnum(node.Declaration)) return visited;

            // const 自身类型是归一 enum（class 引用）→ const 非法，只能降级。
            if (PlanOf(_model.GetTypeInfo(node.Declaration.Type).Type) != null)
                return DowngradeConstField(visited);

            // 值类型 const 的初始化器整体是编译期常量（典型
            // const int TEST_PASS = (int)E.Passed;）→ 折叠成字面量并保留 const：
            // 它可能正被 case 标签/特性实参/默认参数等常量语境引用，降级成
            // static readonly 会让这些站点 CS9135/CS0182（ctestbase 形态）。
            if (FoldConstDeclarators(node.Declaration, visited.Declaration) is { } folded)
                return visited.WithDeclaration(folded)
                    .WithAdditionalAnnotations(EnumDeclarationSyntaxFacts.RewriteAnnotation);

            return DowngradeConstField(visited);
        }

        // Non-const field of a converted enum: a native enum is a value type,
        // so uninitialized fields start at the zero member; the class form is a
        // reference type whose default is null, which would NRE on the first
        // GetValue()/Equals call (Uri._flags pattern). Initialize missing
        // declarators with the DEFAULT zero instance. Struct containers are
        // left alone (field initializer legality depends on ctor chaining).
        var fieldPlan = PlanExact(_model.GetTypeInfo(node.Declaration.Type).Type);
        if (fieldPlan == null) return visited;
        if (node.Declaration.Variables.FirstOrDefault() is not { } first
            || _model.GetDeclaredSymbol(first) is not IFieldSymbol fieldSymbol
            || fieldSymbol.ContainingType.TypeKind != TypeKind.Class)
        {
            return visited;
        }

        var newDeclaration = WithDefaultInitializers(visited.Declaration, fieldPlan);
        return ReferenceEquals(newDeclaration, visited.Declaration)
            ? visited
            : visited.WithDeclaration(newDeclaration);
    }

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var visited = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
        var plan = PlanExact(_model.GetTypeInfo(node.Type).Type);
        if (plan == null || visited.Initializer != null || visited.ExpressionBody != null)
            return visited;
        if (_model.GetDeclaredSymbol(node) is not IPropertySymbol property
            || property.ContainingType.TypeKind != TypeKind.Class)
        {
            return visited;
        }
        // abstract/extern/partial-declaration 属性没有本类后备字段，初始化器非法
        // （CS8050）；它们的零值语义由派生类/实现侧的属性声明负责。
        if (node.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)
            || m.IsKind(SyntaxKind.ExternKeyword)
            || m.IsKind(SyntaxKind.PartialKeyword)))
        {
            return visited;
        }
        // Auto-properties only: each accessor has an empty body, so the
        // compiler-generated backing field zero-default would become null.
        if (visited.AccessorList is not { } accessorList
            || accessorList.Accessors.Any(a => a.Body != null || a.ExpressionBody != null))
        {
            return visited;
        }
        // 原属性没有初始化器：它的 SemicolonToken 是零宽 missing token，
        // WithInitializer 不会自动补真分号，直接挂 Initializer 会渲染成
        // “{ get; set; }\n= X” 且缺分号（CS1002）。把访问器闭合括号后的
        // 换行 trivia 搬到新分号上，给 = 前后留空格。
        var trailing = accessorList.GetTrailingTrivia();
        return visited
            .WithAccessorList(accessorList.WithTrailingTrivia(ElasticSpace))
            .WithInitializer(EqualsValueClause(
                Token(SyntaxKind.EqualsToken).WithTrailingTrivia(ElasticSpace),
                DefaultOf(plan)))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken)
                .WithTrailingTrivia(trailing));
    }

    private static SyntaxToken SpacedEqualsToken()
        => Token(SyntaxKind.EqualsToken)
            .WithLeadingTrivia(ElasticSpace)
            .WithTrailingTrivia(ElasticSpace);

    /// <summary>
    /// Adds <c>= E.DEFAULT</c> to every declarator without an initializer.
    /// Nullable enum declarators never reach here (PlanExact excludes them;
    /// null is their faithful class-form default).
    /// </summary>
    private VariableDeclarationSyntax WithDefaultInitializers(
        VariableDeclarationSyntax declaration, EnumDeclarationPlan plan)
    {
        var missing = declaration.Variables
            .Where(v => v.Initializer == null).ToList();
        if (missing.Count == 0) return declaration;

        return declaration.ReplaceNodes(missing, (variable, _) =>
            variable.WithInitializer(EqualsValueClause(
                SpacedEqualsToken(), DefaultOf(plan))));
    }

    private static FieldDeclarationSyntax DowngradeConstField(FieldDeclarationSyntax visited)
    {
        var modifiers = visited.Modifiers
            .Where(m => !m.IsKind(SyntaxKind.ConstKeyword))
            .ToList();
        if (!modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            modifiers.Add(Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(ElasticSpace));
        if (!modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
            modifiers.Add(Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(ElasticSpace));
        return visited.WithModifiers(TokenList(modifiers))
            .WithAdditionalAnnotations(EnumDeclarationSyntaxFacts.RewriteAnnotation);
    }

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        var visited = (LocalDeclarationStatementSyntax)base.VisitLocalDeclarationStatement(node)!;
        if (!node.Modifiers.Any(SyntaxKind.ConstKeyword))
        {
            // A local of a converted enum declared without an initializer is
            // legally read after being assigned through ref/out (struct
            // definite-assignment rules relied on the zero value); class form
            // leaves it null instead. Seed it with the DEFAULT zero instance.
            var localPlan = PlanExact(_model.GetTypeInfo(node.Declaration.Type).Type);
            if (localPlan == null) return visited;
            var newDeclaration = WithDefaultInitializers(visited.Declaration, localPlan);
            return ReferenceEquals(newDeclaration, visited.Declaration)
                ? visited
                : visited.WithDeclaration(newDeclaration);
        }
        if (!DeclarationReferencesActiveEnum(node.Declaration)) return visited;

        if (PlanOf(_model.GetTypeInfo(node.Declaration.Type).Type) != null
            || FoldConstDeclarators(node.Declaration, visited.Declaration) is not { } foldedLocal)
        {
            var modifiers = visited.Modifiers
                .Where(m => !m.IsKind(SyntaxKind.ConstKeyword));
            return visited.WithModifiers(TokenList(modifiers))
                .WithAdditionalAnnotations(EnumDeclarationSyntaxFacts.RewriteAnnotation);
        }

        return visited.WithDeclaration(foldedLocal)
            .WithAdditionalAnnotations(EnumDeclarationSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>
    /// const 声明的每个初始化器在原模型下都有编译期常量值时，把（已被子重写器
    /// 访问过的）初始化表达式整体替换为字面量，使声明在引用 enum 被归一为 class
    /// 后仍可保持 const。任一 declarator 无法折叠返回 null（调用方走降级）。
    /// </summary>
    private VariableDeclarationSyntax? FoldConstDeclarators(
        VariableDeclarationSyntax original, VariableDeclarationSyntax visited)
    {
        var type = _model.GetTypeInfo(original.Type).Type;
        var literals = new string[original.Variables.Count];
        for (var i = 0; i < original.Variables.Count; i++)
        {
            var init = original.Variables[i].Initializer;
            if (init == null) return null;
            var constant = _model.GetConstantValue(init.Value);
            if (!TryRenderConstLiteral(type, constant, out literals[i]!))
                return null;
        }

        var byVisited = new Dictionary<VariableDeclaratorSyntax, string>(
            visited.Variables.Zip(literals, (v, lit) =>
                new KeyValuePair<VariableDeclaratorSyntax, string>(v, lit)));
        return visited.ReplaceNodes(visited.Variables, (v, _) =>
            v.WithInitializer(v.Initializer!.WithValue(
                Annotate(ParseExpression(byVisited[v])))));
    }

    /// <summary>
    /// 把编译期常量值渲染为与声明类型匹配的 const 字面量文本：整数（含普通 enum
    /// 的成员类型，渲染为带 cast 的常量）、bool、char。
    /// </summary>
    private static bool TryRenderConstLiteral(
        ITypeSymbol? type, Optional<object?> constant, out string text)
    {
        text = "";
        if (type is not INamedTypeSymbol nt || !constant.HasValue || constant.Value == null)
            return false;

        if (EnumDeclarationSyntaxFacts.IntegerSpecialTypes.Contains(nt.SpecialType))
        {
            text = EnumDeclarationSyntaxFacts.RenderIntegerLiteral(constant.Value, nt.SpecialType);
            return true;
        }
        if (nt.SpecialType == SpecialType.System_Boolean)
        {
            text = constant.Value is true ? "true" : "false";
            return true;
        }
        if (nt.SpecialType == SpecialType.System_Char && constant.Value is char c)
        {
            text = SymbolDisplay.FormatLiteral(c, quote: true);
            return true;
        }
        if (nt.TypeKind == TypeKind.Enum && nt.EnumUnderlyingType is { } enumBacking)
        {
            var number = EnumDeclarationSyntaxFacts.RenderIntegerLiteral(
                constant.Value, enumBacking.SpecialType);
            text = $"({nt.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){number}";
            return true;
        }
        return false;
    }

    private bool DeclarationReferencesActiveEnum(VariableDeclarationSyntax declaration)
    {
        // A const whose OWN type is a converted enum must be downgraded even
        // when the initializer is just a literal cast (e.g.
        // const E Sentinel = (E)0x7FFF;): once E becomes a class, the const
        // modifier is illegal regardless of the initializer shape.
        if (PlanOf(_model.GetTypeInfo(declaration.Type).Type) != null)
            return true;

        foreach (var id in declaration.DescendantNodes().OfType<ExpressionSyntax>())
        {
            if (_model.GetSymbolInfo(id).Symbol is IFieldSymbol f
                && f.HasConstantValue && _active.ContainsKey(f.ContainingType))
            {
                return true;
            }
        }
        return false;
    }
}
