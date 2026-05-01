using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.HIR;

public class HIRExpressionGenerator
{
    private ConversionContext _ctx = null!;

    public IrExpression Generate(ExpressionSyntax expr, ConversionContext context)
    {
        _ctx = context;
        return expr switch
        {
            LiteralExpressionSyntax lit => GenerateLiteral(lit),
            IdentifierNameSyntax id => GenerateIdentifier(id),
            BinaryExpressionSyntax bin => GenerateBinary(bin),
            PrefixUnaryExpressionSyntax pre => GeneratePrefixUnary(pre),
            PostfixUnaryExpressionSyntax post => GeneratePostfixUnary(post),
            InvocationExpressionSyntax inv => GenerateInvocation(inv),
            MemberAccessExpressionSyntax mem => GenerateMemberAccess(mem),
            ObjectCreationExpressionSyntax ne => GenerateObjectCreation(ne),
            ArrayCreationExpressionSyntax arr => GenerateArrayCreation(arr),
            ElementAccessExpressionSyntax elem => GenerateElementAccess(elem),
            ConditionalExpressionSyntax cond => GenerateConditional(cond),
            AssignmentExpressionSyntax asgn => GenerateAssignment(asgn),
            CastExpressionSyntax cast => GenerateCast(cast),
            ParenthesizedExpressionSyntax paren => Generate(paren.Expression, context),
            SimpleLambdaExpressionSyntax lam => GenerateSimpleLambda(lam),
            ParenthesizedLambdaExpressionSyntax plam => GenerateParenthesizedLambda(plam),
            ThisExpressionSyntax th => new IrThisExpression { Symbol = GetSymbol(th) },
            BaseExpressionSyntax bas => new IrThisExpression { IsSuper = true, Symbol = GetSymbol(bas) },
            IsPatternExpressionSyntax isPat => GenerateIsPattern(isPat),
            ConditionalAccessExpressionSyntax condAcc => GenerateConditionalAccess(condAcc),
            _ => new IrLiteralExpression { Value = "/* TODO expr: " + expr.Kind() + " */" },
        };
    }

    private IrLiteralExpression GenerateLiteral(LiteralExpressionSyntax node) =>
        new() { Value = node.Token.Text };

    private IrIdentifierExpression GenerateIdentifier(IdentifierNameSyntax node)
    {
        var symbol = GetSymbol(node);
        var name = node.Identifier.Text;
        if (symbol is ITypeSymbol typeSym)
        {
            name = _ctx.MapType(typeSym);
            return new IrIdentifierExpression { Name = name, Symbol = symbol, JavaType = name };
        }
        return new IrIdentifierExpression { Name = name, Symbol = symbol };
    }

    private IrExpression GenerateBinary(BinaryExpressionSyntax node)
    {
        var symbol = GetSymbol(node);
        if (symbol is IMethodSymbol ms && ms.MethodKind == MethodKind.UserDefinedOperator)
        {
            return new IrCSharpOperatorCallExpression
            {
                Left = Generate(node.Left, _ctx),
                OperatorMethodName = ms.Name,
                Right = Generate(node.Right, _ctx),
                Symbol = symbol,
                JavaType = _ctx.MapType(ms.ReturnType),
            };
        }
        return new IrBinaryExpression
        {
            Left = Generate(node.Left, _ctx),
            Operator = MapBinaryOperator(node.OperatorToken.Kind()),
            Right = Generate(node.Right, _ctx),
            Symbol = symbol,
        };
    }

    private IrExpression GeneratePrefixUnary(PrefixUnaryExpressionSyntax node)
    {
        var op = node.OperatorToken.Kind() switch
        {
            SyntaxKind.PlusToken => IrUnaryOp.Plus,
            SyntaxKind.MinusToken => IrUnaryOp.Minus,
            SyntaxKind.ExclamationToken => IrUnaryOp.Not,
            SyntaxKind.TildeToken => IrUnaryOp.BitwiseNot,
            SyntaxKind.PlusPlusToken => IrUnaryOp.PreIncrement,
            SyntaxKind.MinusMinusToken => IrUnaryOp.PreDecrement,
            _ => IrUnaryOp.Not,
        };
        return new IrUnaryExpression { Operator = op, Operand = Generate(node.Operand, _ctx), Symbol = GetSymbol(node) };
    }

    private IrExpression GeneratePostfixUnary(PostfixUnaryExpressionSyntax node)
    {
        var op = node.OperatorToken.Kind() switch
        {
            SyntaxKind.PlusPlusToken => IrUnaryOp.PostIncrement,
            SyntaxKind.MinusMinusToken => IrUnaryOp.PostDecrement,
            _ => IrUnaryOp.PostIncrement,
        };
        return new IrUnaryExpression { Operator = op, Operand = Generate(node.Operand, _ctx), Symbol = GetSymbol(node) };
    }

    private IrExpression GenerateInvocation(InvocationExpressionSyntax node)
    {
        var symbol = GetSymbol(node) as IMethodSymbol;
        var javaMethodName = symbol?.Name ?? node.Expression.ToString();
        IrExpression? targetExpr = null;
        if (node.Expression is MemberAccessExpressionSyntax ma)
            targetExpr = Generate(ma.Expression, _ctx);

        var args = node.ArgumentList.Arguments.Select(a =>
        {
            var argExpr = Generate(a.Expression, _ctx);
            if (a.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || a.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                return new IrCSharpRefOutExpression
                {
                    Inner = argExpr,
                    IsRef = a.RefKindKeyword.IsKind(SyntaxKind.RefKeyword),
                    IsOut = a.RefKindKeyword.IsKind(SyntaxKind.OutKeyword),
                };
            }
            return argExpr;
        }).ToList();

        var invocation = new IrInvocationExpression
        {
            Target = targetExpr,
            MethodName = javaMethodName,
            Symbol = symbol,
            JavaType = symbol != null ? _ctx.MapType(symbol.ReturnType) : null,
        };
        invocation.Arguments.AddRange(args);
        return invocation;
    }

    private IrExpression GenerateMemberAccess(MemberAccessExpressionSyntax node)
    {
        var symbol = GetSymbol(node);
        var target = Generate(node.Expression, _ctx);
        var memberName = node.Name.Identifier.Text;

        if (symbol is IPropertySymbol)
        {
            return new IrCSharpPropertyAccessExpression
            {
                Target = target,
                PropertyName = memberName,
                Symbol = symbol,
                JavaType = symbol is IPropertySymbol ps ? _ctx.MapType(ps.Type) : null,
            };
        }
        if (symbol is IEventSymbol)
        {
            return new IrCSharpEventExpression
            {
                Target = target,
                EventName = memberName,
                Symbol = symbol,
            };
        }
        return new IrMemberAccessExpression
        {
            Target = target,
            MemberName = memberName,
            Symbol = symbol,
        };
    }

    private IrExpression GenerateElementAccess(ElementAccessExpressionSyntax node)
    {
        var symbol = GetSymbol(node);
        var target = Generate(node.Expression, _ctx);
        var indices = node.ArgumentList.Arguments.Select(a => Generate(a.Expression, _ctx)).ToList();
        if (symbol is IPropertySymbol)
        {
            var indexer = new IrCSharpIndexerAccessExpression
            {
                Target = target,
                Symbol = symbol,
            };
            indexer.Indices.AddRange(indices);
            return indexer;
        }
        return new IrArrayAccessExpression { Target = target, Index = indices[0], Symbol = symbol };
    }

    private IrNewExpression GenerateObjectCreation(ObjectCreationExpressionSyntax node)
    {
        var typeName = _ctx.MapTypeFromSyntax(node.Type);
        var args = node.ArgumentList?.Arguments.Select(a => Generate(a.Expression, _ctx)).ToList() ?? new();
        var newExpr = new IrNewExpression { TypeName = typeName, Symbol = GetSymbol(node), JavaType = typeName };
        newExpr.Arguments.AddRange(args);
        return newExpr;
    }

    private IrExpression GenerateArrayCreation(ArrayCreationExpressionSyntax node)
    {
        var elemType = _ctx.MapTypeFromSyntax(node.Type.ElementType);
        return new IrNewExpression
        {
            TypeName = elemType + "[]",
            ArrayInitializer = node.Initializer?.ToString() ?? "{}",
            Symbol = GetSymbol(node),
            JavaType = elemType + "[]",
        };
    }

    private IrConditionalExpression GenerateConditional(ConditionalExpressionSyntax node) =>
        new()
        {
            Condition = Generate(node.Condition, _ctx),
            WhenTrue = Generate(node.WhenTrue, _ctx),
            WhenFalse = Generate(node.WhenFalse, _ctx),
            Symbol = GetSymbol(node),
        };

    private IrAssignmentExpression GenerateAssignment(AssignmentExpressionSyntax node) =>
        new()
        {
            Target = Generate(node.Left, _ctx),
            Operator = MapAssignmentOp(node.OperatorToken.Kind()),
            Value = Generate(node.Right, _ctx),
            Symbol = GetSymbol(node),
        };

    private IrCastExpression GenerateCast(CastExpressionSyntax node)
    {
        var javaType = _ctx.MapTypeFromSyntax(node.Type);
        return new IrCastExpression
        {
            TargetType = javaType,
            Expression = Generate(node.Expression, _ctx),
            Symbol = GetSymbol(node),
            JavaType = javaType,
        };
    }

    private IrLambdaExpression GenerateSimpleLambda(SimpleLambdaExpressionSyntax node) =>
        new()
        {
            Parameters = { new IrLambdaParameter { Name = node.Parameter.Identifier.Text, Type = node.Parameter.Type?.ToString() } },
            ExpressionBody = node.Body is ExpressionSyntax expr ? Generate(expr, _ctx) : null,
            BlockBody = node.Body is BlockSyntax block ? new HIRStatementGenerator().GenerateBlock(block, _ctx) : null,
        };

    private IrLambdaExpression GenerateParenthesizedLambda(ParenthesizedLambdaExpressionSyntax node)
    {
        var lambda = new IrLambdaExpression
        {
            ExpressionBody = node.Body is ExpressionSyntax expr ? Generate(expr, _ctx) : null,
            BlockBody = node.Body is BlockSyntax block ? new HIRStatementGenerator().GenerateBlock(block, _ctx) : null,
        };
        lambda.Parameters.AddRange(node.ParameterList.Parameters.Select(p => new IrLambdaParameter { Name = p.Identifier.Text, Type = p.Type?.ToString() }));
        return lambda;
    }

    private IrExpression GenerateIsPattern(IsPatternExpressionSyntax node)
    {
        var subject = Generate(node.Expression, _ctx);
        if (node.Pattern is DeclarationPatternSyntax declPat)
        {
            var typeName = _ctx.MapTypeFromSyntax(declPat.Type);
            return new IrInstanceOfExpression
            {
                Expression = subject,
                TypeName = typeName,
                PatternVariable = declPat.Designation is SingleVariableDesignationSyntax sv ? sv.Identifier.Text : null,
            };
        }
        if (node.Pattern is ConstantPatternSyntax constPat)
        {
            return new IrBinaryExpression
            {
                Left = subject,
                Operator = IrBinaryOp.Equals,
                Right = Generate(constPat.Expression, _ctx),
            };
        }
        return subject;
    }

    private IrExpression GenerateConditionalAccess(ConditionalAccessExpressionSyntax node)
    {
        var target = Generate(node.Expression, _ctx);
        IrExpression whenNotNull = node.WhenNotNull switch
        {
            MemberBindingExpressionSyntax mb => new IrMemberAccessExpression
            {
                Target = new IrIdentifierExpression { Name = "_tmp" },
                MemberName = mb.Name.Identifier.Text,
            },
            InvocationExpressionSyntax inv => Generate(inv, _ctx),
            _ => Generate((ExpressionSyntax)node.WhenNotNull, _ctx),
        };
        return new IrConditionalExpression
        {
            Condition = new IrBinaryExpression
            {
                Left = target,
                Operator = IrBinaryOp.Equals,
                Right = new IrLiteralExpression { Value = "null" },
            },
            WhenTrue = new IrLiteralExpression { Value = "null" },
            WhenFalse = whenNotNull,
        };
    }

    // -- Helpers --

    private ISymbol? GetSymbol(ExpressionSyntax node)
    {
        return _ctx.SemanticModel?.GetSymbolInfo(node).Symbol;
    }

    private static IrBinaryOp MapBinaryOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.PlusToken => IrBinaryOp.Add,
        SyntaxKind.MinusToken => IrBinaryOp.Subtract,
        SyntaxKind.AsteriskToken => IrBinaryOp.Multiply,
        SyntaxKind.SlashToken => IrBinaryOp.Divide,
        SyntaxKind.PercentToken => IrBinaryOp.Modulo,
        SyntaxKind.AmpersandAmpersandToken => IrBinaryOp.LogicalAnd,
        SyntaxKind.BarBarToken => IrBinaryOp.LogicalOr,
        SyntaxKind.AmpersandToken => IrBinaryOp.BitwiseAnd,
        SyntaxKind.BarToken => IrBinaryOp.BitwiseOr,
        SyntaxKind.CaretToken => IrBinaryOp.BitwiseXor,
        SyntaxKind.LessThanLessThanToken => IrBinaryOp.ShiftLeft,
        SyntaxKind.GreaterThanGreaterThanToken => IrBinaryOp.ShiftRight,
        SyntaxKind.EqualsEqualsToken => IrBinaryOp.Equals,
        SyntaxKind.ExclamationEqualsToken => IrBinaryOp.NotEquals,
        SyntaxKind.LessThanToken => IrBinaryOp.LessThan,
        SyntaxKind.LessThanEqualsToken => IrBinaryOp.LessThanOrEqual,
        SyntaxKind.GreaterThanToken => IrBinaryOp.GreaterThan,
        SyntaxKind.GreaterThanEqualsToken => IrBinaryOp.GreaterThanOrEqual,
        SyntaxKind.QuestionQuestionToken => IrBinaryOp.NullCoalescing,
        _ => IrBinaryOp.Add,
    };

    private static IrAssignmentOp MapAssignmentOp(SyntaxKind kind) => kind switch
    {
        SyntaxKind.EqualsToken => IrAssignmentOp.Assign,
        SyntaxKind.PlusEqualsToken => IrAssignmentOp.AddAssign,
        SyntaxKind.MinusEqualsToken => IrAssignmentOp.SubtractAssign,
        SyntaxKind.AsteriskEqualsToken => IrAssignmentOp.MultiplyAssign,
        SyntaxKind.SlashEqualsToken => IrAssignmentOp.DivideAssign,
        SyntaxKind.AmpersandEqualsToken => IrAssignmentOp.AndAssign,
        SyntaxKind.BarEqualsToken => IrAssignmentOp.OrAssign,
        SyntaxKind.CaretEqualsToken => IrAssignmentOp.XorAssign,
        _ => IrAssignmentOp.Assign,
    };
}
