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
            ? context.GetDeclaredSymbol(firstVariable) as IFieldSymbol
            : null;
        var sharedComment = context.GetDeclarationComments(fieldDecl, fieldSymbol).ToCombinedComment();

        // C09 M5: a static [ThreadStatic] field must already have been lowered to
        // an explicit System.Threading.ThreadLocal<T> holder (reads/writes rewritten
        // to .Value) by the thread-static preprocessor, which is default-on in the
        // CLI preprocess chain. The converter no longer recognizes the attribute
        // itself; reaching this branch means the field was converted without
        // preprocessing. Instance fields are CLR-no-op for [ThreadStatic] and keep
        // passing through (the attribute is governed by attribute normalization).
        if (IsStaticThreadStaticField(fieldDecl, fieldSymbol))
        {
            throw new NotSupportedException(
                "CS2J normalization required: [ThreadStatic] static field(s) '"
                + string.Join(", ", fieldDecl.Declaration.Variables.Select(v => v.Identifier.Text))
                + "' must be lowered to a ThreadLocal<T> holder by the thread-static preprocessor before Java conversion.");
        }

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

        // Static fields in generic classes whose type references the class's own type
        // parameters cannot be expressed in Java (static members may not reference
        // enclosing type parameters). Require a preprocessor to normalize them.
        if (IsStaticFieldReferencingClassTypeParameters(fieldDecl, fieldTypeSymbol, context))
        {
            throw new NotSupportedException(
                $"CS2J normalization required: static field referencing enclosing type parameters in {fieldDecl.Kind()} must be lowered by a preprocessor before Java conversion.");
        }

        foreach (var variable in fieldDecl.Declaration.Variables)
        {
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

            // C# [JsonNumberHandling(flags)] on a field overrides the declaring type's
            // handling; compat materializes the generated attribute class from this marker
            // on the member lookup (JsonNumberHandlingAttribute is a class, not an annotation).
            var fieldNumberHandlingAttr = fieldSymbol?.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "JsonNumberHandlingAttribute");
            if (fieldNumberHandlingAttr?.ConstructorArguments.Length > 0
                && fieldNumberHandlingAttr.ConstructorArguments[0].Value != null)
            {
                javaField.Annotations.Add(new JavaAnnotation(
                    $"CompatJsonNumberHandling({fieldNumberHandlingAttr.ConstructorArguments[0].Value})"));
                context.AddImport("io.github.ningpp.compat.CompatJsonNumberHandling");
            }
            // C# sbyte[] and byte[] are both byte[] in Java but serialize differently (JSON
            // array of numbers vs. Base64 string); mark the field so the compat member lookup
            // reports the integer element type.
            if (fieldSymbol?.Type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_SByte })
            {
                javaField.Annotations.Add(new JavaAnnotation("CompatSByteArray"));
                context.AddImport("io.github.ningpp.compat.CompatSByteArray");
            }
            // C# [JsonInclude] -> compat annotation; the generated JsonIncludeAttribute is a
            // class, so compat materializes it from this marker. The SDK uses its presence to
            // reject a non-public annotated field.
            if (fieldSymbol?.GetAttributes().Any(a =>
                    a.AttributeClass?.Name == "JsonIncludeAttribute") == true)
            {
                javaField.Annotations.Add(new JavaAnnotation("CompatJsonInclude"));
                context.AddImport("io.github.ningpp.compat.CompatJsonInclude");
            }
            // C# non-public field -> compat annotation. C#'s `internal` becomes Java `public`
            // (cross-package reach), so the modifier alone cannot answer "is this member public?";
            // without the marker the SDK caches an internal field as a public member and its
            // "non-public member annotated with [JsonInclude] is invalid" validation never runs
            // (PropertyVisibilityTests.nonPublicProperty_WithJsonInclude_Invalid).
            if (fieldSymbol != null && fieldSymbol.DeclaredAccessibility != Accessibility.Public)
            {
                javaField.Annotations.Add(new JavaAnnotation("CompatNonPublicProperty"));
                context.AddImport("io.github.ningpp.compat.CompatNonPublicProperty");
            }

            // C# [JsonPropertyName("...")] on a FIELD -> compat annotation carrying the name.
            // Without it the C# JSON name never reaches the runtime, so a field whose name
            // collides with a property's JSON name is not detected and the serialize call that
            // must throw InvalidOperationException silently succeeds.
            var fieldNameAttr = fieldSymbol?.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "JsonPropertyNameAttribute");
            if (fieldNameAttr?.ConstructorArguments.Length > 0
                && fieldNameAttr.ConstructorArguments[0].Value is string jsonFieldName)
            {
                javaField.Annotations.Add(new JavaAnnotation(
                    $"CompatJsonPropertyName(\"{jsonFieldName}\")"));
                context.AddImport("io.github.ningpp.compat.CompatJsonPropertyName");
            }

            // C# enum fields default to the zero-valued member; plain Java enums are
            // ordinal-ordered (0..n-1), so values()[0] is the direct equivalent.
            // Int-constant metadata enums map to a Java primitive int — the JVM
            // default (0) is already correct, no initializer needed.
            if (variable.Initializer == null
                && fieldTypeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType
                && !CSharpToJava.Core.Transformers.Type.EnumTransformer.IsIntConstantMetadataEnum(enumType))
            {
                javaField.Initializer = $"{context.MapType(enumType)}.values()[0]";
            }
            else if (variable.Initializer == null
                && fieldTypeSymbol?.SpecialType == SpecialType.System_Decimal)
            {
                context.AddImport("io.github.ningpp.compat.Decimal");
                javaField.Initializer = "Decimal.ZERO";
            }
            else if (variable.Initializer == null
                && fieldTypeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Struct } memoryStruct
                && memoryStruct.OriginalDefinition.ToDisplayString() is "System.Memory<T>" or "System.ReadOnlyMemory<T>")
            {
                // Memory<T> is a C# struct whose default value is an empty memory,
                // never null. The Java compat type is a reference type, so create the
                // empty instance explicitly (e.g. Utf8JsonWriter._memory).
                javaField.Initializer = $"new {javaType}()";
            }
            else if (variable.Initializer == null
                && fieldTypeSymbol != null
                && StructCloneHelper.IsUserDefinedStruct(fieldTypeSymbol))
            {
                // Round 20（TS2673）：TS 目标下结构体字段默认零值填充若直发 `new X()`
                // 而 X 已被构造器工厂化（含隐式无参构造器集合 >1，声明端只剩私有
                // 无参构造器）→ tsc TS2673。零参隐式构造器恒为 Create$1（参数数量
                // 升序排序置首）；未工厂化（单构造器）或非本编译单元类型保持 new。
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
                // DEBUG: Log inlineConstLiteral for byte[] fields
                if (variable.Initializer.Value is Microsoft.CodeAnalysis.CSharp.Syntax.InitializerExpressionSyntax initDbg
                    && initDbg.Expressions.Count > 100)
                {
                    System.Console.WriteLine($"[DEBUG] FieldTransformer: field={variable.Identifier.Text}, hasConstValue={hasConstantValue}, inlineConst={inlineConstLiteral}, isInitExpr=True, count={initDbg.Expressions.Count}");
                }
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

                    // C# const enum fields inline as the ordinal integer; index values() directly.
                    // Int-constant metadata enums (BindingFlags/TypeAttributes/...) map to a
                    // Java primitive int — the folded numeric literal is already the value.
                    if (fieldTypeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumFieldType
                        && !CSharpToJava.Core.Transformers.Type.EnumTransformer.IsIntConstantMetadataEnum(enumFieldType))
                    {
                        var javaEnumType = context.MapType(enumFieldType);
                        javaField.Initializer = $"{javaEnumType}.values()[{javaField.Initializer}]";
                    }
                }
                else
                {
                    javaField.Initializer = Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(variable.Initializer.Value, context);
                    // DEBUG: Log when field initializer uses ExpressionTransformerFacade
                    if (variable.Initializer.Value is Microsoft.CodeAnalysis.CSharp.Syntax.InitializerExpressionSyntax init
                        && init.Expressions.Count > 100)
                    {
                        System.Console.WriteLine($"[DEBUG] FieldTransformer: field={variable.Identifier.Text}, initializer length={javaField.Initializer?.Length}, startsWith={javaField.Initializer?.Substring(0, System.Math.Min(50, javaField.Initializer?.Length ?? 0))}");
                    }
                }
                context.IsInStaticMember = previousStaticContext;

                if (context.SemanticModel != null && fieldTypeSymbol != null && IsCollectionOrListInterface(fieldTypeSymbol))
                {
                    var initTypeInfo = context.GetTypeInfo(variable.Initializer.Value);
                    var arrayType = initTypeInfo.Type as IArrayTypeSymbol ?? initTypeInfo.ConvertedType as IArrayTypeSymbol;
                    if (arrayType != null)
                        javaField.Initializer = ObjectCreationTransformer.WrapArrayForCollectionArg(javaField.Initializer, arrayType, context);
                }
            }

            // Issue 5: suggest AtomicReference for volatile fields of non-primitive types.
            if (isVolatile && !isJavaPrimitive)
                javaField.LeadingComment = ConvertedCommentSet.JoinComments(javaField.LeadingComment, "// Consider replacing with AtomicReference<T> for idiomatic Java concurrency.");

            yield return javaField;

            // Public instance fields whose type is a delegate (Action/Func/etc.) are exposed
            // through getters/setters. Conditional-access expressions on these fields are
            // rewritten to call getXxx()/setXxx(), so the accessors must exist for the Java
            // output to compile.
            if ((modifiers & JavaModifiers.Public) != 0
                && (modifiers & JavaModifiers.Static) == 0
                && fieldTypeSymbol?.TypeKind == TypeKind.Delegate)
            {
                var escapedName = javaField.Name;
                var propertyName = escapedName.Length > 0
                    ? char.ToUpperInvariant(escapedName[0]) + escapedName[1..]
                    : escapedName;
                var getterName = JavaNaming.GetPropertyGetterName(propertyName);
                var setterName = "set" + propertyName;

                yield return new JavaMethodDeclaration
                {
                    Name = getterName,
                    ReturnType = javaType,
                    Modifiers = JavaModifiers.Public,
                    Body = $"return {escapedName};"
                };

                yield return new JavaMethodDeclaration
                {
                    Name = setterName,
                    ReturnType = "void",
                    Modifiers = JavaModifiers.Public,
                    Parameters = { new JavaParameter(javaType, "value") },
                    Body = $"{escapedName} = value;"
                };
            }
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

    /// <summary>
    /// True for a static field declaration carrying [ThreadStatic]. Symbol
    /// identity via attributes (the source syntax may use short/aliased names);
    /// instance fields return false (the CLR ignores [ThreadStatic] on them).
    /// </summary>
    private static bool IsStaticThreadStaticField(FieldDeclarationSyntax fieldDecl, IFieldSymbol? fieldSymbol)
    {
        if (!fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            return false;

        return fieldSymbol?.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() is "System.ThreadStaticAttribute"
                or "ThreadStaticAttribute" or "ThreadStatic") == true;
    }

    /// <summary>
    /// Determines whether a static field's declared type references one of the
    /// enclosing generic class's own type parameters (Java forbids static members
    /// from referencing enclosing type params).
    /// </summary>
    private static bool IsStaticFieldReferencingClassTypeParameters(
        FieldDeclarationSyntax fieldDecl,
        ITypeSymbol? fieldTypeSymbol,
        ConversionContext context)
    {
        if (!fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            return false;

        var enclosingType = context.CurrentEnclosingRoslynType;
        if (enclosingType == null || enclosingType.TypeParameters.Length == 0)
            return false;

        return CollectReferencedClassTypeParameters(fieldTypeSymbol, enclosingType).Count > 0;
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

    private static string RenderConstantValue(object? value)
    {
        if (value == null)
            return "null";

        if (value is bool b)
            return b ? "true" : "false";

        if (value is string s)
            return "\"" + global::CSharpToJava.Core.Utilities.StringEscapeHelper.EscapeJavaString(s) + "\"";

        if (value is char c)
            return EscapeJavaChar(c);

        if (value is float f)
            return f.ToString(System.Globalization.CultureInfo.InvariantCulture) + "f";

        if (value is double d)
        {
            var text = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return text.Contains('.') || text.Contains('E') || text.Contains('e') ? text : text + ".0";
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
