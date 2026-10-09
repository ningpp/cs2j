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

            // Must have a body with yield statements
            if (method.Body == null) continue;
            if (!method.Body.DescendantNodes().Any(n => n.IsKind(SyntaxKind.YieldReturnStatement)
                || n.IsKind(SyntaxKind.YieldBreakStatement)))
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

        // checked/unchecked/fixed 块内含 yield -> M2 不支持（展平会丢失 checked 上下文）
        foreach (var node in body.DescendantNodes()
                     .Where(n => n.IsKind(SyntaxKind.CheckedStatement)
                         || n.IsKind(SyntaxKind.UnsafeStatement)
                         || n.IsKind(SyntaxKind.FixedStatement)))
        {
            if (ContainsYield(node))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside a(n) {node.Kind()} block (M2 unsupported).");
                ok = false;
            }
        }

        // switch section containing yield -> M2 不支持（M4 残余形态盘点再评估）
        foreach (var switchStmt in body.DescendantNodes().OfType<SwitchStatementSyntax>())
        {
            if (ContainsYield(switchStmt))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside a switch section (M2 unsupported).");
                ok = false;
            }
        }

        // 裸 return（迭代器内只允许 yield return / yield break）
        if (body.DescendantNodes().OfType<ReturnStatementSyntax>().Any())
        {
            AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                $"Iterator method '{method.Identifier.Text}' has a plain return statement (use yield break).");
            ok = false;
        }

        // User goto/label inside body -> CS2J0406
        if (body.DescendantNodes().OfType<GotoStatementSyntax>().Any()
            || body.DescendantNodes().OfType<LabeledStatementSyntax>().Any())
        {
            AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0406,
                $"Iterator method '{method.Identifier.Text}' has user goto/label (unsupported).");
            ok = false;
        }

        // Local function inside body -> CS2J0404 (should have been lifted by E02)
        if (body.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Any())
        {
            AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0404,
                $"Iterator method '{method.Identifier.Text}' has residual local function (should be lifted by E02).");
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
}

internal sealed record AnalyzerResult(
    IReadOnlyList<StateMachinePlan> Plans,
    IReadOnlyList<MethodLoweringInfo> Infos,
    IReadOnlyList<IteratorLoweringDiagnostic> Diagnostics,
    IReadOnlySet<string> SkipMemberKeys);
