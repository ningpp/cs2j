package io.github.ningpp.compat;

import java.util.HashMap;
import java.util.Map;

/**
 * Minimal compat stub for System.AppDomain.
 * Mirrors the API surface used by CorrectnessTest.
 */
public class AppDomain {
    private static final AppDomain currentDomain = new AppDomain();
    private final Map<String, Object> data = new HashMap<>();

    private AppDomain() {
    }

    /** Mirrors C# AppDomain.CurrentDomain */
    public static AppDomain getCurrentDomain() {
        return currentDomain;
    }

    /** Mirrors C# AppDomain.BaseDirectory — returns current working directory */
    public String getBaseDirectory() {
        return System.getProperty("user.dir");
    }

    /** Mirrors C# AppDomain.CurrentDomain.GetAssemblies() */
    public AssemblyCompat[] getAssemblies() {
        return new AssemblyCompat[0];
    }

    /** Mirrors C# AppDomain.GetData(String) */
    public Object getData(String name) {
        return data.get(name);
    }

    /** Mirrors C# AppDomain.SetData(String, Object) */
    public void setData(String name, Object value) {
        data.put(name, value);
    }
}
