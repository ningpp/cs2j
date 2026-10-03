namespace SampleClosureLiftLib;

/// <summary>
/// Mutable-capture scenarios for C32 holder lifting (design §7.2). Every value
/// returned here is observable behavior that must be identical before and
/// after the closure-lift intermediate tree is built and tested.
/// </summary>
public static class CaptureScenarios
{
    // External write to a captured local after delegate creation: delegate
    // storage and the local must stay one shared cell.
    public static int ExternalWriteCounter()
    {
        var count = 0;
        Action inc = () => count++;
        count = 10;
        inc();
        inc();
        return count; // 12
    }

    // Two delegates sharing one captured cell with mixed write kinds.
    public static (int First, int Second) SharedDelegates()
    {
        var x = 0;
        Action a = () => x++;
        Action b = () => x += 10;
        Func<int> read = () => x;

        a();
        b();
        a();
        var first = read(); // 12
        b();
        return (first, read()); // (12, 22)
    }

    // A parameter captured and externally reassigned.
    public static int ParameterCapture(int seed)
    {
        Func<int> next = () => ++seed;
        seed += 100;
        next();
        return seed; // 102
    }

    // Nested lambdas: the inner lambda closes over the outer routine's local
    // (outer lambda parameter 'step' is read-only and must NOT be lifted).
    public static int NestedDelegates()
    {
        var outer = 1;
        Func<int, Func<int>> make = step =>
        {
            outer += step;
            return () => outer;
        };

        var reader = make(40);
        return reader(); // 41
    }

    // D4: a three-level nested chain. Writes by the innermost delegate reach
    // the routine-level captured cell through both parent links.
    public static int NestedChainIncrement()
    {
        var x = 0;
        Action a = () =>
        {
            Action b = () =>
            {
                Action c = () => x++;
                c();
            };
            b();
        };

        a();
        a();
        return x; // 2
    }

    // D4: the outer lambda's own parameter is captured by the nested delegate.
    // Sequential non-escaping calls must each see their own argument through
    // the entry assignment.
    public static int NestedParameterPassthrough(int seed)
    {
        var sum = 0;
        Action<int> a = p =>
        {
            Action b = () => sum += p;
            b();
        };

        a(seed);
        a(seed + 5);
        return sum; // 2*seed + 5
    }

    // D4: nested delegates created inside foreach and invoked only after the
    // loop completes — each round's inner delegate must keep that round's
    // iteration variable (the child instance lives inside the migrated outer
    // method, the per-round parent lives in the loop body).
    public static List<int> NestedForeachRoundValues(IEnumerable<int> source)
    {
        var runners = new List<Action>();
        var sink = new List<int>();
        foreach (var item in source)
        {
            runners.Add(() =>
            {
                Action later = () => sink.Add(item);
                later();
            });
        }

        foreach (var runner in runners)
        {
            runner();
        }

        return sink; // [1, 2, 3]
    }

    // for induction: one shared cell across all iterations (current C# semantics).
    public static List<int> ForRoundValues(int n)
    {
        var funcs = new List<Func<int>>();
        for (var i = 0; i < n; i++)
        {
            funcs.Add(() => i);
        }

        return funcs.ConvertAll(f => f()); // n=3 -> [3, 3, 3]
    }

    // foreach iteration variable: a fresh cell per iteration.
    public static List<int> ForeachRoundValues(IEnumerable<int> source)
    {
        var funcs = new List<Action>();
        var results = new List<int>();
        foreach (var item in source)
        {
            funcs.Add(() => results.Add(item));
        }

        foreach (var f in funcs)
        {
            f();
        }

        return results; // [1, 2, 3]
    }

    // lock site on a captured object that is also externally reassigned.
    public static int LockedCounter()
    {
        var locker = new object();
        var count = 0;
        Action inc = () =>
        {
            lock (locker)
            {
                count++;
            }
        };

        lock (locker)
        {
            count = 5;
        }

        locker = new object();
        inc();
        inc();
        return count; // 7
    }

    // Generic capture: holder fields must keep T (no object/Object[] erosion).
    public static T[] GenericCapture<T>(T first, T second)
    {
        var current = first;
        Func<T> get = () => current;
        Action swap = () => current = second;

        var before = get();
        swap();
        return new[] { before, get() };
    }

    // Mirrors an E02-lifted private static method: the reserved-shaped
    // parameter name is an ordinary by-value parameter to C32.
    public static int RunCs2jLiftedShape(int _cs2jCap_count)
    {
        Action inc = () => _cs2jCap_count++;
        inc();
        inc();
        return _cs2jCap_count;
    }
}

/// <summary>
/// D4 instance host: a three-level nested chain captures `this`; writes by the
/// innermost delegate reach host state through every parent link, and only the
/// chain top may carry a `_cs2jThis` reference.
/// </summary>
public sealed class NestedThisHost
{
    private int _n;

    public int NestedThisWrites()
    {
        Action a = () =>
        {
            Action b = () =>
            {
                Action c = () => _n += 2;
                c();
            };
            b();
        };

        a();
        a();
        return _n; // 4
    }
}
