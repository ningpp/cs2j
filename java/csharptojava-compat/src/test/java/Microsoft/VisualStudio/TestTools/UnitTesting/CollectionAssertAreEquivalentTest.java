package Microsoft.VisualStudio.TestTools.UnitTesting;

import io.github.ningpp.compat.CSharpList;
import io.github.ningpp.compat.ArrayHelper;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertThrows;

class CollectionAssertAreEquivalentTest {

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
