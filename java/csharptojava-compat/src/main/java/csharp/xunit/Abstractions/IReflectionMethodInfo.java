package csharp.xunit.Abstractions;

import java.lang.reflect.Method;

public interface IReflectionMethodInfo extends IMethodInfo {
    Method getMethodInfo();
}
