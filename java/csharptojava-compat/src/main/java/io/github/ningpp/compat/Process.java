package io.github.ningpp.compat;

/**
 * System.Diagnostics.Process compatibility stub. The real process API is not
 * available in the Swing-backed compat layer; getMainModule() returns a stub
 * module whose FileName is empty.
 */
public class Process {

    private final ProcessModule mainModule;

    private Process() {
        this.mainModule = new ProcessModule();
    }

    public static Process getCurrentProcess() {
        return new Process();
    }

    public ProcessModule getMainModule() {
        return mainModule;
    }

    public static class ProcessModule {
        public String getFileName() {
            return "";
        }

        public String getModuleName() {
            return "";
        }
    }
}
