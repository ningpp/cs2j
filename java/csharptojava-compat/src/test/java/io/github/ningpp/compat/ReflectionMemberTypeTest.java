package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class ReflectionMemberTypeTest {
    static class Holder {
        public boolean flag;
        public int count;
        public Boolean boxedFlag;

        public boolean getFlag() {
            return flag;
        }

        public int getCount() {
            return count;
        }

        public Boolean getBoxedFlag() {
            return boxedFlag;
        }

        public void setFlag(boolean value) {
            flag = value;
        }

        public void setCount(int value) {
            count = value;
        }
    }

    @Test
    void propertyType_boxesJavaPrimitives() {
        PropertyInfo flag = TypeHelper.getProperty(Holder.class, "Flag");
        PropertyInfo count = TypeHelper.getProperty(Holder.class, "Count");
        PropertyInfo boxed = TypeHelper.getProperty(Holder.class, "BoxedFlag");

        // C# typeof(bool)/typeof(int) map to Boolean.class/Integer.class, so
        // reflection-based member types must match the boxed wrappers.
        assertNotNull(flag);
        assertNotNull(count);
        assertNotNull(boxed);
        assertEquals(Boolean.class, flag.getPropertyType());
        assertEquals(Integer.class, count.getPropertyType());
        assertEquals(Boolean.class, boxed.getPropertyType());
    }

    @Test
    void fieldType_boxesJavaPrimitives() {
        FieldInfo flag = TypeHelper.getField(Holder.class, "flag");
        FieldInfo count = TypeHelper.getField(Holder.class, "count");
        FieldInfo boxed = TypeHelper.getField(Holder.class, "boxedFlag");

        assertNotNull(flag);
        assertNotNull(count);
        assertNotNull(boxed);
        assertEquals(Boolean.class, flag.getFieldType());
        assertEquals(Integer.class, count.getFieldType());
        assertEquals(Boolean.class, boxed.getFieldType());
    }
}
