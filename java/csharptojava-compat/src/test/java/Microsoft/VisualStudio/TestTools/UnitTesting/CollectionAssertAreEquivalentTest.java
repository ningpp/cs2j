package Microsoft.VisualStudio.TestTools.UnitTesting;

import io.github.ningpp.compat.CSharpList;
import io.github.ningpp.compat.ArrayHelper;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertThrows;

class CollectionAssertAreEquivalentTest {

    @Test
    void contains_andAreNotEqual_acceptCompatCollections() {
        var expected = new io.github.ningpp.compat.CSharpList<Integer>();
        expected.add(1);
        expected.add(2);
        var actual = new io.github.ningpp.compat.CSharpList<Integer>();
        actual.add(1);
        actual.add(3);

        CollectionAssert.contains(expected, 2);
        CollectionAssert.areNotEqual(expected, actual);
    }

    @Test
    void areEquivalent_IgnoresOrder() {
        CSharpList<String> expected = ArrayHelper.toList(new String[] {"a", "b", "c"});
        CSharpList<String> actual = ArrayHelper.toList(new String[] {"c", "a", "b"});
        CollectionAssert.areEquivalent(expected, actual);
    }

    @Test
    void areEquivalent_FailsOnDifferentCounts() {
        CSharpList<String> expected = ArrayHelper.toList(new String[] {"a", "b"});
        CSharpList<String> actual = ArrayHelper.toList(new String[] {"a"});
        assertThrows(AssertionError.class, () -> CollectionAssert.areEquivalent(expected, actual));
    }
}
