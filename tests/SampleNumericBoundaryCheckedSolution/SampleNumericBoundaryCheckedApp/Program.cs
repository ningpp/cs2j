// Project-wide CheckForOverflowUnderflow=true: numeric-boundary rewrites the
// whole project to Cs2jUnsigned.UnsignedMath checked helpers (the
// CheckForOverflowUnderflow property is removed from the intermediate csproj).
// Non-overflowing arithmetic keeps its value; overflow still throws at runtime.
var values = new[] { 1, 2, 3 };
var sum = 0;
foreach (var v in values)
{
    sum += v;
}

Console.WriteLine(sum);

// Overflow path: int.MaxValue + 1 must throw OverflowException (.NET),
// which maps to ArithmeticException on the Java side.
var overflowValues = new[] { int.MaxValue, 1 };
var bad = 0;
foreach (var v in overflowValues)
{
    bad += v;
}

Console.WriteLine(bad);
