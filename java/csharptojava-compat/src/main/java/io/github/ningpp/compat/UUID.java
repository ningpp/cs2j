package io.github.ningpp.compat;

/**
 * Bridges C# System.Guid to Java.
 *
 * <p>Delegates to {@link java.util.UUID} for storage and string formatting,
 * but adds the constructors used by translated C# code:
 * <ul>
 *   <li>{@code new UUID()} - the empty/all-zero GUID</li>
 *   <li>{@code new UUID(String)} - C# new Guid(string); throws FormatException
 *       for malformed input, matching C# semantics</li>
 *   <li>{@code new UUID(int, short, short, byte...)} - C# Guid(int, short, short, byte[])</li>
 *   <li>{@code new UUID(long, long)} - same as java.util.UUID</li>
 * </ul>
 */
public final class UUID implements Comparable<UUID> {

    private final java.util.UUID value;

    /** C# Guid.Empty. */
    public static final UUID Empty = new UUID();

    public UUID() {
        this.value = new java.util.UUID(0L, 0L);
    }

    /**
     * C# new Guid(string s). {@link #fromString} normalizes whitespace and
     * wraps {@link IllegalArgumentException} into {@link FormatException},
     * which is what translated C# catch blocks expect.
     */
    public UUID(String name) {
        this.value = fromString(name).value;
    }

    public UUID(long mostSigBits, long leastSigBits) {
        this.value = new java.util.UUID(mostSigBits, leastSigBits);
    }

    /**
     * C# Guid(int a, short b, short c, byte d, byte e, byte f, byte g,
     * byte h, byte i, byte j, byte k).
     *
     * <p>The translated Java code passes int values for the byte positions
     * (typically from {@code & 0xFF} expressions), so this overload accepts
     * int to avoid requiring explicit casts at every call site.
     */
    public UUID(int a, short b, short c,
                int d, int e, int f, int g,
                int h, int i, int j, int k) {
        long mostSigBits = ((long) a << 32)
                | ((long) (b & 0xffff) << 16)
                | (c & 0xffff);
        long leastSigBits = ((long) (d & 0xff) << 56)
                | ((long) (e & 0xff) << 48)
                | ((long) (f & 0xff) << 40)
                | ((long) (g & 0xff) << 32)
                | ((long) (h & 0xff) << 24)
                | ((long) (i & 0xff) << 16)
                | ((long) (j & 0xff) << 8)
                | (k & 0xff);
        this.value = new java.util.UUID(mostSigBits, leastSigBits);
    }

    public static UUID randomUUID() {
        return new UUID(java.util.UUID.randomUUID());
    }

    public static UUID fromString(String name) {
        if (name == null) {
            throw new ArgumentNullException("name");
        }
        String trimmed = name.trim();
        try {
            return new UUID(java.util.UUID.fromString(trimmed));
        } catch (IllegalArgumentException e) {
            // C# Guid constructor throws FormatException for invalid strings;
            // Java UUID.fromString throws IllegalArgumentException.
            // Wrap to match C# behavior so translated catch blocks work correctly.
            throw new FormatException(e.getMessage(), e);
        }
    }

    /** C# Guid.TryParseExact(input, format, out result) — "D"/"N" formats. */
    public static boolean tryParseExact(String input, String format, ObjectHolder<UUID> outResult) {
        try {
            String s = input == null ? "" : input.trim();
            if ("N".equalsIgnoreCase(format)) {
                s = s.replace("-", "");
                if (s.length() != 32) {
                    return false;
                }
            } else if (s.length() != 36) {
                return false;
            }
            java.util.UUID parsed = java.util.UUID.fromString(s);
            outResult.value = new UUID(parsed);
            return true;
        } catch (IllegalArgumentException e) {
            return false;
        }
    }

    private UUID(java.util.UUID value) {
        this.value = value;
    }

    @Override
    public String toString() {
        return value.toString();
    }

    /** C# Guid.ToString(format): "N" = 32 hex digits, no dashes; "D"/"" = dashed; "B" = braced; "P" = parenthesized. */
    public String toString(String format) {
        String dashed = value.toString();
        if ("N".equalsIgnoreCase(format)) {
            return dashed.replace("-", "");
        }
        if ("B".equalsIgnoreCase(format)) {
            return "{" + dashed + "}";
        }
        if ("P".equalsIgnoreCase(format)) {
            return "(" + dashed + ")";
        }
        return dashed;
    }

    /** C# Guid.ToString(format, provider) — provider (CultureInfo) is ignored. */
    public String toString(String format, CultureInfo provider) {
        return toString(format);
    }

    @Override
    public boolean equals(Object obj) {
        return obj instanceof UUID && value.equals(((UUID) obj).value);
    }

    @Override
    public int hashCode() {
        return value.hashCode();
    }

    @Override
    public int compareTo(UUID other) {
        return value.compareTo(other.value);
    }

    /**
     * Exposes the wrapped JDK UUID for interop with Java APIs.
     */
    public java.util.UUID toJavaUUID() {
        return value;
    }
}
