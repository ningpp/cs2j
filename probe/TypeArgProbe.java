import io.github.ningpp.compat.FieldInfo;
import io.github.ningpp.compat.PropertyInfo;
import io.github.ningpp.compat.TypeHelper;

/**
 * Dumps what the converted runtime can recover about declared member types:
 * PropertyInfo.getPropertyType() erases generics, so converter factories that
 * call TypeHelper.getGenericArguments(raw) only see type-parameter bounds.
 */
public final class TypeArgProbe {
    public static void main(String[] args) throws Exception {
        Class<?> type = Class.forName(args[0]);
        System.out.println("== class " + type.getName());

        for (PropertyInfo property : PropertyInfo.getProperties(type, 54)) {
            java.lang.reflect.Method getter = property.getGetter();
            report(
                property.getName(),
                property.getPropertyType(),
                getter == null ? null : getter.getGenericReturnType());
        }

        for (FieldInfo field : TypeHelper.getFields(type, 54)) {
            report(field.getName(), field.getFieldType(), field.getField().getGenericType());
        }
    }

    private static void report(String name, Class<?> erased, java.lang.reflect.Type declared) {
        Class<?>[] recovered = erased == null ? new Class<?>[0] : TypeHelper.getGenericArguments(erased);
        System.out.printf(
            "%-45s erased=%-38s declared=%-42s recovered=%s%n",
            name,
            erased == null ? "null" : erased.getName(),
            declared,
            java.util.Arrays.toString(recovered));
    }
}
