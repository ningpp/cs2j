using SampleUnsignedLib;

var types = new UnsignedTypes { UIntField = 1u };
var ops = new UnsignedOperators();
uint a = 5;
int b = 3;
uint c = ops.Add(a, (uint)b);

var arrays = new UnsignedArrays();
arrays.Arr[0] = 1u;

var generics = new UnsignedGenerics();
generics.List.Add(c);

var flag = Flag.All;
var statics = new UnsignedStaticMembers();
uint max = statics.Max;

var lambda = new UnsignedLambda();
uint fromLambda = lambda.Func(max);

var refOut = new UnsignedRefOut();
refOut.Increment(ref a);
refOut.TryParse("1", out uint parsed);

var nullable = new UnsignedNullable();
uint? maybe = nullable.Maybe;

Console.WriteLine(c + fromLambda + parsed + (maybe ?? 0u) + (uint)flag + arrays.Arr[0] + generics.List[0]);
