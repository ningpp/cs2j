package io.github.ningpp.compat;

import java.util.concurrent.CompletableFuture;

/**
 * Minimal compat stub for System.Net.WebRequest.
 * Supports the API surface used by XmlDownloadManager.
 */
public class WebRequest {

    private Object credentials;
    private Object proxy;
    private Object cachePolicy;

    protected WebRequest() {
    }

    /** Mirrors C# WebRequest.Create(Uri) — accepts Object to avoid compile-time dep on dotnet.system.Uri */
    public static WebRequest create(Object uri) {
        return new WebRequest();
    }

    /** Property setter for Credentials */
    public void setCredentials(Object credentials) {
        this.credentials = credentials;
    }

    /** Property setter for Proxy */
    public void setProxy(Object proxy) {
        this.proxy = proxy;
    }

    /** Property setter for CachePolicy */
    public void setCachePolicy(Object cachePolicy) {
        this.cachePolicy = cachePolicy;
    }

    /** Mirrors C# WebRequest.GetResponse() */
    public WebResponse getResponse() {
        return new WebResponse();
    }

    /** Mirrors C# WebRequest.GetResponseAsync() */
    public CompletableFuture<WebResponse> getResponseAsync() {
        return CompletableFuture.completedFuture(new WebResponse());
    }
}
