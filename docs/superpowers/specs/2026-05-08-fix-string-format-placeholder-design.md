# Fix String.Format Placeholder Rewriting Dead Code

**Date**: 2026-05-08
**Status**: Approved

## Problem

When converting C# `String.Format("{0} {1}", a, b)` to Java, the format string `{0}` placeholders are NOT rewritten to Java `%s` format specifiers. The generated Java `String.format("{0} {1}", a, b)` compiles but produces literal `{0} {1}` output at runtime instead of substituting values.

**Affected files**: GeometryGraphWriter.java line 510 (and any other file using `string.Format`/`String.Format` with literal format strings).

## Root Cause

`InvocationExpressionTransformer.cs` has three code paths handling `Format` calls:

1. **Path A** (line 892-897): `string.Format(...)` — static call on `PredefinedTypeSyntax` keyword `string`. Returns `String.format(...)` with NO format string rewriting.

2. **Path B** (line 990-997): `String.Format(...)` — instance-style call via `MemberAccessExpression`. Returns `String.format(...)` early with NO format string rewriting. This early return prevents reaching Path C.

3. **Path C** (line 2086-2104): `String.Format(...)` — correct rewriting using `RewriteStringFormatLiteral()`. Dead code because Path B returns first.

The fix: integrate `RewriteStringFormatLiteral()` into Paths A and B.

## Fix

### `InvocationExpressionTransformer.cs`

**Path A** (line 892-897): After detecting `string.Format`, rewrite the format literal if the first argument is a string literal:

```csharp
if (primTypeSyntax.Keyword.Text == "string" && originalMethodName == "Format")
{
    int fmtStart = HasIFormatProviderFirstArg(node, context) ? 1 : 0;
    // Rewrite format string: {0} → %s, {1:D4} → %04d, etc.
    if (node.ArgumentList.Arguments.Count > fmtStart
        && node.ArgumentList.Arguments[fmtStart].Expression is LiteralExpressionSyntax
            { RawKind: (int)SyntaxKind.StringLiteralExpression } strLit)
    {
        var rewrittenFormat = RewriteStringFormatLiteral(strLit.Token.ValueText);
        var remainingArgs = ArgumentTransformer.TransformArgumentList(
            node.ArgumentList, context, facade, fmtStart + 1);
        return string.IsNullOrEmpty(remainingArgs)
            ? $"String.format({rewrittenFormat})"
            : $"String.format({rewrittenFormat}, {remainingArgs})";
    }
    var formatArgs = ArgumentTransformer.TransformArgumentList(
        node.ArgumentList, context, facade, fmtStart);
    return $"String.format({formatArgs})";
}
```

**Path B** (line 990-997): Same approach — rewrite before the early return:

```csharp
if (originalMethodName == "Format"
    && IsSystemStringMethod(methodSymbol, memberAccess.Expression, context))
{
    int fmtStart = HasIFormatProviderFirstArg(node, context) ? 1 : 0;
    if (node.ArgumentList.Arguments.Count > fmtStart
        && node.ArgumentList.Arguments[fmtStart].Expression is LiteralExpressionSyntax
            { RawKind: (int)SyntaxKind.StringLiteralExpression } strLit)
    {
        var rewrittenFormat = RewriteStringFormatLiteral(strLit.Token.ValueText);
        var remainingArgs = ArgumentTransformer.TransformArgumentList(
            node.ArgumentList, context, facade, fmtStart + 1);
        return string.IsNullOrEmpty(remainingArgs)
            ? $"{receiver}.{methodName}({rewrittenFormat})"
            : $"{receiver}.{methodName}({rewrittenFormat}, {remainingArgs})";
    }
    var fmtArgs = ArgumentTransformer.TransformArgumentList(
        node.ArgumentList, context, facade, fmtStart, methodSymbol);
    return $"{receiver}.{methodName}({fmtArgs})";
}
```

### Also fix: `StringBuilder.AppendFormat` (line 1890-1904)

Same format string rewriting needed for `StringBuilder.AppendFormat(...)`:

```csharp
if (originalMethodName == "AppendFormat" && ...)
{
    int appendFmtStart = isExtensionInStaticPath ? 1 : 0;
    // Add format string rewriting here (same pattern as above)
}
```

### Cleanup: Remove dead code at lines 2086-2104

After integrating rewriting into Paths A and B, the dead code at lines 2086-2104 can be removed OR left as-is (it will still handle any case that slips through Paths A and B, e.g. `System.String.Format` called differently).

## Test Plan

1. Verify `String.Format("{0} {1}", x, y)` → `String.format("%s %s", x, y)`
2. Verify `String.Format("{0:D4}", n)` → `String.format("%04d", n)`
3. Verify `string.Format("{0}", x)` → `String.format("%s", x)` (static call)
4. Verify format string with `%` escaped: `"{0}% done"` → `"%s%% done"`
5. Verify dynamic format strings (non-literal) still work unchanged
6. Verify `StringBuilder.AppendFormat("{0}", x)` → `sb.append(String.format("%s", x))`
