package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import org.junit.jupiter.api.Test;

/// Non-generic C# IDictionary.GetEnumerator() yields DictionaryEntry values,
/// which convert to CSharpDictEntry. The CSharpIDictionary.from(generic dict)
/// adapter must therefore present CSharpDictEntry elements, not the generic
/// CSharpKeyValuePair elements of the underlying dictionary.
class CSharpIDictionaryAdapterIteratorTest {
    @Test
    void iteratorYieldsCSharpDictEntry() {
        CSharpDictionary<String, String> dict = new CSharpDictionary<>();
        dict.put("key", "value");
        CSharpIDictionary asDict = CSharpIDictionary.from(dict);

        CSharpGenericEnumerator<Object> enumerator = asDict.iterator();
        assertTrue(enumerator.moveNext());
        assertTrue(enumerator.getCurrent() instanceof CSharpDictEntry);

        CSharpDictEntry entry = (CSharpDictEntry) enumerator.getCurrent();
        assertEquals("key", entry.getKey());
        assertEquals("value", entry.getValue());
    }
}
