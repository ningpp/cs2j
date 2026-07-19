package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;

class CSharpCollectionBaseTest {

    static class TestCollection extends CSharpCollectionBase {
        public CSharpArrayList exposeList() {
            return getList();
        }

        public CSharpArrayList exposeInnerList() {
            return getInnerList();
        }
    }

    @Test
    void getListReturnsContents() {
        TestCollection coll = new TestCollection();
        coll.add("a");
        coll.add("b");
        CSharpArrayList list = coll.exposeList();
        assertEquals(2, list.size());
        assertEquals("a", list.get(0));
    }
}
