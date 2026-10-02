using System.Collections.Generic;

namespace SampleExpressionDesugarLib;

/// <summary>C24 fixture: object initializer over properties and fields.</summary>
public sealed class InitializerDto
{
    public int Prop { get; set; }

    public int Field;

    public InitializerDto()
    {
    }

    public InitializerDto(int seed)
    {
        Prop = seed;
    }
}

/// <summary>
/// Object-initializer behavior observations. Every method returns a primitive
/// or string so the same assertions can run against the native C# assembly and
/// the preprocessed (C24-desugared) assembly and be compared.
/// </summary>
public static class InitializerDtoScenarios
{
    private static int _next;

    private static int Next() => ++_next;

    /// <summary>Constructor argument is preserved; the member write wins.</summary>
    public static string ConstructArgsPreserved()
    {
        var d = new InitializerDto(41) { Prop = 42 };
        return d.Prop + ":" + d.Field;
    }

    /// <summary>Members assign in source order and each side effect runs once.</summary>
    public static string MemberOrderAndSideEffects()
    {
        _next = 0;
        var d = new InitializerDto { Prop = Next(), Field = Next() };
        return d.Prop + "," + d.Field + "," + _next;
    }

    /// <summary>Empty initializer folds to the constructor call.</summary>
    public static int EmptyInitializer()
    {
        var d = new InitializerDto { };
        return d.Prop;
    }
}
