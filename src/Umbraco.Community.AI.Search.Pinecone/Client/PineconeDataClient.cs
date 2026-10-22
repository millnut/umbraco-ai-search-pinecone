using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Community.AI.Search.Pinecone.Options;

namespace Umbraco.Community.AI.Search.Pinecone.Client;

/// <summary>
/// A small client for the parts of Pinecone's REST API the vector store needs.
/// </summary>
/// <remarks>
/// Pinecone's .NET SDK is archived and talks to the data plane over gRPC, so this calls the REST API directly.
/// Every call awaits with <c>ConfigureAwait(false)</c> because Umbraco AI Search's telemetry blocks on the count.
/// Retries are done here rather than with Microsoft.Extensions.Http.Resilience, whose Polly telemetry logs every
/// request at Information level in a default Umbraco site.
/// </remarks>
internal sealed class PineconeDataClient
{
    /// <summary>
    /// The Pinecone API version every request asks for.
    /// </summary>
    public const string ApiVersion = "2026-07";

    /// <summary>
    /// Pinecone allows up to 1,000 IDs in a delete.
    /// </summary>
    public const int MaxIdsPerDelete = 1000;

    /// <summary>
    /// Records per list page, which is Pinecone's maximum. Fetches use the same size so the URL stays short.
    /// </summary>
    public const int PageSize = 100;

    /// <summary>
    /// Pinecone's maximum <c>topK</c>.
    /// </summary>
    public const int MaxTopK = 10_000;

    /// <summary>
    /// Most list pages followed for one prefix: 100,000 IDs, far more chunks than one document has. Stops a
    /// pagination token that never runs out from looping forever.
    /// </summary>
    public const int MaxListPages = 1000;

    /// <summary>
    /// Attempts per request, including the first. Every call the store makes is idempotent, so retrying is safe.
    /// </summary>
    public const int MaxAttempts = 4;

    private const string ControlPlaneUrl = "https://api.pinecone.io";

    /// <summary>
    /// Longest wait between attempts, including one Pinecone asks for with <c>Retry-After</c>.
    /// </summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<PineconeVectorStoreOptions> _options;
    private readonly ILogger<PineconeDataClient> _logger;
    private readonly SemaphoreSlim _hostLock = new(1, 1);
    private Uri? _host;

    public PineconeDataClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<PineconeVectorStoreOptions> options,
        ILogger<PineconeDataClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Waits between attempts. Tests replace it so retries don't slow them down.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    private string IndexName => _options.CurrentValue.IndexName ?? string.Empty;

    public async Task UpsertAsync(string @namespace, IReadOnlyList<PineconeVector> vectors, CancellationToken cancellationToken)
    {
        Uri host = await GetHostAsync(cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage _ = await SendAsync(
            HttpMethod.Post, new Uri(host, "vectors/upsert"), new UpsertRequest(vectors, @namespace), "upsert records", allowNotFound: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PineconeMatch>> QueryAsync(string @namespace, float[] vector, int topK, IDictionary<string, object>? filter, CancellationToken cancellationToken)
    {
        Uri host = await GetHostAsync(cancellationToken).ConfigureAwait(false);
        var request = new QueryRequest(@namespace, vector, topK, filter, IncludeMetadata: true, IncludeValues: false);
        QueryResponse? response = await SendForJsonAsync<QueryResponse>(
            HttpMethod.Post, new Uri(host, "query"), request, "query", cancellationToken).ConfigureAwait(false);

        return response?.Matches ?? [];
    }

    /// <summary>
    /// Lists every record ID in the namespace that starts with <paramref name="prefix"/>, following pagination.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListIdsAsync(string @namespace, string prefix, CancellationToken cancellationToken)
    {
        Uri host = await GetHostAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<string>();
        string? paginationToken = null;
        var pages = 0;

        do
        {
            if (++pages > MaxListPages)
            {
                throw new PineconeException(
                    $"Pinecone list records for index '{IndexName}' returned more than {MaxListPages} pages for prefix '{prefix}'. Stopped to avoid looping.");
            }

            var query = $"vectors/list?namespace={Escape(@namespace)}&prefix={Escape(prefix)}&limit={PageSize}";
            if (paginationToken is not null)
            {
                query += $"&paginationToken={Escape(paginationToken)}";
            }

            ListResponse? page = await SendForJsonAsync<ListResponse>(
                HttpMethod.Get, new Uri(host, query), null, "list records", cancellationToken).ConfigureAwait(false);

            ids.AddRange(page?.Vectors?.Select(v => v.Id) ?? []);
            paginationToken = string.IsNullOrEmpty(page?.Pagination?.Next) ? null : page.Pagination.Next;
        }
        while (paginationToken is not null);

        return ids;
    }

    /// <summary>
    /// Fetches records by ID, at most <see cref="PageSize"/> per request.
    /// </summary>
    public async Task<IReadOnlyList<PineconeRecord>> FetchAsync(string @namespace, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        Uri host = await GetHostAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<PineconeRecord>(ids.Count);

        foreach (string[] batch in ids.Chunk(PageSize))
        {
            var query = $"vectors/fetch?namespace={Escape(@namespace)}&" + string.Join("&", batch.Select(id => $"ids={Escape(id)}"));
            FetchResponse? response = await SendForJsonAsync<FetchResponse>(
                HttpMethod.Get, new Uri(host, query), null, "fetch records", cancellationToken).ConfigureAwait(false);

            if (response?.Vectors is { } vectors)
            {
                records.AddRange(vectors.Values);
            }
        }

        return records;
    }

    /// <summary>
    /// Deletes records by ID, at most <see cref="MaxIdsPerDelete"/> per request.
    /// </summary>
    public async Task DeleteAsync(string @namespace, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        Uri host = await GetHostAsync(cancellationToken).ConfigureAwait(false);

        foreach (string[] batch in ids.Chunk(MaxIdsPerDelete))
        {
            using HttpResponseMessage _ = await SendAsync(
                HttpMethod.Post, new Uri(host, "vectors/delete"), new DeleteRequest(batch, @namespace), "delete records", allowNotFound: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes a namespace and everything in it. A namespace that doesn't exist counts as deleted.
    /// </summary>
    public async Task DeleteNamespaceAsync(string @namespace, CancellationToken cancellationToken)
    {
        Uri host = await GetHostAsync(cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage _ = await SendAsync(
            HttpMethod.Delete, new Uri(host, $"namespaces/{Escape(@namespace)}"), null, "delete namespace", allowNotFound: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PineconeIndexStats> DescribeIndexStatsAsync(CancellationToken cancellationToken)
    {
        Uri host = await GetHostAsync(cancellationToken).ConfigureAwait(false);
        PineconeIndexStats? stats = await SendForJsonAsync<PineconeIndexStats>(
            HttpMethod.Post, new Uri(host, "describe_index_stats"), new { }, "describe index stats", cancellationToken).ConfigureAwait(false);

        return stats ?? new PineconeIndexStats(null, null, null);
    }

    /// <summary>
    /// The index's data-plane host: the configured one, or looked up by name once and then cached.
    /// </summary>
    private async Task<Uri> GetHostAsync(CancellationToken cancellationToken)
    {
        if (_host is not null)
        {
            return _host;
        }

        await _hostLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_host is null)
            {
                var configured = _options.CurrentValue.IndexHost;
                var host = string.IsNullOrWhiteSpace(configured)
                    ? await DescribeIndexHostAsync(cancellationToken).ConfigureAwait(false)
                    : configured.Trim();

                _host = CreateHostUri(host);
            }

            return _host;
        }
        finally
        {
            _hostLock.Release();
        }
    }

    /// <summary>
    /// The host as a base URI. Only HTTPS is accepted, because every request carries the API key.
    /// </summary>
    internal static Uri CreateHostUri(string host)
    {
        var withScheme = host.Contains("://", StringComparison.Ordinal) ? host : "https://" + host;

        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw new PineconeException($"'{host}' isn't a valid Pinecone index host. Check {PineconeVectorStoreOptions.SectionName}:IndexHost.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new PineconeException(
                $"The Pinecone index host must use https, because every request carries the API key. Check {PineconeVectorStoreOptions.SectionName}:IndexHost.");
        }

        // Only the host is kept. The trailing slash makes the relative request paths resolve under it.
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    private async Task<string> DescribeIndexHostAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Get, new Uri($"{ControlPlaneUrl}/indexes/{Escape(IndexName)}"), null, "describe index", allowNotFound: true, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new PineconeException(
                $"Pinecone index '{IndexName}' was not found. Create a serverless index with the cosine metric, or fix {PineconeVectorStoreOptions.SectionName}:IndexName.",
                HttpStatusCode.NotFound);
        }

        DescribeIndexResponse? index = await response.Content.ReadFromJsonAsync<DescribeIndexResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);

        if (index?.Status is { Ready: false })
        {
            throw new PineconeException($"Pinecone index '{IndexName}' isn't ready (state: {index.Status.State ?? "unknown"}).");
        }

        if (index?.Schema?.Fields?.Keys.Where(field => !field.StartsWith('_')).ToList() is { Count: > 0 } documentFields)
        {
            throw new PineconeException(
                $"Pinecone index '{IndexName}' has a document schema (fields: {string.Join(", ", documentFields)}), so it can't store " +
                "Umbraco AI Search's vectors. Create a dense index with the cosine metric and no embedding model.");
        }

        return string.IsNullOrWhiteSpace(index?.Host)
            ? throw new PineconeException($"Pinecone didn't return a host for index '{IndexName}'.")
            : index.Host;
    }

    private async Task<T?> SendForJsonAsync<T>(HttpMethod method, Uri uri, object? body, string operation, CancellationToken cancellationToken)
    {
        // A namespace that hasn't been written to yet is a 404 on reads; treat it as empty.
        using HttpResponseMessage response = await SendAsync(method, uri, body, operation, allowNotFound: true, cancellationToken).ConfigureAwait(false);

        return response.StatusCode == HttpStatusCode.NotFound
            ? default
            : await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request with the API key and version headers. Throws <see cref="PineconeException"/> on failure,
    /// except a 404 when <paramref name="allowNotFound"/> is set. The caller disposes the response.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, object? body, string operation, bool allowNotFound, CancellationToken cancellationToken)
    {
        // Checked here, not just at startup, because options can change while the site runs.
        var apiKey = _options.CurrentValue.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new PineconeException($"A Pinecone API key is required: set {PineconeVectorStoreOptions.SectionName}:ApiKey.");
        }

        HttpClient client = _httpClientFactory.CreateClient(PineconeConstants.HttpClientName);

        for (var attempt = 1; ; attempt++)
        {
            // A request message can only be sent once, so each attempt builds its own.
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Add("Api-Key", apiKey);
            request.Headers.Add("X-Pinecone-Api-Version", ApiVersion);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            }

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex, cancellationToken))
            {
                await WaitBeforeRetryAsync(operation, attempt, null, ex.GetType().Name, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
            {
                return response;
            }

            using (response)
            {
                if (attempt < MaxAttempts && IsTransient(response.StatusCode))
                {
                    await WaitBeforeRetryAsync(operation, attempt, response.Headers.RetryAfter, $"{(int)response.StatusCode}", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw await CreateExceptionAsync(response, operation, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    /// <summary>
    /// A network failure, or the client timing out. Not the caller cancelling.
    /// </summary>
    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
        => exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private async Task WaitBeforeRetryAsync(string operation, int attempt, System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter, string reason, CancellationToken cancellationToken)
    {
        // Exponential backoff with jitter (0.5s, 1s, 2s ...), unless Pinecone says how long to wait.
        TimeSpan delay = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
            ?? TimeSpan.FromMilliseconds((250 << attempt) + Random.Shared.Next(0, 250));
        delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaxRetryDelay ? MaxRetryDelay : delay;

        _logger.LogWarning(
            "Pinecone {Operation} for index {IndexName} failed ({Reason}), retrying in {Delay} ms (attempt {Attempt} of {MaxAttempts})",
            operation, IndexName, reason, (int)delay.TotalMilliseconds, attempt + 1, MaxAttempts);

        await Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PineconeException> CreateExceptionAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var detail = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        if (detail.Length > 500)
        {
            detail = detail[..500];
        }

        var status = (int)response.StatusCode;
        var message = response.StatusCode == HttpStatusCode.Unauthorized
            ? $"Pinecone rejected the API key ({status}) on {operation} for index '{IndexName}'. Check {PineconeVectorStoreOptions.SectionName}:ApiKey."
            : $"Pinecone {operation} for index '{IndexName}' failed with {status} {response.ReasonPhrase}.";

        return new PineconeException(detail.Length == 0 ? message : $"{message} {detail}", response.StatusCode);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
