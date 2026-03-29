namespace CSharpToJava.Core.Context;

/// <summary>
/// Static utility methods for Java naming conventions (keyword escaping, etc.).
/// Extracted from ConversionContext.
/// </summary>
public static class JavaNaming
{
    public static bool IsJavaKeyword(string word)
    {
        return word switch
        {
            "abstract" or "assert" or "boolean" or "break" or "byte" or "case" or "catch" or
            "char" or "class" or "const" or "continue" or "default" or "do" or "double" or
            "else" or "enum" or "extends" or "final" or "finally" or "float" or "for" or
            "goto" or "if" or "implements" or "import" or "instanceof" or "int" or
            "interface" or "long" or "native" or "new" or "package" or "private" or
            "protected" or "public" or "return" or "short" or "static" or "strictfp" or
            "super" or "switch" or "synchronized" or "this" or "throw" or "throws" or
            "transient" or "try" or "void" or "volatile" or "while" => true,
            _ => false
        };
    }

    public static string EscapeJavaKeyword(string word)
    {
        return IsJavaKeyword(word) ? word + "Value" : word;
    }
}
