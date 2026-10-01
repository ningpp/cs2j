// Unsupported checked site (NB-12): a checked conversion from dynamic cannot
// be explicitized without runtime type knowledge. numeric-boundary must keep
// the original file byte-identical and fail the chain with CS2J5322.
dynamic d = 123.45;
int x = checked((int)d);
Console.WriteLine(x);

// C34 unsupported site (CS2J5361): a user-defined implicit conversion binds
// the built-in decimal operator, but the operand domain cannot be normalized.
// With EnableDecimalOps=true the whole file must be rolled back, byte
// identical, including this untouched expression:
//     decimal y = DecimalUnsupported(1m, default(MoneyLike));
Console.WriteLine(DecimalUnsupported(1m, default(MoneyLike)));

static decimal DecimalUnsupported(decimal a, MoneyLike m) => a + m;

readonly struct MoneyLike
{
    public static implicit operator decimal(MoneyLike value) => 0m;
}
