using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// One frozen M1 lift unit (design §4.1.1 <c>StaticLambdaPlan</c>, D10): a
/// zero-capture statement lambda lifted to a host-level
/// <c>private static</c> method. The method syntax is built once at plan
/// freeze time from the received-tree lambda (C32 site rewrites inside the
/// body travel with the move) and is only ever appended — never re-resolved.
/// Two span anchors are frozen because C32's instance/snapshot statements
/// shift absolute positions BETWEEN the model tree and the received tree:
/// the model span pairs/ids the lambda for the planner census and the
/// order-based delegate-site stage, while the received span addresses the
/// node copies nested inside to-be-moved received-tree bodies
/// (NestedSiteSubstituter / MigratedBodyRewriter).
/// </summary>
/// <param name="SiteSpanStart">Absolute MODEL-tree SpanStart of the planned
/// lambda node (analysis root; pairing/dedup anchor).</param>
/// <param name="SiteRawKind">RawKind of the planned lambda node (defensive pairing).</param>
/// <param name="ReceivedSpanStart">Absolute RECEIVED-tree SpanStart of the
/// same node (post C32 edits; anchor for nested-site substitution while
/// bodies still hang off the received tree). Equals <paramref name="SiteSpanStart"/>
/// when C32 made no statement insertions before the site.</param>
/// <param name="MethodName">Cs2jLambda_&lt;member&gt;_&lt;k&gt; (D9/D10).</param>
/// <param name="DelegateTypeDisplay">Symbol-rendered target delegate (D8).</param>
/// <param name="Method">The synthesized private static method (built in phase 2; null while planning).</param>
/// <param name="TypeArguments">Explicit type arguments for the delegate site
/// when method-group inference cannot supply every carried enclosing-method
/// type parameter (null = infer, the previous shape).</param>
internal sealed record StaticLambdaPlan(
    int SiteSpanStart,
    int SiteRawKind,
    int ReceivedSpanStart,
    string MethodName,
    string DelegateTypeDisplay,
    MethodDeclarationSyntax? Method = null,
    IReadOnlyList<string>? TypeArguments = null);
