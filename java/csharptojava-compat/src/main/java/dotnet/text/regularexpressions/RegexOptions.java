package dotnet.text.regularexpressions;

/** Minimal System.Text.RegularExpressions.RegexOptions compat constants. */
public final class RegexOptions {
    public static final int None = 0;
    public static final int IgnoreCase = 1;
    public static final int Multiline = 2;
    public static final int ExplicitCapture = 4;
    public static final int Compiled = 8;
    public static final int Singleline = 16;
    public static final int IgnorePatternWhitespace = 32;
    public static final int RightToLeft = 64;

    private RegexOptions() {
    }
}
