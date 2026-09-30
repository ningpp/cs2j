// Unsupported checked site (NB-12): a checked conversion from dynamic cannot
// be explicitized without runtime type knowledge. numeric-boundary must keep
// the original file byte-identical and fail the chain with CS2J5322.
dynamic d = 123.45;
int x = checked((int)d);
Console.WriteLine(x);
