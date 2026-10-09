using SampleIndexRangeLib;

namespace SampleIndexRangeApp;

// 经典 Main（非 top-level statements）：转换后即 JVM 可直接启动的主类。
internal static class Program
{
    private static void Main()
    {
        EqArrInt(new[] { 1, 2, 3 }, SliceOps.IntMiddle(new[] { 0, 1, 2, 3, 4 }));
        EqArrLong(new long[] { 1L, 2L, 3L }, SliceOps.LongMiddle(new long[] { 0L, 1L, 2L, 3L, 4L }));
        EqArrShort(new short[] { 1, 2, 3 }, SliceOps.ShortMiddle(new short[] { 0, 1, 2, 3, 4 }));
        EqArrByte(new byte[] { 1, 2, 3 }, SliceOps.ByteMiddle(new byte[] { 0, 1, 2, 3, 4 }));
        EqArrDouble(new[] { 1.0, 2.0, 3.0 }, SliceOps.DoubleMiddle(new[] { 0.0, 1.0, 2.0, 3.0, 4.0 }));
        EqArrFloat(new[] { 1f, 2f, 3f }, SliceOps.FloatMiddle(new[] { 0f, 1f, 2f, 3f, 4f }));
        EqArrBool(new[] { true, false, true }, SliceOps.BoolMiddle(new[] { false, true, false, true, false }));
        EqArrChar(new[] { 'b', 'c', 'd' }, SliceOps.CharMiddle(new[] { 'a', 'b', 'c', 'd', 'e' }));
        EqArrStr(new[] { "b", "c" }, SliceOps.RefMiddle(new[] { "a", "b", "c", "d" }));

        EqArrInt(new[] { 1, 2, 3, 4 }, SliceOps.OpenLeft(new[] { 0, 1, 2, 3, 4 }));
        EqArrInt(new[] { 0, 1, 2 }, SliceOps.OpenRight(new[] { 0, 1, 2, 3, 4 }));
        EqArrInt(new[] { 0, 1, 2, 3, 4 }, SliceOps.Full(new[] { 0, 1, 2, 3, 4 }));
        EqArrInt(new[] { 3 }, SliceOps.BothFromEnd(new[] { 0, 1, 2, 3, 4 }));
        EqArrInt(new[] { 3, 4 }, SliceOps.OpenLeftFromEnd(new[] { 0, 1, 2, 3, 4 }));

        EqInt(4, SliceOps.LastInt(new[] { 0, 1, 2, 3, 4 }));
        EqInt(2, SliceOps.HatCompound(new[] { 0, 1, 2, 3, 4 }, 2));
        EqChar('z', SliceOps.LastChar("abz"));

        EqStr("bc", SliceOps.StrMiddle("abcd"));
        EqStr("bcd", SliceOps.StrTail("abcd"));
        EqStr("abc", SliceOps.StrHead("abcd"));
        EqStr("cd", SliceOps.StrFromEnd("abcd"));

        // C35+C31 联合：a?[^1] 经 NullConditionalDesugar 后由 index-range 接力。
        int? last = SliceOps.LastOrNull(new[] { 7, 8 });
        EqInt(8, last!.Value);
        int? none = SliceOps.LastOrNull(null);
        if (none != null) Fail("null-conditional", "expected null");

        Console.WriteLine("ALL OK");
    }

    private static void Fail(string label, string detail)
    {
        throw new Exception(label + ": " + detail);
    }

    private static void EqInt(int expected, int actual)
    {
        if (expected != actual) Fail("int", expected + " != " + actual);
    }

    private static void EqChar(char expected, char actual)
    {
        if (expected != actual) Fail("char", expected + " != " + actual);
    }

    private static void EqStr(string expected, string actual)
    {
        if (expected != actual) Fail("string", expected + " != " + actual);
    }

    private static void EqArrInt(int[] expected, int[] actual)
    {
        if (expected.Length != actual.Length) Fail("int[] length", expected.Length + " != " + actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i]) Fail("int[" + i + "]", expected[i] + " != " + actual[i]);
        }
    }

    private static void EqArrLong(long[] expected, long[] actual)
    {
        if (expected.Length != actual.Length) Fail("long[] length", expected.Length + " != " + actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i]) Fail("long[" + i + "]", expected[i] + " != " + actual[i]);
        }
    }

    private static void EqArrShort(short[] expected, short[] actual)
    {
        if (expected.Length != actual.Length) Fail("short[] length", expected.Length + " != " + actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i]) Fail("short[" + i + "]", expected[i] + " != " + actual[i]);
        }
    }

    private static void EqArrByte(byte[] expected, byte[] actual)
    {
        if (expected.Length != actual.Length) Fail("byte[] length", expected.Length + " != " + actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i]) Fail("byte[" + i + "]", expected[i] + " != " + actual[i]);
        }
    }

    private static void EqArrDouble(double[] expected, double[] actual)
    {
        if (expected.Length != actual.Length) Fail("double[] length", expected.Length + " != " + actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i]) Fail("double[" + i + "]", expected[i] + " != " + actual[i]);
        }
    }

    private static void EqArrFloat(float[] expected, float[] actual)
    {
        if (expected.Length != actual.Length) Fail("float[] length", expected.Length + " != " + actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i]) Fail("float[" + i + "]", expected[i] + " != " + actual[i]);
        }
    }

    private static void EqArrBool(bool[] expected, bool[] actual)
    {
        if (expected.Length != actual.Length) Fail("bool[] length", expected.Length + " != " + actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i]) Fail("bool[" + i + "]", expected[i] + " != " + actual[i]);
        }
    }

    private static void EqArrChar(char[] expected, char[] actual)
    {
        if (expected.Length != actual.Length) Fail("char[] length", expected.Length + " != " + actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i]) Fail("char[" + i + "]", expected[i] + " != " + actual[i]);
        }
    }

    private static void EqArrStr(string[] expected, string[] actual)
    {
        if (expected.Length != actual.Length) Fail("string[] length", expected.Length + " != " + actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i]) Fail("string[" + i + "]", expected[i] + " != " + actual[i]);
        }
    }
}
