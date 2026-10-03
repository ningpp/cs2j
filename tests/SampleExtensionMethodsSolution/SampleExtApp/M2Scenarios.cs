using SampleExtLib.Delegates;
using SampleExtLib.NullGuards;
using SampleExtLib.Spans;

/// <summary>
/// C19 M2 runtime scenarios: ref/scoped-ref Span receivers, extension calls
/// on null receivers that never throw, null-conditional chain lifting
/// (single-evaluation temps), and method-group delegate capture.
/// </summary>
public static class M2Scenarios
{
    public static int Run()
    {
        // EM-REF: ref/scoped-ref receivers on a writable Span local.
        Span<int> values = stackalloc int[3];
        values[0] = 41;
        values.IncrementFirst();
        if (values[0] != 42)
        {
            return 8;
        }

        if (values.Sum() != 42)
        {
            return 9;
        }

        // Null receiver that the extension body does not dereference.
        string? missing = null;
        if (!missing.IsNull())
        {
            return 10;
        }

        string? present = "ok";
        if (present.IsNull())
        {
            return 11;
        }

        // EM-COND: null-conditional chains lifted to single-evaluation temps
        // (local declarators are dominant expression positions).
        int? missingLength = missing?.LengthOrNull();
        if (missingLength is not null)
        {
            return 12;
        }

        int? presentLength = present?.LengthOrNull();
        if (presentLength != 2)
        {
            return 13;
        }

        // EM-GROUP: extension method group captured into a delegate.
        var name = "world";
        Action greet = name.Greet;
        greet();
        if (GreetingExtensions.GreetingCount != 1)
        {
            return 14;
        }

        return 0;
    }
}
