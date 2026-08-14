using SampleUnsignedLib;

var types = new UnsignedTypes { UIntField = 1u };
var ops = new UnsignedOperators();
uint sum = ops.Add(types.UIntField, 2u);
var casts = new UnsignedCasts();
int i = casts.ToInt32(sum);
Console.WriteLine(i);
