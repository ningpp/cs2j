package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;

import org.junit.jupiter.api.Test;

/// Reproduces Regex.java: 'new CSharpHashtable(value)' where value is a
/// CSharpIDictionary (converted from C# 'new Hashtable(IDictionary)' copy-ctor).
/// Before the fix there was no CSharpHashtable(CSharpIDictionary) constructor,
/// so javac reported "no suitable constructor".
class CSharpHashtableIDictionaryCtorTest {
    @Test
    void copiesEntriesFromIDictionary() {
        CSharpHashtable source = new CSharpHashtable();
        source.add("a", 1);
        source.add("b", 2);
        CSharpIDictionary asDict = source;

        CSharpHashtable copy = new CSharpHashtable(asDict);

        assertEquals(2, copy.getCount());
        assertEquals(1, copy.get("a"));
        assertEquals(2, copy.get("b"));
    }
}
