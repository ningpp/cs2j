using SampleRefOutInV2Lib;
using System.Collections.Generic;

var calc = new Calculator();

// ref param call site
int a = 1, b = 2;
Calculator.Swap(ref a, ref b);

// out param call site
if (Calculator.TryParse("test", out var p))
{
    Console.WriteLine($"Point: {p.X}, {p.Y}");
}

// out int call site
if (calc.TryGet(out int result))
{
    Console.WriteLine($"Result: {result}");
}

// ref field on generic
var holder = new GenericHolder<int>();
holder.Value = 10;
holder.Update(ref holder.Value);

// External BCL ref/out preserved
var dict = new Dictionary<string, int>();
dict.TryGetValue("key", out int v);

// Array element ref
int[] arr = { 1, 2, 3 };
calc.UpdateField(ref arr[0]);

// int.TryParse (external)
int.TryParse("42", out int parsed);

// ref return + ref local rebinding
ref int slot = ref calc.GetRef(arr, 1);
slot = 99;
slot = ref arr[2];
slot = 42;
