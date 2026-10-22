using System.Net;
using System.Net.Http.Headers;

namespace Lidget.Providers.Claude;

public sealed record ClaudeUsageResponse(HttpStatusCode Status, string Body, TimeSpan? RetryAfter);

public sealed class ClaudeUsageClient(HttpClient http, TimeProvider time)
{
    public async Task<ClaudeUsageResponse> GetUsageAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ClaudeEndpoints.Usage);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("anthropic-beta", ClaudeEndpoints.BetaHeader);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new ClaudeUsageResponse(response.StatusCode, body, ReadRetryAfter(response.Headers.RetryAfter));
    }

    private TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}
