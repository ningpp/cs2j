using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// One frozen M1 lift unit (design §4.1.1 <c>StaticLambdaPlan</c>, D10): a
/// zero-capture statement lambda lifted to a host-level
/// <c>private static</c> method. The method syntax is built once at plan
/// freeze time from the received-tree lambda (C32 site rewrites inside the
/// body travel with the move) and is only ever appended — never re-resolved.
/// The site anchor is the lambda's SpanStart + raw kind: red-node instances
/// are re-materialized per tree path, but the member-spine append of the
/// named-method stage never shifts positions, so DelegateSiteRewriteStage's
/// SpanStart matching is stable for every main-tree site.
/// </summary>
/// <param name="SiteSpanStart">Absolute SpanStart of the planned lambda node.</param>
/// <param name="SiteRawKind">RawKind of the planned lambda node (defensive pairing).</param>
/// <param name="MethodName">Cs2jLambda_&lt;member&gt;_&lt;k&gt; (D9/D10).</param>
/// <param name="DelegateTypeDisplay">Symbol-rendered target delegate (D8).</param>
/// <param name="Method">The synthesized private static method (built in phase 2; null while planning).</param>
internal sealed record StaticLambdaPlan(
    int SiteSpanStart,
    int SiteRawKind,
    string MethodName,
    string DelegateTypeDisplay,
    MethodDeclarationSyntax? Method = null);
