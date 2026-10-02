using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles method invocation expressions.
/// </summary>
[TransformerRegistration]
public class InvocationExpressionTransformer : IIRExpressionTransformer
{
    static InvocationExpressionTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.InvocationExpression
        }, new InvocationExpressionTransformer());
    }

    private static readonly Lazy<InvocationExpressionTransformer> _instance = new(() => new());
    public static InvocationExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context) => node.Kind() switch
    {
        SyntaxKind.InvocationExpression => TransformInvocation((InvocationExpressionSyntax)node, context),
        _ => throw new NotSupportedException($"Invocation expression kind {node.Kind()} not supported.")
    };

    /// <summary>
    /// 检测 nameof(...) 调用——在 cs2j 的解析器中 nameof 被解析为普通调用
    /// （IdentifierNameSyntax("nameof") + 单参数），而非 Roslyn 原生的 NameOfExpressionSyntax。
    /// 模式：IdentifierNameSyntax("nameof") + 单参数。
    /// </summary>
    private static bool IsNameOfInvocation(InvocationExpressionSyntax invocation, out ExpressionSyntax? argument)
    {
        argument = null;
        if (invocation.Expression is not IdentifierNameSyntax id)
            return false;
        if (!string.Equals(id.Identifier.Text, "nameof", StringComparison.Ordinal))
            return false;
        if (invocation.ArgumentList.Arguments.Count != 1)
            return false;
        argument = invocation.ArgumentList.Arguments[0].Expression;
        return true;
    }

    /// <summary>
    /// 从 nameof 参数中提取"名称"字符串。按 C# 规范：
    /// - nameof(variable) → "variable"（剥离 verbatim @ 前缀）
    /// - nameof(Type)、nameof(Type.Member) → 最后一段简单名称
    /// - nameof(Namespace.Type) → 最后一段简单名称
    /// </summary>
    private static string ExtractNameOfArgument(ExpressionSyntax argument)
    {
        return argument switch
        {
            IdentifierNameSyntax id => StripVerbatimPrefix(id.Identifier.Text),
            GenericNameSyntax generic => StripVerbatimPrefix(generic.Identifier.Text),
            MemberAccessExpressionSyntax memberAccess => StripVerbatimPrefix(memberAccess.Name.Identifier.Text),
            QualifiedNameSyntax qualified => ExtractNameOfRightMostName(qualified),
            _ => argument.ToString()
        };
    }

    /// <summary>
    /// 处理 QualifiedNameSyntax（如 Namespace.Type 或 A.B.C）。
    /// 取最右端的 Name 节点。
    /// </summary>
    private static string ExtractNameOfRightMostName(QualifiedNameSyntax qualified)
    {
        if (qualified.Right is GenericNameSyntax rightGeneric)
            return StripVerbatimPrefix(rightGeneric.Identifier.Text);
        return StripVerbatimPrefix(qualified.Right.Identifier.Text);
    }

    private static string StripVerbatimPrefix(string name)
        => name.StartsWith("@") ? name[1..] : name;

    /// <summary>
    /// Escapes a string for use inside Java double-quoted string literals.
    /// nameof 结果一般是普通标识符，但仍需处理 \ " \n 等字符。
    /// </summary>
    private static string JavaEscapeString(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        if (node is not InvocationExpressionSyntax invocation)
            return new JavaRawExpression(Transform(node, context));

        // nameof(X) → Java string literal "argumentName"
        if (IsNameOfInvocation(invocation, out var nameofArgument))
        {
            var name = ExtractNameOfArgument(nameofArgument!);
            return new JavaLiteralExpression("\"" + JavaEscapeString(name) + "\"");
        }

        var facade = ExpressionTransformerFacade.Instance;
        var arguments = invocation.ArgumentList.Arguments
            .Select(a => facade.TransformToIR(a.Expression, context))
            .ToList();

        switch (invocation.Expression)
        {
            // abc.CallMethod(arg1, arg2) → abc.callMethod(arg1, arg2)
            case MemberAccessExpressionSyntax memberAccess:
            {
                var originalName = memberAccess.Name.Identifier.Text;

                if (TryRenderPrimitiveStaticCall(
                        memberAccess,
                        originalName,
                        context,
                        string.Join(", ", arguments.Select(a => a.ToInlineString())),
                        out var primCall))
                {
                    return new JavaRawExpression(primCall);
                }

                var receiver = TransformReceiverToIR(memberAccess.Expression, context);
                var (mappedName, isStatic) =
                    TryResolveMappedName(invocation, memberAccess.Name.Identifier.Text, context);

                var call = new JavaMethodCallExpression { Target = receiver };

                if (mappedName != null)
                {
                    ApplyMappedName(call, mappedName, isStatic, receiver);
                }
                else
                {
                    call.MethodName = ToCamelCase(memberAccess.Name.Identifier.Text);
                }

                foreach (var argument in arguments)
                    call.Arguments.Add(argument);

                return call;
            }

            // CallMethod(arg1, arg2) → callMethod(arg1, arg2)
            case IdentifierNameSyntax identifier:
            {
                var (mappedName, _) = TryResolveMappedName(invocation, identifier.Identifier.Text, context);

                var call = new JavaMethodCallExpression
                {
                    MethodName = mappedName ?? ToCamelCase(identifier.Identifier.Text)
                };
                foreach (var argument in arguments)
                    call.Arguments.Add(argument);
                return call;
            }

            // Other invocation shapes are not part of the minimal supported set.
            default:
                return new JavaRawExpression(Transform(invocation, context));
        }
    }

    private string TransformInvocation(InvocationExpressionSyntax node, ConversionContext context)
    {
        // nameof(X) → "argumentName"
        if (IsNameOfInvocation(node, out var nameofArgument))
        {
            var name = ExtractNameOfArgument(nameofArgument!);
            return "\"" + JavaEscapeString(name) + "\"";
        }

        var facade = ExpressionTransformerFacade.Instance;
        var arguments = string.Join(", ",
            node.ArgumentList.Arguments.Select(a => facade.Transform(a.Expression, context)));

        switch (node.Expression)
        {
            // abc.CallMethod(arg1, arg2) → abc.callMethod(arg1, arg2)
            case MemberAccessExpressionSyntax memberAccess:
            {
                var originalName = memberAccess.Name.Identifier.Text;

                // C# primitive type qualifier — Java forbids using a primitive keyword as a
                // type name, so double.IsNaN(x) must become Double.isNaN(x).
                if (TryRenderPrimitiveStaticCall(memberAccess, originalName, context, arguments, out var primCall))
                    return primCall;

                var receiver = TransformReceiver(memberAccess.Expression, context);
                var (mappedName, isStatic) = TryResolveMappedName(node, originalName, context);

                if (mappedName != null)
                {
                    return RenderMappedCall(mappedName, isStatic, receiver, arguments);
                }

                return $"{receiver}.{AccessorOrCamelName(originalName)}({arguments})";
            }

            // CallMethod(arg1, arg2) → callMethod(arg1, arg2)
            case IdentifierNameSyntax identifier:
            {
                var originalName = identifier.Identifier.Text;
                var (mappedName, _) = TryResolveMappedName(node, originalName, context);

                var methodName = mappedName ?? AccessorOrCamelName(originalName);
                return $"{methodName}({arguments})";
            }

            // Fallback: transform the callee expression as-is and append the arguments.
            default:
                return $"{facade.Transform(node.Expression, context)}({arguments})";
        }
    }

    /// <summary>
    /// Renders an invocation receiver as Java source, erasing type arguments when the
    /// receiver is a static type qualifier written with an explicit generic argument list.
    /// </summary>
    private static string TransformReceiver(ExpressionSyntax expression, ConversionContext context)
    {
        var receiver = ExpressionTransformerFacade.Instance.Transform(expression, context);
        return ContainsGenericTypeQualifier(expression)
            ? StripTypeArgs(receiver)
            : receiver;
    }

    /// <summary>
    /// IR counterpart of <see cref="TransformReceiver"/>.
    /// </summary>
    private static JavaExpression TransformReceiverToIR(ExpressionSyntax expression, ConversionContext context)
    {
        var receiver = ExpressionTransformerFacade.Instance.TransformToIR(expression, context);
        if (!ContainsGenericTypeQualifier(expression))
            return receiver;

        return receiver switch
        {
            JavaRawExpression raw => new JavaRawExpression(
                StripTypeArgs(raw.Code), raw.ResolvedType),
            JavaIdentifierExpression identifier => new JavaIdentifierExpression
            {
                Name = StripTypeArgs(identifier.Name)
            },
            _ => receiver
        };
    }

    /// <summary>
    /// True when the expression contains a generic name segment (<c>Type&lt;T&gt;</c>).
    /// <para>
    /// C# allows an explicit generic type qualifier on a static member access
    /// (<c>ArraySortHelper&lt;T&gt;.IntrospectiveSort(...)</c>), but Java forbids type
    /// arguments on a static qualifier — <c>Type&lt;T&gt;.member()</c> is not valid Java and
    /// javac reports "illegal start of type". Such a receiver must therefore be erased to its
    /// raw type name. In an expression position a generic name can only ever denote a type, so
    /// this only matches static qualifiers and never an instance receiver.
    /// </para>
    /// </summary>
    private static bool ContainsGenericTypeQualifier(ExpressionSyntax expression) => expression switch
    {
        GenericNameSyntax => true,
        QualifiedNameSyntax qualified =>
            ContainsGenericTypeQualifier(qualified.Right) || ContainsGenericTypeQualifier(qualified.Left),
        AliasQualifiedNameSyntax alias => ContainsGenericTypeQualifier(alias.Name),
        MemberAccessExpressionSyntax member =>
            ContainsGenericTypeQualifier(member.Name) || ContainsGenericTypeQualifier(member.Expression),
        _ => false
    };

    /// <summary>
    /// Renders a static call whose receiver is a C# primitive keyword.
    /// <para>
    /// A Java primitive keyword is not a type name, so <c>double.IsNaN(x)</c> is illegal and
    /// must become <c>Double.isNaN(x)</c>. Java's wrapper classes also use different member
    /// names than the C# primitives (<c>IsInfinity</c> → <c>isInfinite</c>, <c>IsLower</c> →
    /// <c>isLowerCase</c>, …), and the C# <c>Parse</c>/<c>TryParse</c> overloads that take a
    /// format provider are routed through the compat <c>MathHelper</c>.
    /// </para>
    /// Returns <c>false</c> when the receiver is not a non-string primitive keyword.
    /// </summary>
    private static bool TryRenderPrimitiveStaticCall(
        MemberAccessExpressionSyntax memberAccess,
        string originalMethodName,
        ConversionContext context,
        string arguments,
        out string rendered)
    {
        rendered = string.Empty;

        // `string` keeps its existing handling: System.String maps to java.lang.String and the
        // TypeMappings table already routes the static helpers (StringHelper.*).
        // `object` is excluded as well: object.Equals/object.ReferenceEquals static calls must
        // resolve through the System.Object method-mapping entries (Objects.equals /
        // ObjectHelper.referenceEquals); the generic primitive path would render non-existent
        // static Object.equals/referenceEquals methods instead.
        if (memberAccess.Expression is not PredefinedTypeSyntax primTypeSyntax
            || primTypeSyntax.Keyword.Text == "string"
            || primTypeSyntax.Keyword.Text == "object")
        {
            return false;
        }

        var keyword = primTypeSyntax.Keyword.Text;
        var argumentCount = memberAccess.Parent is InvocationExpressionSyntax invocation
            ? invocation.ArgumentList.Arguments.Count
            : 0;

        // Numeric TryParse: double.TryParse(s, out result) → MathHelper.tryParseDouble(s, holder)
        if (originalMethodName == "TryParse" && argumentCount >= 2
            && MapPrimitiveTryParseHelper(keyword) is { } tryParseHelper)
        {
            context.AddImport("io.github.ningpp.compat.MathHelper");
            rendered = $"{tryParseHelper}({arguments})";
            return true;
        }

        // Parse → the compat helper, except the single-argument byte/sbyte/short forms which
        // are valid on the Java wrappers themselves (Byte.parseByte(s)).
        if (originalMethodName == "Parse" && MapPrimitiveParseHelper(keyword) is { } parseHelper)
        {
            var useWrapperDirectly = keyword is "byte" or "sbyte" or "short" && argumentCount <= 1;
            if (!useWrapperDirectly)
            {
                if (parseHelper.StartsWith("MathHelper.", StringComparison.Ordinal))
                    context.AddImport("io.github.ningpp.compat.MathHelper");
                else if (parseHelper.StartsWith("Decimal.", StringComparison.Ordinal))
                    context.AddImport("io.github.ningpp.compat.Decimal");
                rendered = $"{parseHelper}({arguments})";
                return true;
            }
        }

        // Double.IsPositiveInfinity(x) → (x == Double.POSITIVE_INFINITY)
        // Java's isInfinite() cannot distinguish the sign, so compare against the constant.
        if (originalMethodName is "IsPositiveInfinity" or "IsNegativeInfinity"
            && keyword is "double" or "float"
            && memberAccess.Parent is InvocationExpressionSyntax posInfInvocation
            && posInfInvocation.ArgumentList.Arguments.Count == 1)
        {
            var javaType = keyword == "float" ? "Float" : "Double";
            var constant = originalMethodName == "IsPositiveInfinity"
                ? "POSITIVE_INFINITY" : "NEGATIVE_INFINITY";
            var arg = ExpressionTransformerFacade.Instance.Transform(
                posInfInvocation.ArgumentList.Arguments[0].Expression, context);
            rendered = $"({arg} == {javaType}.{constant})";
            return true;
        }

        // char.GetUnicodeCategory(ch) → CharUnicodeInfo.getUnicodeCategory(ch)
        // java.lang.Character has no getUnicodeCategory; the compat helper returns the
        // C# System.Globalization.UnicodeCategory values.
        if (keyword == "char" && originalMethodName == "GetUnicodeCategory" && argumentCount >= 1)
        {
            context.AddImport("io.github.ningpp.compat.CharUnicodeInfo");
            rendered = $"CharUnicodeInfo.getUnicodeCategory({arguments})";
            return true;
        }

        // char.IsDigit(string, int) → Character.isDigit(s.charAt(index))
        if (keyword == "char" && originalMethodName == "IsDigit" && argumentCount == 2
            && memberAccess.Parent is InvocationExpressionSyntax isDigitInvocation)
        {
            var strArg = ExpressionTransformerFacade.Instance.Transform(
                isDigitInvocation.ArgumentList.Arguments[0].Expression, context);
            var idxArg = ExpressionTransformerFacade.Instance.Transform(
                isDigitInvocation.ArgumentList.Arguments[1].Expression, context);
            rendered = $"Character.isDigit({strArg}.charAt({idxArg}))";
            return true;
        }

        // byte maps to Java int, but Parse lives on Byte (Byte.parseByte).
        var boxedReceiver = keyword == "byte" && originalMethodName == "Parse"
            ? "Byte"
            : primTypeSyntax.Keyword.Text switch
            {
                "bool" => "Boolean",
                "int" => "Integer",
                "long" => "Long",
                "double" => "Double",
                "float" => "Float",
                "decimal" => "Decimal",
                "char" => "Character",
                "short" => "Short",
                "byte" => "Integer",
                "uint" => "Integer",
                "ulong" => "Long",
                "ushort" => "Short",
                "string" => "String",
                "object" => "Object",
                _ => primTypeSyntax.Keyword.Text
            };
        var mappedMethod = MapPrimitiveStaticMethodName(keyword, originalMethodName);

        // C34 normalization emits decimal.Add/Subtract/Multiply/Divide/Remainder/
        // Negate/Compare static calls; they land on the compat Decimal surface.
        // The import is normally carried by the configured System.Decimal type
        // mapping, but assert it here too so a decimal-call-only file still closes.
        if (keyword == "decimal")
            context.AddImport("io.github.ningpp.compat.Decimal");

        // char.ConvertFromUtf32 returns string in C# but Character.toChars returns char[].
        rendered = keyword == "char" && originalMethodName == "ConvertFromUtf32"
            ? $"new String({boxedReceiver}.{mappedMethod}({arguments}))"
            : $"{boxedReceiver}.{mappedMethod}({arguments})";
        return true;
    }

    /// <summary>
    /// Maps C# built-in primitive static method names to their Java equivalents.
    /// e.g. double.IsInfinity → Double.isInfinite, int.Parse → Integer.parseInt
    /// </summary>
    private static string MapPrimitiveStaticMethodName(string primitiveKeyword, string methodName)
        => (primitiveKeyword, methodName) switch
        {
            // char-specific mappings: the C# and Java Character classes differ.
            ("char", "IsLower") => "isLowerCase",
            ("char", "IsUpper") => "isUpperCase",
            ("char", "IsWhiteSpace") => "isWhitespace",
            ("char", "IsControl") => "isISOControl",
            ("char", "IsSurrogate") => "isSurrogate",
            ("char", "IsHighSurrogate") => "isHighSurrogate",
            ("char", "IsLowSurrogate") => "isLowSurrogate",
            ("char", "IsSurrogatePair") => "isSurrogatePair",
            ("char", "GetNumericValue") => "getNumericValue",
            ("char", "ToLower") => "toLowerCase",
            ("char", "ToLowerInvariant") => "toLowerCase",
            ("char", "ToUpper") => "toUpperCase",
            ("char", "ToUpperInvariant") => "toUpperCase",
            ("char", "ToString") => "toString",
            ("char", "ConvertFromUtf32") => "toChars",
            ("char", "ConvertToUtf32") => "toCodePoint",
            ("char", "Parse") => "valueOf",
            // generic mappings shared across all primitive types
            (_, "IsInfinity") => "isInfinite",
            (_, "IsNaN") => "isNaN",
            (_, "IsFinite") => "isFinite",
            (_, "Parse") => primitiveKeyword switch
            {
                "int" => "parseInt",
                "long" => "parseLong",
                "double" => "parseDouble",
                "float" => "parseFloat",
                "short" => "parseShort",
                "byte" => "parseByte",
                "sbyte" => "parseByte",
                "uint" => "parseUnsignedInt",
                "ulong" => "parseUnsignedLong",
                "ushort" => "parseUnsignedInt",
                _ => "parse" + char.ToUpperInvariant(primitiveKeyword[0]) + primitiveKeyword[1..]
            },
            _ when methodName.Length > 0 => char.ToLowerInvariant(methodName[0]) + methodName[1..],
            _ => methodName
        };

    private static string? MapPrimitiveParseHelper(string primitiveKeyword)
        => primitiveKeyword switch
        {
            "int" => "MathHelper.parseInt",
            "long" => "MathHelper.parseLong",
            "double" => "MathHelper.parseDouble",
            "float" => "MathHelper.parseFloat",
            "decimal" => "Decimal.parse",
            "uint" => "MathHelper.parseUInt",
            "ulong" => "MathHelper.parseULong",
            "ushort" => "MathHelper.parseUShort",
            "short" => "MathHelper.parseShort",
            "byte" => "MathHelper.parseByte",
            "sbyte" => "MathHelper.parseByte",
            _ => null
        };

    private static string? MapPrimitiveTryParseHelper(string primitiveKeyword)
        => primitiveKeyword switch
        {
            "double" => "MathHelper.tryParseDouble",
            "float" => "MathHelper.tryParseFloat",
            "int" => "MathHelper.tryParseInt",
            "long" => "MathHelper.tryParseLong",
            "short" => "MathHelper.tryParseShort",
            "byte" => "MathHelper.tryParseByte",
            "char" => "MathHelper.tryParseChar",
            "bool" => "MathHelper.tryParseBool",
            "uint" => "MathHelper.tryParseUInt",
            "ulong" => "MathHelper.tryParseULong",
            "ushort" => "MathHelper.tryParseUShort",
            _ => null
        };

    /// <summary>
    /// Applies a configured mapped method name to an IR call. Mapped names containing a dot
    /// (e.g. "StringHelper.insert") are static compatibility helpers: for instance calls the
    /// receiver becomes the first argument; for static calls the type receiver is dropped.
    /// Regular call arguments are appended by the caller afterwards.
    /// </summary>
    private static void ApplyMappedName(
        JavaMethodCallExpression call,
        string mappedName,
        bool isStatic,
        JavaExpression receiver)
    {
        if (mappedName.Contains('.'))
        {
            // Static helper call — clear the instance target and pass the
            // receiver as the first argument for instance invocations.
            call.Target = null;
            call.MethodName = mappedName;
            if (!isStatic)
                call.Arguments.Add(receiver);
        }
        else
        {
            call.MethodName = mappedName;
        }
    }

    /// <summary>
    /// Renders the Java source for a method whose name was resolved through TypeMappings.json.
    /// </summary>
    private static string RenderMappedCall(
        string mappedName,
        bool isStatic,
        string receiver,
        string arguments)
    {
        if (mappedName.Contains('.'))
        {
            // Static compatibility helper: instance receiver is passed as the first argument;
            // a static type receiver (e.g. string.IsNullOrEmpty) is dropped entirely.
            if (isStatic)
            {
                // "out.println" is a field access on java.lang.System, not a class name:
                // qualify with System (java.lang needs no import).
                var rendered = mappedName.StartsWith("out.", StringComparison.Ordinal)
                    ? "System." + mappedName
                    : mappedName;
                return $"{rendered}({arguments})";
            }
            return string.IsNullOrEmpty(arguments)
                ? $"{mappedName}({receiver})"
                : $"{mappedName}({receiver}, {arguments})";
        }

        return $"{receiver}.{mappedName}({arguments})";
    }

    /// <summary>
    /// Simple-prefix → FQN import table for dotted mapped names rendered by
    /// <see cref="ApplyMappedName"/>/<see cref="RenderMappedCall"/> (e.g. an
    /// invocation mapped to "Objects.equals" needs <c>import java.util.Objects;</c>).
    /// <para>
    /// The project pipeline already emits <c>java.util.*</c> wildcard imports, but
    /// <c>ObjectHelper</c>/<c>DelegateHelper</c>/<c>StringHelper</c> are not covered
    /// by that wildcard, and the single-file pipeline has no wildcard at all — both
    /// pipelines rely on this rule for explicit helper imports. Prefixes absent from
    /// this table keep their historical behavior (no import added here).
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> DottedNameImports =
        new(StringComparer.Ordinal)
        {
            ["Objects"] = "java.util.Objects",
            ["ObjectHelper"] = "io.github.ningpp.compat.ObjectHelper",
            ["DelegateHelper"] = "io.github.ningpp.compat.DelegateHelper",
            ["Environment"] = "io.github.ningpp.compat.Environment",
            ["StringHelper"] = "io.github.ningpp.compat.StringHelper",
            ["MathHelper"] = "io.github.ningpp.compat.MathHelper",
            ["MemoryExtensions"] = "io.github.ningpp.compat.MemoryExtensions",
        };

    /// <summary>
    /// Adds the import required by a dotted mapped name (simple prefix → FQN).
    /// No-op for non-dotted names or unknown prefixes.
    /// </summary>
    private static void AddImportForMappedName(string mappedName, ConversionContext context)
    {
        var dotIndex = mappedName.IndexOf('.');
        if (dotIndex <= 0)
            return;

        var prefix = mappedName.Substring(0, dotIndex);
        if (DottedNameImports.TryGetValue(prefix, out var import))
            context.AddImport(import);
    }

    /// <summary>
    /// Looks up the configured Java method name for the invocation via TypeMappings.json.
    /// Checks the method's containing type (display name, then namespace+name FQN),
    /// then every implemented interface whose member signature matches the invocation.
    /// Returns (null, false) when the semantic model is unavailable or no mapping exists.
    /// </summary>
    private static (string? MappedName, bool IsStatic) TryResolveMappedName(
        InvocationExpressionSyntax invocation,
        string originalName,
        ConversionContext context)
    {
        if (context.SemanticModel == null)
            return (null, false);
        if (context.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
            return (null, false);

        var paramCount = invocation.ArgumentList.Arguments.Count;
        var containingType = methodSymbol.ContainingType;

        var mappedName = context.TypeMappings.MapMethod(containingType.ToDisplayString(), originalName, paramCount)
            ?? context.TypeMappings.MapMethod(
                $"{containingType.ContainingNamespace}.{containingType.Name}", originalName, paramCount);

        // Implicit interface implementations (e.g. IEquatable<T>.Equals → equalsTo).
        if (mappedName == null && containingType is INamedTypeSymbol namedType)
        {
            foreach (var iface in namedType.AllInterfaces)
            {
                if (!MethodSignatureMatchesInterface(methodSymbol, iface, originalName))
                    continue;

                mappedName = context.TypeMappings.MapMethod(
                    iface.ConstructedFrom.ToDisplayString(), originalName, paramCount)
                    ?? context.TypeMappings.MapMethod(
                        $"{iface.ContainingNamespace}.{iface.Name}", originalName, paramCount);

                if (mappedName != null)
                    break;
            }
        }

        // J6: dotted mapped names need an explicit FQN import (single-file pipeline
        // and non-java.util helpers). Centralized here so all four render paths
        // (IR/text × member-access/identifier) get it.
        if (mappedName != null)
            AddImportForMappedName(mappedName, context);

        return (mappedName, methodSymbol.IsStatic);
    }

    /// <summary>
    /// True when the method has the same arity and parameter types as the named
    /// interface member. Prevents Object.Equals(object) from being remapped via
    /// an IEquatable&lt;T&gt;.Equals(T) entry.
    /// </summary>
    private static bool MethodSignatureMatchesInterface(IMethodSymbol method, INamedTypeSymbol iface, string methodName)
    {
        var interfaceMethod = iface.GetMembers(methodName).OfType<IMethodSymbol>().FirstOrDefault();
        if (interfaceMethod == null || method.Parameters.Length != interfaceMethod.Parameters.Length)
            return false;

        for (var i = 0; i < method.Parameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(method.Parameters[i].Type, interfaceMethod.Parameters[i].Type))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Converts a C# PascalCase method name to Java camelCase, escaping Java keywords.
    /// </summary>
    private static string ToCamelCase(string name)
    {
        if (name.Length == 0)
            return name;

        var camelCase = char.ToLowerInvariant(name[0]) + name[1..];
        return ConversionContext.EscapeJavaKeyword(camelCase);
    }

    /// <summary>
    /// Mirrors MethodTransformer.GetJavaMethodName: member-desugar (C11) accessor
    /// methods get_X/set_X render as X on the Java side, so call sites must strip
    /// exactly as method declarations do. Any other name gets ordinary camel-casing.
    /// Mapped names (e.g. ByRef.get_Value → getValue) are resolved by the caller
    /// before this and never reach here.
    /// </summary>
    private static string AccessorOrCamelName(string name)
    {
        if (name.StartsWith("get_") || name.StartsWith("set_"))
            return ConversionContext.EscapeJavaKeyword(char.ToUpper(name[4]) + name[5..]);
        return ToCamelCase(name);
    }

    /// <summary>
    /// Strips generic type arguments from a type name (e.g. List&lt;T&gt; → List).
    /// </summary>
    private static string StripTypeArgs(string typeName)
    {
        if (!typeName.Contains('<', StringComparison.Ordinal))
            return typeName;

        var builder = new StringBuilder(typeName.Length);
        var depth = 0;

        foreach (var ch in typeName)
        {
            if (ch == '<')
            {
                depth++;
                continue;
            }

            if (ch == '>')
            {
                depth--;
                continue;
            }

            if (depth == 0)
                builder.Append(ch);
        }

        return depth == 0 ? builder.ToString() : typeName;
    }
}
