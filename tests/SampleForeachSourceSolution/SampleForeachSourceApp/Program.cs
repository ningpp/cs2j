using SampleForeachSourceLib;

foreach (var ch in "app")
{
    Console.Write(ch);
}
Console.WriteLine();
Console.WriteLine(string.Concat(ForeachStringSamples.Collect("abc")));
Console.WriteLine(string.Join(",", ForeachStringSamples.CodeUnits("a😊b")));
Console.WriteLine(ForeachStringSamples.EmptyIterations(""));
Console.WriteLine(string.Concat(ForeachStringSamples.Nested(new[] { "ab", "cd" })));
