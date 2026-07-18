package io.github.ningpp.compat;

public final class CustomAttributeExtensions {
    private CustomAttributeExtensions() {
    }

    public static Iterable<Object> getCustomAttributes(MemberInfo member) {
        if (member == null) {
            return java.util.List.of();
        }
        Object[] attrs = member.getCustomAttributes(true);
        return attrs == null ? java.util.List.of() : java.util.Arrays.asList(attrs);
    }
}
