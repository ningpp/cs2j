using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// FS-1: wrap the iteration expression of a plain foreach over
/// <see cref="string"/> with <c>ToCharArray()</c>.
///
/// Hard rules (design decisions 1-4, hard invariants 1-6, ruling 6):
/// <list type="bullet">
/// <item>only <see cref="ForEachStatementSyntax"/> without ref/await guards
/// and with static expression type System.String matches;</item>
/// <item>only <c>Expression</c> is replaced — no temporary for the source
/// expression (single evaluation, null NRE timing preserved), no ?? ""/null
/// branches;</item>
/// <item>char/var iteration variables keep their declaration untouched;</item>
/// <item>an explicit non-char iteration variable with a standard char→T
/// conversion is restructured into a char temp loop plus a first-body
/// <c>T v = (T)__cs2jFsCh;</c> declaration so the conversion is explicit in
/// the middle tree; targets without a standard conversion keep the baseline
/// compile error (plain wrap only).</item>
/// </list>
/// </summary>
internal sealed class StringSourceRule : IForeachSourceRule
{
    public ForeachSourceRuleOutcome? TryHandle(
        ForeachSourceSite site,
        ForeachSourceRewriteContext context,
        CommonForEachStatementSyntax visitedNode)
    {
        if (visitedNode is not ForEachStatementSyntax visited)
            return null;
        if (!ForeachSourceAnalyzer.IsString(site.ExpressionType))
            return null;

        var wrapped = ForeachSourceSyntaxFacts.ToCharArrayInvocation(visited.Expression);

        ForEachStatementSyntax rewritten;
        if (NeedsElementConversion(site, visited, out var targetType, out var isStandardConversion))
        {
            rewritten = BuildElementConversionLoop(site, context, visited, wrapped, targetType, isStandardConversion);
        }
        else
        {
            rewritten = visited.WithExpression(wrapped);
        }

        context.Changed = true;
        context.Statistics.StringForeachWrapped++;
        context.Statistics.ErrorsByCode[PreprocessDiagnosticCodes.ForeachSourceStringWrapped] =
            context.Statistics.ErrorsByCode.GetValueOrDefault(PreprocessDiagnosticCodes.ForeachSourceStringWrapped) + 1;

        return new ForeachSourceRuleOutcome(
            rewritten
                .WithLeadingTrivia(visited.GetLeadingTrivia())
                .WithTrailingTrivia(visited.GetTrailingTrivia()),
            Changed: true);
    }

    /// <summary>
    /// True for an explicitly-typed (non-var, non-char) iteration variable.
    /// <paramref name="standardConversion"/> reports whether char→T is a
    /// standard (implicit or explicit) conversion; when false the baseline
    /// source already fails to compile and the plain wrap preserves that
    /// error without introducing a cast.
    /// </summary>
    private static bool NeedsElementConversion(
        ForeachSourceSite site,
        ForEachStatementSyntax node,
        out ITypeSymbol targetType,
        out bool standardConversion)
    {
        targetType = null!;
        standardConversion = false;

        // "var" (and "char") keep the declaration as written; the foreach
        // over char[] infers/declares char exactly like the original.
        if (node.Type is IdentifierNameSyntax { Identifier.Text: "var" })
            return false;

        var declared = site.Model.GetDeclaredSymbol(node) as ILocalSymbol;
        var element = declared?.Type;
        if (element == null || element.SpecialType == SpecialType.System_Char)
            return false;

        targetType = element;
        var charType = site.Model.Compilation.GetSpecialType(SpecialType.System_Char);
        var conversion = site.Model.Compilation.ClassifyConversion(charType, element);
        // "Standard conversion" in the C# spec sense = any built-in (implicit
        // or explicit) conversion, i.e. exists and is not user-defined
        // (Conversion.IsStandard is unavailable on this Roslyn surface).
        standardConversion = conversion.Exists && !conversion.IsUserDefined;
        return true;
    }

    /// <summary>
    /// <c>foreach (T v in e) body</c> →
    /// <c>foreach (char __cs2jFsCh in e.ToCharArray()) { T v = (T)__cs2jFsCh; body }</c>
    /// when a standard char→T conversion exists; without one the cast is
    /// omitted so the middle tree keeps the same compile error as the source.
    /// </summary>
    private static ForEachStatementSyntax BuildElementConversionLoop(
        ForeachSourceSite site,
        ForeachSourceRewriteContext context,
        ForEachStatementSyntax visited,
        ExpressionSyntax wrappedExpression,
        ITypeSymbol targetType,
        bool standardConversion)
    {
        var tempName = context.FreshName(ForeachSourceSyntaxFacts.CharTempName, visited);

        StatementSyntax body = visited.Statement;
        if (standardConversion)
        {
            var castType = visited.Type.WithoutTrivia();
            ExpressionSyntax initializer = SyntaxFactory.CastExpression(
                castType,
                SyntaxFactory.IdentifierName(tempName));
            var declarator = SyntaxFactory.VariableDeclarator(
                    visited.Identifier.WithoutTrivia())
                .WithInitializer(SyntaxFactory.EqualsValueClause(initializer));
            var conversionDeclaration = SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(castType)
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(declarator)));
            body = ForeachSourceSyntaxFacts.PrependStatements(
                visited.Statement, new[] { conversionDeclaration });
        }

        return SyntaxFactory.ForEachStatement(
            visited.AwaitKeyword,
            visited.ForEachKeyword,
            visited.OpenParenToken,
            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.CharKeyword)),
            SyntaxFactory.Identifier(tempName),
            visited.InKeyword,
            wrappedExpression,
            visited.CloseParenToken,
            body);
    }
}
