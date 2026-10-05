namespace SampleDynamicDesugarLib;

/// <summary>DS012 dynamic property storage (field/property storage is never auto-rewritten).</summary>
public static class DynamicStorage
{
    public static dynamic Bag { get; set; }

    /// <summary>Drives the property through static int in/out: the set is a
    /// DS016 boundary (int -> dynamic) and the get uses a DS002 explicit cast.</summary>
    public static int StoreAndRead(int input)
    {
        Bag = input;
        return (int)Bag + 1;
    }
}
