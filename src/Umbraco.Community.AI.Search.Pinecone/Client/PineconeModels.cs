using System.Text.Json;

namespace Umbraco.Community.AI.Search.Pinecone.Client;

// Wire models for the parts of the Pinecone REST API the store uses. Property names follow the API's
// camelCase through JsonSerializerDefaults.Web, so they need no attributes.

/// <summary>
/// A record to upsert. Metadata values must already be Pinecone types: string, number, bool or a list of strings.
/// </summary>
internal sealed record PineconeVector(string Id, float[] Values, IDictionary<string, object> Metadata);

/// <summary>
/// A record read back from fetch.
/// </summary>
internal sealed record PineconeRecord(string Id, float[]? Values, Dictionary<string, JsonElement>? Metadata);

internal sealed record PineconeMatch(string Id, double Score, Dictionary<string, JsonElement>? Metadata);

/// <summary>
/// The parts of describe_index_stats the store needs.
/// </summary>
internal sealed record PineconeIndexStats(
    Dictionary<string, PineconeNamespaceStats>? Namespaces,
    int? Dimension,
    string? Metric);

internal sealed record PineconeNamespaceStats(long VectorCount);

internal sealed record UpsertRequest(IReadOnlyList<PineconeVector> Vectors, string Namespace);

internal sealed record QueryRequest(
    string Namespace,
    float[] Vector,
    int TopK,
    IDictionary<string, object>? Filter,
    bool IncludeMetadata,
    bool IncludeValues);

internal sealed record QueryResponse(List<PineconeMatch>? Matches);

internal sealed record FetchResponse(Dictionary<string, PineconeRecord>? Vectors);

internal sealed record ListResponse(List<ListedId>? Vectors, ListPagination? Pagination);

internal sealed record ListedId(string Id);

internal sealed record ListPagination(string? Next);

internal sealed record DeleteRequest(IReadOnlyList<string> Ids, string Namespace);

internal sealed record DescribeIndexResponse(string? Host, IndexStatus? Status, IndexSchema? Schema);

/// <summary>
/// A plain vector index only has the <c>_values</c> and <c>_sparse_values</c> fields. Any other field means a
/// document-schema index, which rejects the vector endpoints.
/// </summary>
internal sealed record IndexSchema(Dictionary<string, JsonElement>? Fields);

internal sealed record IndexStatus(bool Ready, string? State);
