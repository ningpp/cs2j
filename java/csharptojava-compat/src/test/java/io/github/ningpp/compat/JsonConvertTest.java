package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Regression tests for the two JSON compat failures:
 *
 * <ol>
 *   <li>JSON files whose keys are lowerCamelCase ({@code "nodeCount"}) left the
 *       PascalCase Java field {@code NodeCount} at 0, because the snake_case
 *       property naming strategy plus the old alias only covered
 *       {@code node_count} and {@code NodeCount}.</li>
 *   <li>{@link JsonConvert#convertToType} handed a {@link CSharpDictionary}
 *       straight to Jackson; its bean property {@code sync_root} returns
 *       {@code this}, so every retype threw "Direct self-reference leading to
 *       cycle".</li>
 * </ol>
 */
class JsonConvertTest {

    /** Mirrors the C#-shaped reference class the cross-validation tests deserialize. */
    public static class SciPyLike {
        public String Name;
        public int NodeCount;
        public int[][] Edges;
    }

    private static final String CAMEL_CASE_JSON =
            "[{\"name\":\"weighted_diamond\",\"nodeCount\":4,"
                    + "\"edges\":[[0,1,5,1],[0,2,1,1],[1,3,1,1],[2,3,5,1]]}]";

    private static final String SNAKE_CASE_JSON =
            "[{\"name\":\"weighted_diamond\",\"node_count\":4,"
                    + "\"edges\":[[0,1,5,1],[0,2,1,1],[1,3,1,1],[2,3,5,1]]}]";

    private static final String PASCAL_CASE_JSON =
            "[{\"Name\":\"weighted_diamond\",\"NodeCount\":4,"
                    + "\"Edges\":[[0,1,5,1],[0,2,1,1],[1,3,1,1],[2,3,5,1]]}]";

    @SuppressWarnings("unchecked")
    private static CSharpList<SciPyLike> deserialize(String json) {
        return (CSharpList<SciPyLike>) JsonConvert.deserializeList(json, SciPyLike.class);
    }

    private static void assertReference(SciPyLike reference) {
        assertNotNull(reference);
        assertEquals("weighted_diamond", reference.Name);
        assertEquals(4, reference.NodeCount);
        assertNotNull(reference.Edges);
        assertEquals(4, reference.Edges.length);
        assertArrayEquals(new int[] { 0, 1, 5, 1 }, reference.Edges[0]);
        assertArrayEquals(new int[] { 2, 3, 5, 1 }, reference.Edges[3]);
    }

    // ---- deserializeList / deserializeObject ----

    @Test
    void deserializeList_camelCaseKeys_populatePascalCaseFields() {
        CSharpList<SciPyLike> list = deserialize(CAMEL_CASE_JSON);
        assertEquals(1, list.size());
        assertReference(list.get(0));
    }

    @Test
    void deserializeList_snakeCaseKeys_populatePascalCaseFields() {
        CSharpList<SciPyLike> list = deserialize(SNAKE_CASE_JSON);
        assertEquals(1, list.size());
        assertReference(list.get(0));
    }

    @Test
    void deserializeList_pascalCaseKeys_populatePascalCaseFields() {
        CSharpList<SciPyLike> list = deserialize(PASCAL_CASE_JSON);
        assertEquals(1, list.size());
        assertReference(list.get(0));
    }

    @Test
    void deserializeObject_singleObject_camelCaseKeys() {
        SciPyLike reference = (SciPyLike) JsonConvert.deserializeObject(
                CAMEL_CASE_JSON.substring(1, CAMEL_CASE_JSON.length() - 1), SciPyLike.class);
        assertReference(reference);
    }

    // ---- convertToType ----

    @Test
    void convertToType_dictionaryWithoutSyncRootCycle_camelCaseKeys() {
        Object generic = JsonConvert.deserializeObject(CAMEL_CASE_JSON);
        assertNotNull(generic);

        Object retyped = JsonConvert.convertToType(generic, CSharpList.class, SciPyLike.class);
        assertNotNull(retyped);
        CSharpList<?> list = (CSharpList<?>) retyped;
        assertEquals(1, list.size());
        assertReference((SciPyLike) list.get(0));
    }

    @Test
    void convertToType_dictionaryWithoutSyncRootCycle_snakeCaseKeys() {
        Object generic = JsonConvert.deserializeObject(SNAKE_CASE_JSON);
        assertNotNull(generic);

        Object retyped = JsonConvert.convertToType(generic, CSharpList.class, SciPyLike.class);
        assertNotNull(retyped);
        CSharpList<?> list = (CSharpList<?>) retyped;
        assertEquals(1, list.size());
        assertReference((SciPyLike) list.get(0));
    }

    @Test
    void convertToType_nestedDictionaryAndArrays() {
        String json = "[{\"name\":\"nested\",\"nodeCount\":2,"
                + "\"edges\":[[0,1,3,0]],\"meta\":{\"weight\":2.5}}]";
        Object generic = JsonConvert.deserializeObject(json);

        Object retyped = JsonConvert.convertToType(generic, CSharpList.class, Nested.class);
        assertNotNull(retyped);
        CSharpList<?> list = (CSharpList<?>) retyped;
        assertEquals(1, list.size());
        Nested nested = (Nested) list.get(0);
        assertEquals("nested", nested.Name);
        assertEquals(2, nested.NodeCount);
        assertArrayEquals(new int[] { 0, 1, 3, 0 }, nested.Edges[0]);
        assertEquals(2.5, nested.Meta.Weight, 1e-9);
    }

    /** CamelCase nested object with a double member. */
    public static class Nested {
        public String Name;
        public int NodeCount;
        public int[][] Edges;
        public Meta Meta;
    }

    public static class Meta {
        public double Weight;
    }
}
