package io.github.ningpp.compat;

import java.lang.reflect.Field;
import java.lang.reflect.Modifier;

public final class FieldInfo implements MemberInfo {
    private final Field field;

    public FieldInfo(Field field) {
        this.field = field;
        this.field.setAccessible(true);
    }

    public Field getField() {
        return field;
    }

    @Override
    public String getName() {
        return field.getName();
    }

    @Override
    public Class<?> getDeclaringType() {
        return field.getDeclaringClass();
    }

    public Class<?> getFieldType() {
        // Declared member types carry the generic arguments written in the converted
        // source (e.g. CSharpDictionary<String, String>); the erased class alone makes
        // converter factories fall back to type-parameter bounds (Object key/element
        // types). Publish them before handing back the raw class.
        TypeHelper.registerMemberType(field.getGenericType(), this);
        // C# sbyte[] and byte[] are both byte[] in Java but serialize differently (JSON array
        // of numbers vs. Base64 string), so a marked field answers int[].class.
        if (field.isAnnotationPresent(CompatSByteArray.class)) {
            return int[].class;
        }
        return TypeHelper.toWrapperType(field.getType());
    }

    /** The declared field type without boxing; see {@link PropertyInfo#getRawPropertyType()}. */
    public Class<?> getRawFieldType() {
        if (field.isAnnotationPresent(CompatSByteArray.class)) {
            return int[].class;
        }
        return field.getType();
    }

    /**
     * The field's DECLARED closed generic type, without any side effect — the closed-type
     * chain's counterpart of {@link #getFieldType()}, which publishes member layers to the
     * ThreadLocal registry. See {@link PropertyInfo#getGenericPropertyType()}.
     */
    public java.lang.reflect.Type getGenericType() {
        return field.getGenericType();
    }

    public boolean getIsStatic() {
        return Modifier.isStatic(field.getModifiers());
    }

    public boolean getIsPublic() {
        // C#'s `internal` is emitted as Java `public` for cross-package reach, so the modifier alone
        // cannot answer this; the converter marks the field (see CompatNonPublicProperty).
        return Modifier.isPublic(field.getModifiers())
            && !field.isAnnotationPresent(CompatNonPublicProperty.class);
    }

    public boolean getIsInitOnly() {
        return Modifier.isFinal(field.getModifiers());
    }

    public boolean getIsSpecialName() {
        return false;
    }

    public Object getValue(Object obj) {
        try {
            return field.get(obj);
        } catch (IllegalAccessException e) {
            throw new RuntimeException(e);
        }
    }

    public void setValue(Object obj, Object value) {
        try {
            field.set(obj, value);
        } catch (IllegalAccessException e) {
            throw new RuntimeException(e);
        }
    }

    /**
     * C# {@code MemberInfo.CustomAttributes} — the no-ARGUMENT overload answers
     * {@link CustomAttributeData} metadata, and the ported debugger helper reads
     * {@code [DebuggerBrowsable]} through exactly this overload:
     * {@code DebuggerAttributes.getDebuggerBrowsableState(MemberInfo)} calls
     * {@code info.getCustomAttributes()} and then reads the attribute's constructor arguments.
     *
     * <p>Only the attribute-OBJECT overloads were implemented, so this one fell through to the
     * {@link ICustomAttributeProvider} default and answered an EMPTY sequence: the RootHidden field
     * could never be found and the caller got a null {@code FieldInfo} even though the field really
     * carries {@code @DebuggerBrowsable(3)} (measured: {@code ImmutableArrayTest.debuggerAttributesValid}
     * died with {@code "itemField" is null}). Mirrors {@link PropertyInfo#getCustomAttributes()}.</p>
     */
    @Override
    public CSharpGenericIterable<CustomAttributeData> getCustomAttributes() {
        DebuggerBrowsable browsable = field.getAnnotation(DebuggerBrowsable.class);
        if (browsable == null) {
            return CSharpGenericIterable.from(new java.util.ArrayList<>());
        }
        CSharpList<CustomAttributeTypedArgument> args = new CSharpList<>();
        args.add(new CustomAttributeTypedArgument(Integer.class, browsable.value()));
        CSharpList<CustomAttributeData> result = new CSharpList<>();
        result.add(new CustomAttributeData(DebuggerBrowsableAttribute.class, args));
        return CSharpGenericIterable.from(result);
    }

    @Override
    public Object[] getCustomAttributes(boolean inherit) {
        Object[] annotations = field.getAnnotations();
        // A field-level [JsonNumberHandling] overrides the type's; the generated attribute is
        // a class, so it is materialized from the compat marker instead of being an annotation.
        Object numberHandlingAttribute = TypeHelper.materializeMemberNumberHandlingAttribute(
            field.getAnnotation(CompatJsonNumberHandling.class));
        // [DebuggerBrowsable] is materialized the same way (it is a compat CLASS too), so a
        // field-level RootHidden state is visible to an unfiltered CustomAttributes query.
        DebuggerBrowsable browsable = field.getAnnotation(DebuggerBrowsable.class);
        Object browsableAttribute = browsable == null ? null : new DebuggerBrowsableAttribute(browsable.value());
        if (numberHandlingAttribute == null && browsableAttribute == null) {
            return annotations;
        }
        java.util.List<Object> combined = new java.util.ArrayList<>(annotations.length + 2);
        combined.addAll(java.util.Arrays.asList(annotations));
        if (numberHandlingAttribute != null) {
            combined.add(numberHandlingAttribute);
        }
        if (browsableAttribute != null) {
            combined.add(browsableAttribute);
        }
        return combined.toArray();
    }

    @Override
    public Object[] getCustomAttributes(Class<?> attributeType, boolean inherit) {
        if (attributeType == null) {
            return new Object[0];
        }
        if ("JsonNumberHandlingAttribute".equals(attributeType.getSimpleName())) {
            Object materialized = TypeHelper.materializeMemberNumberHandlingAttribute(
                field.getAnnotation(CompatJsonNumberHandling.class));
            return materialized == null ? new Object[0] : new Object[] { materialized };
        }
        if (attributeType == DebuggerBrowsableAttribute.class) {
            // Like JsonNumberHandlingAttribute above, this attribute is a compat CLASS rather than a
            // Java annotation, so the generic reflection path below would answer an EMPTY array for
            // it (it returns empty for every non-annotation type). The field really carries
            // @DebuggerBrowsable, so materialise it. Measured: without this branch
            // `DebuggerAttributes.getDebuggerBrowsableState(fieldInfo)` always answered null, so the
            // RootHidden field could not be found and ImmutableArrayTest.debuggerAttributesValid
            // died with `"itemField" is null` (ImmutableHashSetTest diverged the same way).
            DebuggerBrowsable browsable = field.getAnnotation(DebuggerBrowsable.class);
            return browsable == null
                ? new Object[0]
                : new Object[] { new DebuggerBrowsableAttribute(browsable.value()) };
        }
        if (!attributeType.isAnnotation()) {
            // Converted C# attributes (e.g. JsonConverterAttribute) are plain Java
            // classes, not annotations. Java's getAnnotationsByType rejects
            // non-annotation types with ClassCastException, so return empty instead.
            return new Object[0];
        }
        return field.getAnnotationsByType((Class) attributeType);
    }

    public static FieldInfo[] getFields(Class<?> clazz) {
        Field[] fields = clazz.getFields();
        FieldInfo[] result = new FieldInfo[fields.length];
        for (int i = 0; i < fields.length; i++) {
            result[i] = new FieldInfo(fields[i]);
        }
        return result;
    }
}
