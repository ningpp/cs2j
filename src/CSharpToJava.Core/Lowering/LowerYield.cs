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
        foreach (var type in unit.TypeDeclarations) LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        for (int i = type.Methods.Count - 1; i >= 0; i--)
        {
            var method = type.Methods[i];
            if (method.Body != null && HasYield(method.Body))
            {
                TransformYieldMethod(type, method, i);
            }
        }
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private static bool HasYield(IrBlockStatement block)
    {
        foreach (var stmt in block.Statements)
        {
            if (stmt is IrCSharpYieldReturnStatement or IrCSharpYieldBreakStatement) return true;
            if (stmt is IrBlockStatement b && HasYield(b)) return true;
            if (stmt is IrIfStatement ifs)
            {
                if (HasYieldInStatement(ifs.ThenBody)) return true;
                if (ifs.ElseBody != null && HasYieldInStatement(ifs.ElseBody)) return true;
            }
        }
        return false;
    }

    private static bool HasYieldInStatement(IrStatement stmt) =>
        stmt is IrCSharpYieldReturnStatement or IrCSharpYieldBreakStatement ||
        (stmt is IrBlockStatement b && HasYield(b));

    private void TransformYieldMethod(IrTypeDeclaration type, IrMethodDeclaration method, int index)
    {
        _iteratorCounter++;
        var elementType = ExtractElementType(method.ReturnType);

        var iteratorClass = new IrClassDeclaration
        {
            Name = method.Name + "_Iterator" + _iteratorCounter,
            Modifiers = IrModifiers.Private,
            Fields =
            {
                new IrFieldDeclaration { Type = "int", Name = "_state", Initializer = "0" },
                new IrFieldDeclaration { Type = elementType, Name = "_current", Initializer = "null" },
            },
        };

        // Number yield points and build switch cases
        var yieldStates = new List<(int state, IrStatement stmt)>();
        int stateCounter = 0;
        NumberYieldStatements(method.Body!, ref stateCounter, yieldStates);

        // Build next() switch body
        var switchStmt = new IrSwitchStatement
        {
            Expression = new IrIdentifierExpression { Name = "_state" },
        };
        for (int s = 0; s <= stateCounter; s++)
        {
            switchStmt.Sections.Add(new IrSwitchSection { Labels = { "case " + s + ":" }, Statements = {} });
        }
        switchStmt.Sections.Add(new IrSwitchSection { Labels = { "default:" }, Statements =
        {
            new IrReturnStatement { Expression = new IrLiteralExpression { Value = "null" } }
        }});

        // Replace yields with state-based returns, fill switch cases
        ReplaceYieldInBlock(method.Body!, switchStmt);

        iteratorClass.Methods.Add(new IrMethodDeclaration
        {
            Name = "next", ReturnType = elementType,
            Modifiers = IrModifiers.Public,
            Body = new IrBlockStatement { Statements = { switchStmt } },
        });

        iteratorClass.Methods.Add(new IrMethodDeclaration
        {
            Name = "hasNext", ReturnType = "boolean",
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

        // Replace original method body to return iterator
        method.Body = new IrBlockStatement
        {
            Statements =
            {
                new IrReturnStatement { Expression = new IrNewExpression { TypeName = iteratorClass.Name } }
            }
        };
    }

    private static void NumberYieldStatements(IrStatement stmt, ref int counter, List<(int, IrStatement)> states)
    {
        if (stmt is IrCSharpYieldReturnStatement or IrCSharpYieldBreakStatement)
        {
            states.Add((counter, stmt));
            counter++;
        }
        else if (stmt is IrBlockStatement b)
        {
            foreach (var s in b.Statements) NumberYieldStatements(s, ref counter, states);
        }
        else if (stmt is IrIfStatement ifs)
        {
            NumberYieldStatements(ifs.ThenBody, ref counter, states);
            if (ifs.ElseBody != null) NumberYieldStatements(ifs.ElseBody, ref counter, states);
        }
    }

    private static void ReplaceYieldInBlock(IrBlockStatement block, IrSwitchStatement switchStmt)
    {
        var newStmts = new List<IrStatement>();
        int stateIdx = 0;
        var currentCase = new List<IrStatement>();

        foreach (var stmt in block.Statements)
        {
            if (stmt is IrCSharpYieldReturnStatement yr)
            {
                // Save state for next yield
                currentCase.Add(new IrExpressionStatement
                {
                    Expression = new IrAssignmentExpression
                    {
                        Target = new IrIdentifierExpression { Name = "_state" },
                        Value = new IrLiteralExpression { Value = (stateIdx + 1).ToString() },
                    }
                });
                currentCase.Add(new IrExpressionStatement
                {
                    Expression = new IrAssignmentExpression
                    {
                        Target = new IrIdentifierExpression { Name = "_current" },
                        Value = yr.Expression ?? new IrLiteralExpression { Value = "null" },
                    }
                });
                currentCase.Add(new IrReturnStatement { Expression = new IrIdentifierExpression { Name = "_current" } });

                // Add these statements to the switch case and reset
                if (switchStmt.Sections.Count > stateIdx)
                    switchStmt.Sections[stateIdx].Statements.AddRange(currentCase);
                currentCase.Clear();
                stateIdx++;
            }
            else if (stmt is IrCSharpYieldBreakStatement)
            {
                currentCase.Add(new IrExpressionStatement
                {
                    Expression = new IrAssignmentExpression
                    {
                        Target = new IrIdentifierExpression { Name = "_state" },
                        Value = new IrLiteralExpression { Value = "-1" },
                    }
                });
                currentCase.Add(new IrReturnStatement { Expression = new IrLiteralExpression { Value = "null" } });

                if (switchStmt.Sections.Count > stateIdx)
                    switchStmt.Sections[stateIdx].Statements.AddRange(currentCase);
                currentCase.Clear();
                stateIdx++;
            }
            else
            {
                currentCase.Add(stmt);
            }
        }
        // Add remaining non-yield code to first case
        if (currentCase.Count > 0 && switchStmt.Sections.Count > 0)
            switchStmt.Sections[0].Statements.InsertRange(0, currentCase);

        block.Statements.Clear();
    }

    private static string ExtractElementType(string returnType)
    {
        var start = returnType.IndexOf('<');
        var end = returnType.LastIndexOf('>');
        if (start >= 0 && end > start)
            return returnType.Substring(start + 1, end - start - 1);
        return "Object";
    }
}
