using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Umbraco.Community.AI.Search.Pinecone.Tests.Fakes;

/// <summary>
/// Stands in for Pinecone: the control plane's describe index, and an in-memory serverless index behind
/// <see cref="Host"/>. Records every request.
/// </summary>
/// <remarks>
/// Unlike Pinecone, writes are visible straight away. Filters support <c>$eq</c> and <c>$in</c>, which is all
/// the store sends. A namespace that doesn't exist is a 404 for list, fetch, delete and delete namespace.
/// </remarks>
internal sealed class FakePinecone : HttpMessageHandler
{
    public const string Host = "test-index-abc123.svc.aped-1234.pinecone.io";
    public const string ApiKey = "test-api-key";

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>
    /// Namespace name, then record ID, in the order IDs sort, like Pinecone's list.
    /// </summary>
    public Dictionary<string, SortedDictionary<string, StoredRecord>> Namespaces { get; } = [];

    public bool IndexExists { get; set; } = true;

    public bool IndexReady { get; set; } = true;

    /// <summary>
    /// Reported by describe_index_stats. Null leaves it out, which skips the store's dimension check.
    /// </summary>
    public int? Dimension { get; set; }

    public string Metric { get; set; } = "cosine";

    /// <summary>
    /// Describes the index as a document-schema (integrated embedding) one, as the console creates from a model preset.
    /// </summary>
    public bool DocumentSchema { get; set; }

    /// <summary>
    /// When set, every data-plane request fails with this status.
    /// </summary>
    public HttpStatusCode? FailWith { get; set; }

    /// <summary>
    /// List always says there's another page, as a misbehaving server might.
    /// </summary>
    public bool EndlessPagination { get; set; }

    /// <summary>
    /// Data-plane responses to fail with, one per request, before behaving normally again.
    /// </summary>
    public Queue<HttpResponseMessage> FailNext { get; } = [];

    public IEnumerable<RecordedRequest> RequestsTo(string path) => Requests.Where(r => r.Path == path);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!.Host,
            request.RequestUri.AbsolutePath,
            ParseQuery(request.RequestUri.Query),
            request.Headers.TryGetValues("Api-Key", out var keys) ? keys.Single() : null,
            request.Headers.TryGetValues("X-Pinecone-Api-Version", out var versions) ? versions.Single() : null,
            body);
        Requests.Add(recorded);

        if (recorded.ApiKey != ApiKey)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("Invalid API key") };
        }

        if (recorded.Host == "api.pinecone.io")
        {
            return DescribeIndex(recorded);
        }

        if (recorded.Host != Host)
        {
            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        }

        if (FailNext.TryDequeue(out HttpResponseMessage? failure))
        {
            return failure;
        }

        if (FailWith is { } status)
        {
            return Json("""{"code":13,"message":"Something went wrong","details":[]}""", status);
        }

        return (recorded.Method.Method, recorded.Path) switch
        {
            ("POST", "/vectors/upsert") => Upsert(recorded.Body!),
            ("POST", "/query") => Query(recorded.Body!),
            ("GET", "/vectors/list") => List(recorded.Query),
            ("GET", "/vectors/fetch") => Fetch(recorded.Query),
            ("POST", "/vectors/delete") => Delete(recorded.Body!),
            ("POST", "/describe_index_stats") => Stats(),
            ("DELETE", var path) when path.StartsWith("/namespaces/", StringComparison.Ordinal)
                => DeleteNamespace(Uri.UnescapeDataString(path["/namespaces/".Length..])),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private HttpResponseMessage DescribeIndex(RecordedRequest request)
    {
        if (!IndexExists)
        {
            return Json("""{"error":{"code":"NOT_FOUND","message":"Index not found."},"status":404}""", HttpStatusCode.NotFound);
        }

        var name = Uri.UnescapeDataString(request.Path["/indexes/".Length..]);
        return Json(JsonSerializer.Serialize(new
        {
            name,
            host = Host,
            status = new { ready = IndexReady, state = IndexReady ? "Ready" : "Initializing" },
            schema = new
            {
                fields = DocumentSchema
                    ? (object)new { chunk_text = new { type = "string" }, title = new { type = "string" } }
                    : new { _values = new { type = "dense_vector", dimension = Dimension ?? 1024, metric = Metric }, _sparse_values = new { type = "sparse_vector" } },
            },
            deletion_protection = "disabled",
        }));
    }

    private HttpResponseMessage Upsert(string body)
    {
        JsonNode request = JsonNode.Parse(body)!;
        var @namespace = request["namespace"]!.GetValue<string>();
        if (!Namespaces.TryGetValue(@namespace, out SortedDictionary<string, StoredRecord>? records))
        {
            Namespaces[@namespace] = records = new SortedDictionary<string, StoredRecord>(StringComparer.Ordinal);
        }

        JsonArray vectors = request["vectors"]!.AsArray();
        foreach (JsonNode? vector in vectors)
        {
            var id = vector!["id"]!.GetValue<string>();
            records[id] = new StoredRecord(
                id,
                vector["values"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray(),
                vector["metadata"]?.AsObject() ?? []);
        }

        return Json(JsonSerializer.Serialize(new { upsertedCount = vectors.Count }));
    }

    private HttpResponseMessage Query(string body)
    {
        JsonNode request = JsonNode.Parse(body)!;
        var @namespace = request["namespace"]!.GetValue<string>();
        var vector = request["vector"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
        var topK = request["topK"]!.GetValue<int>();
        JsonObject? filter = request["filter"]?.AsObject();
        var includeMetadata = request["includeMetadata"]?.GetValue<bool>() == true;

        var matches = (Namespaces.GetValueOrDefault(@namespace)?.Values ?? Enumerable.Empty<StoredRecord>())
            .Where(record => filter is null || Matches(record.Metadata, filter))
            .Select(record => new { record, score = Cosine(vector, record.Values) })
            .OrderByDescending(m => m.score)
            .Take(topK)
            .Select(m => new
            {
                id = m.record.Id,
                score = m.score,
                metadata = includeMetadata ? m.record.Metadata : null,
            });

        return Json(JsonSerializer.Serialize(new { matches, @namespace }));
    }

    private HttpResponseMessage List(ILookup<string, string> query)
    {
        if (!Namespaces.TryGetValue(query["namespace"].Single(), out SortedDictionary<string, StoredRecord>? records))
        {
            return NamespaceNotFound();
        }

        var prefix = query["prefix"].SingleOrDefault() ?? string.Empty;
        var limit = int.Parse(query["limit"].SingleOrDefault() ?? "100");
        var offset = int.Parse(query["paginationToken"].SingleOrDefault() ?? "0");

        var ids = records.Keys.Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        var page = ids.Skip(offset).Take(limit).Select(id => new { id });
        var next = EndlessPagination || offset + limit < ids.Count ? (offset + limit).ToString() : null;

        return Json(JsonSerializer.Serialize(new { vectors = page, pagination = next is null ? null : new { next } }));
    }

    private HttpResponseMessage Fetch(ILookup<string, string> query)
    {
        if (!Namespaces.TryGetValue(query["namespace"].Single(), out SortedDictionary<string, StoredRecord>? records))
        {
            return NamespaceNotFound();
        }

        var vectors = query["ids"]
            .Where(records.ContainsKey)
            .ToDictionary(id => id, id => new { id, values = records[id].Values, metadata = records[id].Metadata });

        return Json(JsonSerializer.Serialize(new { vectors }));
    }

    private HttpResponseMessage Delete(string body)
    {
        JsonNode request = JsonNode.Parse(body)!;
        if (!Namespaces.TryGetValue(request["namespace"]!.GetValue<string>(), out SortedDictionary<string, StoredRecord>? records))
        {
            return NamespaceNotFound();
        }

        foreach (JsonNode? id in request["ids"]!.AsArray())
        {
            records.Remove(id!.GetValue<string>());
        }

        return Json("{}");
    }

    private HttpResponseMessage DeleteNamespace(string @namespace)
        => Namespaces.Remove(@namespace) ? Json("{}") : NamespaceNotFound();

    private HttpResponseMessage Stats()
    {
        var namespaces = Namespaces.ToDictionary(n => n.Key, n => new { vectorCount = n.Value.Count });
        return Json(JsonSerializer.Serialize(new
        {
            namespaces,
            dimension = Dimension,
            metric = Metric,
            totalVectorCount = Namespaces.Sum(n => n.Value.Count),
        }));
    }

    private static bool Matches(JsonObject metadata, JsonObject filter)
        => filter.All(condition =>
        {
            var value = metadata[condition.Key]?.ToJsonString();
            JsonObject operators = condition.Value!.AsObject();

            return operators.All(op => op.Key switch
            {
                "$eq" => value == op.Value!.ToJsonString(),
                "$in" => op.Value!.AsArray().Any(candidate => value == candidate!.ToJsonString()),
                _ => throw new NotSupportedException($"The fake doesn't support {op.Key}."),
            });
        });

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static ILookup<string, string> ParseQuery(string query)
        => query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToLookup(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair.Length > 1 ? pair[1] : string.Empty));

    private static HttpResponseMessage NamespaceNotFound()
        => Json("""{"code":5,"message":"Namespace not found","details":[]}""", HttpStatusCode.NotFound);

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal sealed record StoredRecord(string Id, float[] Values, JsonObject Metadata);

    internal sealed record RecordedRequest(
        HttpMethod Method,
        string Host,
        string Path,
        ILookup<string, string> Query,
        string? ApiKey,
        string? ApiVersion,
        string? Body)
    {
        public JsonNode? Json { get; } = Body is null ? null : JsonNode.Parse(Body);
    }
}
