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

// ── nested site: inner unchecked wins inside the outer checked block ──
Check("NestedInsideChecked", UncheckedSamples.NestedInsideChecked(40, 2) == 42);

// ── NB-9/11: decimal shell stripped, call intact ──
Check("CheckedDecimalAdd", UncheckedSamples.CheckedDecimalAdd(1m, 2m) == 3m);

// ── NB-6: materialized constant ──
Check("ConstantMaterialized", UncheckedSamples.U64MaxAsLong == -1L);

// ── M2/M3 checked helpers: non-overflowing inputs behave identically ──
Check("CheckedAddNonOverflow", CheckedSamples.CheckedAdd(40, 2) == 42);
Check("CheckedSubNonOverflow", CheckedSamples.CheckedSub(40, 2) == 38);
Check("CheckedMulNonOverflow", CheckedSamples.CheckedMul(40, 2) == 80);
Check("CheckedAdd64", CheckedSamples.CheckedAdd64(40L, 2L) == 42L);
Check("CheckedAddU32", CheckedSamples.CheckedAddU32(40u, 2u) == 42u);
Check("CheckedAddU64", CheckedSamples.CheckedAddU64(40ul, 2ul) == 42ul);
Check("CheckedNegate", CheckedSamples.CheckedNegate(42) == -42);
Check("CheckedNegateUInt", CheckedSamples.CheckedNegateUInt(1u) == -1L);
Check("CheckedNarrowNonOverflow", CheckedSamples.CheckedNarrow(200) == 200);
Check("CheckedNarrowFromU64", CheckedSamples.CheckedNarrowFromU64(42ul) == 42);
Check("CheckedBlockNonOverflow",
    CheckedSamples.CheckedBlockSum(new[] { 1, 2, 3 }) == 6);
Check("CheckedByteCompound", CheckedSamples.CheckedByteCompound(200, 50) == 250);
Check("CheckedPrefix", CheckedSamples.CheckedPrefix(41) == 42);
Check("CheckedPostfixReturned", CheckedSamples.CheckedPostfixReturned(42) == 42);
Check("CheckedEnumIncrement",
    CheckedSamples.CheckedEnumIncrement(CountKind.One) == CountKind.Two);

// ── overflowing inputs still throw at runtime through the helpers ──
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

var byteCompoundThrew = false;
try
{
    CheckedSamples.CheckedByteCompound(200, 100);
}
catch (OverflowException)
{
    byteCompoundThrew = true;
}

Check("CheckedByteCompoundThrows", byteCompoundThrew);

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
