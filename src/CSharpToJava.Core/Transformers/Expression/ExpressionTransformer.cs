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
            SyntaxKind.ImplicitObjectCreationExpression => TransformNew((ImplicitObjectCreationExpressionSyntax)node, context),
            SyntaxKind.ObjectCreationExpression => TransformObjectCreation((ObjectCreationExpressionSyntax)node, context),
            SyntaxKind.AnonymousObjectCreationExpression => TransformAnonymousObjectCreation((AnonymousObjectCreationExpressionSyntax)node, context),
            SyntaxKind.ArrayCreationExpression => TransformArrayCreation((ArrayCreationExpressionSyntax)node, context),
            SyntaxKind.ImplicitArrayCreationExpression => TransformImplicitArrayCreation((ImplicitArrayCreationExpressionSyntax)node, context),
            SyntaxKind.ArrayInitializerExpression => TransformArrayInitializer((InitializerExpressionSyntax)node, context),
            SyntaxKind.InterpolatedStringExpression => TransformInterpolatedString((InterpolatedStringExpressionSyntax)node, context),
            // SyntaxKind.StringEmptyExpression was removed in newer Roslyn versions
            // SyntaxKind.ArgListArgument was removed in newer Roslyn versions
            SyntaxKind.ThisExpression => "this",
            SyntaxKind.BaseExpression => "super",
            SyntaxKind.ArgListExpression => "// TODO: __arglist",
            SyntaxKind.MakeRefExpression or SyntaxKind.RefTypeExpression or SyntaxKind.RefValueExpression => "// TODO: ref expression",
            SyntaxKind.CheckedExpression => TransformChecked((CheckedExpressionSyntax)node, context),
            SyntaxKind.UncheckedExpression => TransformUnchecked((CheckedExpressionSyntax)node, context),
            SyntaxKind.AwaitExpression => TransformAwait((AwaitExpressionSyntax)node, context),
            SyntaxKind.QueryExpression => TransformQuery((QueryExpressionSyntax)node, context),
            // Lambda expressions can be either ParenthesizedLambdaExpression or SimpleLambdaExpression
            SyntaxKind.ParenthesizedLambdaExpression => TransformLambda((LambdaExpressionSyntax)node, context),
            SyntaxKind.SimpleLambdaExpression => TransformLambda((LambdaExpressionSyntax)node, context),
            SyntaxKind.ParenthesizedExpression => $"({Transform(((ParenthesizedExpressionSyntax)node).Expression, context)})",
            SyntaxKind.ThrowExpression => TransformThrowExpression((ThrowExpressionSyntax)node, context),
            SyntaxKind.SwitchExpression => TransformSwitchExpression((SwitchExpressionSyntax)node, context),
            SyntaxKind.WithExpression => "// TODO: with expression",
            SyntaxKind.IndexExpression => TransformIndexExpression((ElementAccessExpressionSyntax)node, context),
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

        // 检查是否是类型名称（使用 GetSymbolInfo 而不是 GetTypeInfo）
        var symbolInfo = context.SemanticModel?.GetSymbolInfo(node);
        if (symbolInfo.HasValue && symbolInfo.Value.Symbol is INamedTypeSymbol typeSymbol)
        {
            return context.MapType(typeSymbol);
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
            return typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : arg.ToString();
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
                    var aliasedLeft = context.MapType(targetType);
                    var aliasMemberName = node.Name.Identifier.Text;
                    return $"{aliasedLeft}.{aliasMemberName}";
                }
            }
        }

        var left = Transform(node.Expression, context);
        var memberName = node.Name.Identifier.Text;

        // 检查是否是方法调用目标（如果有类型信息）
        var symbolInfo = context.SemanticModel?.GetSymbolInfo(node);
        var hasSymbolInfo = symbolInfo.HasValue && symbolInfo.Value.Symbol != null;

        if (hasSymbolInfo)
        {
            var symbol = symbolInfo.Value.Symbol;

            // 检查是否是属性，需要转换为 getter 方法
            if (symbol is IPropertySymbol property)
            {
                // 跳过索引器属性（索引器由 ElementAccessExpression 处理）
                if (property.IsIndexer)
                {
                    return $"{left}[/* indexer */]";
                }

                // 将属性名转换为 getter 方法名
                var getterName = "get" + memberName;
                return $"{left}.{getterName}()";
            }

            // 检查是否是字段
            if (symbol is IFieldSymbol)
            {
                // 字段访问保持原样，但需要检查是否是常量
                return $"{left}.{memberName}";
            }

            // 检查是否是长度属性
            if (node.Name is { Identifier.Text: "Length" } &&
                context.SemanticModel.GetTypeInfo(node.Expression).Type?.SpecialType == SpecialType.System_String)
            {
                return $"{left}.length()";
            }

            // 检查是否是已知的方法/属性映射
            var containingType = symbol.ContainingType?.ToDisplayString();
            if (!string.IsNullOrEmpty(containingType))
            {
                var mappedMethod = context.TypeMappings.MapMethod(containingType, memberName);
                if (!string.IsNullOrEmpty(mappedMethod))
                {
                    return $"{left}.{mappedMethod}";
                }
            }
        }
        else
        {
            // 回退：使用启发式检测
            // 如果成员名以大写字母开头，可能是属性
            if (!string.IsNullOrEmpty(memberName) && char.IsUpper(memberName[0]))
            {
                // 特殊情况：一些已知的字段（即使以大写开头）
                if (memberName is "Length" && left.EndsWith("String"))
                {
                    return $"{left}.length()";
                }
                // 其他大写开头的成员名可能是属性
                return $"{left}.get{memberName}()";
            }
        }

        return $"{left}.{memberName}";
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
            if (typeInfo.HasValue && typeInfo.Value.Type != null)
            {
                var containingType = typeInfo.Value.Type.ToDisplayString();
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

        // 检查是否是用户定义的索引器（使用语义模型）
        var symbolInfo = context.SemanticModel?.GetSymbolInfo(node);
        if (symbolInfo.HasValue && symbolInfo.Value.Symbol != null)
        {
            var symbol = symbolInfo.Value.Symbol;
            // 如果是属性（C# 的索引器就是属性）
            if (symbol is IPropertySymbol property && property.IsIndexer)
            {
                // 转换为方法调用：get(index1, index2, ...)
                var args = node.ArgumentList != null
                    ? string.Join(", ", node.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)))
                    : "";

                // 使用小写的 get 前缀
                return $"{target}.get({args})";
            }
        }

        // 检查是否是多维数组访问
        var argCount = node.ArgumentList?.Arguments.Count ?? 0;
        if (argCount > 1)
        {
            // 多维数组访问，转换为方法调用
            var args = string.Join(", ", node.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)));
            return $"{target}.get({args})";
        }

        // 单维数组访问，保持 Java 数组语法
        var argsSingle = node.ArgumentList != null
            ? string.Join(", ", node.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)))
            : "";
        return $"{target}[{argsSingle}]";
    }

    private string TransformBinaryExpression(BinaryExpressionSyntax node, string op, ConversionContext context)
    {
        // 检查是否是 null 比较（null 比较不应该转换为操作符重载方法）
        if ((node.Right is LiteralExpressionSyntax rightLit && rightLit.Token.Text == "null") ||
            (node.Left is LiteralExpressionSyntax leftLit && leftLit.Token.Text == "null"))
        {
            // 对于 null 比较，使用常规运算符
            var nullLeft = Transform(node.Left, context);
            var nullRight = Transform(node.Right, context);
            return $"({nullLeft} {op} {nullRight})";
        }

        // 检查是否是操作符重载（需要转换为方法调用）
        var symbolInfo = context.SemanticModel?.GetSymbolInfo(node);
        if (symbolInfo.HasValue && symbolInfo.Value.Symbol != null)
        {
            var symbol = symbolInfo.Value.Symbol;
            // 如果是方法（操作符重载在 C# 中是静态方法）
            if (symbol is IMethodSymbol method && method.IsStatic && method.Name.StartsWith("op_"))
            {
                // 获取包含类型
                var containingType = method.ContainingType;
                if (containingType != null)
                {
                    // 检查是否是系统基础类型（这些不需要转换）
                    var containingTypeName = containingType.ToDisplayString();
                    if (IsSystemPrimitiveType(containingTypeName))
                    {
                        // 对于系统基础类型，使用常规运算符
                        var primLeft = Transform(node.Left, context);
                        var primRight = Transform(node.Right, context);
                        return $"({primLeft} {op} {primRight})";
                    }

                    var typeName = context.MapType(containingType);
                    var leftExpr = Transform(node.Left, context);
                    var rightExpr = Transform(node.Right, context);

                    // 将操作符映射到方法名
                    var methodName = method.Name switch
                    {
                        "op_Addition" => "Add",
                        "op_Subtraction" => "Subtract",
                        "op_Multiply" => "Multiply",
                        "op_Division" => "Divide",
                        "op_Modulus" => "Modulus",
                        "op_BitwiseAnd" => "BitwiseAnd",
                        "op_BitwiseOr" => "BitwiseOr",
                        "op_ExclusiveOr" => "Xor",
                        "op_LogicalAnd" => "LogicalAnd",
                        "op_LogicalOr" => "LogicalOr",
                        "op_LeftShift" => "LeftShift",
                        "op_RightShift" => "RightShift",
                        "op_Equality" => "Equals",
                        "op_Inequality" => "NotEquals",
                        "op_GreaterThan" => "CompareTo",
                        "op_GreaterThanOrEqual" => "CompareTo",
                        "op_LessThan" => "CompareTo",
                        "op_LessThanOrEqual" => "CompareTo",
                        "op_Increment" => "Increment",
                        "op_Decrement" => "Decrement",
                        "op_UnaryNegation" => "Negate",
                        "op_UnaryPlus" => "Plus",
                        "op_OnesComplement" => "OnesComplement",
                        _ => method.Name.Substring(3) // 移除 op_ 前缀
                    };

                    // 对于比较操作符，返回比较结果
                    if (method.Name is "op_Equality" or "op_Inequality")
                    {
                        return $"{typeName}.{methodName}({leftExpr}, {rightExpr})"; // 返回 boolean
                    }

                    // 对于关系操作符
                    if (method.Name is "op_GreaterThan" or "op_GreaterThanOrEqual" or "op_LessThan" or "op_LessThanOrEqual")
                    {
                        // CompareTo 返回 int，需要比较
                        var compareOp = method.Name switch
                        {
                            "op_GreaterThan" => ">",
                            "op_GreaterThanOrEqual" => ">=",
                            "op_LessThan" => "<",
                            "op_LessThanOrEqual" => "<=",
                            _ => op
                        };
                        return $"({typeName}.{methodName}({leftExpr}, {rightExpr}) {compareOp} 0)";
                    }

                    // 默认：调用静态方法
                    return $"{typeName}.{methodName}({leftExpr}, {rightExpr})";
                }
            }
        }

        var left = Transform(node.Left, context);
        var right = Transform(node.Right, context);
        return $"({left} {op} {right})";
    }

    private static bool IsSystemPrimitiveType(string typeName)
    {
        // 检查是否是 .NET 系统基础类型（这些不需要转换为方法调用）
        return typeName switch
        {
            "System.Int32" or "int" or "System.Int64" or "long" or
            "System.Int16" or "short" or "System.Byte" or "byte" or
            "System.SByte" or "System.UInt32" or "System.UInt64" or
            "System.UInt16" or "System.Single" or "float" or
            "System.Double" or "double" or "System.Boolean" or "bool" or
            "System.Char" or "char" or "System.String" or "string" or
            "System.Object" or "object" => true,
            _ => false
        };
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
        // 检查是否是索引器赋值（如 this[0, 0] = 1）
        if (node.Left is ElementAccessExpressionSyntax elementAccess)
        {
            var target = Transform(elementAccess.Expression, context);

            // 检查是否是用户定义的索引器
            var symbolInfo = context.SemanticModel?.GetSymbolInfo(elementAccess);
            if (symbolInfo.HasValue && symbolInfo.Value.Symbol is IPropertySymbol property && property.IsIndexer)
            {
                // 转换为 set 方法调用：target.set(i1, i2, value)
                var indexArgs = elementAccess.ArgumentList != null
                    ? string.Join(", ", elementAccess.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)))
                    : "";
                var value = Transform(node.Right, context);
                return $"{target}.set({indexArgs}, {value})";
            }

            // 检查是否是多维数组赋值
            var argCount = elementAccess.ArgumentList?.Arguments.Count ?? 0;
            if (argCount > 1)
            {
                var indexArgs = string.Join(", ", elementAccess.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)));
                var value = Transform(node.Right, context);
                return $"{target}.set({indexArgs}, {value})";
            }
        }

        // 检查是否是属性赋值（如 obj.Property = value）
        if (node.Left is MemberAccessExpressionSyntax memberAccess)
        {
            var symbolInfo = context.SemanticModel?.GetSymbolInfo(memberAccess);

            // 检查是否是事件（C# 事件使用 +=/-= 进行订阅）
            if (symbolInfo.HasValue && symbolInfo.Value.Symbol is IEventSymbol)
            {
                // Java 不支持 C# 风格的事件，需要转换为监听器模式
                // 这里暂时生成注释，用户需要手动实现
                var target = Transform(memberAccess.Expression, context);
                var eventName = memberAccess.Name.Identifier.Text;
                var handler = Transform(node.Right, context);
                var operation = op == "+=" ? "add" : "remove";
                return $"/* TODO: Event subscription: {target}.{operation}{eventName}({handler}) */";
            }

            var isProperty = symbolInfo.HasValue && symbolInfo.Value.Symbol is IPropertySymbol property && !property.IsIndexer;

            // 如果语义模型检测到是属性，或者使用启发式检测（名称以大写字母开头）
            // 但是跳过 += 和 -= 操作，因为它们可能是事件订阅
            if ((isProperty || char.IsUpper(memberAccess.Name.Identifier.Text[0])) && (op == "=" || op == "&&=" || op == "||="))
            {
                var target = Transform(memberAccess.Expression, context);
                var propertyName = memberAccess.Name.Identifier.Text;
                var setterName = "set" + propertyName;
                var value = Transform(node.Right, context);

                // 对于简单赋值，转换为 setter 调用
                return $"{target}.{setterName}({value})";
            }

            // 对于 +=/-= 的情况，可能是事件订阅或复合赋值
            // 如果不确定，生成注释
            if (op == "+=" || op == "-=")
            {
                var target = Transform(memberAccess.Expression, context);
                var propertyName = memberAccess.Name.Identifier.Text;
                var value = Transform(node.Right, context);
                return $"/* TODO: Event or compound assignment: {target}.{propertyName} {op} {value} */ {target}.{propertyName} = {value}";
            }
        }

        // 复合赋值操作符（如 +=, -= 等）需要特殊处理
        if (op != "=" && node.Left is ElementAccessExpressionSyntax)
        {
            // 对于复合赋值，我们需要生成完整的表达式
            // 例如：arr[i] += 1  =>  arr[i] = arr[i] + 1
            var rightValue = Transform(node.Right, context);
            var elemAccess = node.Left as ElementAccessExpressionSyntax;
            var targetObj = Transform(elemAccess.Expression, context);
            var indexArgs = elemAccess.ArgumentList != null
                ? string.Join(", ", elemAccess.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)))
                : "";

            // 生成读取-修改-写入序列
            return $"{targetObj}.set({indexArgs}, {targetObj}.get({indexArgs}) {op} {rightValue})";
        }

        var left = Transform(node.Left, context);
        var right = Transform(node.Right, context);
        return $"{left} {op} {right}";
    }

    private string TransformPostfix(PostfixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        var operand = Transform(node.Operand, context);
        return $"{operand}{op}";
    }

    private string TransformPrefix(PrefixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        var operand = Transform(node.Operand, context);
        return $"{op}{operand}";
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
        var targetType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";
        var expression = Transform(node.Expression, context);
        return $"(({targetType}) {expression})";
    }

    private string TransformIs(BinaryExpressionSyntax node, ConversionContext context)
    {
        var left = Transform(node.Left, context);
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Right);
        var rightType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";
        return $"({left} instanceof {rightType})";
    }

    private string TransformAs(BinaryExpressionSyntax node, ConversionContext context)
    {
        var left = Transform(node.Left, context);
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Right);
        var rightType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";
        return $"({rightType}) {left}"; // Java 没有 as，使用强制转换
    }

    private string TransformTypeOf(TypeOfExpressionSyntax node, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type);
        var type = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";
        return $"{type}.class";
    }

    private string TransformDefault(DefaultExpressionSyntax node, ConversionContext context)
    {
        if (node.Type is null)
        {
            return "null";
        }

        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type);
        var type = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";

        // 根据类型返回默认值
        return type switch
        {
            "boolean" => "false",
            "byte" or "short" or "int" or "long" or "float" or "double" => "0",
            "char" => "'\\0'",
            _ => "null"
        };
    }

    private string TransformNew(ImplicitObjectCreationExpressionSyntax node, ConversionContext context)
    {
        // ImplicitObjectCreationExpressionSyntax 没有 Type 属性，需要从语义模型获取
        var typeInfo = context.SemanticModel?.GetTypeInfo(node);
        var type = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";
        var args = TransformArgumentList(node.ArgumentList, context);
        return $"new {type}({args})";
    }

    private string TransformObjectCreation(ObjectCreationExpressionSyntax node, ConversionContext context)
    {
        string type;
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type);

        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            // 使用语义模型获取类型
            type = context.MapType(typeInfo.Value.Type);
        }
        else
        {
            // 回退：从语法获取类型名
            // 尝试从类型语法中提取类型名
            var typeName = ExtractTypeName(node.Type, context);
            if (!string.IsNullOrEmpty(typeName))
            {
                type = typeName;
            }
            else
            {
                type = "Object";
            }
        }

        var args = TransformArgumentList(node.ArgumentList, context);
        return $"new {type}({args})";
    }

    private string ExtractTypeName(TypeSyntax typeSyntax, ConversionContext context)
    {
        // 处理简单类型名
        if (typeSyntax is IdentifierNameSyntax identifierName)
        {
            var name = identifierName.Identifier.Text;
            // 尝试通过类型映射获取
            var mapped = context.TypeMappings.MapType(name);
            return mapped != name ? mapped : name;
        }

        // 处理泛型类型
        if (typeSyntax is GenericNameSyntax genericName)
        {
            var name = genericName.Identifier.Text;
            var typeArgs = string.Join(", ", genericName.TypeArgumentList.Arguments.Select(t => ExtractTypeName(t, context)));
            return $"{name}<{typeArgs}>";
        }

        // 处理限定名（如 System.Point）
        if (typeSyntax is QualifiedNameSyntax qualifiedName)
        {
            var left = ExtractTypeName(qualifiedName.Left, context);
            var right = qualifiedName.Right.Identifier.Text;
            return $"{left}.{right}";
        }

        // 处理预定义类型
        if (typeSyntax is PredefinedTypeSyntax predefinedType)
        {
            return predefinedType.Keyword.Text switch
            {
                "int" => "int",
                "long" => "long",
                "float" => "float",
                "double" => "double",
                "bool" => "boolean",
                "char" => "char",
                "string" => "String",
                "object" => "Object",
                "void" => "void",
                _ => "Object"
            };
        }

        return null;
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
        var elementType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";

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
        // 处理 from-where-select 和 from-from-select (SelectMany) 模式

        var fromClause = node.FromClause;
        var source = Transform(fromClause.Expression, context);
        var identifier = fromClause.Identifier.ValueText;

        // 首先转换为流
        var result = $"{source}.stream()";

        // 处理查询主体
        result = TransformQueryBodyRecursive(node.Body, identifier, result, context);

        // 收集为列表
        result = $"{result}.collect(Collectors.toList())";

        return result;
    }

    private string TransformQueryBodyRecursive(QueryBodySyntax body, string identifier, string expression, ConversionContext context)
    {
        var result = expression;
        var currentIdentifier = identifier;

        // 处理中间子句
        foreach (var clause in body.Clauses)
        {
            if (clause is WhereClauseSyntax whereClause)
            {
                var condition = Transform(whereClause.Condition, context);
                result = $"{result}.filter({currentIdentifier} -> {condition})";
            }
            else if (clause is FromClauseSyntax fromClause)
            {
                // 处理嵌套 from (SelectMany)
                var newSource = Transform(fromClause.Expression, context);
                var newIdentifier = fromClause.Identifier.ValueText;
                result = $"{result}.flatMap({currentIdentifier} -> {newSource}.map({newIdentifier} -> {newIdentifier})";
                currentIdentifier = newIdentifier;
            }
            else if (clause is JoinClauseSyntax joinClause)
            {
                result = $"{result} /* TODO: join */";
            }
            else if (clause is LetClauseSyntax)
            {
                result = $"{result} /* TODO: let */";
            }
            else if (clause is OrderByClauseSyntax)
            {
                result = $"{result} /* TODO: orderby */";
            }
        }

        // 处理 select 或 groupby
        var selectOrGroup = body.SelectOrGroup;
        if (selectOrGroup is SelectClauseSyntax selectClause)
        {
            var selector = Transform(selectClause.Expression, context);
            result = $"{result}.map({currentIdentifier} -> {selector})";
        }
        else if (selectOrGroup is GroupClauseSyntax groupClause)
        {
            result = $"{result} /* TODO: groupBy */";
        }

        // 如果没有 select，默认选择标识符
        if (selectOrGroup == null)
        {
            result = $"{result}.map({currentIdentifier} -> {currentIdentifier})";
        }

        return result;
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
                    var type = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "";
                    return string.IsNullOrEmpty(type) ? p.Identifier.Text : $"{type} {p.Identifier.Text}";
                }) ?? Enumerable.Empty<string>()
            ),
            SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.Text,
            _ => ""
        };

        var body = node.Body switch
        {
            BlockSyntax block => new Transformers.Statement.StatementTransformer().TransformBlock(block, context),
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

    private string TransformIndexExpression(ElementAccessExpressionSyntax node, ConversionContext context)
    {
        var target = Transform(node.Expression, context);

        // 检查是否是用户定义的索引器（C# 8+ index 表达式）
        var symbolInfo = context.SemanticModel?.GetSymbolInfo(node);
        if (symbolInfo.HasValue && symbolInfo.Value.Symbol is IPropertySymbol property && property.IsIndexer)
        {
            // 转换为方法调用：get(index)
            var args = string.Join(", ", node.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)));
            return $"{target}.get({args})";
        }

        // 默认情况：使用数组语法
        var argList = string.Join(", ", node.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)));
        return $"{target}[{argList}]";
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
        var type = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";
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
