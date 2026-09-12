import dotnet.text.json.Serialization.JsonIncludeAttribute;
import dotnet.text.json.Serialization.Tests.PropertyVisibilityTests;
import io.github.ningpp.compat.CustomAttributeExtensions;
import io.github.ningpp.compat.PropertyInfo;

/** Does the member lookup see [JsonInclude] now? */
public class IncludeAttrProbe {

    public static void main(String[] args) throws Exception {
        Class<?> type = Class.forName(
            "dotnet.text.json.Serialization.Tests.PropertyVisibilityTests$ClassWithPrivateProperty_WithJsonIncludeProperty");
        for (PropertyInfo property : PropertyInfo.getProperties(type, 54)) {
            System.out.println("property = " + property.getName()
                + " getter=" + property.getGetter()
                + " public=" + (property.getGetMethod() != null && property.getGetMethod().getIsPublic()));
            if (property.getGetter() != null) {
                System.out.println("  @CompatJsonInclude present = "
                    + property.getGetter().isAnnotationPresent(io.github.ningpp.compat.CompatJsonInclude.class));
            }
            Object attr = CustomAttributeExtensions.getCustomAttribute(JsonIncludeAttribute.class, property);
            System.out.println("  getCustomAttribute(JsonIncludeAttribute) = " + attr);
            Object viaPropertyInfo = JsonIncludeAttribute.class.cast(attr);
            System.out.println("  cast ok = " + (viaPropertyInfo != null));
        }
    }
}
