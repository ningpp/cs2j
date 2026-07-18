package csharp.xunit.Abstractions;

import io.github.ningpp.compat.CSharpGenericIterable;

public interface IDataDiscoverer {
    CSharpGenericIterable<Object[]> getData(IAttributeInfo dataAttribute, IMethodInfo testMethod);

    boolean supportsDiscoveryEnumeration(IAttributeInfo dataAttribute, IMethodInfo testMethod);
}
