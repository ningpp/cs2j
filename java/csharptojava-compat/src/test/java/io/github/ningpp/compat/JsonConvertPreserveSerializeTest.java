package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

/**
 * JsonConvert.serializeObject is the Newtonsoft.Json parity oracle for the
 * serializer tests: ReferenceHandlerTests compares its output with
 * dotnet.text.json.JsonSerializer output. With
 * PreserveReferencesHandling.All + ReferenceLoopHandling.Serialize it must
 * emit real reference metadata ($id/$ref/$values, ids in discovery order)
 * exactly like Newtonsoft does — not a quoted toString() of the value.
 */
public class JsonConvertPreserveSerializeTest {

    public static class Employee {
        private String name;
        private Employee manager;
        private CSharpList<Employee> subordinates;
        private CSharpDictionary<String, Employee> contacts;

        public String getName() {
            return name;
        }

        public void setName(String value) {
            this.name = value;
        }

        public Employee getManager() {
            return manager;
        }

        public void setManager(Employee value) {
            this.manager = value;
        }

        public CSharpList<Employee> getSubordinates() {
            return subordinates;
        }

        public void setSubordinates(CSharpList<Employee> value) {
            this.subordinates = value;
        }

        public CSharpDictionary<String, Employee> getContacts() {
            return contacts;
        }

        public void setContacts(CSharpDictionary<String, Employee> value) {
            this.contacts = value;
        }
    }

    private static JsonSerializerSettings preserveSettings() {
        JsonSerializerSettings settings = new JsonSerializerSettings();
        settings.setPreserveReferencesHandling(PreserveReferencesHandling.All);
        settings.setReferenceLoopHandling(ReferenceLoopHandling.Serialize);
        return settings;
    }

    @Test
    public void scalarValuesKeepLegacyOutput() {
        assertEquals("null", JsonConvert.serializeObject(null));
        assertEquals("\"Bob\"", JsonConvert.serializeObject("Bob"));
        assertEquals("100", JsonConvert.serializeObject(100));
        assertEquals("true", JsonConvert.serializeObject(Boolean.TRUE));
    }

    @Test
    public void objectLoopEmitsIdAndRef() {
        Employee angela = new Employee();
        angela.setManager(angela);
        assertEquals(
                "{\"$id\":\"1\",\"Name\":null,\"Manager\":{\"$ref\":\"1\"},\"Subordinates\":null,\"Contacts\":null}",
                JsonConvert.serializeObject(angela, preserveSettings()));
    }

    @Test
    public void listCycleEmitsValuesAndRef() {
        CSharpList<Employee> list = new CSharpList<>();
        Employee angela = new Employee();
        angela.setSubordinates(list);
        list.add(angela);
        assertEquals(
                "{\"$id\":\"1\",\"Name\":null,\"Manager\":null,\"Subordinates\":{\"$id\":\"2\",\"$values\":[{\"$ref\":\"1\"}]},\"Contacts\":null}",
                JsonConvert.serializeObject(angela, preserveSettings()));
    }

    @Test
    public void dictionaryCycleEmitsRef() {
        CSharpDictionary<String, Object> dict = new CSharpDictionary<>();
        dict.put("Self", dict);
        assertEquals(
                "{\"$id\":\"1\",\"Self\":{\"$ref\":\"1\"}}",
                JsonConvert.serializeObject(dict, preserveSettings()));
    }

    @Test
    public void duplicateReferencesShareOneId() {
        Employee bob = new Employee();
        bob.setName("Bob");
        Employee angela = new Employee();
        angela.setManager(bob);
        // The same bob instance reachable through two property paths: written
        // in full once (with its $id), the second occurrence is a $ref.
        CSharpList<Employee> subs = new CSharpList<>();
        subs.add(bob);
        angela.setSubordinates(subs);
        assertEquals(
                "{\"$id\":\"1\",\"Name\":null,\"Manager\":{\"$id\":\"2\",\"Name\":\"Bob\",\"Manager\":null,\"Subordinates\":null,\"Contacts\":null},\"Subordinates\":{\"$id\":\"3\",\"$values\":[{\"$ref\":\"2\"}]},\"Contacts\":null}",
                JsonConvert.serializeObject(angela, preserveSettings()));
    }

    @Test
    public void duplicateListReferenceGetsRef() {
        Employee bob = new Employee();
        bob.setName("Bob");
        CSharpList<Employee> subs = new CSharpList<>();
        subs.add(bob);
        Employee root = new Employee();
        root.setSubordinates(subs);
        assertEquals(
                "{\"$id\":\"1\",\"Name\":null,\"Manager\":null,\"Subordinates\":{\"$id\":\"2\",\"$values\":[{\"$id\":\"3\",\"Name\":\"Bob\",\"Manager\":null,\"Subordinates\":null,\"Contacts\":null}]},\"Contacts\":null}",
                JsonConvert.serializeObject(root, preserveSettings()));
    }

    @Test
    public void idsFollowDiscoveryOrder() {
        Employee inner = new Employee();
        inner.setName("Inner");
        Employee outer = new Employee();
        outer.setSubordinates(new CSharpList<>());
        outer.getSubordinates().add(inner);
        outer.setContacts(new CSharpDictionary<>());
        outer.getContacts().put("555-5555", inner);
        String json = JsonConvert.serializeObject(outer, preserveSettings());
        // Subordinates (id 2) is discovered before Contacts (id 3).
        assertEquals(
                "{\"$id\":\"1\",\"Name\":null,\"Manager\":null,\"Subordinates\":{\"$id\":\"2\",\"$values\":[{\"$id\":\"3\",\"Name\":\"Inner\",\"Manager\":null,\"Subordinates\":null,\"Contacts\":null}]},\"Contacts\":{\"$id\":\"4\",\"555-5555\":{\"$ref\":\"3\"}}}",
                json);
    }

    @Test
    public void referenceLoopWithoutSerializeThrows() {
        Employee angela = new Employee();
        angela.setManager(angela);
        JsonSerializerSettings settings = new JsonSerializerSettings();
        settings.setPreserveReferencesHandling(PreserveReferencesHandling.None);
        settings.setReferenceLoopHandling(ReferenceLoopHandling.Error);
        assertThrows(RuntimeException.class, () -> JsonConvert.serializeObject(angela, settings));
    }
}
