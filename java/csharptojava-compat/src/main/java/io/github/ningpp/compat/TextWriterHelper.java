package io.github.ningpp.compat;

import java.io.PrintStream;
import java.io.PrintWriter;

/**
 * Helper for C# TextWriter properties that don't exist on Java's PrintWriter.
 */
public final class TextWriterHelper {
    private TextWriterHelper() {
    }

    /**
     * Mirrors C# TextWriter.Write(string format, args) / Console.Error.Write:
     * the composite format string is expanded like String.Format, so the call
     * cannot lower to PrintStream.print(String, Object) (no such overload).
     */
    public static void writeFormat(PrintStream stream, String format, Object... args) {
        stream.print(StringHelper.formatCs(format, args));
    }

    /**
     * Mirrors C# TextWriter.WriteLine(string format, args) on a stream.
     */
    public static void writeFormatLine(PrintStream stream, String format, Object... args) {
        stream.println(StringHelper.formatCs(format, args));
    }

    /**
     * Mirrors C# TextWriter.Write(string format, args) on a writer (converted
     * StreamWriter/StringWriter/CSharpTextWriter instances are PrintWriters).
     */
    public static void writeFormat(PrintWriter writer, String format, Object... args) {
        writer.print(StringHelper.formatCs(format, args));
    }

    /**
     * Mirrors C# TextWriter.WriteLine(string format, args) on a writer.
     */
    public static void writeFormatLine(PrintWriter writer, String format, Object... args) {
        writer.println(StringHelper.formatCs(format, args));
    }

    /**
     * Mirrors C# TextWriter.Encoding.
     * Returns the encoding of a StreamWriter, or UTF-8 as default for
     * plain PrintWriter instances.
     */
    public static Encoding getEncoding(PrintWriter w) {
        if (w instanceof StreamWriter sw) {
            return sw.getEncoding();
        }
        return Encoding.getDefault();
    }
}
