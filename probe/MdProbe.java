import dotnet.text.json.JsonDocument;
import dotnet.text.json.JsonDocumentOptions;
import dotnet.text.json.JsonElement;

/**
 * Reproduces JsonDocumentTests.readNestedObject without surefire, and dumps the
 * MetadataDb row bytes so the write/read mismatch can be located.
 */
public final class MdProbe {
    public static void main(String[] args) throws Exception {
        String json = args.length > 0 ? args[0] : "{\"first\":{\"true\":true}}";
        try (JsonDocument doc = JsonDocument.parse(json, new JsonDocumentOptions())) {
            JsonElement root = doc.getRootElement();
            System.out.println("root kind = " + root.getValueKind());
            dumpRows(doc);
            System.out.println("root._idx = " + idxOf(root));
            JsonElement first = root.getProperty("first");
            System.out.println("first kind = " + first.getValueKind());
            System.out.println("first._idx = " + idxOf(first));
            JsonDocument parent = parentOf(first);
            System.out.println("first parent is same doc? " + (parent == doc));
            dumpRows(parent);
            System.out.println("first.true = " + first.getProperty("true").getBoolean());
        }
    }

    private static JsonDocument parentOf(JsonElement element) throws Exception {
        for (java.lang.reflect.Field f : element.getClass().getDeclaredFields()) {
            if (JsonDocument.class.isAssignableFrom(f.getType())) {
                f.setAccessible(true);
                return (JsonDocument) f.get(element);
            }
        }
        return null;
    }

    private static int idxOf(JsonElement element) throws Exception {
        for (java.lang.reflect.Field f : element.getClass().getDeclaredFields()) {
            if (f.getName().toLowerCase().contains("idx") || f.getName().toLowerCase().contains("index")) {
                f.setAccessible(true);
                Object v = f.get(element);
                System.out.println("   (field " + f.getName() + " = " + v + ")");
                if (v instanceof Integer i) {
                    return i;
                }
            }
        }
        return -1;
    }

    private static byte[] dbData(JsonDocument doc) throws Exception {
        Object db = db(doc);
        java.lang.reflect.Field dataField = db.getClass().getDeclaredField("_data");
        dataField.setAccessible(true);
        return (byte[]) dataField.get(db);
    }

    private static int dbLength(JsonDocument doc) throws Exception {
        Object db = db(doc);
        java.lang.reflect.Method getLength = db.getClass().getMethod("getLength");
        getLength.setAccessible(true);
        return (int) getLength.invoke(db);
    }

    private static Object db(JsonDocument doc) throws Exception {
        for (java.lang.reflect.Field f : doc.getClass().getDeclaredFields()) {
            if (f.getType().getSimpleName().equals("MetadataDb")) {
                f.setAccessible(true);
                return f.get(doc);
            }
        }
        throw new IllegalStateException("MetadataDb field not found");
    }

    private static void dumpRows(JsonDocument doc) throws Exception {
        byte[] data = dbData(doc);
        int length = dbLength(doc);
        System.out.println("db length = " + length + " (rows = " + (length / 12) + "), data.length = " + data.length);
        System.out.print("  first 24 bytes:");
        for (int i = 0; i < Math.min(24, data.length); i++) {
            System.out.printf(" %02X", data[i]);
        }
        System.out.println();
        for (int row = 0; row < length / 12; row++) {
            int base = row * 12;
            int location = readInt(data, base);
            int sizeOrLength = readInt(data, base + 4);
            int rowsAndType = readInt(data, base + 8);
            System.out.printf(
                "row %2d @%3d: location=%d sizeOrLength=%d rowsAndType=0x%08X (token=%d rows=%d)%n",
                row, base, location, sizeOrLength, rowsAndType,
                (rowsAndType >>> 28) & 0xF, rowsAndType & 0x0FFFFFFF);
            java.lang.reflect.Method get = db.getClass().getMethod("get", int.class);
            get.setAccessible(true);
            Object decoded = get.invoke(db, base);
            System.out.println("        decoded (cast to DbRow) = " + decoded);
            Object raw = readRow(data, base);
            System.out.println("        MemoryMarshal.read       = " + raw);
        }
    }

    private static Object readRow(byte[] data, int base) throws Exception {
        Class<?> dbRow = Class.forName("dotnet.text.json.JsonDocument$DbRow");
        io.github.ningpp.compat.ReadOnlySpan<Integer> span =
            new io.github.ningpp.compat.ReadOnlySpan<Integer>(data, base, data.length - base);
        java.lang.reflect.Method read = io.github.ningpp.compat.MemoryMarshal.class
            .getMethod("read", io.github.ningpp.compat.ReadOnlySpan.class, Class.class);
        return read.invoke(null, span, dbRow);
    }

    private static int readInt(byte[] data, int offset) {
        return (data[offset] & 0xFF)
            | ((data[offset + 1] & 0xFF) << 8)
            | ((data[offset + 2] & 0xFF) << 16)
            | ((data[offset + 3] & 0xFF) << 24);
    }
}
