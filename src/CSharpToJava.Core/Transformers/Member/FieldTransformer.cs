using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Analysis;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;
using CSharpToJava.Core.Transformers.Expression.Utilities;

namespace CSharpToJava.Core.Transformers.Member;

/// <summary>
/// 字段转换器
/// </summary>
public class FieldTransformer : IMemberTransformer
{
    public JavaSyntaxNode Transform(MemberDeclarationSyntax node, ConversionContext context)
    {
        if (node is not FieldDeclarationSyntax fieldDecl)
        {
            throw new ArgumentException($"Expected FieldDeclarationSyntax, got {node.GetType()}");
        }

        // Use TransformAll and return a collection (for interface compatibility)
        var all = TransformAll(fieldDecl, context).ToList();
        return new JavaMemberCollection(all);
    }

    /// <summary>
    /// Transforms all variables in a field declaration (handles multi-variable declarations like: int x, y, z;).
    /// Yields both <see cref="JavaFieldDeclaration"/> nodes and, for static generic fields whose type
    /// references the enclosing class's type parameters, <see cref="JavaMethodDeclaration"/> accessor
    /// methods that replace the inexpressible Java static field.
    /// </summary>
    public IEnumerable<JavaSyntaxNode> TransformAll(FieldDeclarationSyntax fieldDecl, ConversionContext context)
    {
        var typeInfo = context.GetTypeInfo(fieldDecl.Declaration.Type);
        var fieldTypeSymbol = typeInfo.Type;
        var javaType = typeInfo.Type != null
            ? context.MapType(typeInfo.Type)
            : context.MapTypeFromSyntax(fieldDecl.Declaration.Type);
        var modifiers = ConvertModifiers(fieldDecl.Modifiers);
        var firstVariable = fieldDecl.Declaration.Variables.FirstOrDefault();
        var fieldSymbol = firstVariable != null
            ? context.GetDeclaredSymbol(firstVariable)
            : null;
        var sharedComment = context.GetDeclarationComments(fieldDecl, fieldSymbol).ToCombinedComment();

        // Handle const/readonly modifiers
        if (fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
            modifiers |= JavaModifiers.Static | JavaModifiers.Final;
        // static readonly → static final (safe: only static constructors can reassign, which we don't generate)
        // Instance readonly fields are NOT marked final here — C# constructors can reassign them,
        // which would break Java's final semantics. StructTransformer handles struct readonly fields separately.
        if (fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword))
            && fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
            && fieldDecl.Declaration.Variables.All(v => v.Initializer != null))
            modifiers |= JavaModifiers.Final;
        bool commentAssigned = false;
        if (fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.FixedKeyword)))
        {
            var elementType = fieldDecl.Declaration.Type;
            var elementTypeName = elementType is PredefinedTypeSyntax pre
                ? pre.Keyword.Text
                : elementType.ToString();

            foreach (var variable in fieldDecl.Declaration.Variables)
            {
                var varName = variable.Identifier.Text;
                var arraySizeStr = variable.ArgumentList?.Arguments.FirstOrDefault()?.ToString() ?? "0";
                var info = FfmHelper.CreatePointerInfo(varName, elementTypeName);
                long byteSize = info.ElementSize * (int.TryParse(arraySizeStr, out var n) ? n : 0);

                var javaField = new JavaFieldDeclaration
                {
                    Name = varName,
                    Type = "MemorySegment",
                    Modifiers = modifiers,
                    Initializer = $"Arena.ofAuto().allocate({byteSize}, ValueLayout.{info.ValueLayoutName})",
                    LeadingComment = sharedComment
                };
                commentAssigned = true;
                yield return javaField;
            }

            var imports = FfmHelper.GetRequiredImports(true);
            foreach (var imp in imports)
                context.AddImport(imp);

            yield break;
        }

        // Issue 5: volatile non-primitive field needs a heads-up comment.
        bool isVolatile = fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.VolatileKeyword));
        bool isJavaPrimitive = javaType is "int" or "long" or "short" or "byte" or
                                            "float" or "double" or "char" or "boolean";

        // Issue 4: track variable names declared earlier in the same multi-variable declaration
        // so we can warn when a subsequent initializer cross-references a prior variable.
        var declaredNames = new HashSet<string>();

        // Static fields in generic classes whose type references the class's own type parameters
        // cannot be expressed in Java (static members may not reference enclosing type parameters).
        // Convert them to generic static methods that take the runtime Class<?> tokens.
        bool convertStaticGenericFieldToMethod = ShouldConvertStaticFieldToGenericMethod(
            fieldDecl, fieldTypeSymbol, context, out var referencedClassTypeParameters);

        foreach (var variable in fieldDecl.Declaration.Variables)
        {
            if (convertStaticGenericFieldToMethod && variable.Initializer != null)
            {
                var syntheticMethod = BuildStaticGenericFieldAccessorMethod(
                    variable,
                    javaType,
                    modifiers,
                    referencedClassTypeParameters,
                    sharedComment,
                    context);
                if (syntheticMethod != null)
                {
                    yield return syntheticMethod;
                }
                continue;
            }

            var javaField = new JavaFieldDeclaration
            {
                Type = javaType,
                Name = ConversionContext.EscapeJavaKeyword(variable.Identifier.Text),
                Modifiers = modifiers
            };
            if (!commentAssigned)
            {
                javaField.LeadingComment = sharedComment;
                commentAssigned = !string.IsNullOrWhiteSpace(sharedComment);
            }

            // Issue 4: warn when this initializer references an earlier variable in the same declaration.
            if (declaredNames.Count > 0 && variable.Initializer != null)
            {
                bool crossRef = variable.Initializer
                    .DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Any(id => declaredNames.Contains(id.Identifier.Text));
                if (crossRef)
                    javaField.LeadingComment = ConvertedCommentSet.JoinComments(javaField.LeadingComment, "// NOTE: Initializer order may differ from C# instance field semantics.");
            }
            declaredNames.Add(variable.Identifier.Text);

            // C# structs are value types that can never be null — initialize fields
            // with default instances so Java code doesn't encounter null struct references.
            // C# enum fields without initializers default to the member with value 0,
            // but Java enum references default to null. Initialize to the 0-valued member.
            if (variable.Initializer == null
                && fieldTypeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                bool isFlags = context.IsFlagsEnum(enumType.Name)
                    || enumType.GetAttributes().Any(a =>
                        a.AttributeClass?.Name is "FlagsAttribute" or "Flags");
                if (!isFlags)
                {
                    var enumTypeRef = BuildEnumTypeReference(enumType);
                    var zeroMember = FindEnumMemberByValue(enumType, 0);
                    javaField.Initializer = zeroMember != null
                        ? $"{enumTypeRef}.{zeroMember}"
                        : $"{enumTypeRef}.values()[0]";
                }
            }
            else if (variable.Initializer == null
                && fieldTypeSymbol?.SpecialType == SpecialType.System_Decimal)
            {
                context.AddImport("io.github.ningpp.compat.Decimal");
                javaField.Initializer = "Decimal.ZERO";
            }
            else if (variable.Initializer == null
                && fieldTypeSymbol != null
                && StructCloneHelper.IsUserDefinedStruct(fieldTypeSymbol))
            {
                javaField.Initializer = $"new {javaType}()";
            }
            else if (variable.Initializer == null
                && fieldTypeSymbol is ITypeParameterSymbol typeParam)
            {
                var binding = context.GetBindingAnalyzer().GetBinding(typeParam);
                if (binding.Kind == TypeParameterBindingKind.AlwaysSameStruct
                    && binding.ConcreteStructType != null)
                {
                    var concreteJavaType = context.MapType(binding.ConcreteStructType);
                    javaField.Initializer = $"new {concreteJavaType}()";
                }
                else if (binding.Kind == TypeParameterBindingKind.Unknown)
                {
                    // null is the honest default — subclasses with struct bindings
                    // get proper initialization through the ClassTransformer's
                    // AddInheritedStructFieldInitializers constructor patching.
                }
            }

            if (variable.Initializer != null)
            {
                var previousStaticContext = context.IsInStaticMember;
                if ((modifiers & JavaModifiers.Static) != 0)
                    context.IsInStaticMember = true;

                // For const fields, inline the compile-time constant value when available.
                // This avoids non-constant expressions like enumMember.getValue() in Java
                // static final field initializers, which break switch-case usage.
                // However, preserve references to System primitive static constants
                // (e.g. Int32.MaxValue, UInt32.MaxValue) so IdentifierExpressionTransformer
                // can map them to Java wrapper constants like Integer.MAX_VALUE.
                Optional<object> constVal = default;
                bool hasConstantValue = false;
                if ((modifiers & JavaModifiers.Final) != 0
                    && (modifiers & JavaModifiers.Static) != 0
                    && context.SemanticModel != null)
                {
                    var constantValue = context.SemanticModel.GetConstantValue(variable.Initializer.Value);
                    if (constantValue.HasValue)
                    {
                        constVal = constantValue.Value;
                        hasConstantValue = true;
                    }
                }

                bool inlineConstLiteral = hasConstantValue;
                if (inlineConstLiteral
                    && context.SemanticModel!.GetSymbolInfo(variable.Initializer.Value).Symbol is IFieldSymbol { IsStatic: true } staticField
                    && staticField.ContainingType is INamedTypeSymbol containingType
                    && containingType.ContainingNamespace?.ToDisplayString() == "System"
                    && containingType.SpecialType != SpecialType.None)
                {
                    inlineConstLiteral = false;
                }

                if (inlineConstLiteral)
                {
                    javaField.Initializer = RenderConstantValue(constVal.Value);
                }
                else
                {
                    javaField.Initializer = Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(variable.Initializer.Value, context);
                }
                context.IsInStaticMember = previousStaticContext;

                if (context.SemanticModel != null && fieldTypeSymbol != null && IsCollectionOrListInterface(fieldTypeSymbol))
                {
                    var initTypeInfo = context.GetTypeInfo(variable.Initializer.Value);
                    var arrayType = initTypeInfo.Type as IArrayTypeSymbol ?? initTypeInfo.ConvertedType as IArrayTypeSymbol;
                    if (arrayType != null)
                        javaField.Initializer = ObjectCreationTransformer.WrapArrayForCollectionArg(javaField.Initializer, arrayType, context);
                }

                if (IsEnumerableOrCollectionTypeSyntax(fieldDecl.Declaration.Type)
                    && variable.Initializer.Value is ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax)
                {
                    // Fallback when semantic model doesn't surface array type information.
                    // Java Arrays.stream only supports int[], long[], double[], and T[].
                    if (javaField.Initializer.StartsWith("new int[", StringComparison.Ordinal)
                        || javaField.Initializer.StartsWith("new long[", StringComparison.Ordinal)
                        || javaField.Initializer.StartsWith("new double[", StringComparison.Ordinal))
                    {
                        context.AddImport("java.util.Arrays");
                        context.AddImport("java.util.stream.Collectors");
                        context.AddImport("java.util.ArrayList"); context.AddImport("io.github.ningpp.compat.CSharpList");
                        javaField.Initializer = $"Arrays.stream({javaField.Initializer}).boxed().collect(CSharpList.toCSharpList())";
                    }
                    else if (javaField.Initializer.StartsWith("new short[", StringComparison.Ordinal)
                        || javaField.Initializer.StartsWith("new byte[", StringComparison.Ordinal)
                        || javaField.Initializer.StartsWith("new float[", StringComparison.Ordinal)
                        || javaField.Initializer.StartsWith("new boolean[", StringComparison.Ordinal)
                        || javaField.Initializer.StartsWith("new char[", StringComparison.Ordinal))
                    {
                        context.AddImport("java.util.stream.IntStream");
                        context.AddImport("java.util.stream.Collectors");
                        context.AddImport("java.util.ArrayList"); context.AddImport("io.github.ningpp.compat.CSharpList");
                        javaField.Initializer = $"IntStream.range(0, {javaField.Initializer}.length).mapToObj(i -> {javaField.Initializer}[i]).collect(CSharpList.toCSharpList())";
                    }
                    else
                    {
                        context.AddImport("io.github.ningpp.compat.ArrayHelper");
                        javaField.Initializer = $"ArrayHelper.toList({javaField.Initializer})";
                    }
                }

                javaField.Initializer = ExpressionTransformerHelpers.AdaptExpressionToTargetType(
                    variable.Initializer.Value,
                    javaField.Initializer,
                    fieldTypeSymbol,
                    context);
            }

            // Issue 5: suggest AtomicReference for volatile fields of non-primitive types.
            if (isVolatile && !isJavaPrimitive)
                javaField.LeadingComment = ConvertedCommentSet.JoinComments(javaField.LeadingComment, "// Consider replacing with AtomicReference<T> for idiomatic Java concurrency.");

            yield return javaField;
        }
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;

        foreach (var modifier in modifiers)
        {
            result |= modifier.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                SyntaxKind.InternalKeyword => JavaModifiers.Public,
                SyntaxKind.StaticKeyword => JavaModifiers.Static,
                SyntaxKind.ConstKeyword => JavaModifiers.Static | JavaModifiers.Final,
                SyntaxKind.ReadOnlyKeyword => JavaModifiers.None,
                SyntaxKind.VolatileKeyword => JavaModifiers.Volatile,
                SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                SyntaxKind.NewKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
        }

        // C# "protected internal" maps to Protected | Public via individual keyword rules.
        // Java doesn't allow both; "protected" is the most restrictive useful choice.
        if ((result & JavaModifiers.Protected) != 0 && (result & JavaModifiers.Public) != 0)
            result &= ~JavaModifiers.Public;

        // C# fields default to private when no access modifier is specified
        if ((result & (JavaModifiers.Public | JavaModifiers.Protected | JavaModifiers.Private)) == 0)
            result |= JavaModifiers.Private;

        return result;
    }

    private static bool IsCollectionOrListInterface(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named)
        {
            if (named.Name is "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
                && named.ContainingNamespace?.ToDisplayString().StartsWith("System") == true)
                return true;

            return named.AllInterfaces.Any(i =>
                i.Name is "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
                && i.ContainingNamespace?.ToDisplayString().StartsWith("System") == true);
        }

        return false;
    }

    private static bool IsEnumerableOrCollectionTypeSyntax(TypeSyntax type)
    {
        var text = type.ToString();
        return text.Contains("IEnumerable", StringComparison.Ordinal)
            || text.Contains("ICollection", StringComparison.Ordinal)
            || text.Contains("IList", StringComparison.Ordinal)
            || text.Contains("IReadOnlyCollection", StringComparison.Ordinal)
            || text.Contains("IReadOnlyList", StringComparison.Ordinal);
    }

    private static string BuildEnumTypeReference(INamedTypeSymbol enumType)
    {
        return enumType.ContainingType is INamedTypeSymbol parentType
            ? $"{BuildEnumTypeReference(parentType)}.{enumType.Name}"
            : enumType.Name;
    }

    private static string? FindEnumMemberByValue(INamedTypeSymbol enumType, long value)
    {
        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol { IsConst: true, HasConstantValue: true } field)
            {
                if (field.ConstantValue is int intVal && intVal == value)
                    return field.Name;
                if (field.ConstantValue is long longVal && longVal == value)
                    return field.Name;
                if (field.ConstantValue is short shortVal && shortVal == value)
                    return field.Name;
                if (field.ConstantValue is byte byteVal && byteVal == value)
                    return field.Name;
                if (field.ConstantValue is sbyte sbyteVal && sbyteVal == value)
                    return field.Name;
                if (field.ConstantValue is ushort ushortVal && ushortVal == value)
                    return field.Name;
                if (field.ConstantValue is uint uintVal && uintVal == value)
                    return field.Name;
                if (field.ConstantValue is ulong ulongVal && ulongVal == (ulong)value)
                    return field.Name;
            }
        }
        return null;
    }

    /// <summary>
    /// Determines whether a static field in a generic class must be converted to a
    /// generic static method because its type references one of the enclosing class's
    /// type parameters (Java forbids static members from referencing enclosing type params).
    /// </summary>
    private static bool ShouldConvertStaticFieldToGenericMethod(
        FieldDeclarationSyntax fieldDecl,
        ITypeSymbol? fieldTypeSymbol,
        ConversionContext context,
        out List<ITypeParameterSymbol> referencedClassTypeParameters)
    {
        referencedClassTypeParameters = new List<ITypeParameterSymbol>();

        if (!fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            return false;

        var enclosingType = context.CurrentEnclosingRoslynType;
        if (enclosingType == null || enclosingType.TypeParameters.Length == 0)
            return false;

        referencedClassTypeParameters = CollectReferencedClassTypeParameters(
            fieldTypeSymbol, enclosingType);
        return referencedClassTypeParameters.Count > 0;
    }

    /// <summary>
    /// Collects the enclosing class's type parameters that are referenced by the given type,
    /// preserving the class's type parameter declaration order.
    /// </summary>
    private static List<ITypeParameterSymbol> CollectReferencedClassTypeParameters(
        ITypeSymbol? typeSymbol,
        INamedTypeSymbol enclosingType)
    {
        var referenced = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);

        Visit(typeSymbol);

        return enclosingType.TypeParameters
            .Where(tp => referenced.Contains(tp))
            .ToList();

        void Visit(ITypeSymbol? type)
        {
            if (type == null)
                return;

            if (type is ITypeParameterSymbol tp
                && SymbolEqualityComparer.Default.Equals(tp.DeclaringType, enclosingType))
            {
                referenced.Add(tp);
                return;
            }

            if (type is INamedTypeSymbol named)
            {
                foreach (var arg in named.TypeArguments)
                    Visit(arg);
            }
            else if (type is IArrayTypeSymbol array)
            {
                Visit(array.ElementType);
            }
        }
    }

    /// <summary>
    /// Builds a generic static accessor method that replaces a static field whose type
    /// references the enclosing class's type parameters.
    /// </summary>
    private static JavaMethodDeclaration? BuildStaticGenericFieldAccessorMethod(
        VariableDeclaratorSyntax variable,
        string fieldJavaType,
        JavaModifiers fieldModifiers,
        List<ITypeParameterSymbol> classTypeParameters,
        string? leadingComment,
        ConversionContext context)
    {
        if (variable.Initializer == null)
            return null;

        var previousStaticContext = context.IsInStaticMember;
        context.IsInStaticMember = true;

        try
        {
            var initializer = Transformers.Expression.ExpressionTransformerFacade.Instance
                .Transform(variable.Initializer.Value, context);

            // Drain any pre-statements produced by the initializer (e.g. object initializers)
            // so they become part of the method body rather than a static initializer block.
            var bodyBuilder = new System.Text.StringBuilder();
            if (context.HasPendingPreStatements)
            {
                foreach (var preStmt in context.DrainPreStatements())
                {
                    bodyBuilder.AppendLine(preStmt.TrimEnd().TrimEnd(';') + ";");
                }
            }
            bodyBuilder.Append("return ").Append(initializer).Append(';');

            var method = new JavaMethodDeclaration
            {
                Modifiers = fieldModifiers & ~(JavaModifiers.Final | JavaModifiers.Volatile | JavaModifiers.Transient),
                ReturnType = fieldJavaType,
                Name = ConversionContext.EscapeJavaKeyword(variable.Identifier.Text),
                Body = bodyBuilder.ToString(),
                LeadingComment = leadingComment
            };

            foreach (var typeParameter in classTypeParameters)
            {
                method.TypeParameters.Add(new JavaTypeParameter(typeParameter.Name));
                var paramName = Transformers.Type.ClassTransformer.RuntimeClassParameterName(typeParameter.Name);
                method.Parameters.Add(new JavaParameter("Class<?>", paramName));
            }

            return method;
        }
        finally
        {
            context.IsInStaticMember = previousStaticContext;
        }
    }

    private static string RenderConstantValue(object? value)
    {
        if (value == null)
            return "null";

        if (value is bool b)
            return b ? "true" : "false";

        if (value is string s)
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";

        if (value is char c)
            return EscapeJavaChar(c);

        if (value is float f)
            return f.ToString(System.Globalization.CultureInfo.InvariantCulture) + "f";

        if (value is double d)
        {
            var text = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return text.Contains('.') ? text : text + ".0";
        }

        if (value is decimal dec)
            return $"Decimal.parse(\"{dec.ToString(System.Globalization.CultureInfo.InvariantCulture)}\")";

        if (value is long l)
            return l.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L";

        // Render unsigned integral constants as hexadecimal literals. C# uint/ulong values
        // may exceed the signed range (e.g. 0xFF000000 = 4278190080), which is illegal as a
        // decimal Java int/long literal. Hexadecimal literals encode the same bit pattern
        // and are valid for any 32/64-bit value.
        if (value is uint ui)
            return $"0x{ui:X}";

        if (value is ulong ul)
            return $"0x{ul:X}L";

        return value.ToString() ?? "null";
    }

    /// <summary>
    /// Escapes a C# character value as a valid Java character literal.
    /// </summary>
    private static string EscapeJavaChar(char c)
    {
        return c switch
        {
            '\n' => "'\\n'",
            '\r' => "'\\r'",
            '\t' => "'\\t'",
            '\0' => "'\\0'",
            '\b' => "'\\b'",
            '\f' => "'\\f'",
            '\\' => "'\\\\'",
            '\'' => "'\\''",
            _ when c < 0x20 => $"'\\u{((int)c):X4}'",
            _ => $"'{c}'"
        };
    }
}
