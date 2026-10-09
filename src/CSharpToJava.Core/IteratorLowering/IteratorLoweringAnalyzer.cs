using CSharpToJava.Core.IteratorLowering.ControlFlow;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.IteratorLowering;

/// <summary>
/// Discovers iterator members in a compilation, validates them against the
/// M2 support matrix, and produces a <see cref="MethodLoweringInfo"/> for each
/// lowerable member. Unsupported forms get a skip diagnostic.
/// </summary>
internal static class IteratorLoweringAnalyzer
{
    public static AnalyzerResult Analyze(SemanticModel semanticModel, IteratorLoweringOptions options)
    {
        var infos = new List<MethodLoweringInfo>();
        var diagnostics = new List<IteratorLoweringDiagnostic>();
        var skipMemberKeys = new HashSet<string>(StringComparer.Ordinal);

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

        var memberIndex = 0;

        foreach (var method in methods)
        {
            var symbol = semanticModel.GetDeclaredSymbol(method);
            if (symbol == null) continue;

            // Must have a body that itself owns yield statements. Yields inside
            // nested routines (local functions/lambdas) do NOT make the outer
            // method an iterator — e.g. a regular method may return the result
            // of invoking an iterator local function.
            if (method.Body == null) continue;
            if (!MethodOwnsYield(method.Body))
                continue;

            // Check return type: IEnumerable<T>, IEnumerator<T>, IEnumerable, IEnumerator
            var elementType = ResolveElementType(symbol.ReturnType);
            if (elementType == null)
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0401,
                    $"Cannot resolve iterator element type for '{method.Identifier.Text}'.");
                skipMemberKeys.Add(GetMemberKey(method));
                continue;
            }

            // Legality checks (M2: try-finally with yield is now supported)
            if (!CheckLegality(method, semanticModel, diagnostics))
            {
                skipMemberKeys.Add(GetMemberKey(method));
                continue;
            }

            // Collect existing nested type names for collision avoidance
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            var declaringType = method.Parent as TypeDeclarationSyntax;
            if (declaringType != null)
            {
                foreach (var nested in declaringType.Members.OfType<BaseTypeDeclarationSyntax>())
                {
                    usedNames.Add(nested.Identifier.ValueText);
                }
            }

            var className = IteratorLoweringNaming.GetStateMachineClassName(
                method.Identifier.ValueText, memberIndex, usedNames);

            var info = IteratorBodyAnalysis.Analyze(method, symbol, semanticModel, className);
            infos.Add(info);

            memberIndex++;
        }

        return new AnalyzerResult(
            infos.Select(i => i.Plan).ToList(),
            infos,
            diagnostics,
            skipMemberKeys);
    }

    private static string? ResolveElementType(ITypeSymbol returnType)
    {
        if (returnType is not INamedTypeSymbol named) return null;

        return named.OriginalDefinition.ToDisplayString() switch
        {
            "System.Collections.Generic.IEnumerable<T>" => named.TypeArguments[0].ToDisplayString(),
            "System.Collections.Generic.IEnumerator<T>" => named.TypeArguments[0].ToDisplayString(),
            "System.Collections.IEnumerable" => "object",
            "System.Collections.IEnumerator" => "object",
            _ => null
        };
    }

    private static bool CheckLegality(
        MethodDeclarationSyntax method,
        SemanticModel semanticModel,
        List<IteratorLoweringDiagnostic> diagnostics)
    {
        var body = method.Body!;
        var ok = true;

        // ref/in/out params -> CS2J0402 (CS1623 mirror)
        foreach (var param in method.ParameterList.Parameters)
        {
            if (param.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)
                || m.IsKind(SyntaxKind.InKeyword)
                || m.IsKind(SyntaxKind.OutKeyword)))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has ref/in/out parameter '{param.Identifier.Text}' (CS1623 mirror).");
                ok = false;
            }
        }

        foreach (var tryStmt in body.DescendantNodes().OfType<TryStatementSyntax>())
        {
            // try-with-catch: yield anywhere in the try block or its catches is
            // illegal C# (CS1626/CS1631). Defensive mirror.
            if (tryStmt.Catches.Count > 0 && ContainsYield(tryStmt.Block)
                || tryStmt.Catches.Any(c => ContainsYield(c)))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside try-with-catch (CS1626/CS1631 mirror).");
                ok = false;
            }
            // yield inside finally -> CS1625 mirror
            if (tryStmt.Finally != null && ContainsYield(tryStmt.Finally))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside finally (CS1625 mirror).");
                ok = false;
            }
            // try-finally (no catch) with yield in the try block: M2 supported.
        }

        // M3: using statements/declarations and lock statements are normalized
        // to try/finally by IteratorResourceNormalizer before analysis. Anything
        // left here is a form the normalizer deliberately keeps:
        //   - await using -> belongs to C38 async streams (CS2J0402)
        //   - ref struct resource -> cannot cross a yield boundary (CS2J0403)
        //   - a residual lock statement is a defensive CS2J0402 (should be unreachable)
        foreach (var usingStmt in body.DescendantNodes().OfType<UsingStatementSyntax>())
        {
            if (!ContainsYield(usingStmt)) continue;
            if (usingStmt.AwaitKeyword.RawKind != 0)
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has await using (owned by async streams, C38).");
                ok = false;
            }
            else if (UsingResourceIsRefLike(usingStmt, semanticModel))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0403,
                    $"Iterator method '{method.Identifier.Text}' has a ref struct using resource that cannot be hoisted across a yield boundary.");
                ok = false;
            }
            else
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has a residual using statement after normalization.");
                ok = false;
            }
        }

        foreach (var usingDecl in body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (usingDecl.UsingKeyword.RawKind == 0 || !ContainsYield(usingDecl)) continue;
            if (usingDecl.AwaitKeyword.RawKind != 0)
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has an await using declaration (owned by async streams, C38).");
                ok = false;
            }
            else if (usingDecl.Declaration.Variables
                .Select(v => semanticModel.GetDeclaredSymbol(v) is ILocalSymbol local ? local.Type : null)
                .Any(t => t is { IsRefLikeType: true }))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0403,
                    $"Iterator method '{method.Identifier.Text}' has a ref struct using declaration that cannot be hoisted across a yield boundary.");
                ok = false;
            }
            else
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has a residual using declaration after normalization.");
                ok = false;
            }
        }

        foreach (var lockStmt in body.DescendantNodes().OfType<LockStatementSyntax>())
        {
            if (ContainsYield(lockStmt))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has a residual lock statement after normalization.");
                ok = false;
            }
        }

        // Deconstruction foreach (foreach (var (a,b) in ...)) containing yield
        foreach (var feVar in body.DescendantNodes().OfType<ForEachVariableStatementSyntax>())
        {
            if (ContainsYield(feVar))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside a deconstruction foreach (M2 unsupported).");
                ok = false;
            }
        }

        // foreach over a ref-struct enumerator with yield -> CS4007 mirror
        foreach (var fe in body.DescendantNodes().OfType<ForEachStatementSyntax>())
        {
            if (!ContainsYield(fe)) continue;
            var feInfo = semanticModel.GetForEachStatementInfo(fe);
            var enumType = feInfo.GetEnumeratorMethod?.ReturnType;
            if (enumType is { IsRefLikeType: true })
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0403,
                    $"Iterator method '{method.Identifier.Text}': foreach enumerator '{enumType.Name}' is a ref struct and cannot be hoisted across a yield boundary (CS4007 mirror).");
                ok = false;
            }
        }

        // checked/unchecked 块内含 yield -> 展平会丢失 checked 算术上下文
        // （M4 盘点决策：保持拒绝；恢复需在每条搬运表达式外包 checked 表达式）。
        foreach (var node in body.DescendantNodes().OfType<CheckedStatementSyntax>())
        {
            if (ContainsYield(node))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside a checked/unchecked block (checked context cannot survive state-machine flattening).");
                ok = false;
            }
        }

        // M4 实测矩阵（Roslyn net10）：unsafe 块内 yield 为 CS9238、fixed 块内
        // yield 同族（fixed 必须处于 unsafe 上下文）。镜像为 CS2J0403；不含
        // yield 的 unsafe/fixed 块原样透传（块内指针局部按受限局部规则保留）。
        foreach (var node in body.DescendantNodes()
                     .Where(n => n.IsKind(SyntaxKind.UnsafeStatement)
                         || n.IsKind(SyntaxKind.FixedStatement)))
        {
            if (ContainsYield(node))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0403,
                    $"Iterator method '{method.Identifier.Text}' has yield inside an unsafe/fixed block (CS9238 mirror: unsafe contexts and pinned pointers cannot cross a yield boundary).");
                ok = false;
            }
        }

        // M4：ref struct/ref local/指针局部跨 yield 边界 -> CS2J0403
        // （CS4007/CS9217 镜像）。不跨边界的受限局部合法，analyzer 放行并由
        // IteratorBodyAnalysis 排除提升、rewriter 保留为 MoveNext 真局部。
        foreach (var restricted in IteratorLocalEscaping.FindRestrictedLocals(method, semanticModel))
        {
            if (IteratorLocalEscaping.EscapesAcrossYield(restricted, method, semanticModel))
            {
                var kindText = restricted.Kind switch
                {
                    IteratorLocalEscaping.RestrictedKind.RefStruct => "ref struct",
                    IteratorLocalEscaping.RestrictedKind.RefLocal => "ref local",
                    IteratorLocalEscaping.RestrictedKind.Pointer => "pointer local",
                    _ => "restricted",
                };
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0403,
                    $"Iterator method '{method.Identifier.Text}': {kindText} local '{restricted.Symbol.Name}' is alive across a yield boundary (CS4007/CS9217 mirror).");
                ok = false;
            }
        }

        // M4：对局部/参数取址（&x）在迭代器方法体内无条件非法（CS9239），
        // 与是否跨 yield 无关——状态机化后局部没有稳定地址。
        foreach (var addressOf in body.DescendantNodes()
                     .OfType<PrefixUnaryExpressionSyntax>()
                     .Where(e => e.IsKind(SyntaxKind.AddressOfExpression)
                         && e.Operand is IdentifierNameSyntax))
        {
            // 嵌套局部函数/lambda 内的取址归该嵌套成员（局部函数残留已 CS2J0404、
            // 残留 lambda 已 CS2J0405 拦截），不重复计入外层方法。
            if (addressOf.FirstAncestorOrSelf<SyntaxNode>(
                    n => n is LocalFunctionStatementSyntax
                        or LambdaExpressionSyntax
                        or AnonymousMethodExpressionSyntax) != null)
            {
                continue;
            }
            var target = semanticModel.GetSymbolInfo(((IdentifierNameSyntax)addressOf.Operand)).Symbol;
            if (target is ILocalSymbol or IParameterSymbol)
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0403,
                    $"Iterator method '{method.Identifier.Text}': the address-of operator cannot be applied to local/parameter '{target.Name}' (CS9239 mirror).");
                ok = false;
            }
        }

        // M4：closure-lift 已在本 pass 之前运行，正常情况下迭代器体内不会残留
        // lambda/匿名方法；残留意味着 closure-lift 主动 skip 了该形态，而其捕获
        // 契约未与状态机提升做过等价验证，防御性 CS2J0405 拒绝。
        if (body.DescendantNodes().Any(n => n is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax))
        {
            AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0405,
                $"Iterator method '{method.Identifier.Text}' contains a residual lambda/anonymous method after closure-lift (capture-across-yield is unverified).");
            ok = false;
        }

        // M4b：迭代器内裸 `return;` 合法（C# 语义等价 yield break），由
        // IteratorLoweringPreprocessor 的 BareReturnToYieldBreakRewriter
        // 在分析前统一归一为 yield break，此处不再设闸。`return <expr>;`
        // 在迭代器块内本就是 CS1622 编译错误，不会进入预处理。

        // M4：含 yield 的 switch 中，case 标签绑定模式变量（case int x:）
        // v1 不支持——模式绑定局部未纳入提升表，展平后无法保证其跨 section
        // 的绑定语义。只检查标签本身：section 体内的 out var 等普通局部
        // 不在此列。无变量的常量/关系/属性模式与 when 过滤正常支持。
        foreach (var switchStmt in body.DescendantNodes().OfType<SwitchStatementSyntax>())
        {
            if (!ContainsYield(switchStmt)) continue;
            var bindsPatternVariable = switchStmt.Sections
                .SelectMany(s => s.Labels)
                .OfType<CasePatternSwitchLabelSyntax>()
                .SelectMany(l => l.DescendantNodes())
                .OfType<SingleVariableDesignationSyntax>()
                .Any(d => d.Identifier.Text != "_");
            if (bindsPatternVariable)
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has a yield inside a switch whose case label binds a pattern variable (M4 v1 supports variable-free labels only).");
                ok = false;
            }
        }

        // M4 盘点决策：用户 goto/label 与 yield 交织 -> CS2J0406 保持显式拒绝。
        // eliminate-goto 只负责消化状态机自身生成的 goto；用户 goto 跨 yield
        // 挂起点的活性/区域分析在 v1 不做，留待后续里程碑。GotoStatementSyntax
        // 按 Kind 同时覆盖 goto / goto case / goto default（转发 switch 不保留
        // case 跳转语义，同族拒绝）。
        if (body.DescendantNodes().OfType<GotoStatementSyntax>().Any()
            || body.DescendantNodes().OfType<LabeledStatementSyntax>().Any())
        {
            AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0406,
                $"Iterator method '{method.Identifier.Text}' has user goto/label (unsupported).");
            ok = false;
        }

        // M4：closure-lift 已前置运行，自带 yield 的迭代器局部函数会被提升为
        // 兄弟方法（CS2J0404 实质消解）。此处仍残留局部函数仅当 closure-lift
        // 主动 skip 了该形态，防御性 CS2J0404 拒绝。
        if (body.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Any())
        {
            AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0404,
                $"Iterator method '{method.Identifier.Text}' has a residual local function after closure-lift (unsupported form).");
            ok = false;
        }

        return ok;
    }

    private static bool ContainsYield(SyntaxNode node)
        => node.DescendantNodes().Any(n => n.IsKind(SyntaxKind.YieldReturnStatement)
            || n.IsKind(SyntaxKind.YieldBreakStatement));

    private static bool UsingResourceIsRefLike(UsingStatementSyntax usingStmt, SemanticModel semanticModel)
    {
        if (usingStmt.Declaration != null)
        {
            foreach (var variable in usingStmt.Declaration.Variables)
            {
                if (semanticModel.GetDeclaredSymbol(variable) is ILocalSymbol { Type.IsRefLikeType: true })
                    return true;
            }
            return false;
        }
        if (usingStmt.Expression != null)
        {
            var type = semanticModel.GetTypeInfo(usingStmt.Expression).ConvertedType
                ?? semanticModel.GetTypeInfo(usingStmt.Expression).Type;
            return type is { IsRefLikeType: true };
        }
        return false;
    }

    private static void AddSkipDiagnostic(
        List<IteratorLoweringDiagnostic> diagnostics,
        MethodDeclarationSyntax method,
        IteratorLoweringDiagnosticCode code,
        string message)
    {
        var lineSpan = method.GetLocation().GetLineSpan();
        diagnostics.Add(new IteratorLoweringDiagnostic(
            IteratorLoweringSeverity.Error,
            code,
            message,
            FilePath: null,
            Line: lineSpan.StartLinePosition.Line + 1));
    }

    private static string GetMemberKey(MethodDeclarationSyntax method)
        => method.Identifier.ValueText + "_" + method.SpanStart;

    /// <summary>
    /// Whether the method body itself (not any nested local function/lambda/
    /// anonymous method) contains a yield statement. Nested routines own their
    /// own yields and must not be attributed to the enclosing method.
    /// </summary>
    internal static bool MethodOwnsYield(BlockSyntax body)
        => body.DescendantNodes(n => n is not
                (LocalFunctionStatementSyntax
                or ParenthesizedLambdaExpressionSyntax
                or SimpleLambdaExpressionSyntax
                or AnonymousMethodExpressionSyntax))
            .Any(n => n.IsKind(SyntaxKind.YieldReturnStatement)
                || n.IsKind(SyntaxKind.YieldBreakStatement));
}

internal sealed record AnalyzerResult(
    IReadOnlyList<StateMachinePlan> Plans,
    IReadOnlyList<MethodLoweringInfo> Infos,
    IReadOnlyList<IteratorLoweringDiagnostic> Diagnostics,
    IReadOnlySet<string> SkipMemberKeys);
