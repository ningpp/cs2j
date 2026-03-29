namespace CSharpToJava.Core.Java;

/// <summary>
/// Java IR 语法树重写器 — 遍历并可选地替换每个节点。
/// 子类覆盖 Visit* 方法来修改特定节点类型。
/// 默认实现返回原节点（深度优先遍历子节点）。
/// </summary>
public class JavaSyntaxRewriter
{
    // ─── 编译单元 ─────────────────────────────────────────

    public virtual JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        for (int i = 0; i < node.TypeDeclarations.Count; i++)
        {
            node.TypeDeclarations[i] = VisitTypeDeclaration(node.TypeDeclarations[i]);
        }
        return node;
    }

    // ─── 类型声明 ─────────────────────────────────────────

    public virtual JavaTypeDeclaration VisitTypeDeclaration(JavaTypeDeclaration node)
    {
        return node switch
        {
            JavaClassDeclaration classDecl => VisitClassDeclaration(classDecl),
            JavaInterfaceDeclaration ifDecl => VisitInterfaceDeclaration(ifDecl),
            JavaEnumDeclaration enumDecl => VisitEnumDeclaration(enumDecl),
            _ => node,
        };
    }

    public virtual JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        for (int i = 0; i < node.Fields.Count; i++)
            node.Fields[i] = VisitFieldDeclaration(node.Fields[i]);

        for (int i = 0; i < node.Constructors.Count; i++)
            node.Constructors[i] = VisitConstructorDeclaration(node.Constructors[i]);

        for (int i = 0; i < node.Methods.Count; i++)
            node.Methods[i] = VisitMethodDeclaration(node.Methods[i]);

        for (int i = 0; i < node.NestedTypes.Count; i++)
            node.NestedTypes[i] = VisitTypeDeclaration(node.NestedTypes[i]);

        return node;
    }

    public virtual JavaInterfaceDeclaration VisitInterfaceDeclaration(JavaInterfaceDeclaration node)
    {
        for (int i = 0; i < node.Fields.Count; i++)
            node.Fields[i] = VisitFieldDeclaration(node.Fields[i]);

        for (int i = 0; i < node.Methods.Count; i++)
            node.Methods[i] = VisitMethodDeclaration(node.Methods[i]);

        for (int i = 0; i < node.NestedTypes.Count; i++)
            node.NestedTypes[i] = VisitTypeDeclaration(node.NestedTypes[i]);

        return node;
    }

    public virtual JavaEnumDeclaration VisitEnumDeclaration(JavaEnumDeclaration node)
    {
        for (int i = 0; i < node.Fields.Count; i++)
            node.Fields[i] = VisitFieldDeclaration(node.Fields[i]);

        for (int i = 0; i < node.Constructors.Count; i++)
            node.Constructors[i] = VisitConstructorDeclaration(node.Constructors[i]);

        for (int i = 0; i < node.Methods.Count; i++)
            node.Methods[i] = VisitMethodDeclaration(node.Methods[i]);

        return node;
    }

    // ─── 成员声明 ─────────────────────────────────────────

    public virtual JavaFieldDeclaration VisitFieldDeclaration(JavaFieldDeclaration node) => node;

    public virtual JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        if (node.StructuredBody != null)
        {
            node.StructuredBody = VisitMethodBody(node.StructuredBody);
        }
        return node;
    }

    public virtual JavaConstructorDeclaration VisitConstructorDeclaration(JavaConstructorDeclaration node)
    {
        if (node.StructuredBody != null)
        {
            node.StructuredBody = VisitMethodBody(node.StructuredBody);
        }
        return node;
    }

    // ─── 方法体 ────────────────────────────────────────────

    public virtual JavaMethodBody VisitMethodBody(JavaMethodBody node)
    {
        for (int i = 0; i < node.Statements.Count; i++)
        {
            node.Statements[i] = VisitStatement(node.Statements[i]);
        }
        return node;
    }

    // ─── 语句 ──────────────────────────────────────────────

    public virtual JavaStatement VisitStatement(JavaStatement node)
    {
        return node switch
        {
            JavaBlockStatement block => VisitBlockStatement(block),
            JavaVariableDeclarationStatement varDecl => VisitVariableDeclarationStatement(varDecl),
            JavaExpressionStatement exprStmt => VisitExpressionStatement(exprStmt),
            JavaReturnStatement ret => VisitReturnStatement(ret),
            JavaIfStatement ifStmt => VisitIfStatement(ifStmt),
            JavaForEachStatement forEach => VisitForEachStatement(forEach),
            JavaForStatement forStmt => VisitForStatement(forStmt),
            JavaWhileStatement whileStmt => VisitWhileStatement(whileStmt),
            JavaDoWhileStatement doWhile => VisitDoWhileStatement(doWhile),
            JavaTryCatchStatement tryCatch => VisitTryCatchStatement(tryCatch),
            JavaThrowStatement throwStmt => VisitThrowStatement(throwStmt),
            JavaSwitchStatement switchStmt => VisitSwitchStatement(switchStmt),
            JavaBreakStatement breakStmt => VisitBreakStatement(breakStmt),
            JavaContinueStatement contStmt => VisitContinueStatement(contStmt),
            JavaRawStatement raw => VisitRawStatement(raw),
            _ => node,
        };
    }

    public virtual JavaBlockStatement VisitBlockStatement(JavaBlockStatement node)
    {
        for (int i = 0; i < node.Statements.Count; i++)
        {
            node.Statements[i] = VisitStatement(node.Statements[i]);
        }
        return node;
    }

    public virtual JavaVariableDeclarationStatement VisitVariableDeclarationStatement(JavaVariableDeclarationStatement node)
    {
        if (node.Initializer != null)
        {
            node.Initializer = VisitExpression(node.Initializer);
        }
        return node;
    }

    public virtual JavaExpressionStatement VisitExpressionStatement(JavaExpressionStatement node)
    {
        node.Expression = VisitExpression(node.Expression);
        return node;
    }

    public virtual JavaReturnStatement VisitReturnStatement(JavaReturnStatement node)
    {
        if (node.Expression != null)
        {
            node.Expression = VisitExpression(node.Expression);
        }
        return node;
    }

    public virtual JavaIfStatement VisitIfStatement(JavaIfStatement node)
    {
        node.Condition = VisitExpression(node.Condition);
        node.ThenBody = VisitStatement(node.ThenBody);
        if (node.ElseBody != null)
        {
            node.ElseBody = VisitStatement(node.ElseBody);
        }
        return node;
    }

    public virtual JavaForEachStatement VisitForEachStatement(JavaForEachStatement node)
    {
        node.Collection = VisitExpression(node.Collection);
        node.Body = VisitStatement(node.Body);
        return node;
    }

    public virtual JavaForStatement VisitForStatement(JavaForStatement node)
    {
        if (node.Condition != null)
        {
            node.Condition = VisitExpression(node.Condition);
        }
        node.Body = VisitStatement(node.Body);
        return node;
    }

    public virtual JavaWhileStatement VisitWhileStatement(JavaWhileStatement node)
    {
        node.Condition = VisitExpression(node.Condition);
        node.Body = VisitStatement(node.Body);
        return node;
    }

    public virtual JavaDoWhileStatement VisitDoWhileStatement(JavaDoWhileStatement node)
    {
        node.Condition = VisitExpression(node.Condition);
        node.Body = VisitStatement(node.Body);
        return node;
    }

    public virtual JavaTryCatchStatement VisitTryCatchStatement(JavaTryCatchStatement node)
    {
        node.TryBody = VisitBlockStatement(node.TryBody);
        for (int i = 0; i < node.CatchClauses.Count; i++)
        {
            node.CatchClauses[i].Body = VisitBlockStatement(node.CatchClauses[i].Body);
        }
        if (node.FinallyBody != null)
        {
            node.FinallyBody = VisitBlockStatement(node.FinallyBody);
        }
        return node;
    }

    public virtual JavaThrowStatement VisitThrowStatement(JavaThrowStatement node)
    {
        node.Expression = VisitExpression(node.Expression);
        return node;
    }

    public virtual JavaSwitchStatement VisitSwitchStatement(JavaSwitchStatement node)
    {
        node.Expression = VisitExpression(node.Expression);
        for (int i = 0; i < node.Sections.Count; i++)
        {
            for (int j = 0; j < node.Sections[i].Statements.Count; j++)
            {
                node.Sections[i].Statements[j] = VisitStatement(node.Sections[i].Statements[j]);
            }
        }
        return node;
    }

    public virtual JavaBreakStatement VisitBreakStatement(JavaBreakStatement node) => node;
    public virtual JavaContinueStatement VisitContinueStatement(JavaContinueStatement node) => node;
    public virtual JavaRawStatement VisitRawStatement(JavaRawStatement node) => node;

    // ─── 表达式 ────────────────────────────────────────────

    public virtual JavaExpression VisitExpression(JavaExpression node)
    {
        return node switch
        {
            JavaMethodCallExpression call => VisitMethodCallExpression(call),
            JavaMemberAccessExpression access => VisitMemberAccessExpression(access),
            JavaIdentifierExpression id => VisitIdentifierExpression(id),
            JavaLiteralExpression lit => VisitLiteralExpression(lit),
            JavaBinaryExpression binary => VisitBinaryExpression(binary),
            JavaUnaryExpression unary => VisitUnaryExpression(unary),
            JavaConditionalExpression cond => VisitConditionalExpression(cond),
            JavaCastExpression cast => VisitCastExpression(cast),
            JavaNewExpression newExpr => VisitNewExpression(newExpr),
            JavaLambdaExpression lambda => VisitLambdaExpression(lambda),
            JavaArrayAccessExpression arr => VisitArrayAccessExpression(arr),
            JavaAssignmentExpression assign => VisitAssignmentExpression(assign),
            JavaParenthesizedExpression paren => VisitParenthesizedExpression(paren),
            JavaInstanceOfExpression inst => VisitInstanceOfExpression(inst),
            JavaThisExpression thisExpr => VisitThisExpression(thisExpr),
            JavaRawExpression raw => VisitRawExpression(raw),
            _ => node,
        };
    }

    public virtual JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        if (node.Target != null)
        {
            node.Target = VisitExpression(node.Target);
        }
        for (int i = 0; i < node.Arguments.Count; i++)
        {
            node.Arguments[i] = VisitExpression(node.Arguments[i]);
        }
        return node;
    }

    public virtual JavaMemberAccessExpression VisitMemberAccessExpression(JavaMemberAccessExpression node)
    {
        node.Target = VisitExpression(node.Target);
        return node;
    }

    public virtual JavaIdentifierExpression VisitIdentifierExpression(JavaIdentifierExpression node) => node;
    public virtual JavaLiteralExpression VisitLiteralExpression(JavaLiteralExpression node) => node;

    public virtual JavaBinaryExpression VisitBinaryExpression(JavaBinaryExpression node)
    {
        node.Left = VisitExpression(node.Left);
        node.Right = VisitExpression(node.Right);
        return node;
    }

    public virtual JavaUnaryExpression VisitUnaryExpression(JavaUnaryExpression node)
    {
        node.Operand = VisitExpression(node.Operand);
        return node;
    }

    public virtual JavaConditionalExpression VisitConditionalExpression(JavaConditionalExpression node)
    {
        node.Condition = VisitExpression(node.Condition);
        node.WhenTrue = VisitExpression(node.WhenTrue);
        node.WhenFalse = VisitExpression(node.WhenFalse);
        return node;
    }

    public virtual JavaCastExpression VisitCastExpression(JavaCastExpression node)
    {
        node.Expression = VisitExpression(node.Expression);
        return node;
    }

    public virtual JavaNewExpression VisitNewExpression(JavaNewExpression node)
    {
        for (int i = 0; i < node.Arguments.Count; i++)
        {
            node.Arguments[i] = VisitExpression(node.Arguments[i]);
        }
        return node;
    }

    public virtual JavaLambdaExpression VisitLambdaExpression(JavaLambdaExpression node)
    {
        if (node.ExpressionBody != null)
        {
            node.ExpressionBody = VisitExpression(node.ExpressionBody);
        }
        if (node.BlockBody != null)
        {
            node.BlockBody = VisitBlockStatement(node.BlockBody);
        }
        return node;
    }

    public virtual JavaArrayAccessExpression VisitArrayAccessExpression(JavaArrayAccessExpression node)
    {
        node.Target = VisitExpression(node.Target);
        node.Index = VisitExpression(node.Index);
        return node;
    }

    public virtual JavaAssignmentExpression VisitAssignmentExpression(JavaAssignmentExpression node)
    {
        node.Target = VisitExpression(node.Target);
        node.Value = VisitExpression(node.Value);
        return node;
    }

    public virtual JavaParenthesizedExpression VisitParenthesizedExpression(JavaParenthesizedExpression node)
    {
        node.InnerExpression = VisitExpression(node.InnerExpression);
        return node;
    }

    public virtual JavaInstanceOfExpression VisitInstanceOfExpression(JavaInstanceOfExpression node)
    {
        node.Expression = VisitExpression(node.Expression);
        return node;
    }

    public virtual JavaThisExpression VisitThisExpression(JavaThisExpression node) => node;
    public virtual JavaRawExpression VisitRawExpression(JavaRawExpression node) => node;
}
