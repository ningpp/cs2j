package io.github.ningpp.compat;

/** Compatibility constants for C# enum flag values that the converter
 *  maps to {@code IntegerHelper.CONSTANT_NAME} when the enum type cannot be resolved. */
public final class IntegerHelper {
    private IntegerHelper() {
    }

    // System.Globalization.NumberStyles
    public static final int AllowLeadingWhite = 1;
    public static final int AllowTrailingWhite = 2;
    public static final int AllowLeadingSign = 4;
    public static final int AllowDecimalPoint = 32;
    public static final int AllowExponent = 128;

    // System.IO.FileAccess
    public static final int Read = 1;
    public static final int Write = 2;

    // System.Xml.NamespaceHandling
    public static final int Default = 0;
    public static final int OmitDuplicates = 1;

    // System.Globalization.DateTimeStyles
    public static final int NoCurrentDateDefault = 8;
    public static final int RoundtripKind = 128;

    // System.StringSplitOptions
    public static final int RemoveEmptyEntries = 1;

    // dotnet.xml (XmlPreloadedResolver)
    public static final int All = -1;
    public static final int Xhtml10 = 1;
    public static final int Rss091 = 2;
}
