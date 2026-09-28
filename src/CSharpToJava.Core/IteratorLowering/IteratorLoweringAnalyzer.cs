using CSharpToJava.Core.IteratorLowering.ControlFlow;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.IteratorLowering;

/// <summary>
/// Discovers iterator members in a compilation, validates them against the
/// M1 support matrix, and produces a <see cref="StateMachinePlan"/> for each
/// lowerable member. Unsupported forms get a skip diagnostic.
/// </summary>
internal sealed class IteratorLoweringAnalyzer
{
    public static AnalyzerResult Analyze(SemanticModel semanticModel, IteratorLoweringOptions options)
    {
        var plans = new List<StateMachinePlan>();
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
            var (elementType, isEnumerable, isIEnumerator, _) = ResolveReturnType(symbol.ReturnType);
            if (elementType == null)
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0401,
                    $"Cannot resolve iterator element type for '{method.Identifier.Text}'.");
                skipMemberKeys.Add(GetMemberKey(method));
                continue;
            }

            // M1 legality checks
            if (!CheckLegality(method, diagnostics))
            {
                skipMemberKeys.Add(GetMemberKey(method));
                continue;
            }

            // Build the plan
            var cfgInfo = CfgBuilder.Analyze(method, semanticModel);

            // Collect existing nested type names for collision avoidance
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            var declaringType = method.Parent as TypeDeclarationSyntax;
            if (declaringType != null)
            {
                foreach (var member in declaringType.Members)
                {
                    if (member is ClassDeclarationSyntax cls)
                        usedNames.Add(cls.Identifier.ValueText);
                }
            }

            var className = IteratorLoweringNaming.GetStateMachineClassName(
                method.Identifier.ValueText, memberIndex, usedNames);

            var declaringTypeName = declaringType?.Identifier.ValueText ?? "global";

            // Parameters
            var paramFields = new List<CaptureField>();
            foreach (var param in method.ParameterList.Parameters)
            {
                if (cfgInfo.HoistedParameterNames.Contains(param.Identifier.ValueText))
                {
                    var paramType = param.Type?.ToString() ?? "object";
                    paramFields.Add(new CaptureField(
                        param.Identifier.ValueText,
                        paramType,
                        IteratorLoweringNaming.GetCaptureFieldName(param.Identifier.ValueText)));
                }
            }

            // Hoisted locals
            var localFields = new List<CaptureField>();
            foreach (var localName in cfgInfo.HoistedLocalNames)
            {
                var localType = ResolveLocalType(method, localName, semanticModel) ?? "object";
                localFields.Add(new CaptureField(
                    localName,
                    localType,
                    IteratorLoweringNaming.GetLocalFieldName(localName)));
            }

            plans.Add(new StateMachinePlan(
                MemberName: method.Identifier.ValueText,
                StateMachineClassName: className,
                ElementType: elementType,
                IsIEnumerableReturn: isEnumerable,
                IsIEnumeratorReturn: isIEnumerator,
                IsGenericMethod: method.TypeParameterList?.Parameters.Count > 0,
                DeclaringTypeName: declaringTypeName,
                IsStatic: method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)),
                Parameters: paramFields,
                HoistedLocals: localFields,
                YieldReturnCount: cfgInfo.YieldReturnCount,
                HasYieldBreak: cfgInfo.HasYieldBreak));

            memberIndex++;
        }

        return new AnalyzerResult(plans, diagnostics, skipMemberKeys);
    }

    private static (string? elementType, bool isEnumerable, bool isIEnumerator, bool isNonGeneric)
        ResolveReturnType(ITypeSymbol returnType)
    {
        if (returnType is not INamedTypeSymbol named) return (null, false, false, false);

        var fullName = named.OriginalDefinition.ToDisplayString();

        return fullName switch
        {
            "System.Collections.Generic.IEnumerable<T>" =>
                (named.TypeArguments[0].ToDisplayString(), true, false, false),
            "System.Collections.Generic.IEnumerator<T>" =>
                (named.TypeArguments[0].ToDisplayString(), false, true, false),
            "System.Collections.IEnumerable" =>
                ("object", true, false, true),
            "System.Collections.IEnumerator" =>
                ("object", false, true, true),
            _ => (null, false, false, false)
        };
    }

    private static bool CheckLegality(
        MethodDeclarationSyntax method,
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

        // try-with-catch containing yield -> CS2J0402 (CS1626 mirror)
        // try-finally containing yield -> CS2J0402 (CS1625 mirror + M1 unsupported)
        foreach (var tryStmt in body.DescendantNodes().OfType<TryStatementSyntax>())
        {
            if (tryStmt.Catches.Count > 0 && ContainsYield(tryStmt))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside try-with-catch (CS1626 mirror).");
                ok = false;
            }
            if (tryStmt.Finally != null && ContainsYield(tryStmt.Finally))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside finally (CS1625 mirror / M1 unsupported).");
                ok = false;
            }
            // M1: try-finally (no catch) containing yield in the try block -> also unsupported
            if (tryStmt.Finally != null && ContainsYield(tryStmt.Block))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside try-finally (M1 unsupported, planned for M2).");
                ok = false;
            }
        }

        // using statement containing yield -> CS2J0402 (M1 unsupported)
        foreach (var usingStmt in body.DescendantNodes().OfType<UsingStatementSyntax>())
        {
            if (ContainsYield(usingStmt))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside using (M1 unsupported, planned for M3).");
                ok = false;
            }
        }

        // lock statement containing yield -> CS2J0402 (M1 unsupported)
        foreach (var lockStmt in body.DescendantNodes().OfType<LockStatementSyntax>())
        {
            if (ContainsYield(lockStmt))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside lock (M1 unsupported, planned for M3).");
                ok = false;
            }
        }

        // User goto/label inside body -> CS2J0406
        if (body.DescendantNodes().OfType<GotoStatementSyntax>().Any()
            || body.DescendantNodes().OfType<LabeledStatementSyntax>().Any())
        {
            AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0406,
                $"Iterator method '{method.Identifier.Text}' has user goto/label (v1 unsupported).");
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

    private static string? ResolveLocalType(
        MethodDeclarationSyntax method,
        string localName,
        SemanticModel model)
    {
        foreach (var localDecl in method.Body!.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>())
        {
            var varDecl = localDecl.Declaration.Variables
                .FirstOrDefault(v => v.Identifier.ValueText == localName);
            if (varDecl != null)
            {
                var symbol = model.GetDeclaredSymbol(varDecl);
                if (symbol is ILocalSymbol localSym)
                    return localSym.Type?.ToDisplayString();
            }
        }

        foreach (var forStmt in method.Body!.DescendantNodes().OfType<ForStatementSyntax>())
        {
            if (forStmt.Declaration != null)
            {
                var varDecl = forStmt.Declaration.Variables
                    .FirstOrDefault(v => v.Identifier.ValueText == localName);
                if (varDecl != null)
                {
                    var symbol = model.GetDeclaredSymbol(varDecl);
                    if (symbol is ILocalSymbol localSym2)
                        return localSym2.Type?.ToDisplayString();
                }
            }
        }

        foreach (var foreachStmt in method.Body!.DescendantNodes().OfType<ForEachStatementSyntax>())
        {
            if (foreachStmt.Identifier.ValueText == localName)
            {
                var info = model.GetForEachStatementInfo(foreachStmt);
                return info.ElementType?.ToDisplayString();
            }
        }

        return null;
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
    IReadOnlyList<IteratorLoweringDiagnostic> Diagnostics,
    IReadOnlySet<string> SkipMemberKeys);
