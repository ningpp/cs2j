package csharp.xunit.Abstractions;

import io.github.ningpp.compat.MethodInfo;

public interface IReflectionMethodInfo extends IMethodInfo {
    MethodInfo getMethodInfo();
}
