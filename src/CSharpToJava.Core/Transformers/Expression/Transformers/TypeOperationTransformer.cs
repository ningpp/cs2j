using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using System.Text;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles type-related expressions (cast, is, as, typeof, default, checked, unchecked).
/// </summary>
[TransformerRegistration]
public class TypeOperationTransformer : IExpressionTransformer
{
    static TypeOperationTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.CastExpression,
            SyntaxKind.IsExpression,
            SyntaxKind.IsPatternExpression,
            SyntaxKind.AsExpression,
            SyntaxKind.TypeOfExpression,
            SyntaxKind.DefaultExpression,
            SyntaxKind.CheckedExpression,
            SyntaxKind.UncheckedExpression,
            SyntaxKind.SizeOfExpression
        }, new TypeOperationTransformer());
    }

    private static readonly Lazy<TypeOperationTransformer> _instance = new(() => new());
    public static TypeOperationTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.CastExpression => TransformCast((CastExpressionSyntax)node, context),
            SyntaxKind.IsExpression => TransformIs((BinaryExpressionSyntax)node, context),
            SyntaxKind.IsPatternExpression => TransformIsPattern((IsPatternExpressionSyntax)node, context),
            SyntaxKind.AsExpression => TransformAs((BinaryExpressionSyntax)node, context),
            SyntaxKind.TypeOfExpression => TransformTypeOf((TypeOfExpressionSyntax)node, context),
            SyntaxKind.DefaultExpression => TransformDefault((DefaultExpressionSyntax)node, context),
            SyntaxKind.CheckedExpression => TransformChecked((CheckedExpressionSyntax)node, context),
            SyntaxKind.UncheckedExpression => TransformUnchecked((CheckedExpressionSyntax)node, context),
            SyntaxKind.SizeOfExpression => TransformSizeOf((SizeOfExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Type operation kind {node.Kind()} not supported.")
        };

    private string TransformCast(CastExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expression = facade.Transform(node.Expression, context);

        // Get the target type
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type);
        string targetType;
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            targetType = context.MapType(typeInfo.Value.Type);
        }
        else
        {
            targetType = context.MapTypeFromSyntax(node.Type);
        }

        // Java cast syntax: (Type)expression
        // For primitives to wrapper types, use valueOf
        if (IsPrimitiveToWrapperCast(node.Expression, targetType, context))
        {
            return $"{targetNameOf(targetType)}({expression})";
        }

        return $"({targetType})({expression})";
    }

    private string TransformIs(BinaryExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var left = facade.Transform(node.Left, context);

        // Get the type being checked
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Right);
        string targetType;
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            targetType = context.MapType(typeInfo.Value.Type);
        }
        else
        {
            targetType = context.MapTypeFromSyntax(node.Right as TypeSyntax ?? throw new ArgumentException("Expected type"));
        }

        // C#: obj is Type  → Java: obj instanceof Type
        return $"{left} instanceof {ToRuntimeTypeForInstanceOf(targetType)}";
    }

    private string TransformIsPattern(IsPatternExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expression = facade.Transform(node.Expression, context);

        // Handle different pattern types
        var pattern = node.Pattern;
        return pattern switch
        {
            DeclarationPatternSyntax declPattern => TransformDeclarationPattern(expression, declPattern, context),
            ConstantPatternSyntax constPattern => TransformConstantPattern(expression, constPattern, context),
            RecursivePatternSyntax recPattern => TransformRecursivePattern(expression, recPattern, context),
            _ => $"/* TODO: complex pattern */ {expression}"
        };
    }

    private string TransformDeclarationPattern(string expression, DeclarationPatternSyntax pattern, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // C#: obj is Type variable  → Java needs instanceof check then cast
        var typeInfo = context.SemanticModel?.GetTypeInfo(pattern.Type);
        string targetType;
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            targetType = context.MapType(typeInfo.Value.Type);
        }
        else
        {
            targetType = context.MapTypeFromSyntax(pattern.Type);
        }

        var variableName = ConversionContext.EscapeJavaKeyword(pattern.Designation.ToString());

        // In Java, we use: expression instanceof Type && ((Type)expression).property
        // Or for newer Java: expression instanceof Type variableName
        if ((int)context.Options.TargetJavaVersion >= 16)
        {
            // Java 16+ pattern matching
            return $"{expression} instanceof {ToRuntimeTypeForInstanceOf(targetType)} {variableName}";
        }
        else
        {
            // Older Java - explicit cast and assignment
            return $"{expression} instanceof {ToRuntimeTypeForInstanceOf(targetType)} && ({variableName} = ({targetType}){expression}) != null";
        }
    }

    private string TransformConstantPattern(string expression, ConstantPatternSyntax pattern, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var constant = facade.Transform(pattern.Expression, context);

        // C#: obj is null  → Java: obj == null
        if (pattern.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NullLiteralExpression))
        {
            return $"{expression} == null";
        }

        // Other constant patterns
        return $"{expression} == {constant}";
    }

    private string TransformRecursivePattern(string expression, RecursivePatternSyntax pattern, ConversionContext context)
    {
        // Determine the type name for the instanceof check
        string? typeName = null;
        if (pattern.Type != null)
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(pattern.Type);
            typeName = (typeInfo.HasValue && typeInfo.Value.Type != null)
                ? context.MapType(typeInfo.Value.Type)
                : context.MapTypeFromSyntax(pattern.Type);
        }

        var conditions = new List<string>();
        if (typeName != null)
            conditions.Add($"{expression} instanceof {typeName}");

        // Translate property pattern subpatterns to getter calls + comparisons
        if (pattern.PropertyPatternClause != null && typeName != null)
        {
            var cast = $"(({typeName}){expression})";
            foreach (var sub in pattern.PropertyPatternClause.Subpatterns)
            {
                string? propName = sub.NameColon?.Name.Identifier.Text
                    ?? (sub.ExpressionColon?.Expression is IdentifierNameSyntax idName ? idName.Identifier.Text : null);
                if (propName == null) continue;
                string getter = $"{cast}.get{char.ToUpperInvariant(propName[0])}{propName[1..]}()";
                string cond = TransformSubPattern(getter, sub.Pattern, context);
                conditions.Add(cond);
            }
        }

        return conditions.Count > 0
            ? string.Join(" && ", conditions)
            : $"/* TODO: recursive pattern */ {expression}";
    }

    private string TransformSubPattern(string subject, PatternSyntax pattern, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        return pattern switch
        {
            ConstantPatternSyntax constPat when constPat.Expression is LiteralExpressionSyntax lit
                && lit.IsKind(SyntaxKind.NullLiteralExpression)
                => $"{subject} == null",
            ConstantPatternSyntax constPat
                => $"{subject} == {facade.Transform(constPat.Expression, context)}",
            RelationalPatternSyntax relPat
                => $"{subject} {relPat.OperatorToken.Text} {facade.Transform(relPat.Expression, context)}",
            UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax nullLit } }
                when nullLit.IsKind(SyntaxKind.NullLiteralExpression)
                => $"{subject} != null",
            _ => $"/* TODO: sub-pattern */ {subject}"
        };
    }

    private string TransformAs(BinaryExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expression = facade.Transform(node.Left, context);

        // Get the target type
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Right);
        string targetType;
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            targetType = context.MapType(typeInfo.Value.Type);
        }
        else
        {
            targetType = context.MapTypeFromSyntax(node.Right as TypeSyntax ?? throw new ArgumentException("Expected type"));
        }

        // C#: obj as Type  → Java doesn't have direct equivalent
        // We use: obj instanceof Type ? (Type)obj : null
        return $"({expression} instanceof {ToRuntimeTypeForInstanceOf(targetType)} ? ({targetType})({expression}) : null) /* result may be null — check before use */";
    }

    private static string ToRuntimeTypeForInstanceOf(string mappedType)
    {
        // Java instanceof does not accept parameterized types (e.g. Set<T>).
        var lt = mappedType.IndexOf('<');
        return lt >= 0 ? mappedType[..lt] : mappedType;
    }

    private string TransformTypeOf(TypeOfExpressionSyntax node, ConversionContext context)
    {
        // Get the type
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type);
        string typeName;
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            typeName = context.MapType(typeInfo.Value.Type);
        }
        else
        {
            typeName = context.MapTypeFromSyntax(node.Type);
        }

        // Warn if type parameter (subject to type erasure in Java)
        if (typeInfo.HasValue && typeInfo.Value.Type is ITypeParameterSymbol)
            return $"/* WARNING: type parameter erased at runtime; T.class may fail */ {typeName}.class";
        // C#: typeof(Type)  → Java: Type.class
        return $"{typeName}.class";
    }

    private string TransformDefault(DefaultExpressionSyntax node, ConversionContext context)
    {
        if (node.Type != null)
        {
            // Get the type
            var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type);
            string typeName;
            if (typeInfo.HasValue && typeInfo.Value.Type != null)
            {
                typeName = context.MapType(typeInfo.Value.Type);
            }
            else
            {
                typeName = context.MapTypeFromSyntax(node.Type);
            }

            // C#: default(Type)  → Java default values
            // For struct/value types, emit new T()
            if (typeInfo.HasValue && typeInfo.Value.Type is INamedTypeSymbol { TypeKind: TypeKind.Struct })
                return $"new {typeName}()";
            return typeName switch
            {
                "int" => "0",
                "long" => "0L",
                "short" => "(short)0",
                "byte" => "(byte)0",
                "float" => "0.0f",
                "double" => "0.0",
                "boolean" => "false",
                "char" => "'\\0'",
                _ => "null" // Reference types default to null
            };
        }
        else
        {
            // default literal (C# 7.1+) - infer from context
            return "/* TODO: default literal */ null";
        }
    }

    private string TransformChecked(CheckedExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expression = facade.Transform(node.Expression, context);

        // C# checked context - Java doesn't have overflow checking by default
        // For Java, we might want to add Math.addExact(), etc. but that's complex
        // For now, emit the expression with a comment
        return $"/* checked */ {expression}";
    }

    private string TransformUnchecked(CheckedExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expression = facade.Transform(node.Expression, context);

        // C# unchecked context - Java's default behavior
        return expression;
    }

    private string TransformSizeOf(SizeOfExpressionSyntax node, ConversionContext context)
    {
        // Map C# built-in types to their byte sizes (platform-independent for well-known types).
        if (node.Type is PredefinedTypeSyntax predefined)
        {
            return predefined.Keyword.Text switch
            {
                "byte" or "sbyte" or "bool" => "1",
                "short" or "ushort" or "char" => "2",
                "int" or "uint" or "float" => "4",
                "long" or "ulong" or "double" => "8",
                "decimal" => "16",
                _ => $"/* sizeof({node.Type}) */"
            };
        }
        // For user-defined struct types, emit a comment — Java has no sizeof operator.
        var typeName = context.MapTypeFromSyntax(node.Type);
        return $"/* sizeof({typeName}) */";
    }

    // Helper methods

    private static bool IsPrimitiveToWrapperCast(ExpressionSyntax expr, string targetType, ConversionContext context)
    {
        // Check if we're casting from a primitive type to its wrapper
        var typeInfo = context.SemanticModel?.GetTypeInfo(expr);
        if (!typeInfo.HasValue || typeInfo.Value.Type == null) return false;

        var sourceType = context.MapType(typeInfo.Value.Type);

        return (sourceType, targetType) switch
        {
            ("int", "Integer") or ("long", "Long") or ("short", "Short") or
            ("byte", "Byte") or ("float", "Float") or ("double", "Double") or
            ("boolean", "Boolean") or ("char", "Character") => true,
            _ => false
        };
    }

    private static string targetNameOf(string wrapperType)
    {
        return wrapperType switch
        {
            "Integer" => "Integer.valueOf",
            "Long" => "Long.valueOf",
            "Short" => "Short.valueOf",
            "Byte" => "Byte.valueOf",
            "Float" => "Float.valueOf",
            "Double" => "Double.valueOf",
            "Boolean" => "Boolean.valueOf",
            "Character" => "Character.valueOf",
            _ => wrapperType
        };
    }
}
