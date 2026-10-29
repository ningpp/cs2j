using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;

namespace CSharpToJava.Core.Transformers.Statement;

public partial class StatementTransformer
{
    /// <summary>
    /// Local declarations map directly: one explicit Java type, one or more
    /// declarators with optional initializers, 'const'/'readonly' → final.
    /// Implicit 'var' is resolved through the semantic model. ref locals,
    /// pointers, fixed-size buffers and tuple deconstructions must be lowered
    /// by a C#→C# preprocessor and fail fast here.
    /// </summary>
    private JavaSyntaxNode TransformLocalDeclaration(LocalDeclarationStatementSyntax stmt, ConversionContext context)
    {
        if (stmt.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
        {
            throw new NotSupportedException(
                "CS2J normalization required: using-declarations are handled as try-with-resources by the statement dispatcher.");
        }

        if (stmt.Declaration.Type is RefTypeSyntax)
        {
            throw new NotSupportedException(
                "CS2J normalization required: ref local must be lowered by a preprocessor before Java conversion.");
        }

        if (stmt.Declaration.Type is TupleTypeSyntax)
        {
            throw new NotSupportedException(
                "CS2J normalization required: deconstruction local declaration must be lowered by a preprocessor before Java conversion.");
        }

        var javaType = ResolveLocalJavaType(stmt, context);
        var isFinal = stmt.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword) || m.IsKind(SyntaxKind.ReadOnlyKeyword));

        var exprTransformer = ExpressionTransformerFacade.Instance;
        var plainDeclarators = new List<string>();
        JavaExpression? singleInitializerIR = null;
        string? singleResolvedInitType = null;

        foreach (var variable in stmt.Declaration.Variables)
        {
            var localSymbol = context.GetDeclaredSymbol(variable) as ILocalSymbol;
            if (localSymbol?.Type is IPointerTypeSymbol)
            {
                throw new NotSupportedException(
                    $"CS2J normalization required: pointer local '{variable.Identifier.Text}' must be lowered by a preprocessor before Java conversion.");
            }

            var name = context.MethodState.GetEffectiveLocalJavaName(
                context.GetDeclaredSymbol(variable),
                ConversionContext.EscapeJavaKeyword(variable.Identifier.Text));

            var initExprText = variable.Initializer != null
                ? exprTransformer.Transform(variable.Initializer.Value, context)
                : null;
            if (initExprText != null
                && variable.Initializer != null
                && localSymbol?.Type is { } localType)
            {
                initExprText = SpanCoercion.CoerceOrSame(
                    variable.Initializer.Value, initExprText, localType, context);
                // IList 家族局部槽位（List<T>/getValues() 值 → CSharpIList）：包适配
                // 视图（真实链 XmlSchemaSet 的 chameleonKeys 读取 Hashtable.Keys）。
                initExprText = IListCoercion.CoerceOrSame(
                    variable.Initializer.Value, initExprText, localType, context);
                // JsonConvert.DeserializeObject<T> erases to Object in Java;
                // restore the declared local type so member access resolves.
                if (initExprText.StartsWith("JsonConvert.deserializeObject(", StringComparison.Ordinal))
                {
                    var mappedLocal = context.MapType(localType);
                    if (mappedLocal is not ("Object" or "var"))
                        initExprText = $"({mappedLocal}) {initExprText}";
                }
                // 常量数字 → 枚举局部（`RegexOptions optionsLast = 0;`）：C# 允许
                // 隐式常量转换，Java 枚举是类，必须 fromValue/values() 包装。
                // Int-constant 元数据枚举（BindingFlags/TypeAttributes 等）在 Java
                // 侧就是 int，数字初始化直接赋值即可。
                if (localType is INamedTypeSymbol { TypeKind: TypeKind.Enum } localEnum
                    && !CSharpToJava.Core.Transformers.Type.EnumTransformer
                        .IsIntConstantMetadataEnum(localEnum)
                    && context.GetTypeInfo(variable.Initializer.Value).Type?.TypeKind
                        != TypeKind.Enum)
                {
                    var mappedEnum = context.MapType(localEnum);
                    var storage = CSharpToJava.Core.Transformers.Type.EnumTransformer.StorageTypeName(
                        localEnum.EnumUnderlyingType?.SpecialType ?? SpecialType.System_Int32);
                    initExprText = CSharpToJava.Core.Transformers.Type.EnumTransformer.IsValueEnum(localEnum)
                        ? $"{mappedEnum}.fromValue(({storage}){initExprText})"
                        : $"{mappedEnum}.values()[{initExprText}]";
                }
                // 委托目标局部声明的方法组 → 方法引用（真实链
                // ReflectionXmlSerializationReader 的
                // `UnknownNodeAction elementElseAction = CreateUnknownNodeException;`，
                // 裸方法名落成标识符，javac: 找不到符号 变量 CreateUnknownNodeException）。
                if (ArgumentTransformer.TryEmitDelegateMethodReference(
                        variable.Initializer.Value, localType, context, out var declMethodRef))
                {
                    initExprText = declMethodRef;
                }
                // IEnumerable<T> locals receive concrete collections whose Java
                // classes may not implement compat CSharpGenericIterable (e.g. a
                // class implementing only non-generic interfaces).
                initExprText = IterableCoercion.CoerceOrSame(
                    variable.Initializer.Value, initExprText, localType, context);
            }

            // 无初始化器的引用类型局部：Java 没有 C# 的 definite-assignment 流分析，
            // 后续若在分支/短路表达式中先读后写会报“可能尚未初始化”，补 null。
            plainDeclarators.Add(initExprText != null
                ? $"{name} = {initExprText}"
                : localSymbol?.Type.IsReferenceType == true
                    ? $"{name} = null"
                    : name);

            if (stmt.Declaration.Variables.Count == 1 && variable.Initializer != null)
            {
                singleInitializerIR = new JavaRawExpression(initExprText!);
                var initType = context.GetTypeInfo(variable.Initializer.Value) is { } ti
                    ? (ti.Type ?? ti.ConvertedType)
                    : null;
                if (initType is { TypeKind: not TypeKind.Error })
                {
                    var mapped = context.MapType(initType);
                    if (!string.IsNullOrWhiteSpace(mapped))
                        singleResolvedInitType = mapped;
                }
            }
        }

        var preStatements = context.HasPendingPreStatements ? context.DrainPreStatements() : new List<string>();
        var postStatements = context.HasPendingPostStatements ? context.DrainPostStatements() : new List<string>();

        // Fast path: one plain declarator, no surrounding statements → structured IR.
        if (plainDeclarators.Count == 1
            && preStatements.Count == 0 && postStatements.Count == 0)
        {
            return new JavaVariableDeclarationStatement
            {
                Type = javaType,
                Name = stmt.Declaration.Variables.Count == 1
                    ? context.MethodState.GetEffectiveLocalJavaName(
                        context.GetDeclaredSymbol(stmt.Declaration.Variables[0]),
                        ConversionContext.EscapeJavaKeyword(stmt.Declaration.Variables[0].Identifier.Text))
                    : plainDeclarators[0],
                Initializer = singleInitializerIR,
                IsFinal = isFinal,
                ResolvedInitializerType = singleResolvedInitType
            };
        }

        var emitted = new List<JavaSyntaxNode>();
        foreach (var pre in preStatements)
            emitted.Add(new JavaStatementNode(pre.TrimEnd(';') + ";"));
        if (plainDeclarators.Count > 0)
        {
            var finalKeyword = isFinal ? "final " : "";
            emitted.Add(new JavaStatementNode($"{finalKeyword}{javaType} {string.Join(", ", plainDeclarators)};"));
        }
        foreach (var post in postStatements)
            emitted.Add(new JavaStatementNode(post.TrimEnd(';') + ";"));

        return emitted.Count == 1 ? emitted[0] : new JavaMemberCollection(emitted.ToArray());
    }

    private static string ResolveLocalJavaType(LocalDeclarationStatementSyntax stmt, ConversionContext context)
    {
        // C# 'var' maps directly to Java 'var' (Java 10+ local variable inference),
        // except when the inferred type is a type parameter: Java cannot infer a
        // generic method's T from `var`, but an explicit `T` declaration provides
        // the assignment target type (e.g. `var ex = Assert.Throws<T>(action)`).
        if (stmt.Declaration.Type is IdentifierNameSyntax { Identifier.Text: "var" })
        {
            var first = stmt.Declaration.Variables.FirstOrDefault();
            if (first != null
                && (context.GetDeclaredSymbol(first) as ILocalSymbol)?.Type is ITypeParameterSymbol typeParam)
            {
                return typeParam.Name;
            }
            // Java's `var` cannot infer a target type for a bare method reference
            // (e.g. `var projection = cond ? A::f : B::g`); use the mapped
            // functional interface so the conditional has an explicit target.
            if (first != null
                && (context.GetDeclaredSymbol(first) as ILocalSymbol)?.Type is INamedTypeSymbol
                { TypeKind: TypeKind.Delegate } delegateLocal)
            {
                var mappedDelegate = context.MapType(delegateLocal);
                if (!string.IsNullOrWhiteSpace(mappedDelegate) && mappedDelegate != "var")
                    return mappedDelegate;
            }
            // C# tuple locals map to Map.Entry; declaring them as `var` narrows
            // Java to SimpleImmutableEntry and breaks later assignments from
            // methods whose declared return type is Map.Entry.
            if (first != null
                && (context.GetDeclaredSymbol(first) as ILocalSymbol)?.Type is INamedTypeSymbol
                {
                    Name: "Tuple",
                    ContainingNamespace: { } tupleNamespace
                } tupleLocal
                && tupleNamespace.ToDisplayString() == "System"
                && context.MapType(tupleLocal) is { } mappedTuple
                && mappedTuple.StartsWith("Map.Entry", StringComparison.Ordinal))
            {
                return mappedTuple;
            }
            return "var";
        }

        var firstVariable = stmt.Declaration.Variables.FirstOrDefault();
        var localType = firstVariable != null
            ? (context.GetDeclaredSymbol(firstVariable) as ILocalSymbol)?.Type
            : null;

        string javaType;
        if (localType is { } type && type is not IErrorTypeSymbol)
        {
            javaType = context.MapType(type);
        }
        else
        {
            javaType = context.MapTypeFromSyntax(stmt.Declaration.Type);
        }

        if (string.IsNullOrWhiteSpace(javaType) || javaType == "var")
        {
            throw new NotSupportedException(
                $"CS2J normalization required: cannot resolve the Java type of local declaration '{stmt.Declaration.Type}' without a semantic model; add an explicit type before Java conversion.");
        }

        return javaType;
    }

    private JavaSyntaxNode TransformYieldReturn(YieldStatementSyntax? stmt, ConversionContext context)
        => throw new NotSupportedException(
            "CS2J normalization required: 'yield return' must be lowered by a preprocessor (iterator lowering) before Java conversion.");

    private JavaSyntaxNode TransformYieldBreak(YieldStatementSyntax? stmt, ConversionContext context)
        => throw new NotSupportedException(
            "CS2J normalization required: 'yield break' must be lowered by a preprocessor (iterator lowering) before Java conversion.");
}
