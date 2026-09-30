using SampleNumericBoundaryLib;

var failures = 0;

void Check(string name, bool ok)
{
    if (!ok)
    {
        Console.Error.WriteLine($"FAIL: {name}");
        failures++;
    }
}

// ── stripped unchecked sites: wraparound behavior preserved ──
Check("WrapUInt32Max", UncheckedSamples.WrapUInt32ToInt32(0xFFFFFFFFu) == -1);
Check("WrapToByteOverflow", UncheckedSamples.WrapToByte(256) == 0);
Check("WrapToByteNegative", UncheckedSamples.WrapToByte(-1) == 255);
Check("WrapAddWraps", UncheckedSamples.WrapAdd(int.MaxValue, 1) == int.MinValue);
Check("UnwrapBlockWraps", UncheckedSamples.UnwrapBlock(int.MaxValue, 0) == int.MinValue);

// ── nested site (non-overflowing inputs only in M1) ──
Check("NestedInsideChecked", UncheckedSamples.NestedInsideChecked(40, 2) == 42);

// ── NB-9/11: decimal shell stripped, call intact ──
Check("CheckedDecimalAdd", UncheckedSamples.CheckedDecimalAdd(1m, 2m) == 3m);

// ── NB-6: materialized constant ──
Check("ConstantMaterialized", UncheckedSamples.U64MaxAsLong == -1L);

// ── M1 retained checked sites: still throw on overflow at runtime ──
Check("CheckedAddNonOverflow", CheckedSamples.CheckedAdd(40, 2) == 42);
Check("CheckedNarrowNonOverflow", CheckedSamples.CheckedNarrow(200) == 200);
Check("CheckedBlockNonOverflow",
    CheckedSamples.CheckedBlockSum(new[] { 1, 2, 3 }) == 6);

var addThrew = false;
try
{
    CheckedSamples.CheckedAdd(int.MaxValue, 1);
}
catch (OverflowException)
{
    addThrew = true;
}

Check("CheckedAddThrows", addThrew);

var narrowThrew = false;
try
{
    CheckedSamples.CheckedNarrow(256);
}
catch (OverflowException)
{
    narrowThrew = true;
}

Check("CheckedNarrowThrows", narrowThrew);

var blockThrew = false;
try
{
    CheckedSamples.CheckedBlockSum(new[] { int.MaxValue, 1 });
}
catch (OverflowException)
{
    blockThrew = true;
}

Check("CheckedBlockThrows", blockThrew);

if (failures > 0)
{
    Console.Error.WriteLine($"{failures} numeric-boundary sample failure(s).");
    return 1;
}

Console.WriteLine("numeric-boundary sample OK");
return 0;
