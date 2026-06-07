package io.github.ningpp.compat;

/**
 * Provides IntPtr compatibility for C# to Java conversion.
 * IntPtr.Size returns the pointer size in bytes (4 on 32-bit, 8 on 64-bit).
 */
public class IntPtrHelper {
    public static int getSize() {
        // Java always runs on the native platform; use Long.BYTES for pointer size
        // since Java pointers are reference-sized (8 bytes on 64-bit JVM)
        return Long.BYTES;
    }
}
