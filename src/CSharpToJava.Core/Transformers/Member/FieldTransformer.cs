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

        // Use TransformAll and return first result (for interface compatibility)
        var all = TransformAll(fieldDecl, context).ToList();
        return all.Count > 0 ? all[0] : throw new InvalidOperationException("Field declaration has no variables");
    }

    /// <summary>
    /// Transforms all variables in a field declaration (handles multi-variable declarations like: int x, y, z;)
    /// </summary>
    public IEnumerable<JavaFieldDeclaration> TransformAll(FieldDeclarationSyntax fieldDecl, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(fieldDecl.Declaration.Type);
        var fieldTypeSymbol = typeInfo.HasValue ? typeInfo.Value.Type : null;
        var javaType = typeInfo.HasValue && typeInfo.Value.Type != null
            ? context.MapType(typeInfo.Value.Type)
            : context.MapTypeFromSyntax(fieldDecl.Declaration.Type);
        var modifiers = ConvertModifiers(fieldDecl.Modifiers);
        var firstVariable = fieldDecl.Declaration.Variables.FirstOrDefault();
        var fieldSymbol = firstVariable != null
            ? context.SemanticModel?.GetDeclaredSymbol(firstVariable)
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
        if (fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.FixedKeyword)))
            context.Diagnostics.Error("Java doesn't support fixed-size buffers. Field needs manual conversion.", fieldDecl.GetLocation());

        // Issue 5: volatile non-primitive field needs a heads-up comment.
        bool isVolatile = fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.VolatileKeyword));
        bool isJavaPrimitive = javaType is "int" or "long" or "short" or "byte" or
                                            "float" or "double" or "char" or "boolean";

        // Issue 4: track variable names declared earlier in the same multi-variable declaration
        // so we can warn when a subsequent initializer cross-references a prior variable.
        var declaredNames = new HashSet<string>();

        bool commentAssigned = false;
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

            // C# structs are value types that can never be null — initialize fields
            // with default instances so Java code doesn't encounter null struct references.
            if (variable.Initializer == null
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
            }

            if (variable.Initializer != null)
            {
                javaField.Initializer = Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(variable.Initializer.Value, context);

                if (context.SemanticModel != null && fieldTypeSymbol != null && IsCollectionOrListInterface(fieldTypeSymbol))
                {
                    var initTypeInfo = context.SemanticModel.GetTypeInfo(variable.Initializer.Value);
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
                        context.AddImport("java.util.ArrayList");
                        javaField.Initializer = $"Arrays.stream({javaField.Initializer}).boxed().collect(Collectors.toCollection(() -> new ArrayList<>()))";
                    }
                    else if (javaField.Initializer.StartsWith("new short[", StringComparison.Ordinal)
                        || javaField.Initializer.StartsWith("new byte[", StringComparison.Ordinal)
                        || javaField.Initializer.StartsWith("new float[", StringComparison.Ordinal)
                        || javaField.Initializer.StartsWith("new boolean[", StringComparison.Ordinal)
                        || javaField.Initializer.StartsWith("new char[", StringComparison.Ordinal))
                    {
                        context.AddImport("java.util.stream.IntStream");
                        context.AddImport("java.util.stream.Collectors");
                        context.AddImport("java.util.ArrayList");
                        javaField.Initializer = $"IntStream.range(0, {javaField.Initializer}.length).mapToObj(i -> {javaField.Initializer}[i]).collect(Collectors.toCollection(() -> new ArrayList<>()))";
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
}
