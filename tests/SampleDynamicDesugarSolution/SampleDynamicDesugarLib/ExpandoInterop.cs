namespace SampleDynamicDesugarLib;

/// <summary>
/// DS017 boundary: System.Dynamic.ExpandoObject has no Java equivalent. No
/// Microsoft.CSharp PackageReference is needed on the modern .NET SDK (the
/// runtime binder ships in the shared framework); the site is detected via the
/// real BCL symbol.
/// </summary>
public static class ExpandoInterop
{
    public static string ExpandoName()
    {
        var expando = new System.Dynamic.ExpandoObject();

        // Static population path (IDictionary<string, object>).
        ((IDictionary<string, object>)expando)["Name"] = "expando-ada";

        // Dynamic read path: DS016 (dynamic cast) + DS013 (dynamic member access).
        return ((dynamic)expando).Name;
    }
}
