using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using CSharpToJava.Core.Transformers.Utilities;
using System.Text;
namespace CSharpToJava.Core.Transformers.Expression;
/// <summary>Handles object and array creation expressions.</summary>
[TransformerRegistration]
public class ObjectCreationTransformer : IIRExpressionTransformer
{
    static ObjectCreationTransformer() => ExpressionTransformerRegistry.Register(new[]
    {
        SyntaxKind.ImplicitObjectCreationExpression, SyntaxKind.ObjectCreationExpression,
        SyntaxKind.AnonymousObjectCreationExpression, SyntaxKind.ArrayCreationExpression,
        SyntaxKind.ImplicitArrayCreationExpression, SyntaxKind.ArrayInitializerExpression,
        SyntaxKind.StackAllocArrayCreationExpression
    }, new ObjectCreationTransformer());
    private static readonly Lazy<ObjectCreationTransformer> _instance = new(() => new());
    public static ObjectCreationTransformer Instance => _instance.Value;
    public string Transform(ExpressionSyntax n, ConversionContext c) => n switch
    {
        ObjectCreationExpressionSyntax o => NewObject(c.GetTypeInfo(o).Type, o.Type, o.ArgumentList, o.Initializer, c),
        ImplicitObjectCreationExpressionSyntax io => NewObject(c.GetTypeInfo(io).Type, null, io.ArgumentList, io.Initializer, c),
        AnonymousObjectCreationExpressionSyntax a => NewAnonymous(a, c),
        ArrayCreationExpressionSyntax ar => NewArray(ar, c),
        ImplicitArrayCreationExpressionSyntax ia => NewImplicitArray(ia, c),
        InitializerExpressionSyntax i => NewBareArray(i, c),
        StackAllocArrayCreationExpressionSyntax s => NewStackAlloc(s, c),
        _ => throw new NotSupportedException($"Object creation kind {n.Kind()} not supported.")
    };
    public JavaExpression TransformToIR(ExpressionSyntax n, ConversionContext c)
        => new JavaRawExpression(Transform(n, c));
    private string NewObject(ITypeSymbol? type, TypeSyntax? ts, ArgumentListSyntax? al, InitializerExpressionSyntax? init, ConversionContext c)
    {
        var name = type is null or IErrorTypeSymbol ? (ts != null ? c.MapTypeFromSyntax(ts) : "Object") : c.MapType(type);
        var f = ExpressionTransformerFacade.Instance;

        // new string(char, int): Java has no such constructor; compat repeats the char.
        if (type?.SpecialType == SpecialType.System_String
            && al is { Arguments.Count: 2 }
            && c.GetTypeInfo(al.Arguments[0].Expression).Type?.SpecialType == SpecialType.System_Char
            && c.GetTypeInfo(al.Arguments[1].Expression).Type?.SpecialType == SpecialType.System_Int32)
        {
            c.AddImport("io.github.ningpp.compat.StringHelper");
            return $"StringHelper.newString({f.Transform(al.Arguments[0].Expression, c)}, {f.Transform(al.Arguments[1].Expression, c)})";
        }

        // new string(ReadOnlySpan<char>/Span<char>): Java has no such constructor;
        // the compat spans render their characters through toString().
        if (type?.SpecialType == SpecialType.System_String
            && al is { Arguments.Count: 1 }
            && c.GetTypeInfo(al.Arguments[0].Expression).Type is INamedTypeSymbol spanArg
            && spanArg.Name is "Span" or "ReadOnlySpan"
            && spanArg.ContainingNamespace?.ToDisplayString() == "System"
            && spanArg.TypeArguments.Length == 1
            && spanArg.TypeArguments[0].SpecialType == SpecialType.System_Char)
        {
            return $"{f.Transform(al.Arguments[0].Expression, c)}.toString()";
        }
        if (init?.IsKind(SyntaxKind.ObjectInitializerExpression) == true)
        {
            if (init.Expressions.Count == 0) return EmitNew(name, al, type as INamedTypeSymbol, c);
            var tmp = c.GenerateSyntheticName("_obj");
            c.AddPreStatement($"{name} {tmp} = {EmitNew(name, al, type as INamedTypeSymbol, c)};");
            foreach (AssignmentExpressionSyntax e in init.Expressions)
            {
                if (e.Left is not IdentifierNameSyntax id) continue;
                var v = f.Transform(e.Right, c);
                c.AddPreStatement(c.GetSymbolInfo(id).Symbol is IPropertySymbol p
                    ? $"{tmp}.set{char.ToUpperInvariant(p.Name[0])}{p.Name[1..]}({v});"
                    : $"{tmp}.{id.Identifier.Text} = {v};");
            }
            return tmp;
        }
        if (init?.IsKind(SyntaxKind.CollectionInitializerExpression) == true)
        {
            var tmp = c.GenerateSyntheticName("_col");
            c.AddPreStatement($"var {tmp} = new {name}();");
            foreach (var e in init.Expressions)
                if (e is InitializerExpressionSyntax { Expressions.Count: 2 } kv)
                    c.AddPreStatement($"{tmp}.put({f.Transform(kv.Expressions[0], c)}, {f.Transform(kv.Expressions[1], c)});");
                else
                    c.AddPreStatement($"{tmp}.add({f.Transform(e, c)});");
            return tmp;
        }
        return EmitNew(name, al, type as INamedTypeSymbol, c);
    }
    private static string EmitNew(string name, ArgumentListSyntax? al, INamedTypeSymbol? type, ConversionContext c)
    {
        // `new Runnable(methodGroup)` (closure-lift emits `new Action(LocalMethod)`):
        // Java functional interfaces are satisfied by a method reference, not by
        // construction — emit `Owner::method` and let target typing pick the shape.
        if (type is { TypeKind: TypeKind.Delegate }
            && al is { Arguments.Count: 1 }
            && c.GetSymbolInfo(al.Arguments[0].Expression).Symbol is IMethodSymbol methodGroup)
        {
            return EmitDelegateMethodReference(al.Arguments[0].Expression, methodGroup, c);
        }

        // C09 Java 半区：C# 的 new ThreadLocal<T>() 首访返回 default(T)（值类型为
        // 0/false/默认 struct），而 java.lang.ThreadLocal 首访恒为 null，值类型
        // 拆箱即 NPE。无参构造非 Nullable 值类型 T 时，补一个每线程默认值工厂
        // （C# 侧 () => default(T) 与无参 holder 语义恒等）。引用类型与 Nullable<T>
        // 的默认本就是 null，保持无参构造。预处理产物与用户直写的 ThreadLocal 同走此路。
        if (al is null or { Arguments.Count: 0 }
            && type is { IsValueType: false, TypeArguments.Length: 1 } named
            && named.OriginalDefinition.MetadataName == "ThreadLocal`1"
            && named.ContainingNamespace?.ToDisplayString() == "System.Threading"
            && named.TypeArguments[0] is { } held
            && held.IsValueType
            && held is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
        {
            var defaultValue = TypeOperationTransformer.GetDefaultValueForType(c.MapType(held), held, c);
            return $"new {name}(() -> {defaultValue})";
        }

        var args = ArgumentTransformer.TransformArgumentList(al, c, ExpressionTransformerFacade.Instance);
        return $"new {name}({args})";
    }
    /// <summary>
    /// Renders a single-method-group delegate construction as a Java method
    /// reference: local/static identifier → <c>Owner::camelName</c>, member
    /// access → <c>receiver::camelName</c> (instance) or the mapped owner type.
    /// </summary>
    internal static string EmitDelegateMethodReference(
        ExpressionSyntax groupExpression, IMethodSymbol methodGroup, ConversionContext c)
    {
        string owner;
        if (groupExpression is MemberAccessExpressionSyntax memberAccess)
        {
            // A primitive-keyword receiver is a Java keyword, not a type name:
            // char.IsHighSurrogate → Character::isHighSurrogate.
            owner = memberAccess.Expression is PredefinedTypeSyntax
                ? PrimitiveWrapperName(methodGroup.ContainingType.SpecialType) ?? c.MapType(methodGroup.ContainingType)
                : ExpressionTransformerFacade.Instance.Transform(memberAccess.Expression, c);
        }
        else
        {
            owner = methodGroup.IsStatic
                ? c.MapType(methodGroup.ContainingType)
                : "this";
        }

        var methodName = methodGroup.Name.Length == 0
            ? methodGroup.Name
            : char.ToLowerInvariant(methodGroup.Name[0]) + methodGroup.Name[1..];
        return $"{owner}::{methodName}";
    }

    private static string PrimitiveWrapperName(SpecialType specialType) => specialType switch
    {
        SpecialType.System_Boolean => "Boolean",
        SpecialType.System_Byte or SpecialType.System_UInt16 or SpecialType.System_Int32
            or SpecialType.System_UInt32 => "Integer",
        SpecialType.System_SByte => "Byte",
        SpecialType.System_Int16 => "Short",
        SpecialType.System_Char => "Character",
        SpecialType.System_Int64 or SpecialType.System_UInt64 => "Long",
        SpecialType.System_Single => "Float",
        SpecialType.System_Double => "Double",
        _ => null
    };

    private static string NewAnonymous(AnonymousObjectCreationExpressionSyntax n, ConversionContext c)
    {
        // C21 M5: anonymous object creation is lowered to a named type by the
        // anonymous-type-lift preprocessor (default-on in the convert-project
        // chain) before Java conversion. The legacy Map.of(...) arm is retired;
        // a residual anonymous creation on the direct pipeline is a hard error
        // instead of an untyped heterogeneous map that drops shape/equality.
        throw new NotSupportedException(
            "CS2J normalization required: anonymous object creation 'new { ... }' must be lifted to a named type " +
            "by the anonymous-type-lift preprocessor before Java conversion.");
    }
    private string NewArray(ArrayCreationExpressionSyntax n, ConversionContext c)
    {
        var f = ExpressionTransformerFacade.Instance;
        var es = c.GetTypeInfo(n.Type.ElementType).Type;
        var elem = es?.SpecialType switch
        {
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte => "int",
            _ => es != null ? c.MapType(es) : c.MapTypeFromSyntax(n.Type.ElementType)
        };
        var sizes = n.Type.RankSpecifiers.SelectMany(rs => rs.Sizes
            .Select(s => s.IsKind(SyntaxKind.OmittedArraySizeExpression) ? "" : f.Transform(s, c))).ToList();
        return EmitArray(elem, sizes, n.Initializer, c, es?.TypeKind == TypeKind.TypeParameter);
    }
    private string NewImplicitArray(ImplicitArrayCreationExpressionSyntax n, ConversionContext c)
    {
        var at = (c.GetTypeInfo(n).Type ?? c.GetTypeInfo(n).ConvertedType) as IArrayTypeSymbol;
        return EmitArray(at != null ? c.MapType(at.ElementType) : "Object",
            Enumerable.Repeat("", n.Commas.Count + 1).ToList(), n.Initializer, c);
    }
    private string NewBareArray(InitializerExpressionSyntax n, ConversionContext c)
    {
        var arr = (c.GetTypeInfo(n).ConvertedType ?? c.GetTypeInfo(n).Type) as IArrayTypeSymbol;
        if (arr != null)
            return EmitArray(arr.ElementType.SpecialType == SpecialType.System_Byte ? "byte" : c.MapType(arr.ElementType),
                Enumerable.Repeat("", arr.Rank).ToList(), n, c);
        if (n.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Type: ArrayTypeSyntax at } } })
            return EmitArray(c.MapTypeFromSyntax(at.ElementType),
                Enumerable.Repeat("", at.RankSpecifiers.FirstOrDefault()?.Sizes.Count ?? 1).ToList(), n, c);
        return ArrayBody(n, "Object", c);
    }
    private static string EmitArray(string elem, List<string> sizes, InitializerExpressionSyntax? init,
        ConversionContext c, bool typeParam = false)
    {
        // C# decimal[] elements default to 0m; Java Decimal reference arrays
        // start as null. Allocate through the compat factory which fills with
        // Decimal.ZERO (only for sized 1D creation without an initializer).
        if (init == null && elem == "Decimal" && sizes.Count == 1
            && sizes[0] is { Length: > 0 } length)
        {
            c.AddImport("io.github.ningpp.compat.Decimal");
            return $"Decimal.array({length})";
        }
        var gen = elem.IndexOf('<');
        var sb = new StringBuilder();
        if (typeParam || gen > 0)
            sb.Append('(').Append(elem).Append(string.Concat(Enumerable.Repeat("[]", sizes.Count))).Append(") ");
        sb.Append("new ").Append(typeParam ? "Object" : gen > 0 ? elem[..gen] : elem);
        for (var i = 0; i < sizes.Count; i++)
            sb.Append('[').Append(init == null ? sizes[i] : "").Append(']');
        if (init != null) sb.Append(ArrayBody(init, elem, c));
        return sb.ToString();
    }
    private static string ArrayBody(InitializerExpressionSyntax init, string elem, ConversionContext c)
    {
        var f = ExpressionTransformerFacade.Instance;
        var vals = init.Expressions.Select(e =>
        {
            var v = f.Transform(e, c);
            // C# byte is unsigned: an element expression frequently lowers to a
            // widened int (0xF4 & 0xFF, UnsignedMath.toByteInt32, ...), which Java
            // rejects in a byte[] initializer. Restore the narrowing cast.
            if (elem == "byte" && !v.TrimStart().StartsWith("(byte)", StringComparison.Ordinal))
                v = $"(byte) ({v})";
            return v;
        });
        return $"{{ {string.Join(", ", vals)} }}";
    }
    private string NewStackAlloc(StackAllocArrayCreationExpressionSyntax n, ConversionContext c)
        => throw new NotSupportedException(
            $"CS2J normalization required: stackalloc in {n.Kind()} must be lowered by a preprocessor before Java conversion.");
    internal static string WrapArrayForCollectionArg(string expr, IArrayTypeSymbol arrayType, ConversionContext c)
        => expr;
    public static bool HasNewConstraintObjectCreation(IMethodSymbol method)
        => method.TypeParameters.Any(tp => tp.HasConstructorConstraint);
}
