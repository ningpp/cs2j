using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.HIR;

public class HIRStatementGenerator
{
    private ConversionContext _ctx = null!;
    private HIRExpressionGenerator? _exprGen;

    private HIRExpressionGenerator ExprGen => _exprGen ??= new HIRExpressionGenerator();

    public IrBlockStatement GenerateBlock(BlockSyntax node, ConversionContext context)
    {
        _ctx = context;
        var block = new IrBlockStatement();
        foreach (var stmt in node.Statements)
        {
            var irStmt = Generate(stmt);
            if (irStmt != null) block.Statements.Add(irStmt);
        }
        return block;
    }

    public IrStatement? Generate(StatementSyntax stmt, ConversionContext context)
    {
        _ctx = context;
        return Generate(stmt);
    }

    public IrStatement? Generate(StatementSyntax stmt)
    {
        return stmt switch
        {
            BlockSyntax block => GenerateBlock(block, _ctx),
            ExpressionStatementSyntax es => new IrExpressionStatement { Expression = ExprGen.Generate(es.Expression, _ctx) },
            LocalDeclarationStatementSyntax lds => GenerateLocalDecl(lds),
            ReturnStatementSyntax rs => new IrReturnStatement { Expression = rs.Expression != null ? ExprGen.Generate(rs.Expression, _ctx) : null },
            IfStatementSyntax ifs => GenerateIf(ifs),
            ForEachStatementSyntax fe => GenerateForEach(fe),
            ForStatementSyntax f => GenerateFor(f),
            WhileStatementSyntax ws => new IrWhileStatement { Condition = ExprGen.Generate(ws.Condition, _ctx), Body = Generate(ws.Statement)! },
            DoStatementSyntax dw => new IrDoWhileStatement { Condition = ExprGen.Generate(dw.Condition, _ctx), Body = Generate(dw.Statement)! },
            TryStatementSyntax ts => GenerateTryCatch(ts),
            ThrowStatementSyntax th => new IrThrowStatement { Expression = ExprGen.Generate(th.Expression, _ctx) },
            BreakStatementSyntax => new IrBreakStatement(),
            ContinueStatementSyntax => new IrContinueStatement(),
            UsingStatementSyntax us => new IrCSharpUsingStatement
            {
                Resource = us.Declaration != null ? new IrVariableDeclarationStatement
                {
                    Type = _ctx.MapTypeFromSyntax(us.Declaration.Type),
                    Name = us.Declaration.Variables[0].Identifier.Text,
                } : null,
                ResourceExpression = us.Expression != null ? ExprGen.Generate(us.Expression, _ctx) : null,
                Body = Generate(us.Statement)!,
            },
            YieldStatementSyntax ys => GenerateYield(ys),
            _ => new IrExpressionStatement { Expression = new IrIdentifierExpression { Name = "/* ERROR: unsupported statement " + stmt.Kind() + " */" } },
        };
    }

    private IrVariableDeclarationStatement GenerateLocalDecl(LocalDeclarationStatementSyntax node)
    {
        var decl = node.Declaration;
        return new IrVariableDeclarationStatement
        {
            Type = _ctx.MapTypeFromSyntax(decl.Type),
            Name = decl.Variables[0].Identifier.Text,
            Initializer = decl.Variables[0].Initializer != null
                ? ExprGen.Generate(decl.Variables[0].Initializer!.Value, _ctx)
                : null,
        };
    }

    private IrIfStatement GenerateIf(IfStatementSyntax node) => new()
    {
        Condition = ExprGen.Generate(node.Condition, _ctx),
        ThenBody = Generate(node.Statement)!,
        ElseBody = node.Else != null ? Generate(node.Else.Statement) : null,
    };

    private IrForEachStatement GenerateForEach(ForEachStatementSyntax node) => new()
    {
        VariableType = _ctx.MapTypeFromSyntax(node.Type),
        VariableName = node.Identifier.Text,
        Collection = ExprGen.Generate(node.Expression, _ctx),
        Body = Generate(node.Statement)!,
    };

    private IrForStatement GenerateFor(ForStatementSyntax node)
    {
        var init = node.Declaration != null
            ? _ctx.MapTypeFromSyntax(node.Declaration.Type) + " " + string.Join(", ", node.Declaration.Variables.Select(v => v.ToString()))
            : string.Join(", ", node.Initializers.Select(i => i.ToString()));
        return new IrForStatement
        {
            Initializer = init,
            Condition = node.Condition != null ? ExprGen.Generate(node.Condition, _ctx) : null,
            Increment = string.Join(", ", node.Incrementors.Select(i => i.ToString())),
            Body = Generate(node.Statement)!,
        };
    }

    private IrTryCatchStatement GenerateTryCatch(TryStatementSyntax node)
    {
        var tc = new IrTryCatchStatement { TryBody = GenerateBlock(node.Block, _ctx) };
        foreach (var cc in node.Catches)
            tc.CatchClauses.Add(new IrCatchClause
            {
                ExceptionType = cc.Declaration != null ? _ctx.MapTypeFromSyntax(cc.Declaration.Type) : "Exception",
                VariableName = cc.Declaration?.Identifier.Text,
                Body = GenerateBlock(cc.Block, _ctx),
            });
        if (node.Finally != null)
            tc.FinallyBody = GenerateBlock(node.Finally.Block, _ctx);
        return tc;
    }

    private IrStatement GenerateYield(YieldStatementSyntax node)
    {
        if (node.ReturnOrBreakKeyword.Kind() == SyntaxKind.ReturnKeyword)
            return new IrCSharpYieldReturnStatement { Expression = node.Expression != null ? ExprGen.Generate(node.Expression, _ctx) : null };
        return new IrCSharpYieldBreakStatement();
    }
}
