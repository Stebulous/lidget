using System.Net.Http.Headers;
using Lidget.Core;

namespace Lidget.Providers;

public static class LidgetHttp
{
    public static HttpClient Create(HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(15);
        var version = typeof(LidgetHttp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, version));
        return client;
    }
}
