package io.github.ningpp.compat;

/**
 * Bridges C# System.Guid to Java.
 *
 * <p>Delegates to {@link java.util.UUID} for storage and string formatting,
 * but adds the constructors used by translated C# code:
 * <ul>
 *   <li>{@code new UUID()} - the empty/all-zero GUID</li>
 *   <li>{@code new UUID(int, short, short, byte...)} - C# Guid(int, short, short, byte[])</li>
 *   <li>{@code new UUID(long, long)} - same as java.util.UUID</li>
 * </ul>
 */
public final class UUID implements Comparable<UUID> {

    private final java.util.UUID value;

    public UUID() {
        this.value = new java.util.UUID(0L, 0L);
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
        return new UUID(java.util.UUID.fromString(name));
    }

    private UUID(java.util.UUID value) {
        this.value = value;
    }

    @Override
    public String toString() {
        return value.toString();
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
