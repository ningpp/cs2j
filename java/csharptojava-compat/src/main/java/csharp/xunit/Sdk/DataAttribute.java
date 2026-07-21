package csharp.xunit.Sdk;

import io.github.ningpp.compat.CSharpGenericIterable;
import io.github.ningpp.compat.MethodInfo;

public abstract class DataAttribute {
    public abstract CSharpGenericIterable<Object[]> getData(MethodInfo testMethod);
}
