package io.github.ningpp.compat;

import java.lang.management.ManagementFactory;
import java.util.Arrays;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

/**
 * Tests for {@link MemoryExtensions#sequenceEqual(ReadOnlySpan, ReadOnlySpan)} and
 * {@link MemoryExtensions#sequenceEqual(Span, ReadOnlySpan)}.
 *
 * <p>Both overloads gained element-typed fast paths that compare the backing
 * {@code String}/{@code char[]}/{@code byte[]} directly instead of routing every element
 * through {@code get(int)} (which boxes a wrapper per element and re-tests the backing
 * kind on each access). These tests pin the semantics of every dispatch branch — most
 * importantly that string-backed, char[]-backed, byte[]-backed, mixed and object-backed
 * spans all agree with one another.
 */
public class MemoryExtensionsSequenceEqualTest {

    // ---------------------------------------------------------------- string backing

    @Test
    public void SequenceEqual_TwoStringBackedSpans_Equal() {
        ReadOnlySpan<Character> left = MemoryExtensions.asSpan("hello world");
        ReadOnlySpan<Character> right = MemoryExtensions.asSpan("hello world");

        assertTrue(MemoryExtensions.sequenceEqual(left, right));
    }

    @Test
    public void SequenceEqual_TwoStringBackedSpans_DifferAtFirstMiddleAndLast() {
        ReadOnlySpan<Character> base = MemoryExtensions.asSpan("abcdef");

        assertFalse(MemoryExtensions.sequenceEqual(base, MemoryExtensions.asSpan("zbcdef")),
                "difference at the first character must be detected");
        assertFalse(MemoryExtensions.sequenceEqual(base, MemoryExtensions.asSpan("abzdef")),
                "difference in the middle must be detected");
        assertFalse(MemoryExtensions.sequenceEqual(base, MemoryExtensions.asSpan("abcdez")),
                "difference at the last character must be detected");
    }

    @Test
    public void SequenceEqual_NonAsciiStringBackedSpans_CompareByCodeUnit() {
        // Registered inside String.regionMatches since both sides are String-backed.
        assertTrue(MemoryExtensions.sequenceEqual(
                MemoryExtensions.asSpan("\u6F22\u5B57\uD83D\uDE00"),
                MemoryExtensions.asSpan("\u6F22\u5B57\uD83D\uDE00")));
        assertFalse(MemoryExtensions.sequenceEqual(
                MemoryExtensions.asSpan("\u6F22\u5B57"),
                MemoryExtensions.asSpan("\u6F22\u5B58")));
    }

    // ----------------------------------------------------------------- char[] backing

    @Test
    public void SequenceEqual_TwoCharArrayBackedSpans_EqualAndDiffer() {
        char[] left = { 'a', 'b', 'c', 'd' };
        char[] right = { 'a', 'b', 'c', 'd' };
        char[] differing = { 'a', 'b', 'c', 'z' };

        assertTrue(MemoryExtensions.sequenceEqual(new ReadOnlySpan<>(left), new ReadOnlySpan<>(right)));
        assertFalse(MemoryExtensions.sequenceEqual(new ReadOnlySpan<>(left), new ReadOnlySpan<>(differing)));
    }

    // ----------------------------------------------------------------- byte[] backing

    @Test
    public void SequenceEqual_TwoByteArrayBackedSpans_CompareMaskedValues() {
        byte[] left = { 0, 1, 127, (byte) 0x80, (byte) 0xFF };
        byte[] same = { 0, 1, 127, (byte) 0x80, (byte) 0xFF };
        byte[] differing = { 0, 1, 127, (byte) 0x80, (byte) 0xFE };

        // get(int) exposes bytes as (b & 0xFF) in 0..255, so the raw byte[] fast path
        // must agree with that masking for high-bit values.
        assertTrue(MemoryExtensions.sequenceEqual(
                MemoryExtensions.asSpan(left), MemoryExtensions.asSpan(same)));
        assertFalse(MemoryExtensions.sequenceEqual(
                MemoryExtensions.asSpan(left), MemoryExtensions.asSpan(differing)));
    }

    // -------------------------------------------------------------- mixed backings

    @Test
    public void SequenceEqual_StringBackedVsCharArrayBacked_Agrees() {
        // Exercises the mixed String/char[] branch: neither side may be cast blindly to
        // the other's representation.
        String text = "mixed";
        char[] chars = { 'm', 'i', 'x', 'e', 'd' };
        char[] differing = { 'm', 'i', 'x', 'e', 'Z' };

        assertTrue(MemoryExtensions.sequenceEqual(MemoryExtensions.asSpan(text), new ReadOnlySpan<>(chars)));
        assertTrue(MemoryExtensions.sequenceEqual(new ReadOnlySpan<>(chars), MemoryExtensions.asSpan(text)));
        assertFalse(MemoryExtensions.sequenceEqual(MemoryExtensions.asSpan(text), new ReadOnlySpan<>(differing)));
    }

    @Test
    public void SequenceEqual_NonAsciiStringBackedVsCharArrayBacked_Agrees() {
        String text = "\u6F22\u5B57";
        char[] chars = { '\u6F22', '\u5B57' };

        assertTrue(MemoryExtensions.sequenceEqual(MemoryExtensions.asSpan(text), new ReadOnlySpan<>(chars)));
    }

    @SuppressWarnings({ "rawtypes", "unchecked" })
    @Test
    public void SequenceEqual_CharBackedVsByteBacked_ReturnsFalse() {
        // Different element kinds must never compare equal, whatever the backings are.
        // Raw spans are used because ReadOnlySpan<Character> and ReadOnlySpan<Integer>
        // share no inferable type argument.
        ReadOnlySpan chars = MemoryExtensions.asSpan("ab");
        ReadOnlySpan bytes = MemoryExtensions.asSpan(new byte[] { 'a', 'b' });

        assertFalse(MemoryExtensions.sequenceEqual(chars, bytes));
    }

    // -------------------------------------------------------------- object backing

    @Test
    public void SequenceEqual_BoxedObjectArrayBacked_UsesFallbackAndStaysCorrect() {
        Character[] left = { 'x', 'y' };
        Character[] right = { 'x', 'y' };
        Character[] differing = { 'x', 'z' };

        assertTrue(MemoryExtensions.sequenceEqual(new ReadOnlySpan<>(left), new ReadOnlySpan<>(right)));
        assertFalse(MemoryExtensions.sequenceEqual(new ReadOnlySpan<>(left), new ReadOnlySpan<>(differing)));
    }

    @Test
    public void SequenceEqual_NullElements_StayEqual() {
        // The generic fallback compares with Objects.equals, so null slots match null.
        Character[] left = { 'a', null };
        Character[] right = { 'a', null };

        assertTrue(MemoryExtensions.sequenceEqual(new ReadOnlySpan<>(left), new ReadOnlySpan<>(right)));
    }

    @Test
    public void SequenceEqual_StringArrayBacked_UsesFallbackAndStaysCorrect() {
        String[] left = { "a", "b" };
        String[] right = { "a", "b" };

        assertTrue(MemoryExtensions.sequenceEqual(new ReadOnlySpan<>(left), new ReadOnlySpan<>(right)));
        assertFalse(MemoryExtensions.sequenceEqual(new ReadOnlySpan<>(left), new ReadOnlySpan<>(new String[] { "a", "c" })));
    }

    // ----------------------------------------------------- lengths, empties, nulls

    @Test
    public void SequenceEqual_DifferentLengths_ReturnsFalse() {
        assertFalse(MemoryExtensions.sequenceEqual(
                MemoryExtensions.asSpan("abc"), MemoryExtensions.asSpan("abcd")));
        assertFalse(MemoryExtensions.sequenceEqual(
                MemoryExtensions.asSpan(new char[] { 'a' }), MemoryExtensions.asSpan(new char[] { 'a', 'b' })));
        assertFalse(MemoryExtensions.sequenceEqual(
                MemoryExtensions.asSpan(new byte[] { 1 }), MemoryExtensions.asSpan(new byte[] { 1, 2 })));
    }

    @Test
    public void SequenceEqual_EmptySpans_ReturnsTrue() {
        assertTrue(MemoryExtensions.sequenceEqual(MemoryExtensions.asSpan(""), MemoryExtensions.asSpan("")));
        assertTrue(MemoryExtensions.sequenceEqual(
                MemoryExtensions.asSpan(new char[0]), MemoryExtensions.asSpan(new char[0])));
        assertTrue(MemoryExtensions.sequenceEqual(
                MemoryExtensions.asSpan(new byte[0]), MemoryExtensions.asSpan(new byte[0])));
    }

    @Test
    public void SequenceEqual_NullSpan_EqualsEmptySpan() {
        // C# default(ReadOnlySpan&lt;T&gt;) IS the empty span (Length 0), so a null Java
        // span must compare equal to an empty one, not only to another null.
        // Utf8JsonReaderTests.defaultUtf8JsonReader compares a default reader's
        // GetValueSpan() (transpiled to Java null) against the expected empty span.
        assertTrue(MemoryExtensions.sequenceEqual((ReadOnlySpan<Character>) null, MemoryExtensions.asSpan("")));
        assertTrue(MemoryExtensions.sequenceEqual(MemoryExtensions.asSpan(""), (ReadOnlySpan<Character>) null));
        assertTrue(MemoryExtensions.sequenceEqual(
                (ReadOnlySpan<Integer>) null, new ReadOnlySpan<>(new byte[0])));
    }

    @Test
    public void SequenceEqual_NullSpans_FollowExistingNullSemantics() {
        // ReadOnlySpan overload: both null -> true, exactly one null -> false.
        assertTrue(MemoryExtensions.sequenceEqual((ReadOnlySpan<Character>) null, (ReadOnlySpan<Character>) null));
        assertFalse(MemoryExtensions.sequenceEqual(MemoryExtensions.asSpan("a"), (ReadOnlySpan<Character>) null));
        assertFalse(MemoryExtensions.sequenceEqual((ReadOnlySpan<Character>) null, MemoryExtensions.asSpan("a")));

        // Span overload keeps returning false for null on either side.
        assertFalse(MemoryExtensions.sequenceEqual((Span<Character>) null, MemoryExtensions.asSpan("a")));
        assertFalse(MemoryExtensions.sequenceEqual(new Span<>(new char[] { 'a' }), (ReadOnlySpan<Character>) null));
    }

    // ------------------------------------------------------------------- offsets

    @Test
    public void SequenceEqual_SlicedSpans_CompareOnlyTheViewedRange() {
        char[] backing = { 'x', 'a', 'b', 'c', 'y' };
        // Same three characters 'a','b','c' reached from different backings/offsets.
        ReadOnlySpan<Character> fromArray = new ReadOnlySpan<>(backing, 1, 3);
        ReadOnlySpan<Character> fromString = MemoryExtensions.asSpan("zabcz", 1, 3);

        assertTrue(MemoryExtensions.sequenceEqual(fromArray, fromString));

        char[] differingBacking = { 'x', 'a', 'b', 'Z', 'y' };
        ReadOnlySpan<Character> differing = new ReadOnlySpan<>(differingBacking, 1, 3);
        assertFalse(MemoryExtensions.sequenceEqual(fromArray, differing));
    }

    @Test
    public void SequenceEqual_SlicedByteSpans_CompareOnlyTheViewedRange() {
        byte[] backing = { 9, 1, 2, 3, 9 };
        ReadOnlySpan<Integer> sliced = new ReadOnlySpan<>(backing, 1, 3);

        assertTrue(MemoryExtensions.sequenceEqual(sliced, MemoryExtensions.asSpan(new byte[] { 1, 2, 3 })));
        assertFalse(MemoryExtensions.sequenceEqual(sliced, MemoryExtensions.asSpan(new byte[] { 1, 2, 4 })));
    }

    // ------------------------------------------------------- mutable Span overload

    @Test
    public void SequenceEqual_MutableCharSpanVsStringBacked_Agrees() {
        Span<Character> mutable = new Span<>(new char[] { 'a', 'b', 'c' });

        assertTrue(MemoryExtensions.sequenceEqual(mutable, MemoryExtensions.asSpan("abc")));
        assertFalse(MemoryExtensions.sequenceEqual(mutable, MemoryExtensions.asSpan("abd")));
    }

    @Test
    public void SequenceEqual_MutableCharSpanVsCharArrayBacked_Agrees() {
        Span<Character> mutable = new Span<>(new char[] { 'a', 'b', 'c' });

        assertTrue(MemoryExtensions.sequenceEqual(mutable, new ReadOnlySpan<>(new char[] { 'a', 'b', 'c' })));
        assertFalse(MemoryExtensions.sequenceEqual(mutable, new ReadOnlySpan<>(new char[] { 'a', 'b', 'd' })));
    }

    @Test
    public void SequenceEqual_MutableByteSpanVsByteBacked_Agrees() {
        Span<Integer> mutable = new Span<>(new byte[] { 1, 2, (byte) 0xFF });

        assertTrue(MemoryExtensions.sequenceEqual(mutable, MemoryExtensions.asSpan(new byte[] { 1, 2, (byte) 0xFF })));
        assertFalse(MemoryExtensions.sequenceEqual(mutable, MemoryExtensions.asSpan(new byte[] { 1, 2, 3 })));
    }

    @Test
    public void SequenceEqual_MutableSpan_LengthMismatchAndNulls_ReturnFalse() {
        assertFalse(MemoryExtensions.sequenceEqual(
                new Span<>(new char[] { 'a' }), MemoryExtensions.asSpan("ab")));
        assertFalse(MemoryExtensions.sequenceEqual((Span<Character>) null, MemoryExtensions.asSpan("a")));
    }

    @Test
    public void SequenceEqual_Int32ViewSpan_ComparesElementsNotRawBytes() {
        // Span.castToInt32 builds a 4-byte-per-element int view over a byte[]. Such a
        // span must not be compared as if it were a plain byte span: element 0 here is
        // the little-endian int 0x00000100 = 256, while the byte span's element 0 is 0.
        Span<Integer> bytes = new Span<>(new byte[] { 0x00, 0x01, 0x00, 0x00 });
        Span<Integer> intView = Span.castToInt32(bytes);
        ReadOnlySpan<Integer> singleZeroByte = new ReadOnlySpan<>(new byte[] { 0x00 });

        assertTrue(intView.get(0) == 256, "guard assumes the int view reads a little-endian int");
        assertFalse(MemoryExtensions.sequenceEqual(intView, singleZeroByte),
                "an int32 view must be compared per 4-byte element, not per raw byte");
    }

    // ------------------------------------------------------------ allocation guard

    @Test
    public void SequenceEqual_CharSpans_DoNotBoxPerElement() {
        // U+6F22 is outside Character's 0..127 cache, so the boxed get(int)-based loop
        // allocates one 16-byte Character per element per side (~32 MB for 1M elements)
        // while the char[] fast path allocates nothing. This is the regression guard for
        // the per-element boxing that dominated ValueTests.VeryLongInputString.
        java.lang.management.ThreadMXBean bean = ManagementFactory.getThreadMXBean();
        assumeTrue(bean instanceof com.sun.management.ThreadMXBean,
                "com.sun.management.ThreadMXBean is not available on this JVM");
        com.sun.management.ThreadMXBean allocationBean = (com.sun.management.ThreadMXBean) bean;
        assumeTrue(allocationBean.isThreadAllocatedMemorySupported(),
                "thread allocation counting is not supported on this JVM");
        allocationBean.setThreadAllocatedMemoryEnabled(true);

        int length = 1_000_000;
        char[] leftBacking = new char[length];
        char[] rightBacking = new char[length];
        Arrays.fill(leftBacking, '\u6F22');
        Arrays.fill(rightBacking, '\u6F22');

        ReadOnlySpan<Character> left = new ReadOnlySpan<>(leftBacking);
        ReadOnlySpan<Character> right = new ReadOnlySpan<>(rightBacking);

        // Warm up so JIT compilation costs are not attributed to the measured call.
        for (int i = 0; i < 3; i++) {
            assertTrue(MemoryExtensions.sequenceEqual(left, right));
        }

        long before = allocationBean.getCurrentThreadAllocatedBytes();
        boolean equal = MemoryExtensions.sequenceEqual(left, right);
        long allocated = allocationBean.getCurrentThreadAllocatedBytes() - before;

        assertTrue(equal);
        assertTrue(allocated < 4L * 1024 * 1024,
                "char-backed sequenceEqual must not box per element, but allocated "
                        + allocated + " bytes for " + length + " elements");
    }

    @Test
    public void SequenceEqual_CharSpans_StillDetectsDifferenceAtScale() {
        int length = 1_000_000;
        char[] leftBacking = new char[length];
        char[] rightBacking = new char[length];
        Arrays.fill(leftBacking, '\u6F22');
        Arrays.fill(rightBacking, '\u6F22');
        rightBacking[length - 1] = '\u5B57';

        assertFalse(MemoryExtensions.sequenceEqual(
                new ReadOnlySpan<>(leftBacking), new ReadOnlySpan<>(rightBacking)));
        assertFalse(MemoryExtensions.sequenceEqual(
                MemoryExtensions.asSpan(new String(leftBacking)),
                MemoryExtensions.asSpan(new String(rightBacking))));
    }
}
