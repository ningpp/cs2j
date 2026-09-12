import io.github.ningpp.compat.CSharpGenericIDictionary;
import io.github.ningpp.compat.FieldInfo;
import io.github.ningpp.compat.TypeHelper;

/** Prints the declared type the extension-data check sees for a field member. */
public final class ExtDataProbe {
    public static void main(String[] args) throws Exception {
        Class<?> type = Class.forName(args[0]);
        String wanted = args[1];
        for (FieldInfo field : TypeHelper.getFields(type, 0xFFFF)) {
            if (!field.getName().equalsIgnoreCase(wanted)) continue;
            Class<?> declared = field.getFieldType();
            System.out.println("field         = " + field.getName());
            System.out.println("declaredType  = " + declared);
            System.out.println("assignable?   = " + TypeHelper.isAssignableFrom(CSharpGenericIDictionary.class, declared));
            System.out.println("interfaces    = " + (declared == null ? "-" : java.util.Arrays.toString(declared.getInterfaces())));
        }
    }
}