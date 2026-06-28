package io.github.ningpp.compat;

public final class CustomAttributeExtensions {
    private CustomAttributeExtensions() {
    }

    public static Iterable<Object> getCustomAttributes(MemberInfo member) {
        return member == null ? java.util.List.of() : member.getCustomAttributes(true);
    }
}
