using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

public class StructClonePatchTests
{
    private static ProjectPassState CreateState(ConversionResult result)
    {
        var compilation = CSharpCompilation.Create("TestAssembly");
        var library = Cs2jLibraryFactory.CreateFromCompilation(compilation);
        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };
        var context = new ConversionContext(options, new CSharpToJava.TypeMapping.TypeMappingRegistry((string?)null));

        var state = new ProjectPassState
        {
            Library = library,
            Context = context,
            Compilation = compilation,
        };
        state.Results.Add(result);
        return state;
    }

    [Fact]
    public void PushdownPrefixState_ClonePatch_AddedToPushMethod()
    {
        var code = "\npublic class PushdownPrefixState<T> {\n" +
                   "    public void push(T value) {\n" +
                   "        try {\n" +
                   "            this.array[this.tos++] = value;\n" +
                   "        } catch (Exception _e_cs2j) {\n" +
                   "            throw new RuntimeException(_e_cs2j);\n" +
                   "        }\n" +
                   "    }\n" +
                   "}";

        var result = new ConversionResult { FileName = "PushdownPrefixState.java", GeneratedCode = code };
        var state = CreateState(result);
        new ProjectStructClonePatchPass().Execute(state);

        Assert.Contains("clonePushValue(value)", result.GeneratedCode);
        Assert.Contains("private T clonePushValue(T value)", result.GeneratedCode);
        Assert.DoesNotContain("this.array[this.tos++] = value;", result.GeneratedCode);
    }

    [Fact]
    public void PushdownPrefixState_NotMatchingFile_Untouched()
    {
        var code = "public class OtherState<T> { public void push(T v) { this.arr[i++] = v; } }";
        var result = new ConversionResult { FileName = "OtherState.java", GeneratedCode = code };
        var state = CreateState(result);
        new ProjectStructClonePatchPass().Execute(state);
        Assert.Equal(code, result.GeneratedCode);
    }

    [Fact]
    public void PushdownPrefixState_WindowsCrlf_Handled()
    {
        var code = "public class PushdownPrefixState<T> {\r\n" +
                   "    public void push(T value) {\r\n" +
                   "        try {\r\n" +
                   "            this.array[this.tos++] = value;\r\n" +
                   "        } catch (Exception _e_cs2j) {\r\n" +
                   "            throw new RuntimeException(_e_cs2j);\r\n" +
                   "        }\r\n" +
                   "    }\r\n" +
                   "}";
        var result = new ConversionResult { FileName = "PushdownPrefixState.java", GeneratedCode = code };
        var state = CreateState(result);
        new ProjectStructClonePatchPass().Execute(state);
        Assert.Contains("clonePushValue(value)", result.GeneratedCode);
    }

    [Fact]
    public void AttributeValuePair_EnumCast_FixedToDirectCast()
    {
        var code = @"
public class AttributeValuePair {
    void f() {
        couple.getValue().setLayerDirection(LayerDirection.values()[(int)(attrVal.val)]);
        edgeAttr.setArrowheadAtTarget(ArrowStyle.values()[(int)(attrVal.val)]);
        edgeAttr.setArrowheadAtSource(ArrowStyle.values()[(int)(attrVal.val)]);
        if (EdgeDirection.values()[(int)(attrVal.val)] == EdgeDirection.Both) { }
        nodeAttr.setShape(Microsoft.Msagl.Drawing.Shape.values()[(int)(attrVal.val)]);
    }
}";
        var result = new ConversionResult { FileName = "AttributeValuePair.java", GeneratedCode = code };
        var state = CreateState(result);
        new ProjectStructClonePatchPass().Execute(state);

        Assert.DoesNotContain("values()[(int)(attrVal.val)]", result.GeneratedCode);
        Assert.Contains("(LayerDirection) attrVal.val", result.GeneratedCode);
        Assert.Contains("(ArrowStyle) attrVal.val", result.GeneratedCode);
        Assert.Contains("(EdgeDirection) attrVal.val", result.GeneratedCode);
        Assert.Contains("(Microsoft.Msagl.Drawing.Shape) attrVal.val", result.GeneratedCode);
    }
}
