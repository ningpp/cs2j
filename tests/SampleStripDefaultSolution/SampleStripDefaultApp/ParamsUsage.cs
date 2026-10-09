using SampleStripDefaultLib;

// C48 M2 (design §7.2): call-site coverage for the Params* fixture APIs.
// Every check throws on mismatch so `dotnet run` on either the original or the
// preprocessed tree validates the .NET half-region equivalence (content/order,
// Count, empty state, form B identity, builder semantics, generic tiers).
public static class ParamsUsage
{
    public static void Run()
    {
        // K2 span: comma expansion, empty state and span writes through the
        // synthesized backing array.
        if (ParamsApi.SumRos(1, 2, 3) != 6)
        {
            throw new InvalidOperationException("SumRos expansion mismatch");
        }

        if (ParamsApi.SumRos() != 0)
        {
            throw new InvalidOperationException("SumRos empty mismatch");
        }

        if (ParamsApi.FormatRos('/', "a", "b") != "a/b")
        {
            throw new InvalidOperationException("FormatRos mismatch");
        }

        // K4 concrete collection: fresh per call, form B keeps the reference.
        if (ParamsApi.BatchCount(1, 2, 3) != 3)
        {
            throw new InvalidOperationException("BatchCount expansion mismatch");
        }

        if (ParamsApi.BatchCount() != 0)
        {
            throw new InvalidOperationException("BatchCount empty mismatch");
        }

        var batch = new List<int> { 7, 8 };
        if (ParamsApi.BatchCount(batch) != 2 || batch.Count != 2)
        {
            throw new InvalidOperationException("BatchCount form B identity mismatch");
        }

        // K5 interface.
        if (ParamsApi.CountAll(1, 2, 3, 4) != 4)
        {
            throw new InvalidOperationException("CountAll mismatch");
        }

        // K3 custom builder: expansion, empty, form B and the defaulted seed.
        if (ParamsApi.BagCount(1, 2, 3) != 3)
        {
            throw new InvalidOperationException("BagCount expansion mismatch");
        }

        if (ParamsApi.BagCount() != 0)
        {
            throw new InvalidOperationException("BagCount empty mismatch");
        }

        var bag = new ParamsBag(new[] { 9 });
        if (ParamsApi.BagCount(bag) != 1 || bag[0] != 9)
        {
            throw new InvalidOperationException("BagCount form B mismatch");
        }

        if (ParamsApi.BagWithSeed() != 1)
        {
            throw new InvalidOperationException("BagWithSeed default mismatch");
        }

        // K3 generic arity (int / struct / reference tiers).
        if (ParamsApi.ImmCount<int>(1, 2) != 2)
        {
            throw new InvalidOperationException("ImmCount<int> mismatch");
        }

        if (ParamsApi.ImmCount<string>("a", "b") != 2)
        {
            throw new InvalidOperationException("ImmCount<string> mismatch");
        }

        if (ParamsApi.TierList(new ParamsPoint { X = 1 }, new ParamsPoint { X = 2 }) != 2
            || ParamsApi.TierSpan(1, 2, 3) != 3
            || ParamsApi.TierImm<string>("x") != 1)
        {
            throw new InvalidOperationException("generic tier mismatch");
        }

        // K3 KeyValuePair span builder.
        var map = new ParamsMap(new[] { new KeyValuePair<string, int>("a", 1) });
        if (ParamsApi.MapCount(
                new KeyValuePair<string, int>("a", 1),
                new KeyValuePair<string, int>("b", 2)) != 2)
        {
            throw new InvalidOperationException("MapCount expansion mismatch");
        }

        if (ParamsApi.MapCount() != 0 || ParamsApi.MapCount(map) != 1)
        {
            throw new InvalidOperationException("MapCount empty/form B mismatch");
        }

        // PC-8 virtual/interface family dispatch.
        ParamsSinkBase sink = new ParamsConsoleSink();
        if (sink.Emit("a", "b") != "console:a|b")
        {
            throw new InvalidOperationException("virtual family dispatch mismatch");
        }

        if (new ParamsConsoleSink().Flush('o', 'k') != "ok")
        {
            throw new InvalidOperationException("override flush mismatch");
        }

        var ctorSink = new ParamsConsoleSink(10, 1, 2, 3);
        if (ctorSink.SeedSum != 16)
        {
            throw new InvalidOperationException("base ctor params chain mismatch");
        }
    }
}
