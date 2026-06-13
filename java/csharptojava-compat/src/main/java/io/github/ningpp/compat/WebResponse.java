package io.github.ningpp.compat;

/**
 * Minimal compat stub for System.Net.WebResponse.
 * Supports the API surface used by XmlDownloadManager.
 */
public class WebResponse implements AutoCloseable {

    protected WebResponse() {
    }

    /** Mirrors C# WebResponse.GetResponseStream() */
    public StreamWrapper getResponseStream() {
        return StreamWrapper.of(new java.io.ByteArrayInputStream(new byte[0]));
    }

    /** AutoCloseable for try-with-resources (mirrors IDisposable) */
    @Override
    public void close() {
    }
}
