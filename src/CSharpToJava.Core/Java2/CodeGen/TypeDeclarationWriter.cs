// src/CSharpToJava.Core/Java2/CodeGen/TypeDeclarationWriter.cs
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Java2.CodeGen;

public class TypeDeclarationWriter
{
    private readonly MemberWriter _memberWriter = new();

    public void Write(IrTypeDeclaration type, IndentedWriter w)
    {
        switch (type)
        {
            case IrClassDeclaration cls: WriteClass(cls, w); break;
            case IrInterfaceDeclaration iface: WriteInterface(iface, w); break;
            case IrEnumDeclaration enm: WriteEnum(enm, w); break;
        }
    }

    private void WriteModifiers(IrModifiers mods, IndentedWriter w)
    {
        var m = new List<string>();
        if ((mods & IrModifiers.Public) != 0) m.Add("public");
        else if ((mods & IrModifiers.Protected) != 0) m.Add("protected");
        else if ((mods & IrModifiers.Private) != 0) m.Add("private");
        if ((mods & IrModifiers.Static) != 0) m.Add("static");
        if ((mods & IrModifiers.Final) != 0) m.Add("final");
        if ((mods & IrModifiers.Abstract) != 0) m.Add("abstract");
        if (m.Count > 0) w.Write(string.Join(" ", m) + " ");
    }

    private void WriteClass(IrClassDeclaration cls, IndentedWriter w)
    {
        CommentWriter.WriteLeading(w, cls.LeadingComment);
        foreach (var ann in cls.Annotations) w.WriteLine("@" + ann);
        WriteModifiers(cls.Modifiers, w);
        if (cls.IsValueClass)
            w.Write("value ");
        w.Write(cls.IsRecord ? "record " : "class ");
        w.Write(cls.Name);
        if (cls.IsRecord && cls.RecordComponents.Count > 0)
            w.Write("(" + string.Join(", ", cls.RecordComponents.Select(rc => rc.Type + " " + rc.Name)) + ")");
        if (cls.ExtendedType != null) w.Write(" extends " + cls.ExtendedType);
        if (cls.ImplementedTypes.Count > 0) w.Write(" implements " + string.Join(", ", cls.ImplementedTypes));
        w.WriteLine(" {");
        w.Indent();
        foreach (var field in cls.Fields) _memberWriter.WriteField(field, w);
        foreach (var ctor in cls.Constructors) _memberWriter.WriteConstructor(ctor, w);
        foreach (var method in cls.Methods) _memberWriter.WriteMethod(method, w);
        foreach (var nested in cls.NestedTypes) Write(nested, w);
        w.Unindent();
        w.WriteLine("}");
    }

    private void WriteInterface(IrInterfaceDeclaration iface, IndentedWriter w)
    {
        CommentWriter.WriteLeading(w, iface.LeadingComment);
        foreach (var ann in iface.Annotations) w.WriteLine("@" + ann);
        WriteModifiers(iface.Modifiers, w);
        w.Write("interface " + iface.Name);
        if (iface.ExtendedTypes.Count > 0) w.Write(" extends " + string.Join(", ", iface.ExtendedTypes));
        w.WriteLine(" {");
        w.Indent();
        foreach (var field in iface.Fields) _memberWriter.WriteField(field, w);
        foreach (var method in iface.Methods) _memberWriter.WriteMethod(method, w);
        foreach (var nested in iface.NestedTypes) Write(nested, w);
        w.Unindent();
        w.WriteLine("}");
    }

    private void WriteEnum(IrEnumDeclaration enm, IndentedWriter w)
    {
        CommentWriter.WriteLeading(w, enm.LeadingComment);
        foreach (var ann in enm.Annotations) w.WriteLine("@" + ann);
        WriteModifiers(enm.Modifiers, w);
        w.Write("enum " + enm.Name + " {");
        w.Indent();
        bool hasBody = enm.Fields.Count > 0 || enm.Constructors.Count > 0 || enm.Methods.Count > 0;
        for (int i = 0; i < enm.Values.Count; i++)
        {
            var v = enm.Values[i];
            var line = v.Name;
            if (v.Arguments.Count > 0)
                line += "(" + string.Join(", ", v.Arguments.Select(a => new ExpressionWriter().Write(a))) + ")";
            line += (i < enm.Values.Count - 1) ? "," : (hasBody ? ";" : "");
            w.WriteLine(line);
        }
        if (hasBody)
        {
            w.WriteLine("");
            foreach (var field in enm.Fields) _memberWriter.WriteField(field, w);
            foreach (var ctor in enm.Constructors) _memberWriter.WriteConstructor(ctor, w);
            foreach (var method in enm.Methods) _memberWriter.WriteMethod(method, w);
        }
        w.Unindent();
        w.WriteLine("}");
    }
}
