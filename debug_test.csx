using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Context;

var pipeline = new ConversionPipeline();
var result = pipeline.Convert(new ConversionRequest
{
    SourceCode = @""
using System.Collections.Generic;
using System.Linq;
class IntPair { public int X { get; set; } public int Y { get; set; } }
class Edge { }
class Database
{
    Dictionary<IntPair, List<Edge>> multiedges = new Dictionary<IntPair, List<Edge>>();
    public Dictionary<IntPair, List<Edge>> Multiedges { get { return this.multiedges; } }
    internal IEnumerable<Edge> SkeletonEdges()
    {
        return from kv in Multiedges where kv.Key.X != kv.Key.Y select kv.Value[0];
    }
}"",
    FileName = ""Test.cs"",
    Options = new ConversionOptions(),
});
Console.WriteLine(result.GeneratedCode);
