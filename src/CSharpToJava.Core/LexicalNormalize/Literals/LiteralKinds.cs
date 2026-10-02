namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>Syntactic classification bucket for a literal expression (design §4.2).</summary>
internal enum LiteralKind
{
    Other,
    Regular,
    RawSingleLine,
    RawMultiLine,
    Utf8,
    Verbatim,
    EscString,
    EscChar,
}
