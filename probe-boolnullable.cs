using System;
public class TypeCheck
{
    public static bool IsMatch(Type type)
    {
        Type? stackType = Type.GetType("System.Collections.Stack, System.Collections.NonGeneric");
        return stackType?.IsAssignableFrom(type) == true;
    }
}
