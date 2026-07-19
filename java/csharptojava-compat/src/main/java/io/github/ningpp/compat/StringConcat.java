package io.github.ningpp.compat;

/**
 * Compatibility helper for C# string concatenation patterns.
 * Used by generated code when the original C# code relies on
 * StringBuilder-like append-then-join semantics.
 */
public final class StringConcat {

    private final StringBuilder builder;

    public StringConcat() {
        this.builder = new StringBuilder();
    }

    public StringConcat(int capacity) {
        this.builder = new StringBuilder(capacity);
    }

    public StringConcat append(String value) {
        builder.append(value);
        return this;
    }

    public StringConcat append(Object value) {
        builder.append(value);
        return this;
    }

    public StringConcat append(char value) {
        builder.append(value);
        return this;
    }

    public StringConcat append(char[] value) {
        builder.append(value);
        return this;
    }

    public StringConcat appendLine() {
        builder.append(System.lineSeparator());
        return this;
    }

    public StringConcat appendLine(String value) {
        builder.append(value).append(System.lineSeparator());
        return this;
    }

    public StringConcat appendLine(Object value) {
        builder.append(value).append(System.lineSeparator());
        return this;
    }

    /**
     * Returns the concatenated string built so far and clears internal state
     * so the instance can be reused.
     */
    public String join() {
        String result = builder.toString();
        builder.setLength(0);
        return result;
    }

    public String toString() {
        return builder.toString();
    }

    public int getLength() {
        return builder.length();
    }
}
