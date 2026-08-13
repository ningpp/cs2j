package io.github.ningpp.compat;

/**
 * Compat for System.IO.Pipelines.Pipe — minimal in-memory pipe.
 */
public class Pipe {
    private final PipeWriter writer = new PipeWriter();

    public Pipe() {
    }

    public PipeWriter getWriter() {
        return writer;
    }

    public PipeReader getReader() {
        return new PipeReader(writer);
    }

    public void complete() {
    }

    public static class PipeReader {
        private final PipeWriter writer;

        private PipeReader(PipeWriter writer) {
            this.writer = writer;
        }

        public byte[] toArray() {
            return writer.toArray();
        }

        public void complete() {
        }
    }
}
