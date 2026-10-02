using SampleLexicalNormalizeLib;

var item = new FileScopedFile { Id = 3, Name = "x" };
Console.WriteLine(LineAndUsings.Describe(item.Id));
Console.WriteLine(item.Name);
