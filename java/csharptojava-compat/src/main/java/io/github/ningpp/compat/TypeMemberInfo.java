package io.github.ningpp.compat;

/**
 * Bridges a Java {@link Class} to the compat {@link MemberInfo} interface.
 * C# System.Type derives from System.Reflection.MemberInfo, but the Java mapping
 * for System.Type is java.lang.Class, which does not implement MemberInfo.
 * Generated code that passes a Type where MemberInfo is expected must wrap the
 * Class through this helper.
 */
public final class TypeMemberInfo implements MemberInfo {
    private final Class<?> type;

    public TypeMemberInfo(Class<?> type) {
        this.type = type;
    }

    @Override
    public String getName() {
        return type == null ? null : type.getName();
    }

    @Override
    public Class<?> getDeclaringType() {
        return type == null ? null : type.getDeclaringClass();
    }

    @Override
    @SuppressWarnings("unchecked")
    public Object[] getCustomAttributes(boolean inherit) {
        return type == null ? new Object[0] : type.getAnnotations();
    }

    @Override
    @SuppressWarnings("unchecked")
    public Object[] getCustomAttributes(Class<?> attributeType, boolean inherit) {
        if (type == null || attributeType == null) {
            return new Object[0];
        }
        return type.getAnnotationsByType((Class) attributeType);
    }
}
