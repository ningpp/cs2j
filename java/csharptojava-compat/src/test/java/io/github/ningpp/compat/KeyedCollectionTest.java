package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

class KeyedCollectionTest {
    @Test
    void addContainsRemoveAndIterateByDerivedKey() {
        class ItemCollection extends KeyedCollection<String, Item> {
            @Override
            protected String getKeyForItem(Item item) {
                return item.key();
            }
        }

        ItemCollection items = new ItemCollection();
        Item alpha = new Item("a", "alpha");
        Item beta = new Item("b", "beta");

        items.add(alpha);
        items.add(beta);

        assertEquals(2, items.size());
        assertTrue(items.contains("a"));
        assertSame(alpha, items.get("a"));
        assertSame(beta, items.get(1));

        List<String> seen = new ArrayList<>();
        for (Item item : items) {
            seen.add(item.value());
        }
        assertEquals(List.of("alpha", "beta"), seen);

        assertTrue(items.remove(alpha));
        assertFalse(items.contains("a"));
        assertEquals(1, items.getCount());
    }

    private record Item(String key, String value) {
    }
}
