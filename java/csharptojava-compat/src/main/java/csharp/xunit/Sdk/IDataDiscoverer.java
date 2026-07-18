package csharp.xunit.Sdk;

import csharp.xunit.Abstractions.IAttributeInfo;
import csharp.xunit.Abstractions.IMethodInfo;
import io.github.ningpp.compat.CSharpGenericIterable;

public interface IDataDiscoverer {
    CSharpGenericIterable<Object[]> getData(IAttributeInfo dataAttribute, IMethodInfo testMethod);

    boolean supportsDiscoveryEnumeration(IAttributeInfo dataAttribute, IMethodInfo testMethod);
}
