// src/CSharpToJava.Core/Java2/CodeGen/MemberWriter.cs
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Java2.CodeGen;

public class MemberWriter
{
    private readonly StatementWriter _stmtWriter = new();

    public void WriteField(IrFieldDeclaration field, IndentedWriter w)
    {
        CommentWriter.WriteLeading(w, field.LeadingComment);
        var mod = ModifiersToString(field.Modifiers);
        if (mod.Length > 0) mod += " ";
        var init = field.Initializer != null ? " = " + field.Initializer : "";
        w.WriteLine(mod + field.Type + " " + field.Name + init + ";");
    }

    public void WriteMethod(IrMethodDeclaration method, IndentedWriter w)
    {
        CommentWriter.WriteLeading(w, method.LeadingComment);
        foreach (var ann in method.Annotations)
            w.WriteLine("@" + ann);
        var mod = ModifiersToString(method.Modifiers);
        if (mod.Length > 0) mod += " ";
        var tparams = method.TypeParameters.Count > 0
            ? "<" + string.Join(", ", method.TypeParameters.Select(tp =>
                tp.Name + (tp.Bounds.Count > 0 ? " extends " + string.Join(" & ", tp.Bounds) : ""))) + "> "
            : "";
        var paramStr = string.Join(", ", method.Parameters.Select(p =>
            (p.IsFinal ? "final " : "") + p.Type + " " + p.Name));
        var throws = method.ThrownExceptions.Count > 0
            ? " throws " + string.Join(", ", method.ThrownExceptions) : "";
        w.WriteLine(mod + method.ReturnType + " " + method.Name + tparams + "(" + paramStr + ")" + throws + " {");
        if (method.Body != null)
        {
            w.Indent();
            foreach (var stmt in method.Body.Statements)
                _stmtWriter.Write(stmt, w);
            w.Unindent();
        }
        w.WriteLine("}");
    }

    public void WriteConstructor(IrConstructorDeclaration ctor, IndentedWriter w)
    {
        CommentWriter.WriteLeading(w, ctor.LeadingComment);
        var mod = ModifiersToString(ctor.Modifiers);
        if (mod.Length > 0) mod += " ";
        var paramStr = string.Join(", ", ctor.Parameters.Select(p => p.Type + " " + p.Name));
        w.WriteLine(mod + ctor.TypeName + "(" + paramStr + ") {");
        if (ctor.Body != null)
        {
            w.Indent();
            foreach (var stmt in ctor.Body.Statements)
                _stmtWriter.Write(stmt, w);
            w.Unindent();
        }
        w.WriteLine("}");
    }

    private static string ModifiersToString(IrModifiers mods)
    {
        var parts = new List<string>();
        if ((mods & IrModifiers.Public) != 0) parts.Add("public");
        else if ((mods & IrModifiers.Protected) != 0) parts.Add("protected");
        else if ((mods & IrModifiers.Private) != 0) parts.Add("private");
        if ((mods & IrModifiers.Static) != 0) parts.Add("static");
        if ((mods & IrModifiers.Final) != 0) parts.Add("final");
        if ((mods & IrModifiers.Abstract) != 0) parts.Add("abstract");
        return string.Join(" ", parts);
    }
}
