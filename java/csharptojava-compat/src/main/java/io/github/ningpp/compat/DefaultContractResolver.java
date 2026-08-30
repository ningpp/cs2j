package io.github.ningpp.compat;

/**
 * Compat stub for Newtonsoft.Json.Serialization.DefaultContractResolver.
 * Sufficient for converted test code that constructs it and sets NamingStrategy.
 */
public class DefaultContractResolver {
    public Object namingStrategy;

    public DefaultContractResolver() {
    }

    public void setNamingStrategy(Object namingStrategy) {
        this.namingStrategy = namingStrategy;
    }

    public Object getNamingStrategy() {
        return namingStrategy;
    }
}
