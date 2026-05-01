using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerYield : ILoweringPass
{
    public string Name => "LowerYield";
    private int _iteratorCounter;

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        _iteratorCounter = 0;
        foreach (var type in unit.TypeDeclarations) LowerType(type, context);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type, ConversionContext context)
    {
        for (int i = type.Methods.Count - 1; i >= 0; i--)
        {
            var method = type.Methods[i];
            if (method.Body != null && HasYield(method.Body))
            {
                TransformYieldMethod(type, method, i, context);
            }
        }
        if (type is IrClassDeclaration cls)
            foreach (var ctor in cls.Constructors)
                if (ctor.Body != null) ReplaceYieldInBlock(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested, context);
    }

    private static bool HasYield(IrBlockStatement block)
    {
        foreach (var stmt in block.Statements)
        {
            if (stmt is IrCSharpYieldReturnStatement or IrCSharpYieldBreakStatement)
                return true;
            if (stmt is IrBlockStatement b && HasYield(b))
                return true;
            if (stmt is IrIfStatement ifs)
            {
                if (HasYieldInStatement(ifs.ThenBody) || (ifs.ElseBody != null && HasYieldInStatement(ifs.ElseBody)))
                    return true;
            }
        }
        return false;
    }

    private static bool HasYieldInStatement(IrStatement stmt) =>
        stmt is IrCSharpYieldReturnStatement or IrCSharpYieldBreakStatement ||
        (stmt is IrBlockStatement b && HasYield(b));

    private void TransformYieldMethod(IrTypeDeclaration type, IrMethodDeclaration method, int index, ConversionContext context)
    {
        _iteratorCounter++;
        var elementType = ExtractElementType(method.ReturnType);

        // Create inner iterator class
        var iteratorClass = new IrClassDeclaration
        {
            Name = method.Name + "_Iterator" + _iteratorCounter,
            Modifiers = IrModifiers.Private,
        };
        iteratorClass.Fields.Add(new IrFieldDeclaration { Type = "int", Name = "_state", Initializer = "0" });
        iteratorClass.Fields.Add(new IrFieldDeclaration { Type = elementType, Name = "_current", Initializer = "null" });

        // Build next() method body
        var switchStmt = new IrSwitchStatement
        {
            Expression = new IrIdentifierExpression { Name = "_state" },
        };
        switchStmt.Sections.Add(new IrSwitchSection { Labels = { "case 0:" } });
        var nextBody = new IrBlockStatement();
        nextBody.Statements.Add(switchStmt);

        iteratorClass.Methods.Add(new IrMethodDeclaration
        {
            Name = "next",
            ReturnType = elementType,
            Modifiers = IrModifiers.Public,
            Body = nextBody,
        });

        iteratorClass.Methods.Add(new IrMethodDeclaration
        {
            Name = "hasNext",
            ReturnType = "boolean",
            Modifiers = IrModifiers.Public,
            Body = new IrBlockStatement
            {
                Statements =
                {
                    new IrReturnStatement
                    {
                        Expression = new IrBinaryExpression
                        {
                            Left = new IrIdentifierExpression { Name = "_state" },
                            Operator = IrBinaryOp.NotEquals,
                            Right = new IrLiteralExpression { Value = "-1" },
                        }
                    }
                }
            },
        });

        type.NestedTypes.Add(iteratorClass);

        // Transform the original method body
        ReplaceYieldInBlock(method.Body!);

        // Replace method to return iterator instance
        method.Body = new IrBlockStatement
        {
            Statements =
            {
                new IrReturnStatement
                {
                    Expression = new IrNewExpression { TypeName = iteratorClass.Name },
                }
            }
        };
    }

    private void ReplaceYieldInBlock(IrBlockStatement block)
    {
        var newStmts = new List<IrStatement>();
        foreach (var stmt in block.Statements)
        {
            if (stmt is IrCSharpYieldReturnStatement yr)
            {
                newStmts.Add(new IrExpressionStatement
                {
                    Expression = new IrAssignmentExpression
                    {
                        Target = new IrIdentifierExpression { Name = "_current" },
                        Value = yr.Expression ?? new IrLiteralExpression { Value = "null" },
                    }
                });
                newStmts.Add(new IrReturnStatement
                {
                    Expression = new IrIdentifierExpression { Name = "_current" },
                });
            }
            else if (stmt is IrCSharpYieldBreakStatement)
            {
                newStmts.Add(new IrExpressionStatement
                {
                    Expression = new IrAssignmentExpression
                    {
                        Target = new IrIdentifierExpression { Name = "_state" },
                        Value = new IrLiteralExpression { Value = "-1" },
                    }
                });
                newStmts.Add(new IrReturnStatement { Expression = new IrLiteralExpression { Value = "null" } });
            }
            else
            {
                if (stmt is IrBlockStatement b) ReplaceYieldInBlock(b);
                newStmts.Add(stmt);
            }
        }
        block.Statements.Clear();
        block.Statements.AddRange(newStmts);
    }

    private static string ExtractElementType(string returnType)
    {
        // Iterator<T> -> T, IEnumerable<T> -> T
        var start = returnType.IndexOf('<');
        var end = returnType.LastIndexOf('>');
        if (start >= 0 && end > start)
            return returnType.Substring(start + 1, end - start - 1);
        return "Object";
    }
}
