using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.Community.AI.Search.Pinecone.Client;
using Umbraco.Community.AI.Search.Pinecone.Options;

namespace Umbraco.Community.AI.Search.Pinecone.VectorStore;

/// <summary>
/// An <see cref="IAIVectorStore"/> that keeps Umbraco AI Search's vectors in a Pinecone serverless index.
/// </summary>
/// <remarks>
/// Each Umbraco index is a Pinecone namespace (<c>{NamespacePrefix}{indexName}</c>) and each chunk is one record.
/// Cultures are filtered with metadata, not namespaces, because a search needs a culture's records and the
/// invariant ones in a single query. The design is in docs/vector-store-notes.md §4.
/// </remarks>
internal sealed class PineconeAIVectorStore : IAIVectorStore
{
    private readonly PineconeDataClient _client;
    private readonly IOptionsMonitor<PineconeVectorStoreOptions> _options;
    private readonly ILogger<PineconeAIVectorStore> _logger;

    // An index's dimension and metric are fixed when it is created, so they're checked once.
    private volatile bool _indexChecked;

    public PineconeAIVectorStore(
        PineconeDataClient client,
        IOptionsMonitor<PineconeVectorStoreOptions> options,
        ILogger<PineconeAIVectorStore> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(string indexName, string documentId, string? culture, int chunkIndex, ReadOnlyMemory<float> vector, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        await EnsureIndexMatchesAsync(vector.Length, cancellationToken).ConfigureAwait(false);

        Dictionary<string, object> pineconeMetadata = PineconeMetadata.ToPinecone(metadata);
        pineconeMetadata[PineconeMetadata.DocumentIdKey] = documentId;
        pineconeMetadata[PineconeMetadata.CultureKey] = PineconeRecordIds.CultureToken(culture);
        pineconeMetadata[PineconeMetadata.ChunkIndexKey] = (double)chunkIndex;

        var record = new PineconeVector(PineconeRecordIds.Create(documentId, culture, chunkIndex), vector.ToArray(), pineconeMetadata);
        await _client.UpsertAsync(GetNamespace(indexName), [record], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string indexName, string documentId, string? culture, CancellationToken cancellationToken = default)
        => DeleteByPrefixAsync(indexName, PineconeRecordIds.CulturePrefix(documentId, culture), cancellationToken);

    /// <inheritdoc />
    public Task DeleteDocumentAsync(string indexName, string documentId, CancellationToken cancellationToken = default)
        => DeleteByPrefixAsync(indexName, PineconeRecordIds.DocumentPrefix(documentId), cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// A culture matches that culture's records and the invariant ones; no culture matches invariant records
    /// only. That's the convention the database stores follow. Scores are Pinecone's cosine similarity.
    /// </remarks>
    public async Task<IReadOnlyList<AIVectorSearchResult>> SearchAsync(string indexName, ReadOnlyMemory<float> queryVector, string? culture = null, int topK = 10, CancellationToken cancellationToken = default)
    {
        if (topK <= 0)
        {
            return [];
        }

        await EnsureIndexMatchesAsync(queryVector.Length, cancellationToken).ConfigureAwait(false);

        object cultureFilter = culture is null
            ? new Dictionary<string, object> { ["$eq"] = PineconeRecordIds.Invariant }
            : new Dictionary<string, object> { ["$in"] = new[] { PineconeRecordIds.CultureToken(culture), PineconeRecordIds.Invariant } };

        IReadOnlyList<PineconeMatch> matches = await _client.QueryAsync(
            GetNamespace(indexName),
            queryVector.ToArray(),
            Math.Min(topK, PineconeDataClient.MaxTopK),
            new Dictionary<string, object> { [PineconeMetadata.CultureKey] = cultureFilter },
            cancellationToken).ConfigureAwait(false);

        return matches
            .Select(match =>
            {
                Dictionary<string, object> metadata = PineconeMetadata.FromPinecone(match.Metadata);
                return new AIVectorSearchResult(GetDocumentId(match.Id, metadata), match.Score, metadata);
            })
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unlike search, no culture means every culture here, which is what the database stores do.
    /// </remarks>
    public async Task<IReadOnlyList<AIVectorEntry>> GetVectorsByDocumentAsync(string indexName, string documentId, string? culture = null, CancellationToken cancellationToken = default)
    {
        var @namespace = GetNamespace(indexName);
        var prefix = culture is null
            ? PineconeRecordIds.DocumentPrefix(documentId)
            : PineconeRecordIds.CulturePrefix(documentId, culture);

        IReadOnlyList<string> ids = await _client.ListIdsAsync(@namespace, prefix, cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return [];
        }

        IReadOnlyList<PineconeRecord> records = await _client.FetchAsync(@namespace, ids, cancellationToken).ConfigureAwait(false);

        return records
            .Select(record =>
            {
                Dictionary<string, object> metadata = PineconeMetadata.FromPinecone(record.Metadata);
                return new AIVectorEntry(
                    GetDocumentId(record.Id, metadata),
                    PineconeRecordIds.CultureFromToken(metadata.GetValueOrDefault(PineconeMetadata.CultureKey) as string),
                    GetChunkIndex(record.Id, metadata),
                    record.Values ?? [],
                    metadata);
            })
            .OrderBy(entry => entry.ChunkIndex)
            .ThenBy(entry => entry.Culture, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public Task ResetAsync(string indexName, CancellationToken cancellationToken = default)
        => _client.DeleteNamespaceAsync(GetNamespace(indexName), cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Counts records (chunks), like the database stores. Pinecone's stats lag writes by a few seconds.
    /// </remarks>
    public async Task<long> GetDocumentCountAsync(string indexName, CancellationToken cancellationToken = default)
    {
        PineconeIndexStats stats = await _client.DescribeIndexStatsAsync(cancellationToken).ConfigureAwait(false);

        return stats.Namespaces?.GetValueOrDefault(GetNamespace(indexName))?.VectorCount ?? 0;
    }

    private string GetNamespace(string indexName) => _options.CurrentValue.NamespacePrefix + indexName;

    /// <summary>
    /// Lists the matching IDs and deletes them. Pinecone is eventually consistent, so a record written a
    /// moment ago may not be listed yet; see docs/vector-store-notes.md §5.
    /// </summary>
    private async Task DeleteByPrefixAsync(string indexName, string prefix, CancellationToken cancellationToken)
    {
        var @namespace = GetNamespace(indexName);
        IReadOnlyList<string> ids = await _client.ListIdsAsync(@namespace, prefix, cancellationToken).ConfigureAwait(false);

        if (ids.Count > 0)
        {
            await _client.DeleteAsync(@namespace, ids, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Checks once that the index can hold these vectors, so a mismatch fails with a clear message instead of
    /// a bare 400 from Pinecone, or with scores that mean nothing to Umbraco AI Search's minimum score.
    /// </summary>
    private async Task EnsureIndexMatchesAsync(int dimension, CancellationToken cancellationToken)
    {
        if (_indexChecked)
        {
            return;
        }

        PineconeIndexStats stats = await _client.DescribeIndexStatsAsync(cancellationToken).ConfigureAwait(false);
        var indexName = _options.CurrentValue.IndexName;

        if (stats.Metric is { } metric && !string.Equals(metric, "cosine", StringComparison.OrdinalIgnoreCase))
        {
            throw new PineconeException(
                $"Pinecone index '{indexName}' uses the {metric} metric, but Umbraco AI Search scores results by cosine similarity. " +
                "Create an index with the cosine metric and rebuild the search index.");
        }

        if (stats.Dimension is { } indexDimension && indexDimension != dimension)
        {
            throw new PineconeException(
                $"Pinecone index '{indexName}' has {indexDimension} dimensions, but the embedding profile produces {dimension}. " +
                $"Create an index with {dimension} dimensions and rebuild the search index.");
        }

        _logger.LogDebug("Pinecone index {IndexName} matches: {Metric} metric, {Dimension} dimensions", indexName, stats.Metric, stats.Dimension);
        _indexChecked = true;
    }

    /// <summary>
    /// The document ID from metadata, or from the record ID if the metadata is missing.
    /// </summary>
    private static string GetDocumentId(string recordId, IReadOnlyDictionary<string, object> metadata)
        => metadata.GetValueOrDefault(PineconeMetadata.DocumentIdKey) as string ?? recordId.Split('#')[0];

    /// <summary>
    /// The chunk index from metadata, or from the record ID if the metadata is missing.
    /// </summary>
    private static int GetChunkIndex(string recordId, IReadOnlyDictionary<string, object> metadata)
    {
        if (metadata.GetValueOrDefault(PineconeMetadata.ChunkIndexKey) is double chunkIndex)
        {
            return (int)chunkIndex;
        }

        return int.TryParse(recordId[(recordId.LastIndexOf('#') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}
