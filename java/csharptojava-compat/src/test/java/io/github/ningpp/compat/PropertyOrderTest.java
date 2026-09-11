package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

/**
 * C# reflection returns properties/fields in <em>metadata declaration order</em>, and the
 * JSON serializer builds its property cache straight from
 * {@code PropertyInfo.getProperties} (insertion-ordered map), so the JSON member order of
 * every serialized object depends on this order.
 *
 * <p>{@code Class#getMethods()} does not guarantee any order and HotSpot's order changes
 * between runs of the same JVM (observed: {@code [getValue1, getValue2]} in one run,
 * {@code [getValue2, getValue1]} in the next), which made order-asserting tests such as
 * {@code dotnet.text.json.Tests.EnumConverterTests.serializeNegativeEnum} flaky. The order
 * must therefore come from the class file's member table, which javac writes in source
 * declaration order.</p>
 */
class PropertyOrderTest {

    /** Deliberately not alphabetical and not hash-ordered: declaration order is the contract. */
    @SuppressWarnings("unused")
    static class ScrambledProperties {
        private int delta;
        private int alpha;
        private int zulu;
        private int mike;
        private int bravo;
        private int yankee;
        private int charlie;
        private int xray;

        public int getDelta() { return delta; }
        public int getAlpha() { return alpha; }
        public int getZulu() { return zulu; }
        public int getMike() { return mike; }
        public int getBravo() { return bravo; }
        public int getYankee() { return yankee; }
        public int getCharlie() { return charlie; }
        public int getXray() { return xray; }
    }

    @SuppressWarnings("unused")
    static class BaseProperties {
        private int baseOne;
        private int baseTwo;

        public int getBaseOne() { return baseOne; }
        public int getBaseTwo() { return baseTwo; }
    }

    @SuppressWarnings("unused")
    static class DerivedProperties extends BaseProperties {
        private int derivedOne;
        private int derivedTwo;

        public int getDerivedOne() { return derivedOne; }
        public int getDerivedTwo() { return derivedTwo; }
    }

    private static List<String> propertyNames(Class<?> type) {
        List<String> names = new ArrayList<>();
        for (PropertyInfo property : PropertyInfo.getProperties(type)) {
            names.add(property.getName());
        }
        return names;
    }

    @Test
    void getProperties_returnsSourceDeclarationOrder() {
        assertEquals(
            List.of("Delta", "Alpha", "Zulu", "Mike", "Bravo", "Yankee", "Charlie", "Xray"),
            propertyNames(ScrambledProperties.class));
    }

    @Test
    void getProperties_returnsDeclaredMembersBeforeInheritedOnes() {
        assertEquals(
            List.of("DerivedOne", "DerivedTwo", "BaseOne", "BaseTwo"),
            propertyNames(DerivedProperties.class));
    }

    @Test
    void getProperties_isStableAcrossRepeatedCalls() {
        for (int i = 0; i < 25; i++) {
            assertEquals(
                List.of("Delta", "Alpha", "Zulu", "Mike", "Bravo", "Yankee", "Charlie", "Xray"),
                propertyNames(ScrambledProperties.class));
        }
    }

    @Test
    void getFields_returnsSourceDeclarationOrder() {
        List<String> names = new ArrayList<>();
        for (FieldInfo field : TypeHelper.getFields(PublicFieldHolder.class, 0xFFFF)) {
            names.add(field.getName());
        }
        assertEquals(List.of("zeta", "alpha", "mike"), names);
    }

    @SuppressWarnings("unused")
    public static class PublicFieldHolder {
        public int zeta;
        public int alpha;
        public int mike;
    }

    @SuppressWarnings("unused")
    public static class DeclaredMembers {
        public void zeta() { }
        public void alpha() { }
        public static void mike() { }
        public void alpha(int overload) { }
    }

    @Test
    void getMethods_returnsSourceDeclarationOrderForDeclaredMembers() {
        List<String> names = new ArrayList<>();
        for (MethodInfo method : TypeHelper.getMethods(DeclaredMembers.class, 0xFFFF)) {
            if (method.getDeclaringType() == DeclaredMembers.class) {
                names.add(method.getName() + "/" + method.getMethod().getParameterCount());
            }
        }
        assertEquals(List.of("zeta/0", "alpha/0", "mike/0", "alpha/1"), names);
    }

    @SuppressWarnings("unused")
    public static class TwoConstructors {
        public final int kind;

        public TwoConstructors(String value) { this.kind = 1; }
        public TwoConstructors(int value) { this.kind = 2; }
        public TwoConstructors() { this.kind = 3; }
    }

    @Test
    void getConstructors_returnsSourceDeclarationOrder() {
        List<Class<?>> signature = new ArrayList<>();
        for (ConstructorInfo constructor : TypeHelper.getConstructors(TwoConstructors.class, 0xFFFF)) {
            Class<?>[] parameters = constructor.getConstructor().getParameterTypes();
            signature.add(parameters.length == 0 ? void.class : parameters[0]);
        }
        assertEquals(List.of(String.class, int.class, void.class), signature);
    }
}
