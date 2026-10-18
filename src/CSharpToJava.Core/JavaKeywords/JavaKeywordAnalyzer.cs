using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.JavaKeywords;

/// <summary>
/// Phase 1：遍历编译中全部源内声明符号，构建改名表（含碰撞消歧），
/// 并枚举 operator true/false 布尔站点。
/// </summary>
internal sealed class JavaKeywordAnalyzer
{
    private readonly JavaKeywordOptions _options;

    public JavaKeywordAnalyzer(JavaKeywordOptions options)
    {
        _options = options;
    }

    public JavaKeywordAnalysis Analyze(Compilation compilation)
    {
        var analysis = new JavaKeywordAnalysis();

        // Phase 1a: 收集所有需要改名的源内符号
        var candidates = new List<(ISymbol Symbol, string NewName)>();
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var root = syntaxTree.GetRoot();
            var model = compilation.GetSemanticModel(syntaxTree);
            CollectRenameCandidates(root, model, candidates);
        }

        // Phase 1b: 碰撞消歧，构建改名表
        BuildRenameTable(candidates, analysis);

        // Phase 1c: operator true/false 站点分析
        AnalyzeOperatorTrueFalse(compilation, analysis);

        return analysis;
    }

    private static void CollectRenameCandidates(SyntaxNode root, SemanticModel model,
        List<(ISymbol Symbol, string NewName)> candidates)
    {
        // 遍历所有声明节点，提取符号
        foreach (var node in root.DescendantNodes())
        {
            ISymbol? symbol = null;
            string? name = null;

            switch (node)
            {
                case MethodDeclarationSyntax m:
                {
                    symbol = model.GetDeclaredSymbol(m);
                    name = m.Identifier.Text;
                    // For partial methods, only collect the definition part
                    // (IsPartialDefinition == true). Implementation parts are
                    // skipped because: (a) collecting them creates a second
                    // rename candidate that collision-disambiguates to a
                    // different name (ThrowsValue2 vs ThrowsValue), and
                    // (b) Roslyn only links the first implementation to the
                    // definition via PartialImplementationPart — a second
                    // implementation file's PartialDefinitionPart is null,
                    // so the old check (PartialDefinitionPart != null) missed
                    // it. The rewriter's TryLookupRename fallback resolves
                    // all parts from the definition part's rename table entry.
                    if (symbol is IMethodSymbol method
                        && !method.IsPartialDefinition)
                        continue;
                    break;
                }
                case PropertyDeclarationSyntax p:
                    symbol = model.GetDeclaredSymbol(p);
                    name = p.Identifier.Text;
                    break;
                case FieldDeclarationSyntax:
                    // VariableDeclaratorSyntax case below will handle each variable.
                    continue;
                case EventDeclarationSyntax e:
                    symbol = model.GetDeclaredSymbol(e);
                    name = e.Identifier.Text;
                    break;
                case EventFieldDeclarationSyntax:
                    // VariableDeclaratorSyntax case below will handle each variable.
                    continue;
                case ClassDeclarationSyntax c:
                    symbol = model.GetDeclaredSymbol(c);
                    name = c.Identifier.Text;
                    break;
                case StructDeclarationSyntax s:
                    symbol = model.GetDeclaredSymbol(s);
                    name = s.Identifier.Text;
                    break;
                case InterfaceDeclarationSyntax i:
                    symbol = model.GetDeclaredSymbol(i);
                    name = i.Identifier.Text;
                    break;
                case EnumDeclarationSyntax e:
                    symbol = model.GetDeclaredSymbol(e);
                    name = e.Identifier.Text;
                    break;
                case DelegateDeclarationSyntax d:
                    symbol = model.GetDeclaredSymbol(d);
                    name = d.Identifier.Text;
                    break;
                case RecordDeclarationSyntax r:
                    symbol = model.GetDeclaredSymbol(r);
                    name = r.Identifier.Text;
                    break;
                case TypeParameterSyntax t:
                    symbol = model.GetDeclaredSymbol(t);
                    name = t.Identifier.Text;
                    break;
                case NamespaceDeclarationSyntax ns:
                    symbol = model.GetDeclaredSymbol(ns);
                    name = ns.Name.ToString();
                    break;
                case FileScopedNamespaceDeclarationSyntax fsns:
                    symbol = model.GetDeclaredSymbol(fsns);
                    name = fsns.Name.ToString();
                    break;
                case UsingDirectiveSyntax u when u.Alias != null:
                    symbol = model.GetDeclaredSymbol(u.Alias);
                    name = u.Alias.Name.Identifier.Text;
                    break;
                case LocalFunctionStatementSyntax lf:
                    symbol = model.GetDeclaredSymbol(lf);
                    name = lf.Identifier.Text;
                    break;
                case VariableDeclaratorSyntax vd:
                    symbol = model.GetDeclaredSymbol(vd);
                    name = vd.Identifier.Text;
                    break;
                case SingleVariableDesignationSyntax svd:
                    symbol = model.GetDeclaredSymbol(svd);
                    name = svd.Identifier.Text;
                    break;
                case ParameterSyntax p:
                    symbol = model.GetDeclaredSymbol(p);
                    name = p.Identifier.Text;
                    break;
                case ForEachStatementSyntax fe:
                    symbol = model.GetDeclaredSymbol(fe);
                    name = fe.Identifier.Text;
                    break;
                case CatchDeclarationSyntax cd:
                    symbol = model.GetDeclaredSymbol(cd);
                    name = cd.Identifier.Text;
                    break;
            }

            if (symbol != null && name != null && JavaKeywordSyntaxFacts.NeedsEscape(name))
            {
                // 跳过隐式声明的符号（如自动属性后备字段）
                if (symbol.IsImplicitlyDeclared) continue;
                // 跳过丢弃模式 _ 无符号的情况
                // 跳过丢弃模式 _ （Roslyn 4.12 ILocalSymbol 无 IsDiscard 属性，用 name 判断即可）
                candidates.Add((symbol, JavaKeywordSyntaxFacts.NewName(name)));
            }
        }
    }

    /// <summary>
    /// JK-4b：碰撞消歧。同一声明空间内目标名被占用时追加序号 Value2/Value3/…
    /// 消歧顺序按符号的源位置（文件路径、span）排序，保证确定性。
    /// </summary>
    private static void BuildRenameTable(
        List<(ISymbol Symbol, string NewName)> candidates,
        JavaKeywordAnalysis analysis)
    {
        // 按源位置排序，保证确定性
        var sorted = candidates
            .OrderBy(c => GetSourceLocationSortKey(c.Symbol))
            .ToList();

        // 追踪每个声明空间已占用名字
        var spaceOccupied = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (symbol, baseName) in sorted)
        {
            var spaceKey = GetDeclarationSpaceKey(symbol);
            if (!spaceOccupied.TryGetValue(spaceKey, out var occupied))
            {
                occupied = new HashSet<string>(StringComparer.Ordinal);
                spaceOccupied[spaceKey] = occupied;
            }

            // 先把声明空间中既有名字填入（来自 GetMembers 等）
            PopulateExistingNames(symbol, occupied);

            var newName = baseName;
            var suffix = 0;
            while (occupied.Contains(newName))
            {
                suffix++;
                newName = JavaKeywordSyntaxFacts.CollisionSuffix(baseName, suffix);
            }

            occupied.Add(newName);
            analysis.RenameTable[symbol] = newName;
        }

        // Synchronize partial method parts: the definition part was
        // collected (implementation part was skipped), but the rewriter
        // calls GetDeclaredSymbol on each file, which returns the part
        // local to that file. Add the implementation part to the table
        // with the same name so TryGetNewNameForDeclaration succeeds in
        // implementation-declaration files too.
        SyncPartialMethodParts(analysis.RenameTable);
    }

    /// <summary>
    /// For each partial method in the rename table, ensure both the
    /// definition part and the implementation part share the same new
    /// name. The analyzer only collected the definition part; the
    /// rewriter needs both parts in the table to rename declarations
    /// across all partial files.
    /// </summary>
    private static void SyncPartialMethodParts(
        Dictionary<ISymbol, string> renameTable)
    {
        var additions = new List<(ISymbol Symbol, string NewName)>();
        foreach (var (symbol, newName) in renameTable)
        {
            if (symbol is IMethodSymbol method)
            {
                // symbol is the definition part; sync the implementation part.
                if (method.PartialImplementationPart is { } impl
                    && !renameTable.ContainsKey(impl))
                {
                    additions.Add((impl, newName));
                }
            }
        }
        foreach (var (symbol, newName) in additions)
            renameTable[symbol] = newName;
    }

    private static string GetSourceLocationSortKey(ISymbol symbol)
    {
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc == null) return "\uFFFF";
        var span = loc.GetLineSpan();
        return $"{loc.SourceTree?.FilePath ?? ""}\t{span.StartLinePosition.Line}\t{span.StartLinePosition.Character}";
    }

    /// <summary>
    /// 声明空间键：用 ContainingSymbol 的唯一标识 + 空间种类。
    /// </summary>
    private static string GetDeclarationSpaceKey(ISymbol symbol)
    {
        return symbol switch
        {
            IParameterSymbol => "params_" + (symbol.ContainingSymbol?.ToDisplayString() ?? ""),
            ILocalSymbol => "locals_" + (symbol.ContainingSymbol?.ToDisplayString() ?? ""),
            IRangeVariableSymbol => "range_" + (symbol.ContainingSymbol?.ToDisplayString() ?? ""),
            _ => "members_" + (symbol.ContainingType?.ToDisplayString() ?? symbol.ContainingNamespace?.ToDisplayString() ?? ""),
        };
    }

    /// <summary>把声明空间中既有名字填入 occupied 集合。</summary>
    private static void PopulateExistingNames(ISymbol symbol, HashSet<string> occupied)
    {
        if (symbol.ContainingType is { } type)
        {
            foreach (var m in type.GetMembers())
                occupied.Add(m.Name);
        }
        else if (symbol.ContainingNamespace is { } ns)
        {
            foreach (var t in ns.GetTypeMembers())
                occupied.Add(t.Name);
        }
    }

    /// <summary>
    /// 分析 operator true/false 声明，收集布尔站点，并执行 JK-8 闸门判定。
    /// </summary>
    private void AnalyzeOperatorTrueFalse(Compilation compilation, JavaKeywordAnalysis analysis)
    {
        // Phase 1: 收集所有声明了 op_True+op_False 的类型
        var operatorTypeMap = new Dictionary<INamedTypeSymbol, OperatorTypeInfo>(
            SymbolEqualityComparer.Default);

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var root = syntaxTree.GetRoot();
            var model = compilation.GetSemanticModel(syntaxTree);

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var typeSymbol = model.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                if (typeSymbol == null) continue;

                IMethodSymbol? opTrue = null, opFalse = null;
                foreach (var member in typeSymbol.GetMembers())
                {
                    if (member is IMethodSymbol method)
                    {
                        if (method.Name == "op_True") opTrue = method;
                        if (method.Name == "op_False") opFalse = method;
                    }
                }

                if (opTrue == null || opFalse == null) continue;

                // JK-5: 检查 IsTrue/IsFalse 名字碰撞
                var hasIsTrue = typeSymbol.GetMembers("IsTrue").Any();
                var hasIsFalse = typeSymbol.GetMembers("IsFalse").Any();
                if (hasIsTrue || hasIsFalse)
                {
                    var opInfo = new OperatorTypeInfo
                    {
                        Type = typeSymbol,
                        OpTrue = opTrue,
                        OpFalse = opFalse,
                        Infeasible = true,
                        InfeasibleReason = "IsTrue/IsFalse name collision with existing member (JK-5/JK-8)",
                    };
                    analysis.OperatorTypes.Add(opInfo);
                    operatorTypeMap[typeSymbol] = opInfo;
                    continue;
                }

                var info = new OperatorTypeInfo
                {
                    Type = typeSymbol,
                    OpTrue = opTrue,
                    OpFalse = opFalse,
                };
                analysis.OperatorTypes.Add(info);
                operatorTypeMap[typeSymbol] = info;
            }
        }

        if (operatorTypeMap.Count == 0) return;

        // Phase 2: 扫描全部语法树中的布尔站点
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var root = syntaxTree.GetRoot();
            var model = compilation.GetSemanticModel(syntaxTree);
            CollectBooleanSites(root, model, operatorTypeMap, compilation);
        }

        // Phase 3: JK-8 闸门——检查站点不可行性
        foreach (var opInfo in analysis.OperatorTypes)
        {
            if (opInfo.Infeasible) continue;
            foreach (var site in opInfo.Sites)
            {
                if (site.InExpressionTree && !_options.ForceQualifiedNames.Contains(
                        opInfo.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
                {
                    opInfo.Infeasible = true;
                    opInfo.InfeasibleReason =
                        "Boolean site inside expression tree lambda (JK-8); use overrides.force to override";
                    break;
                }
                if (site.IsDynamic)
                {
                    opInfo.Infeasible = true;
                    opInfo.InfeasibleReason = "Dynamic binding site (JK-8)";
                    break;
                }
                if (site.NeedsHoistButCannot)
                {
                    opInfo.Infeasible = true;
                    opInfo.InfeasibleReason = "Cannot hoist temporary variable in expression context (JK-8)";
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 扫描语法树中的布尔站点：if/while/do/for (JK-6a)、conditional (JK-6b)、
    /// when (JK-6c)、&&/|| (JK-6d/e)。
    /// </summary>
    private static void CollectBooleanSites(
        SyntaxNode root,
        SemanticModel model,
        Dictionary<INamedTypeSymbol, OperatorTypeInfo> operatorTypeMap,
        Compilation compilation)
    {
        foreach (var node in root.DescendantNodes())
        {
            ExpressionSyntax? condition = null;
            BooleanSiteKind kind = default;

            switch (node)
            {
                case IfStatementSyntax ifStmt:
                    condition = ifStmt.Condition;
                    kind = BooleanSiteKind.IfWhileDoFor;
                    break;
                case WhileStatementSyntax whileStmt:
                    condition = whileStmt.Condition;
                    kind = BooleanSiteKind.IfWhileDoFor;
                    break;
                case DoStatementSyntax doStmt:
                    condition = doStmt.Condition;
                    kind = BooleanSiteKind.IfWhileDoFor;
                    break;
                case ForStatementSyntax forStmt:
                    if (forStmt.Condition != null)
                    {
                        condition = forStmt.Condition;
                        kind = BooleanSiteKind.IfWhileDoFor;
                    }
                    break;
                case ConditionalExpressionSyntax cond:
                    condition = cond.Condition;
                    kind = BooleanSiteKind.Conditional;
                    break;
                case WhenClauseSyntax whenClause:
                    // when 子句的 Condition 可能是 null（模式匹配），非 null 时处理
                    condition = whenClause.Condition;
                    kind = BooleanSiteKind.WhenGuard;
                    break;
                case BinaryExpressionSyntax binary
                    when binary.IsKind(SyntaxKind.LogicalAndExpression)
                         || binary.IsKind(SyntaxKind.LogicalOrExpression):
                    // JK-6d/e: && / ||
                    // 标注为后续迭代——只收集但不在此改写
                    // 仍然需要收集以检查 JK-8
                    condition = binary;
                    kind = binary.IsKind(SyntaxKind.LogicalAndExpression)
                        ? BooleanSiteKind.AndAlso
                        : BooleanSiteKind.OrElse;
                    break;
            }

            if (condition == null) continue;

            // 检查条件表达式是否绑定了 op_True
            var opType = TryGetOperatorType(model, condition, operatorTypeMap);
            if (opType == null) continue;

            // 跳过已在列表中的重复站点（同一节点同一类型）
            if (opType.Sites.Any(s => s.Node == node)) continue;

            var site = new BooleanSiteInfo
            {
                Location = node.GetLocation(),
                FilePath = node.SyntaxTree.FilePath,
                Node = node,
                Kind = kind,
                OperatorType = opType.Type,
            };

            // JK-8: 检查是否在表达式树 lambda 内
            CheckBooleanSite(site, node, model, compilation);

            opType.Sites.Add(site);
        }
    }

    /// <summary>
    /// 用 SemanticModel.GetConversion 判断条件表达式到 bool 的转换是否
    /// 绑定了用户定义的 op_True。
    /// </summary>
    private static OperatorTypeInfo? TryGetOperatorType(
        SemanticModel model,
        ExpressionSyntax condition,
        Dictionary<INamedTypeSymbol, OperatorTypeInfo> operatorTypeMap)
    {
        // 获取条件表达式到 bool 的隐式转换
        var conversion = model.GetConversion(condition);

        // 如果是用户定义转换，检查绑定方法
        if (conversion.IsUserDefined)
        {
            var method = conversion.MethodSymbol;
            if (method != null && method.Name == "op_True"
                && operatorTypeMap.TryGetValue(method.ContainingType, out var opInfo))
            {
                return opInfo;
            }
        }

        // 对于 && / ||，条件本身不是直接 bool 转换，
        // 但运算元的类型可能有 op_True。检查运算元类型。
        if (condition is BinaryExpressionSyntax binary
            && (binary.IsKind(SyntaxKind.LogicalAndExpression)
                || binary.IsKind(SyntaxKind.LogicalOrExpression)))
        {
            // 检查左右运算元类型
            foreach (var operand in new[] { binary.Left, binary.Right })
            {
                var operandType = model.GetTypeInfo(operand).Type as INamedTypeSymbol;
                if (operandType != null
                    && operatorTypeMap.TryGetValue(operandType, out var opInfo))
                {
                    return opInfo;
                }
            }
        }

        // 直接检查条件表达式的类型是否匹配运算符类型
        var condType = model.GetTypeInfo(condition).Type as INamedTypeSymbol;
        if (condType != null
            && operatorTypeMap.TryGetValue(condType, out var opInfo2))
        {
            return opInfo2;
        }

        // 检查 ConvertedType
        var convertedType = model.GetTypeInfo(condition).ConvertedType as INamedTypeSymbol;
        if (convertedType != null
            && operatorTypeMap.TryGetValue(convertedType, out var opInfo3))
        {
            return opInfo3;
        }

        return null;
    }

    /// <summary>
    /// JK-8 闸门检查：表达式树、dynamic、不可提升。
    /// </summary>
    private static void CheckBooleanSite(
        BooleanSiteInfo site,
        SyntaxNode node,
        SemanticModel model,
        Compilation compilation)
    {
        // 检查是否在表达式树 lambda 内
        var ancestors = node.Ancestors();
        foreach (var ancestor in ancestors)
        {
            if (ancestor is LambdaExpressionSyntax lambda)
            {
                if (JavaKeywordSyntaxFacts.IsExpressionTreeLambda(model, lambda))
                {
                    site.InExpressionTree = true;
                }
                break; // 只检查最近的 lambda
            }
        }

        // 检查是否为 dynamic 绑定
        var condition = site.Node switch
        {
            IfStatementSyntax i => i.Condition,
            WhileStatementSyntax w => w.Condition,
            DoStatementSyntax d => d.Condition,
            ForStatementSyntax f => f.Condition,
            ConditionalExpressionSyntax c => c.Condition,
            WhenClauseSyntax wc => wc.Condition,
            BinaryExpressionSyntax b => b,
            _ => null,
        };

        if (condition != null)
        {
            var typeInfo = model.GetTypeInfo(condition);
            if (typeInfo.Type?.TypeKind == TypeKind.Dynamic)
            {
                site.IsDynamic = true;
            }
        }

        // 检查是否需要提升临时变量但不可插入语句
        // （字段初始化器、特性实参等上下文）
        if (site.Kind is BooleanSiteKind.AndAlso or BooleanSiteKind.OrElse)
        {
            // && / || 可能需要临时变量提升
            // 检查是否在不可插入语句的上下文中
            var context = node.Ancestors().FirstOrDefault(a =>
                a is FieldDeclarationSyntax
                || a is AttributeArgumentSyntax
                || a is ConstructorInitializerSyntax
                || a is ArrowExpressionClauseSyntax);
            if (context != null
                && !JavaKeywordSyntaxFacts.IsSimpleExpression(
                    ((BinaryExpressionSyntax)node).Left))
            {
                site.NeedsHoistButCannot = true;
            }
        }
    }
}
