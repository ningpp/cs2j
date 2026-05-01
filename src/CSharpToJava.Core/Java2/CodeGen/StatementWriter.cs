// src/CSharpToJava.Core/Java2/CodeGen/StatementWriter.cs
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Java2.CodeGen;

public class StatementWriter
{
    private readonly ExpressionWriter _exprWriter = new();

    public void Write(IrStatement stmt, IndentedWriter w)
    {
        CommentWriter.WriteLeading(w, stmt.LeadingComment);
        switch (stmt)
        {
            case IrBlockStatement block:
                WriteBlock(block, w); break;
            case IrExpressionStatement es:
                w.WriteLine(_exprWriter.Write(es.Expression) + ";"); break;
            case IrVariableDeclarationStatement vd:
                WriteVariableDecl(vd, w); break;
            case IrReturnStatement ret:
                if (ret.Expression != null)
                    w.WriteLine("return " + _exprWriter.Write(ret.Expression) + ";");
                else
                    w.WriteLine("return;");
                break;
            case IrIfStatement ifs:
                WriteIf(ifs, w); break;
            case IrForEachStatement fe:
                WriteForEach(fe, w); break;
            case IrForStatement f:
                WriteFor(f, w); break;
            case IrWhileStatement ws:
                w.Write("while (" + _exprWriter.Write(ws.Condition) + ") ");
                WriteBodyOrInline(ws.Body, w); break;
            case IrDoWhileStatement dw:
                w.Write("do ");
                WriteBodyOrInline(dw.Body, w);
                w.WriteLine(" while (" + _exprWriter.Write(dw.Condition) + ");"); break;
            case IrTryCatchStatement tc:
                WriteTryCatch(tc, w); break;
            case IrThrowStatement th:
                w.WriteLine("throw " + _exprWriter.Write(th.Expression) + ";"); break;
            case IrSwitchStatement sw:
                WriteSwitch(sw, w); break;
            case IrBreakStatement br:
                w.WriteLine("break" + (br.Label != null ? " " + br.Label : "") + ";"); break;
            case IrContinueStatement ct:
                w.WriteLine("continue" + (ct.Label != null ? " " + ct.Label : "") + ";"); break;
        }
    }

    private void WriteBlock(IrBlockStatement block, IndentedWriter w)
    {
        w.WriteLine("{");
        w.Indent();
        foreach (var s in block.Statements) Write(s, w);
        w.Unindent();
        w.WriteLine("}");
    }

    private void WriteVariableDecl(IrVariableDeclarationStatement vd, IndentedWriter w)
    {
        var parts = (vd.IsFinal ? "final " : "") + vd.Type + " " + vd.Name;
        if (vd.Initializer != null) parts += " = " + _exprWriter.Write(vd.Initializer);
        w.WriteLine(parts + ";");
    }

    private void WriteIf(IrIfStatement ifs, IndentedWriter w)
    {
        w.Write("if (" + _exprWriter.Write(ifs.Condition) + ") ");
        WriteBodyOrInline(ifs.ThenBody, w);
        if (ifs.ElseBody != null)
        {
            w.Write(" else ");
            if (ifs.ElseBody is IrIfStatement)
            {
                // else-if chain: write inline without extra newline
                var inner = new IndentedWriter();
                Write(ifs.ElseBody, inner);
                w.Write(inner.ToString().Trim());
            }
            else
            {
                WriteBodyOrInline(ifs.ElseBody, w);
            }
        }
        w.WriteLine("");
    }

    private void WriteForEach(IrForEachStatement fe, IndentedWriter w)
    {
        w.Write("for (" + fe.VariableType + " " + fe.VariableName + " : " + _exprWriter.Write(fe.Collection) + ") ");
        WriteBodyOrInline(fe.Body, w);
        w.WriteLine("");
    }

    private void WriteFor(IrForStatement f, IndentedWriter w)
    {
        w.Write("for (" + (f.Initializer ?? "") + "; " + (f.Condition != null ? _exprWriter.Write(f.Condition) : "") + "; " + (f.Increment ?? "") + ") ");
        WriteBodyOrInline(f.Body, w);
        w.WriteLine("");
    }

    private void WriteTryCatch(IrTryCatchStatement tc, IndentedWriter w)
    {
        w.Write("try");
        if (tc.Resources.Count > 0)
            w.Write(" (" + string.Join("; ", tc.Resources) + ")");
        w.Write(" ");
        WriteBlock(tc.TryBody, w);
        foreach (var cc in tc.CatchClauses)
        {
            w.Write(" catch (" + cc.ExceptionType + (cc.VariableName != null ? " " + cc.VariableName : "") + ") ");
            WriteBlock(cc.Body, w);
        }
        if (tc.FinallyBody != null)
        {
            w.Write(" finally ");
            WriteBlock(tc.FinallyBody, w);
        }
        w.WriteLine("");
    }

    private void WriteSwitch(IrSwitchStatement sw, IndentedWriter w)
    {
        w.WriteLine("switch (" + _exprWriter.Write(sw.Expression) + ") {");
        w.Indent();
        foreach (var section in sw.Sections)
        {
            foreach (var label in section.Labels)
                w.WriteLine(label);
            w.Indent();
            foreach (var s in section.Statements) Write(s, w);
            w.Unindent();
        }
        w.Unindent();
        w.WriteLine("}");
    }

    private void WriteBodyOrInline(IrStatement body, IndentedWriter w)
    {
        if (body is IrBlockStatement block)
            WriteBlock(block, w);
        else
        {
            w.WriteLine("{");
            w.Indent();
            Write(body, w);
            w.Unindent();
            w.Write("}");
        }
    }
}
