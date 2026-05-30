using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;
using CSharpToJava.Core.Transformers;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Lowering;

public class LowerStruct : ILoweringPass
{
    public string Name => "LowerStruct";

    private ConversionContext _context = null!;
    private int _tempCounter;

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        _context = context;
        _tempCounter = 0;
        foreach (var type in unit.TypeDeclarations) LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        foreach (var method in type.Methods) if (method.Body != null) LowerBlock(method.Body);
        if (type is IrClassDeclaration cls) foreach (var ctor in cls.Constructors) if (ctor.Body != null) LowerBlock(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private void LowerBlock(IrBlockStatement block)
    {
        var newStmts = new List<IrStatement>();
        foreach (var stmt in block.Statements)
            newStmts.AddRange(LowerStatement(stmt));
        block.Statements.Clear();
        block.Statements.AddRange(newStmts);
    }

    private List<IrStatement> LowerStatement(IrStatement stmt)
    {
        switch (stmt)
        {
            case IrBlockStatement b:
                LowerBlock(b);
                return new List<IrStatement> { b };
            case IrExpressionStatement es:
                return LowerExpressionStatement(es);
            case IrVariableDeclarationStatement vd:
                if (vd.Initializer != null)
                {
                    vd.Initializer = LowerExpression(vd.Initializer);
                    if (IsStructVariableDecl(vd) && !IsFreshExpression(vd.Initializer))
                        vd.Initializer = MakeClone(vd.Initializer);
                }
                return new List<IrStatement> { vd };
            case IrReturnStatement rs:
                if (rs.Expression != null) rs.Expression = LowerExpression(rs.Expression);
                return new List<IrStatement> { rs };
            case IrIfStatement ifs:
                ifs.Condition = LowerExpression(ifs.Condition);
                ifs.ThenBody = WrapExpanded(LowerStatement(ifs.ThenBody));
                if (ifs.ElseBody != null) ifs.ElseBody = WrapExpanded(LowerStatement(ifs.ElseBody));
                return new List<IrStatement> { ifs };
            case IrForEachStatement fe:
                fe.Collection = LowerExpression(fe.Collection);
                fe.Body = WrapExpanded(LowerStatement(fe.Body));
                return new List<IrStatement> { fe };
            case IrForStatement f:
                if (f.Condition != null) f.Condition = LowerExpression(f.Condition);
                f.Body = WrapExpanded(LowerStatement(f.Body));
                return new List<IrStatement> { f };
            case IrWhileStatement w:
                w.Condition = LowerExpression(w.Condition);
                w.Body = WrapExpanded(LowerStatement(w.Body));
                return new List<IrStatement> { w };
            case IrDoWhileStatement dw:
                dw.Condition = LowerExpression(dw.Condition);
                dw.Body = WrapExpanded(LowerStatement(dw.Body));
                return new List<IrStatement> { dw };
            case IrTryCatchStatement tc:
                LowerBlock(tc.TryBody);
                foreach (var cc in tc.CatchClauses) LowerBlock(cc.Body);
                if (tc.FinallyBody != null) LowerBlock(tc.FinallyBody);
                return new List<IrStatement> { tc };
            case IrSwitchStatement sw:
                sw.Expression = LowerExpression(sw.Expression);
                foreach (var sec in sw.Sections)
                {
                    var newSecStmts = new List<IrStatement>();
                    foreach (var s in sec.Statements)
                        newSecStmts.AddRange(LowerStatement(s));
                    sec.Statements.Clear();
                    sec.Statements.AddRange(newSecStmts);
                }
                return new List<IrStatement> { sw };
            default:
                return new List<IrStatement> { stmt };
        }
    }

    private IrStatement WrapExpanded(List<IrStatement> stmts)
    {
        if (stmts.Count == 1) return stmts[0];
        var block = new IrBlockStatement();
        block.Statements.AddRange(stmts);
        return block;
    }

    private List<IrStatement> LowerExpressionStatement(IrExpressionStatement es)
    {
        if (es.Expression is IrAssignmentExpression asgn && asgn.Operator == IrAssignmentOp.Assign)
        {
            var (targets, source) = CollectChainedAssignments(asgn);
            if (targets.Count > 1 && IsStructTypeExpression(targets[0]))
                return ExpandChainedStructAssignment(targets, source);

            es.Expression = LowerExpression(asgn);
            if (IsStructTypeExpression(asgn.Target) && !IsFreshExpression(asgn.Value))
                asgn.Value = MakeClone(asgn.Value);
            return new List<IrStatement> { es };
        }

        es.Expression = LowerExpression(es.Expression);
        return new List<IrStatement> { es };
    }

    private (List<IrExpression> targets, IrExpression source) CollectChainedAssignments(IrAssignmentExpression asgn)
    {
        var targets = new List<IrExpression> { asgn.Target };
        var current = asgn.Value;
        while (current is IrAssignmentExpression inner && inner.Operator == IrAssignmentOp.Assign)
        {
            targets.Add(inner.Target);
            current = inner.Value;
        }
        return (targets, current);
    }

    private List<IrStatement> ExpandChainedStructAssignment(List<IrExpression> targets, IrExpression source)
    {
        var stmts = new List<IrStatement>();
        var loweredSource = LowerExpression(source);
        var javaType = GetStructJavaType(targets[0]) ?? "var";
        var tempName = $"_structCopy{++_tempCounter}";

        stmts.Add(new IrVariableDeclarationStatement
        {
            Type = javaType,
            Name = tempName,
            Initializer = loweredSource,
        });

        for (int i = targets.Count - 1; i >= 0; i--)
        {
            var loweredTarget = LowerExpression(targets[i]);
            stmts.Add(new IrExpressionStatement(
                new IrAssignmentExpression
                {
                    Target = loweredTarget,
                    Operator = IrAssignmentOp.Assign,
                    Value = MakeClone(new IrIdentifierExpression { Name = tempName, JavaType = javaType }),
                }));
        }

        return stmts;
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        switch (expr)
        {
            case IrCSharpStructCopyExpression copy:
                return MakeClone(LowerExpression(copy.Source));
            case IrAssignmentExpression asgn:
                asgn.Target = LowerExpression(asgn.Target);
                asgn.Value = LowerExpression(asgn.Value);
                return asgn;
            case IrBinaryExpression bin:
                bin.Left = LowerExpression(bin.Left);
                bin.Right = LowerExpression(bin.Right);
                return bin;
            case IrUnaryExpression un:
                un.Operand = LowerExpression(un.Operand);
                return un;
            case IrConditionalExpression cond:
                cond.Condition = LowerExpression(cond.Condition);
                cond.WhenTrue = LowerExpression(cond.WhenTrue);
                cond.WhenFalse = LowerExpression(cond.WhenFalse);
                return cond;
            case IrCastExpression cast:
                cast.Expression = LowerExpression(cast.Expression);
                return cast;
            case IrNewExpression n:
                for (int i = 0; i < n.Arguments.Count; i++)
                    n.Arguments[i] = LowerExpression(n.Arguments[i]);
                return n;
            case IrMemberAccessExpression mem:
                mem.Target = LowerExpression(mem.Target);
                return mem;
            case IrInvocationExpression inv:
                if (inv.Target != null) inv.Target = LowerExpression(inv.Target);
                for (int i = 0; i < inv.Arguments.Count; i++)
                    inv.Arguments[i] = LowerExpression(inv.Arguments[i]);
                return inv;
            case IrArrayAccessExpression arr:
                arr.Target = LowerExpression(arr.Target);
                arr.Index = LowerExpression(arr.Index);
                return arr;
            case IrLambdaExpression lam:
                if (lam.ExpressionBody != null) lam.ExpressionBody = LowerExpression(lam.ExpressionBody);
                if (lam.BlockBody != null) LowerBlock(lam.BlockBody);
                return lam;
            case IrInstanceOfExpression inst:
                inst.Expression = LowerExpression(inst.Expression);
                return inst;
            default:
                return expr;
        }
    }

    private bool IsStructTypeExpression(IrExpression expr)
    {
        var type = GetExpressionTypeSymbol(expr);
        return StructCloneHelper.IsUserDefinedStruct(type);
    }

    private ITypeSymbol? GetExpressionTypeSymbol(IrExpression expr)
    {
        if (expr.Symbol is IFieldSymbol f) return f.Type;
        if (expr.Symbol is ILocalSymbol l) return l.Type;
        if (expr.Symbol is IParameterSymbol p) return p.Type;
        if (expr.Symbol is IPropertySymbol pr) return pr.Type;
        if (expr is IrMemberAccessExpression mem) return GetExpressionTypeSymbol(mem.Target);
        if (expr is IrAssignmentExpression asgn) return GetExpressionTypeSymbol(asgn.Target);
        return null;
    }

    private bool IsFreshExpression(IrExpression expr)
    {
        return expr is IrNewExpression
            or IrInvocationExpression
            or IrBinaryExpression
            or IrUnaryExpression
            or IrLiteralExpression;
    }

    private IrInvocationExpression MakeClone(IrExpression source)
    {
        return new IrInvocationExpression
        {
            Target = source,
            MethodName = "clone",
            JavaType = source.JavaType,
        };
    }

    private string? GetStructJavaType(IrExpression expr)
    {
        if (expr.JavaType != null) return expr.JavaType;
        var type = GetExpressionTypeSymbol(expr);
        if (type != null) return _context.MapType(type);
        return null;
    }

    private bool IsStructVariableDecl(IrVariableDeclarationStatement vd)
    {
        if (vd.Initializer == null) return false;
        var type = GetExpressionTypeSymbol(vd.Initializer);
        if (type != null) return StructCloneHelper.IsUserDefinedStruct(type);
        return false;
    }
}
