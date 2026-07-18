package csharp.xunit.Sdk;

import io.github.ningpp.compat.CSharpGenericIterable;
import java.lang.reflect.Method;

public abstract class DataAttribute {
    public abstract CSharpGenericIterable<Object[]> getData(Method testMethod);
}
