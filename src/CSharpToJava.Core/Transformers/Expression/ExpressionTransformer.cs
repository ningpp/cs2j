using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// 表达式转换器
/// </summary>
public class ExpressionTransformer : IExpressionTransformer
{
    public string Transform(ExpressionSyntax node, ConversionContext context)
    {
        return node.Kind() switch
        {
            // 字面量
            SyntaxKind.NumericLiteralExpression => TransformNumericLiteral((LiteralExpressionSyntax)node),
            SyntaxKind.StringLiteralExpression => TransformStringLiteral((LiteralExpressionSyntax)node),
            SyntaxKind.CharacterLiteralExpression => TransformCharacterLiteral((LiteralExpressionSyntax)node),
            SyntaxKind.TrueLiteralExpression => "true",
            SyntaxKind.FalseLiteralExpression => "false",
            SyntaxKind.NullLiteralExpression => "null",

            // 标识符和成员访问
            SyntaxKind.IdentifierName => TransformIdentifier((IdentifierNameSyntax)node, context),
            SyntaxKind.GenericName => TransformGenericName((GenericNameSyntax)node, context),
            SyntaxKind.MemberAccessExpression => TransformMemberAccess((MemberAccessExpressionSyntax)node, context),
            SyntaxKind.SimpleMemberAccessExpression => TransformMemberAccess((MemberAccessExpressionSyntax)node, context),
            SyntaxKind.PointerMemberAccessExpression => TransformPointerMemberAccess((MemberAccessExpressionSyntax)node, context),

            // 调用
            SyntaxKind.InvocationExpression => TransformInvocation((InvocationExpressionSyntax)node, context),
            SyntaxKind.ElementAccessExpression => TransformElementAccess((ElementAccessExpressionSyntax)node, context),

            // 运算符
            SyntaxKind.AddExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "+", context),
            SyntaxKind.SubtractExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "-", context),
            SyntaxKind.MultiplyExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "*", context),
            SyntaxKind.DivideExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "/", context),
            SyntaxKind.ModuloExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "%", context),
            SyntaxKind.GreaterThanExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, ">", context),
            SyntaxKind.GreaterThanOrEqualExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, ">=", context),
            SyntaxKind.LessThanExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "<", context),
            SyntaxKind.LessThanOrEqualExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "<=", context),
            SyntaxKind.EqualsExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "==", context),
            SyntaxKind.NotEqualsExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "!=", context),
            SyntaxKind.LogicalAndExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "&&", context),
            SyntaxKind.LogicalOrExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "||", context),
            SyntaxKind.BitwiseAndExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "&", context),
            SyntaxKind.BitwiseOrExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "|", context),
            SyntaxKind.ExclusiveOrExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "^", context),
            SyntaxKind.LeftShiftExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "<<", context),
            SyntaxKind.RightShiftExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, ">>", context),

            // 一元运算符
            SyntaxKind.UnaryPlusExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "+", context),
            SyntaxKind.UnaryMinusExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "-", context),
            SyntaxKind.LogicalNotExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "!", context),
            SyntaxKind.BitwiseNotExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "~", context),
            SyntaxKind.AddressOfExpression => TransformAddressOf((PrefixUnaryExpressionSyntax)node, context),
            SyntaxKind.PointerIndirectionExpression => TransformPointerIndirection((PrefixUnaryExpressionSyntax)node, context),

            // 赋值
            SyntaxKind.SimpleAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "=", context),
            SyntaxKind.AddAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "+=", context),
            SyntaxKind.SubtractAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "-=", context),
            SyntaxKind.MultiplyAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "*=", context),
            SyntaxKind.DivideAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "/=", context),
            SyntaxKind.ModuloAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "%=", context),
            SyntaxKind.AndAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "&=", context),
            SyntaxKind.OrAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "|=", context),
            SyntaxKind.ExclusiveOrAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "^=", context),
            SyntaxKind.LeftShiftAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "<<=", context),
            SyntaxKind.RightShiftAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, ">>=", context),

            // 递增/递减
            SyntaxKind.PostIncrementExpression => TransformPostfix((PostfixUnaryExpressionSyntax)node, "++", context),
            SyntaxKind.PostDecrementExpression => TransformPostfix((PostfixUnaryExpressionSyntax)node, "--", context),
            SyntaxKind.PreIncrementExpression => TransformPrefix((PrefixUnaryExpressionSyntax)node, "++", context),
            SyntaxKind.PreDecrementExpression => TransformPrefix((PrefixUnaryExpressionSyntax)node, "--", context),

            // 其他
            SyntaxKind.ConditionalExpression => TransformConditional((ConditionalExpressionSyntax)node, context),
            SyntaxKind.CastExpression => TransformCast((CastExpressionSyntax)node, context),
            SyntaxKind.IsExpression => TransformIs((BinaryExpressionSyntax)node, context),
            SyntaxKind.AsExpression => TransformAs((BinaryExpressionSyntax)node, context),
            SyntaxKind.TypeOfExpression => TransformTypeOf((TypeOfExpressionSyntax)node, context),
            SyntaxKind.DefaultExpression => TransformDefault((DefaultExpressionSyntax)node, context),
            SyntaxKind.NewExpression => TransformNew((NewExpressionSyntax)node, context),
            SyntaxKind.ObjectCreationExpression => TransformObjectCreation((ObjectCreationExpressionSyntax)node, context),
            SyntaxKind.AnonymousObjectCreationExpression => TransformAnonymousObjectCreation((AnonymousObjectCreationExpressionSyntax)node, context),
            SyntaxKind.ArrayCreationExpression => TransformArrayCreation((ArrayCreationExpressionSyntax)node, context),
            SyntaxKind.ImplicitArrayCreationExpression => TransformImplicitArrayCreation((ImplicitArrayCreationExpressionSyntax)node, context),
            SyntaxKind.ArrayInitializerExpression => TransformArrayInitializer((InitializerExpressionSyntax)node, context),
            SyntaxKind.InterpolatedStringExpression => TransformInterpolatedString((InterpolatedStringExpressionSyntax)node, context),
            SyntaxKind.StringEmptyExpression => "\"\"",
            SyntaxKind.ThisExpression => "this",
            SyntaxKind.BaseExpression => "super",
            SyntaxKind.ArgListExpression or SyntaxKind.ArgListArgument => "// TODO: __arglist",
            SyntaxKind.MakeRefExpression or SyntaxKind.RefTypeExpression or SyntaxKind.RefValueExpression => "// TODO: ref expression",
            SyntaxKind.CheckedExpression => TransformChecked((CheckedExpressionSyntax)node, context),
            SyntaxKind.UncheckedExpression => TransformUnchecked((CheckedExpressionSyntax)node, context),
            SyntaxKind.AwaitExpression => TransformAwait((AwaitExpressionSyntax)node, context),
            SyntaxKind.QueryExpression => TransformQuery((QueryExpressionSyntax)node, context),
            SyntaxKind.LambdaExpression => TransformLambda((LambdaExpressionSyntax)node, context),
            SyntaxKind.ParenthesizedExpression => $"({Transform(((ParenthesizedExpressionSyntax)node).Expression, context)})",
            SyntaxKind.ThrowExpression => TransformThrowExpression((ThrowExpressionSyntax)node, context),
            SyntaxKind.SwitchExpression => TransformSwitchExpression((SwitchExpressionSyntax)node, context),
            SyntaxKind.WithExpression => "// TODO: with expression",
            SyntaxKind.IndexExpression => TransformIndexExpression((IndexExpressionSyntax)node, context),
            SyntaxKind.RangeExpression => "// TODO: range expression",

            _ => $"/* TODO: {node.Kind()} */ {node}"
        };
    }

    private string TransformNumericLiteral(LiteralExpressionSyntax node)
    {
        var token = node.Token;
        var text = token.Text;

        // 处理后缀
        if (text.EndsWith("f") || text.EndsWith("F"))
        {
            return text.TrimEnd('f', 'F') + "f";
        }
        if (text.EndsWith("d") || text.EndsWith("D"))
        {
            return text.TrimEnd('d', 'D');
        }
        if (text.EndsWith("m") || text.EndsWith("M"))
        {
            return "/* TODO: decimal */ " + text.TrimEnd('m', 'M');
        }
        if (text.EndsWith("ul") || text.EndsWith("UL"))
        {
            return text.TrimEnd('u', 'U', 'l', 'L') + "L"; // Java long
        }
        if (text.EndsWith("u") || text.EndsWith("U"))
        {
            return text.TrimEnd('u', 'U'); // Java 没有 unsigned
        }
        if (text.EndsWith("l") || text.EndsWith("L"))
        {
            return text.TrimEnd('l', 'L') + "L";
        }

        return text;
    }

    private string TransformStringLiteral(LiteralExpressionSyntax node)
    {
        // 处理逐字字符串（@"..."）
        if (node.Token.IsKind(SyntaxKind.StringLiteralToken))
        {
            var text = node.Token.Text;
            if (text.StartsWith("@"))
            {
                // 移除 @ 和双引号转义
                var content = text.Substring(2, text.Length - 3);
                return "\"" + content.Replace("\"", "\"\"") + "\"";
            }

            // 转义字符串
            return text;
        }

        return node.Token.Text;
    }

    private string TransformCharacterLiteral(LiteralExpressionSyntax node)
    {
        return node.Token.Text switch
        {
            "'\\n'" => "'\\n'",
            "'\\r'" => "'\\r'",
            "'\\t'" => "'\\t'",
            "'\\0'" => "'\\0'",
            _ => node.Token.Text
        };
    }

    private string TransformIdentifier(IdentifierNameSyntax node, ConversionContext context)
    {
        var name = node.Identifier.Text;

        // 检查是否是别名
        if (context.IsAlias(name))
        {
            var targetType = context.ResolveAlias(name);
            if (targetType != null)
            {
                // 将别名替换为实际类型
                return context.MapType(targetType);
            }

            // 别名解析失败，使用原始名称并警告
            context.Diagnostics.Warning(
                $"Could not resolve alias '{name}'",
                node.GetLocation()
            );
        }

        // 处理 C# 关键字作为标识符的情况
        if (IsJavaKeyword(name))
        {
            // 可能在 Java 中需要转义
        }

        // 检查是否是类型名称
        var typeInfo = context.SemanticModel?.GetTypeInfo(node);
        if (typeInfo?.Type != null)
        {
            return context.MapType(typeInfo.Type);
        }

        return name;
    }

    private string TransformGenericName(GenericNameSyntax node, ConversionContext context)
    {
        var typeName = node.Identifier.Text;

        // 检查是否是泛型别名
        if (context.IsAlias(typeName))
        {
            var targetType = context.ResolveAlias(typeName);
            if (targetType != null)
            {
                return context.MapType(targetType);
            }
        }

        // 处理泛型类型参数
        var typeArgs = node.TypeArgumentList?.Arguments.Select(arg =>
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(arg);
            return typeInfo?.Type != null ? context.MapType(typeInfo.Type) : arg.ToString();
        }) ?? Enumerable.Empty<string>();

        return $"{typeName}<{string.Join(", ", typeArgs)}>";
    }

    private string TransformMemberAccess(MemberAccessExpressionSyntax node, ConversionContext context)
    {
        // 检查左侧是否是别名（如 P2.SubType）
        if (node.Expression is IdentifierNameSyntax identifierExpr)
        {
            var leftName = identifierExpr.Identifier.Text;
            if (context.IsAlias(leftName))
            {
                var targetType = context.ResolveAlias(leftName);
                if (targetType != null)
                {
                    var left = context.MapType(targetType);
                    var right = node.Name.Identifier.Text;
                    return $"{left}.{right}";
                }
            }
        }

        var left = Transform(node.Expression, context);
        var right = node.Name.Identifier.Text;

        // 检查是否是方法调用目标（如果有类型信息）
        var typeInfo = context.SemanticModel?.GetSymbolInfo(node);
        if (typeInfo?.Symbol != null)
        {
            // 检查是否是长度属性
            if (node.Name is { Identifier.Text: "Length" } &&
                context.SemanticModel.GetTypeInfo(node.Expression).Type?.SpecialType == SpecialType.System_String)
            {
                return $"{left}.length()";
            }

            // 检查是否是已知的方法/属性映射
            var containingType = typeInfo.Symbol.ContainingType?.ToDisplayString();
            if (!string.IsNullOrEmpty(containingType))
            {
                var mappedMethod = context.TypeMappings.MapMethod(containingType, right);
                if (!string.IsNullOrEmpty(mappedMethod))
                {
                    return $"{left}.{mappedMethod}";
                }
            }
        }

        return $"{left}.{right}";
    }

    private string TransformPointerMemberAccess(MemberAccessExpressionSyntax node, ConversionContext context)
    {
        context.Diagnostics.Error("Java doesn't support pointer member access", node.GetLocation());
        return "/* TODO: pointer member access */";
    }

    private string TransformInvocation(InvocationExpressionSyntax node, ConversionContext context)
    {
        if (node.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var target = Transform(memberAccess.Expression, context);
            var methodName = memberAccess.Name.Identifier.Text;

            // 检查是否是 LINQ 方法
            if (IsLinqMethod(methodName))
            {
                return TransformLinqInvocation(node, target, methodName, context);
            }

            // 检查方法映射
            var typeInfo = context.SemanticModel?.GetTypeInfo(memberAccess.Expression);
            if (typeInfo?.Type != null)
            {
                var containingType = typeInfo.Type.ToDisplayString();
                var mappedMethod = context.TypeMappings.MapMethod(containingType, methodName);
                if (!string.IsNullOrEmpty(mappedMethod))
                {
                    methodName = mappedMethod;
                }
            }

            var args = TransformArgumentList(node.ArgumentList, context);
            return $"{target}.{methodName}({args})";
        }

        var expr = Transform(node.Expression, context);
        var arguments = TransformArgumentList(node.ArgumentList, context);
        return $"{expr}({arguments})";
    }

    private string TransformLinqInvocation(InvocationExpressionSyntax node, string target, string methodName, ConversionContext context)
    {
        var args = TransformArgumentList(node.ArgumentList, context);

        return methodName switch
        {
            "Where" => $"{target}.filter({args})",
            "Select" => $"{target}.map({args})",
            "SelectMany" => $"{target}.flatMap({args})",
            "FirstOrDefault" => $"{target}.findFirst().orElse(null)",
            "First" => $"{target}.findFirst().orElseThrow()",
            "SingleOrDefault" => $"{target}.findFirst().orElse(null)",
            "Single" => $"{target}.findFirst().orElseThrow()",
            "ToList" => $"{target}.collect(Collectors.toList())",
            "ToArray" => $"{target}.toArray()",
            "ToListAsync" when context.IsInAsyncContext => $"{target}.collect(Collectors.toList())",
            "Count" => $"{target}.count()",
            "Any" => $"{target}.anyMatch({args})",
            "All" => $"{target}.allMatch({args})",
            "OrderBy" => $"{target}.sorted(Comparator.comparing({args}))",
            "OrderByDescending" => $"{target}.sorted(Comparator.comparing({args}).reversed())",
            "ThenBy" => $"{target}.thenComparing({args})",
            "GroupBy" => $"{target}.collect(Collectors.groupingBy({args}))",
            "Join" => "/* TODO: LINQ Join */",
            "Sum" => $"{target}.mapToLong(x -> x).sum()",
            "Average" => $"{target}.mapToDouble(x -> x).average().orElse(0)",
            "Min" => $"{target}.min(Comparator.naturalOrder()).orElse(null)",
            "Max" => $"{target}.max(Comparator.naturalOrder()).orElse(null)",
            "Take" => $"{target}.limit({args})",
            "Skip" => $"{target}.skip({args})",
            "Distinct" => $"{target}.distinct()",
            "Reverse" => "/* TODO: Reverse */",
            "Contains" => $"{target}.anyMatch(x -> x.equals({args}))",
            "Concat" => $"Stream.concat({target}, {args})",
            "Zip" => "/* TODO: Zip */",
            _ => $"{target}.{methodName}({args})"
        };
    }

    private bool IsLinqMethod(string methodName)
    {
        return methodName switch
        {
            "Where" or "Select" or "SelectMany" or "FirstOrDefault" or "First" or
            "SingleOrDefault" or "Single" or "ToList" or "ToArray" or "Count" or
            "Any" or "All" or "OrderBy" or "OrderByDescending" or "ThenBy" or
            "GroupBy" or "Join" or "Sum" or "Average" or "Min" or "Max" or
            "Take" or "Skip" or "Distinct" or "Reverse" or "Contains" or
            "Concat" or "Zip" => true,
            _ => false
        };
    }

    private string TransformElementAccess(ElementAccessExpressionSyntax node, ConversionContext context)
    {
        var target = Transform(node.Expression, context);
        var args = TransformArgumentList(node.ArgumentList, context);
        return $"{target}[{args}]";
    }

    private string TransformBinaryExpression(BinaryExpressionSyntax node, string op, ConversionContext context)
    {
        var left = Transform(node.Left, context);
        var right = Transform(node.Right, context);
        return $"({left} {op} {right})";
    }

    private string TransformUnaryExpression(PrefixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        var operand = Transform(node.Operand, context);
        return $"{op}{operand}";
    }

    private string TransformAddressOf(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        context.Diagnostics.Error("Java doesn't support address-of operator", node.GetLocation());
        return "/* TODO: & operator */";
    }

    private string TransformPointerIndirection(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        context.Diagnostics.Error("Java doesn't support pointer indirection", node.GetLocation());
        return "/* TODO: * operator */";
    }

    private string TransformAssignment(AssignmentExpressionSyntax node, string op, ConversionContext context)
    {
        var left = Transform(node.Left, context);
        var right = Transform(node.Right, context);
        return $"{left} {op} {right}";
    }

    private string TransformPostfix(PostfixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        var operand = Transform(node.Operand, context);
        return $"({operand}{op})";
    }

    private string TransformPrefix(PrefixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        var operand = Transform(node.Operand, context);
        return $"({op}{operand})";
    }

    private string TransformConditional(ConditionalExpressionSyntax node, ConversionContext context)
    {
        var condition = Transform(node.Condition, context);
        var whenTrue = Transform(node.WhenTrue, context);
        var whenFalse = Transform(node.WhenFalse, context);
        return $"({condition} ? {whenTrue} : {whenFalse})";
    }

    private string TransformCast(CastExpressionSyntax node, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type);
        var targetType = typeInfo?.Type != null ? context.MapType(typeInfo.Type) : "Object";
        var expression = Transform(node.Expression, context);
        return $"(({targetType}) {expression})";
    }

    private string TransformIs(BinaryExpressionSyntax node, ConversionContext context)
    {
        var left = Transform(node.Left, context);
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Right);
        var rightType = typeInfo?.Type != null ? context.MapType(typeInfo.Type) : "Object";
        return $"({left} instanceof {rightType})";
    }

    private string TransformAs(BinaryExpressionSyntax node, ConversionContext context)
    {
        var left = Transform(node.Left, context);
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Right);
        var rightType = typeInfo?.Type != null ? context.MapType(typeInfo.Type) : "Object";
        return $"({rightType}) {left}"; // Java 没有 as，使用强制转换
    }

    private string TransformTypeOf(TypeOfExpressionSyntax node, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type);
        var type = typeInfo?.Type != null ? context.MapType(typeInfo.Type) : "Object";
        return $"{type}.class";
    }

    private string TransformDefault(DefaultExpressionSyntax node, ConversionContext context)
    {
        if (node.Type is null)
        {
            return "null";
        }

        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type);
        var type = typeInfo?.Type != null ? context.MapType(typeInfo.Type) : "Object";

        // 根据类型返回默认值
        return type switch
        {
            "boolean" => "false",
            "byte" or "short" or "int" or "long" or "float" or "double" => "0",
            "char" => "'\\0'",
            _ => "null"
        };
    }

    private string TransformNew(NewExpressionSyntax node, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type);
        var type = typeInfo?.Type != null ? context.MapType(typeInfo.Type) : "Object";
        var args = TransformArgumentList(node.ArgumentList, context);
        return $"new {type}({args})";
    }

    private string TransformObjectCreation(ObjectCreationExpressionSyntax node, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type);
        var type = typeInfo?.Type != null ? context.MapType(typeInfo.Type) : "Object";
        var args = TransformArgumentList(node.ArgumentList, context);
        return $"new {type}({args})";
    }

    private string TransformAnonymousObjectCreation(AnonymousObjectCreationExpressionSyntax node, ConversionContext context)
    {
        // Java 需要显式类型或 record
        context.Diagnostics.Warning("Anonymous object converted to generic Object", node.GetLocation());
        return "new Object() /* TODO: anonymous object */";
    }

    private string TransformArrayCreation(ArrayCreationExpressionSyntax node, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type.ElementType);
        var elementType = typeInfo?.Type != null ? context.MapType(typeInfo.Type) : "Object";

        if (node.Initializer != null)
        {
            var init = TransformArrayInitializer(node.Initializer, context);
            return $"new {elementType}[]{init}";
        }

        var sizes = string.Join("", node.Type.RankSpecifiers.SelectMany(rs =>
            rs.Sizes.Select(s => $"[{Transform(s, context)}]")));

        return $"new {elementType}{sizes}";
    }

    private string TransformImplicitArrayCreation(ImplicitArrayCreationExpressionSyntax node, ConversionContext context)
    {
        var init = TransformArrayInitializer(node.Initializer, context);
        return $"new Object[]{init}";
    }

    private string TransformArrayInitializer(InitializerExpressionSyntax node, ConversionContext context)
    {
        var values = string.Join(", ", node.Expressions.Select(e => Transform(e, context)));
        return $"{{ {values} }}";
    }

    private string TransformInterpolatedString(InterpolatedStringExpressionSyntax node, ConversionContext context)
    {
        // 转换为字符串拼接或 String.format
        var parts = new List<string>();

        foreach (var content in node.Contents)
        {
            if (content is InterpolatedStringTextSyntax text)
            {
                parts.Add($"\"{text.TextToken.Text}\"");
            }
            else if (content is InterpolationSyntax interpolation)
            {
                var expr = Transform(interpolation.Expression, context);
                parts.Add($"String.valueOf({expr})");
            }
        }

        return string.Join(" + ", parts);
    }

    private string TransformChecked(CheckedExpressionSyntax node, ConversionContext context)
    {
        // Java 不需要 checked
        return Transform(node.Expression, context);
    }

    private string TransformUnchecked(CheckedExpressionSyntax node, ConversionContext context)
    {
        return Transform(node.Expression, context);
    }

    private string TransformAwait(AwaitExpressionSyntax node, ConversionContext context)
    {
        var expr = Transform(node.Expression, context);
        // Task<T>.await() -> CompletableFuture.join()
        return $"({expr}).join()";
    }

    private string TransformQuery(QueryExpressionSyntax node, ConversionContext context)
    {
        // LINQ 查询语法转换为方法调用
        context.Diagnostics.Info("LINQ query syntax converted to method calls", node.GetLocation());
        return "/* TODO: LINQ query syntax */";
    }

    private string TransformLambda(LambdaExpressionSyntax node, ConversionContext context)
    {
        context.IsInLambdaContext = true;

        var parameters = node switch
        {
            ParenthesizedLambdaExpressionSyntax parenthesized => string.Join(", ",
                parenthesized.ParameterList?.Parameters.Select(p =>
                {
                    var typeInfo = p.Type != null ? context.SemanticModel?.GetTypeInfo(p.Type) : null;
                    var type = typeInfo?.Type != null ? context.MapType(typeInfo.Type) : "";
                    return string.IsNullOrEmpty(type) ? p.Identifier.Text : $"{type} {p.Identifier.Text}";
                }) ?? Enumerable.Empty<string>()
            ),
            SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.Text,
            _ => ""
        };

        var body = node.Body switch
        {
            BlockSyntax block => new StatementTransformer().TransformBlock(block, context),
            ExpressionSyntax expr => Transform(expr, context),
            _ => ""
        };

        context.IsInLambdaContext = false;

        return $"({parameters}) -> {body}";
    }

    private string TransformThrowExpression(ThrowExpressionSyntax node, ConversionContext context)
    {
        var expr = Transform(node.Expression, context);
        return $"(() => {{ throw new {expr}; }})()"; // Java throw 是语句，不是表达式
    }

    private string TransformSwitchExpression(SwitchExpressionSyntax node, ConversionContext context)
    {
        // Java 14+ 支持 switch 表达式
        var governingExpr = Transform(node.GoverningExpression, context);
        var arms = new List<string>();

        foreach (var arm in node.Arms)
        {
            var pattern = TransformPattern(arm.Pattern, context);
            var whenClause = arm.WhenClause != null
                ? $" when {Transform(arm.WhenClause.Condition, context)}"
                : "";
            var result = Transform(arm.Expression, context);
            arms.Add($"case {pattern}{whenClause} -> {result}");
        }

        return $"switch ({governingExpr}) {{ {string.Join(", ", arms)} }}";
    }

    private string TransformIndexExpression(IndexExpressionSyntax node, ConversionContext context)
    {
        var target = Transform(node.Expression, context);
        var arg = Transform(node.Argument, context);
        return $"{target}[{arg}]";
    }

    private string TransformPattern(PatternSyntax pattern, ConversionContext context)
    {
        return pattern switch
        {
            ConstantPatternSyntax constant => Transform(constant.Expression, context),
            DeclarationPatternSyntax decl => TransformDeclarationPattern(decl, context),
            DiscardPatternSyntax => "_",
            _ => "/* TODO: pattern */"
        };
    }

    private string TransformDeclarationPattern(DeclarationPatternSyntax pattern, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(pattern.Type);
        var type = typeInfo?.Type != null ? context.MapType(typeInfo.Type) : "Object";
        return $"{type} {pattern.Designation}";
    }

    private string TransformArgumentList(ArgumentListSyntax? argumentList, ConversionContext context)
    {
        if (argumentList == null) return "";

        return string.Join(", ", argumentList.Arguments.Select(arg =>
        {
            var expr = Transform(arg.Expression, context);

            // 处理命名参数
            if (arg.NameColon != null)
            {
                // Java 不支持命名参数
                context.Diagnostics.Warning(
                    "Named arguments not supported in Java",
                    arg.NameColon.GetLocation()
                );
            }

            // 处理 ref/out
            if (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) ||
                arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                context.Diagnostics.Warning(
                    "ref/out parameters not supported in Java",
                    arg.RefKindKeyword.GetLocation()
                );
            }

            return expr;
        }));
    }

    private bool IsJavaKeyword(string word)
    {
        return word switch
        {
            "abstract" or "assert" or "boolean" or "break" or "byte" or "case" or "catch" or
            "char" or "class" or "const" or "continue" or "default" or "do" or "double" or
            "else" or "enum" or "extends" or "final" or "finally" or "float" or "for" or
            "goto" or "if" or "implements" or "import" or "instanceof" or "int" or
            "interface" or "long" or "native" or "new" or "package" or "private" or
            "protected" or "public" or "return" or "short" or "static" or "strictfp" or
            "super" or "switch" or "synchronized" or "this" or "throw" or "throws" or
            "transient" or "try" or "void" or "volatile" or "while" => true,
            _ => false
        };
    }
}
