// src/CSharpToJava.Core/Java2/CodeGen/ImportCollector.cs
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Java2.CodeGen;

public class ImportCollector
{
    private readonly HashSet<string> _imports = new();

    public void Collect(IrCompilationUnit unit)
    {
        foreach (var type in unit.TypeDeclarations)
            CollectFromType(type);
    }

    private void CollectFromType(IrTypeDeclaration type)
    {
        foreach (var field in type.Fields)
            ExtractTypeName(field.Type);
        foreach (var method in type.Methods)
        {
            ExtractTypeName(method.ReturnType);
            foreach (var param in method.Parameters)
                ExtractTypeName(param.Type);
            if (method.Body != null) CollectFromBlock(method.Body);
        }
        if (type is IrClassDeclaration cls)
        {
            foreach (var ctor in cls.Constructors)
            {
                foreach (var param in ctor.Parameters)
                    ExtractTypeName(param.Type);
                if (ctor.Body != null) CollectFromBlock(ctor.Body);
            }
        }
        foreach (var nested in type.NestedTypes)
            CollectFromType(nested);
    }

    private void CollectFromBlock(IrBlockStatement block)
    {
        foreach (var stmt in block.Statements)
            CollectFromStatement(stmt);
    }

    private void CollectFromStatement(IrStatement stmt)
    {
        switch (stmt)
        {
            case IrBlockStatement b:
                foreach (var s in b.Statements) CollectFromStatement(s);
                break;
            case IrVariableDeclarationStatement v:
                ExtractTypeName(v.Type);
                if (v.Initializer != null) CollectFromExpression(v.Initializer);
                break;
            case IrExpressionStatement e:
                CollectFromExpression(e.Expression);
                break;
            case IrReturnStatement r:
                if (r.Expression != null) CollectFromExpression(r.Expression);
                break;
            case IrIfStatement i:
                CollectFromExpression(i.Condition);
                CollectFromStatement(i.ThenBody);
                if (i.ElseBody != null) CollectFromStatement(i.ElseBody);
                break;
            case IrForEachStatement fe:
                ExtractTypeName(fe.VariableType);
                CollectFromExpression(fe.Collection);
                CollectFromStatement(fe.Body);
                break;
            case IrForStatement f:
                if (f.Condition != null) CollectFromExpression(f.Condition);
                CollectFromStatement(f.Body);
                break;
            case IrWhileStatement w:
                CollectFromExpression(w.Condition);
                CollectFromStatement(w.Body);
                break;
            case IrDoWhileStatement dw:
                CollectFromExpression(dw.Condition);
                CollectFromStatement(dw.Body);
                break;
            case IrTryCatchStatement tc:
                CollectFromBlock(tc.TryBody);
                foreach (var cc in tc.CatchClauses)
                    CollectFromBlock(cc.Body);
                if (tc.FinallyBody != null) CollectFromBlock(tc.FinallyBody);
                break;
            case IrThrowStatement th:
                CollectFromExpression(th.Expression);
                break;
            case IrSwitchStatement sw:
                CollectFromExpression(sw.Expression);
                foreach (var sec in sw.Sections)
                    foreach (var s in sec.Statements) CollectFromStatement(s);
                break;
            default: break;
        }
    }

    private void CollectFromExpression(IrExpression expr)
    {
        if (expr.JavaType != null) ExtractTypeName(expr.JavaType);
        switch (expr)
        {
            case IrNewExpression n:
                ExtractTypeName(n.TypeName);
                foreach (var a in n.Arguments) CollectFromExpression(a);
                break;
            case IrCastExpression c:
                ExtractTypeName(c.TargetType);
                CollectFromExpression(c.Expression);
                break;
            case IrInstanceOfExpression i:
                ExtractTypeName(i.TypeName);
                CollectFromExpression(i.Expression);
                break;
            case IrMemberAccessExpression m:
                CollectFromExpression(m.Target);
                break;
            case IrInvocationExpression inv:
                if (inv.Target != null) CollectFromExpression(inv.Target);
                foreach (var a in inv.Arguments) CollectFromExpression(a);
                break;
            case IrBinaryExpression bin:
                CollectFromExpression(bin.Left);
                CollectFromExpression(bin.Right);
                break;
            case IrUnaryExpression un:
                CollectFromExpression(un.Operand);
                break;
            case IrConditionalExpression cond:
                CollectFromExpression(cond.Condition);
                CollectFromExpression(cond.WhenTrue);
                CollectFromExpression(cond.WhenFalse);
                break;
            case IrAssignmentExpression asgn:
                CollectFromExpression(asgn.Target);
                CollectFromExpression(asgn.Value);
                break;
            case IrArrayAccessExpression arr:
                CollectFromExpression(arr.Target);
                CollectFromExpression(arr.Index);
                break;
            case IrLambdaExpression lam:
                if (lam.ExpressionBody != null) CollectFromExpression(lam.ExpressionBody);
                if (lam.BlockBody != null) CollectFromBlock(lam.BlockBody);
                break;
            default: break;
        }
    }

    private void ExtractTypeName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName) || IsPrimitive(typeName) || typeName == "void" || typeName == "var")
            return;
        var baseType = typeName;
        var ai = baseType.IndexOf('<');
        if (ai > 0) baseType = baseType[..ai];
        if (!baseType.Contains('.') || baseType.StartsWith("java.lang."))
            return;
        _imports.Add(baseType);
    }

    private static bool IsPrimitive(string t) => t is "int" or "long" or "short" or "byte"
        or "float" or "double" or "boolean" or "char" or "String" or "Object";

    public List<string> GetSortedImports() => _imports
        .OrderBy(i => i.StartsWith("java.") ? 0 : i.StartsWith("javax.") ? 1 : 2)
        .ThenBy(i => i)
        .ToList();
}
