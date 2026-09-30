using SampleEnumDeclarationLib;

var c = Color.Red | Color.Blue;
Console.WriteLine(c.ToString());
Console.WriteLine(EnumSamples.Describe(c));
Console.WriteLine(EnumSamples.Rank(Code.NotFound));
Console.WriteLine(EnumSamples.IsNormal(Priority.Normal));
