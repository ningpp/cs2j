public class CpProbe {
    public static void main(String[] a) throws Exception {
        System.out.println("TypeHelper from: " + io.github.ningpp.compat.TypeHelper.class.getProtectionDomain().getCodeSource());
        System.out.println("CompatJsonNumberHandling from: " + io.github.ningpp.compat.CompatJsonNumberHandling.class.getProtectionDomain().getCodeSource());
        System.out.println("materialize methods: " + java.util.Arrays.toString(
            java.util.Arrays.stream(io.github.ningpp.compat.TypeHelper.class.getDeclaredMethods())
                .map(java.lang.reflect.Method::getName)
                .filter(n -> n.contains("aterialize"))
                .toArray()));
    }
}
