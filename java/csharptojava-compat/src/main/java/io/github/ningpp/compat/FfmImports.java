package io.github.ningpp.compat;

import java.lang.foreign.Arena;
import java.lang.foreign.FunctionDescriptor;
import java.lang.foreign.Linker;
import java.lang.foreign.MemorySegment;
import java.lang.foreign.SymbolLookup;
import java.lang.invoke.MethodHandle;
import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;

/**
 * C06 R8 FFM whitelist facade binder. The C# unsafe-ffm preprocessor lowers
 * whitelisted [DllImport]/[LibraryImport] methods to methods marked with
 * {@code [Cs2j.Generation.InteropFacade(library, entryPoint)]}; the Java
 * emitter gives each such method a static final MethodHandle initialized
 * through this class, and the method body becomes an invokeExact call.
 *
 * <p>v1 ABI subset: primitive parameters/return plus read-only strings
 * (marshalled through an auto arena as UTF-8 or UTF-16). A null C# string is
 * marshalled to {@link MemorySegment#NULL}, matching the .NET P/Invoke
 * convention.</p>
 *
 * <p>An empty library name selects the platform default lookup
 * ({@code Linker.nativeLinker().defaultLookup()}); otherwise the named
 * shared library is resolved by the operating system. A missing symbol fails
 * loudly with {@link LinkageError} at class initialization.</p>
 */
public final class FfmImports {

    private static final Linker LINKER = Linker.nativeLinker();
    private static final Arena AUTO_ARENA = Arena.ofAuto();

    /** Windows W 入口要求的无 BOM、NUL 结尾 UTF-16 little-endian。 */
    private static final Charset UTF_16_LE = StandardCharsets.UTF_16LE;

    private FfmImports() {
    }

    /**
     * Resolves {@code symbol} in {@code library} (or the platform default
     * lookup when {@code library} is null/empty) and creates a downcall
     * method handle with the given function descriptor.
     */
    public static MethodHandle bind(String library, String symbol, FunctionDescriptor descriptor) {
        SymbolLookup lookup = (library == null || library.isEmpty())
                ? LINKER.defaultLookup()
                : SymbolLookup.libraryLookup(library, AUTO_ARENA);
        MemorySegment address = lookup.find(symbol).orElseThrow(() ->
                new LinkageError("cs2j FFM symbol not found: " + library + "!" + symbol));
        return LINKER.downcallHandle(address, descriptor);
    }

    /**
     * Allocates a NUL-terminated UTF-8 copy; null maps to the NULL address.
     * Uses SegmentAllocator.allocateFrom(String, Charset) (JDK 24+ FFM API
     * replaced Arena.allocateUtf8String/allocateUtf16String).
     */
    public static MemorySegment utf8(String value) {
        return value == null ? MemorySegment.NULL
                : AUTO_ARENA.allocateFrom(value, StandardCharsets.UTF_8);
    }

    /**
     * Allocates a NUL-terminated UTF-16 little-endian copy without BOM (the
     * encoding expected by Windows *W entry points); null maps to NULL.
     */
    public static MemorySegment utf16(String value) {
        return value == null ? MemorySegment.NULL
                : AUTO_ARENA.allocateFrom(value, UTF_16_LE);
    }
}
