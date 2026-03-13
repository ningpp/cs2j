using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Type;

namespace CSharpToJava.Core.Transformers.Member;

/// <summary>
/// 方法转换器
/// </summary>
public class MethodTransformer : IMemberTransformer
{
    public JavaSyntaxNode Transform(MemberDeclarationSyntax node, ConversionContext context)
    {
        if (node is not MethodDeclarationSyntax methodDecl)
        {
            throw new ArgumentException($"Expected MethodDeclarationSyntax, got {node.GetType()}");
        }

        var methodInfo = context.SemanticModel?.GetSymbolInfo(methodDecl).Symbol as IMethodSymbol;
        context.EnterMethod(methodInfo);

        var javaMethod = new JavaMethodDeclaration
        {
            Name = GetJavaMethodName(methodDecl, methodInfo, context),
            Modifiers = ConvertModifiers(methodDecl.Modifiers),
            ReturnType = GetReturnType(methodDecl, context)
        };

        // Explicit interface implementations (e.g., ICurve ICurve.Clone()) have no access modifier in C#,
        // but interface implementations in Java MUST be public.
        if (methodDecl.ExplicitInterfaceSpecifier != null && !javaMethod.Modifiers.HasFlag(JavaModifiers.Public))
            javaMethod.Modifiers |= JavaModifiers.Public;

        // 处理类型参数（泛型方法）
        foreach (var typeParam in methodDecl.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            javaMethod.TypeParameters.Add(new JavaTypeParameter(typeParam.Identifier.Text));
        }

        // Propagate generic type parameter constraints
        if (methodDecl.ConstraintClauses.Count > 0)
        {
            ClassTransformer.ApplyTypeParameterConstraints(methodDecl.ConstraintClauses, javaMethod.TypeParameters, context);
        }

        // Check if this is an extension method (has 'this' on first param)
        bool isExtensionMethod = methodDecl.ParameterList?.Parameters.Count > 0 &&
            methodDecl.ParameterList.Parameters[0].Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword));

        // 处理参数
        foreach (var param in methodDecl.ParameterList?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
        {
            var converted = ConvertParameter(param, context);
            if (converted != null)
                javaMethod.Parameters.Add(converted);
        }

        // 注意：C# 异常规范在 Java 中需要通过 throws 子句声明
        // 这里可以添加对异常的处理逻辑

        // 处理方法体
        if (methodDecl.Body != null)
        {
            var statementTransformer = new Transformers.Statement.StatementTransformer();
            javaMethod.Body = statementTransformer.TransformBlock(methodDecl.Body, context);
        }
        else if (methodDecl.ExpressionBody != null)
        {
            var exprTransformer = new Transformers.Expression.ExpressionTransformer();
            javaMethod.Body = exprTransformer.Transform(methodDecl.ExpressionBody.Expression, context);
            javaMethod.IsBodyExpression = true;
        }
        else if (methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)) ||
                 methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword)))
        {
            // 抽象方法或外部方法没有主体
            javaMethod.Body = null;
        }

        context.LeaveMethod();

        // Java 规则：静态方法不能引用外部类的类型参数。
        // 如果方法为 static 且签名中出现了外部类的类型参数，需将其提升为方法级类型参数。
        if (javaMethod.Modifiers.HasFlag(JavaModifiers.Static) && context.CurrentType?.TypeParameters.Count > 0)
        {
            var methodOwnTypeParamNames = javaMethod.TypeParameters.Select(tp => tp.Name).ToHashSet(StringComparer.Ordinal);
            // Include the body in the search so that type params used in foreach element types are also promoted
            var signatureText = javaMethod.ReturnType + " " +
                string.Join(" ", javaMethod.Parameters.Select(p => p.Type)) + " " +
                (javaMethod.Body ?? "");
            var toAdd = new List<JavaTypeParameter>();
            foreach (var classParam in context.CurrentType.TypeParameters)
            {
                if (!methodOwnTypeParamNames.Contains(classParam.Name) &&
                    System.Text.RegularExpressions.Regex.IsMatch(signatureText,
                        $@"\b{System.Text.RegularExpressions.Regex.Escape(classParam.Name)}\b"))
                {
                    toAdd.Add(classParam);
                }
            }
            // Prepend class type params (in order) before any method-own type params
            for (int i = toAdd.Count - 1; i >= 0; i--)
                javaMethod.TypeParameters.Insert(0, toAdd[i]);
        }

        return javaMethod;
    }

    private string GetJavaMethodName(MethodDeclarationSyntax methodDecl, IMethodSymbol? methodInfo, ConversionContext context)
    {
        var name = methodDecl.Identifier.Text;

        // 处理运算符重载
        if (methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.OperatorKeyword)))
        {
            return ConvertOperatorName(name);
        }

        // 处理属性访问器样式的方法（get_Name, set_Name）
        if (name.StartsWith("get_") || name.StartsWith("set_"))
        {
            return char.ToUpper(name[4]) + name.Substring(5);
        }

        // 检查类型映射中的方法名映射（含实现的接口）
        if (methodInfo?.ContainingType != null)
        {
            var containingType = methodInfo.ContainingType.ToDisplayString();
            var mappedName = context.TypeMappings.MapMethod(containingType, name);
            if (!string.IsNullOrEmpty(mappedName))
            {
                return mappedName;
            }

            // Also check all interfaces implemented by the containing type
            foreach (var iface in methodInfo.ContainingType.AllInterfaces)
            {
                var ifaceType = iface.ConstructedFrom.ToDisplayString();
                mappedName = context.TypeMappings.MapMethod(ifaceType, name);
                if (!string.IsNullOrEmpty(mappedName))
                    return mappedName;
            }
        }

        // Java uses camelCase for all method names
        // Special cases where simple camelCase gives the wrong Java name
        if (name == "GetHashCode") return "hashCode";
        if (name == "GetEnumerator") return "iterator";
        if (name == "GetType") return "getClass";

        var camelName = name.Length > 0 ? char.ToLower(name[0]) + name.Substring(1) : name;
        // Escape Java keywords (e.g. Assert → assert → assertValue)
        return ConversionContext.EscapeJavaKeyword(camelName);
    }

    private string ConvertOperatorName(string csharpOperator)
    {
        return csharpOperator switch
        {
            "op_Addition" => "add",
            "op_Subtraction" => "subtract",
            "op_Multiply" => "multiply",
            "op_Division" => "divide",
            "op_Modulus" => "mod",
            "op_Equality" => "equals",
            "op_Inequality" => "notEquals",
            "op_GreaterThan" => "greaterThan",
            "op_LessThan" => "lessThan",
            "op_GreaterThanOrEqual" => "greaterThanOrEqual",
            "op_LessThanOrEqual" => "lessThanOrEqual",
            "op_Implicit" => "valueOf",
            "op_Explicit" => "valueOf",
            "op_Increment" => "increment",
            "op_Decrement" => "decrement",
            "op_UnaryNegation" => "negate",
            "op_UnaryPlus" => "plus",
            "op_LogicalNot" => "not",
            "op_BitwiseAnd" => "and",
            "op_BitwiseOr" => "or",
            "op_ExclusiveOr" => "xor",
            _ => csharpOperator
        };
    }

    private string GetReturnType(MethodDeclarationSyntax methodDecl, ConversionContext context)
    {
        if (methodDecl.ReturnType is PredefinedTypeSyntax predefinedType &&
            predefinedType.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            return "void";
        }

        var typeInfo = context.SemanticModel?.GetTypeInfo(methodDecl.ReturnType);
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            // 处理 async 方法
            if (methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            {
                context.IsInAsyncContext = true;
                var returnType = context.MapType(typeInfo.Value.Type);

                // 如果是 Task<T>，返回 CompletableFuture<T>
                // 如果是 Task，返回 CompletableFuture<Void>
                return returnType.StartsWith("CompletableFuture") ? returnType : $"CompletableFuture<{returnType}>";
            }

            return context.MapType(typeInfo.Value.Type);
        }

        return "Object";
    }

    private JavaParameter? ConvertParameter(ParameterSyntax param, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(param.Type!);
        var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";

        var paramName = ConversionContext.EscapeJavaKeyword(param.Identifier.Text);
        var javaParam = new JavaParameter(javaType, paramName);

        // 处理修饰符
        foreach (var modifier in param.Modifiers)
        {
            switch (modifier.Kind())
            {
                case SyntaxKind.RefKeyword:
                case SyntaxKind.OutKeyword:
                    // Convert ref/out to Holder pattern
                    javaParam = new JavaParameter(GetHolderType(javaType), paramName);
                    // Add import for the holder type if needed (Holder classes are in the project package)
                    break;
                case SyntaxKind.ParamsKeyword:
                    javaParam.IsVarArgs = true;
                    break;
                case SyntaxKind.ThisKeyword:
                    // Extension method first parameter - skip it (the method stays static)
                    return null;
                case SyntaxKind.InKeyword:
                    // 'in' parameter - treat like ref (read-only ref) but just pass by value in Java
                    break;
            }
        }

        // 处理默认值
        if (param.Default != null)
        {
            // Java 不支持参数默认值，需要方法重载
            context.Diagnostics.Warning(
                $"Java doesn't support default parameter values. Parameter '{param.Identifier.Text}' default value ignored.",
                param.GetLocation()
            );
        }

        return javaParam;
    }

    private static string GetHolderType(string javaType)
    {
        return CSharpToJava.Core.Transformers.Type.DelegateTransformer.GetHolderType(javaType);
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
                SyntaxKind.VirtualKeyword => JavaModifiers.None,  // Java 默认 virtual
                SyntaxKind.OverrideKeyword => JavaModifiers.Override,
                SyntaxKind.NewKeyword => JavaModifiers.Override,
                SyntaxKind.AbstractKeyword => JavaModifiers.Abstract,
                SyntaxKind.SealedKeyword => JavaModifiers.Final,
                SyntaxKind.AsyncKeyword => JavaModifiers.None,
                SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                SyntaxKind.ExternKeyword => JavaModifiers.Native,
                SyntaxKind.PartialKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
        }

        // C# "protected internal" maps to Protected | Public — Java doesn't allow both; keep Protected.
        if ((result & JavaModifiers.Protected) != 0 && (result & JavaModifiers.Public) != 0)
            result &= ~JavaModifiers.Public;

        return result;
    }
}
