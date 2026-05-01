// src/CSharpToJava.Core/Java2/CodeGen/IndentedWriter.cs
using System.Text;

namespace CSharpToJava.Core.Java2.CodeGen;

public class IndentedWriter
{
    private readonly StringBuilder _sb = new();
    private readonly string _indentUnit;
    private int _indentLevel;
    private bool _startOfLine = true;

    public IndentedWriter(string indentUnit = "    ")
    {
        _indentUnit = indentUnit;
    }

    public void Indent() => _indentLevel++;
    public void Unindent() { if (_indentLevel > 0) _indentLevel--; }

    public IndentedWriter Write(string text)
    {
        if (_startOfLine && text.Length > 0)
        {
            for (int i = 0; i < _indentLevel; i++)
                _sb.Append(_indentUnit);
            _startOfLine = false;
        }
        _sb.Append(text);
        return this;
    }

    public IndentedWriter WriteLine(string text = "")
    {
        Write(text);
        _sb.AppendLine();
        _startOfLine = true;
        return this;
    }

    public override string ToString() => _sb.ToString();
}
