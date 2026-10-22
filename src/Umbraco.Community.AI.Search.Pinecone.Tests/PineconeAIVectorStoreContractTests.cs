using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.Community.AI.Search.Pinecone.Tests.Fakes;
using Umbraco.Community.AI.Search.Pinecone.VectorStore;

namespace Umbraco.Community.AI.Search.Pinecone.Tests;

/// <summary>
/// Umbraco AI Search's own InMemoryAIVectorStoreTests, run against the Pinecone store, so it behaves like the
/// built-in stores.
/// </summary>
public class PineconeAIVectorStoreContractTests
{
    private const string IndexName = "test-index";

    private readonly PineconeAIVectorStore _store = TestStore.Create(new FakePinecone());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpsertAsync_StoresVector_GetDocumentCountReturnsOne()
    {
        await _store.UpsertAsync(IndexName, "doc1", null, 0, new float[] { 1.0f, 0.0f, 0.0f }, cancellationToken: Ct);

        Assert.Equal(1, await _store.GetDocumentCountAsync(IndexName, Ct));
    }

    [Fact]
    public async Task UpsertAsync_SameChunk_OverwritesPrevious()
    {
        await _store.UpsertAsync(IndexName, "doc1", null, 0, new float[] { 1.0f, 0.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc1", null, 0, new float[] { 0.0f, 1.0f, 0.0f }, cancellationToken: Ct);

        Assert.Equal(1, await _store.GetDocumentCountAsync(IndexName, Ct));
    }

    [Fact]
    public async Task UpsertAsync_MultipleChunks_StoresAll()
    {
        await _store.UpsertAsync(IndexName, "doc1", null, 0, new float[] { 1.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc1", null, 1, new float[] { 0.0f, 1.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc1", null, 2, new float[] { 0.5f, 0.5f }, cancellationToken: Ct);

        Assert.Equal(3, await _store.GetDocumentCountAsync(IndexName, Ct));
    }

    [Fact]
    public async Task DeleteAsync_RemovesAllChunksForDocumentAndCulture()
    {
        await _store.UpsertAsync(IndexName, "doc1", "en", 0, new float[] { 1.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc1", "en", 1, new float[] { 0.0f, 1.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc1", "da", 0, new float[] { 0.5f, 0.5f }, cancellationToken: Ct);

        await _store.DeleteAsync(IndexName, "doc1", "en", Ct);

        Assert.Equal(1, await _store.GetDocumentCountAsync(IndexName, Ct)); // Only da chunk 0 remains
    }

    [Fact]
    public async Task DeleteDocumentAsync_RemovesAllCultures()
    {
        await _store.UpsertAsync(IndexName, "doc1", "en", 0, new float[] { 1.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc1", "da", 0, new float[] { 0.0f, 1.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc2", null, 0, new float[] { 0.5f, 0.5f }, cancellationToken: Ct);

        await _store.DeleteDocumentAsync(IndexName, "doc1", Ct);

        Assert.Equal(1, await _store.GetDocumentCountAsync(IndexName, Ct)); // Only doc2 remains
    }

    [Fact]
    public async Task SearchAsync_ReturnsResultsOrderedBySimilarity()
    {
        await _store.UpsertAsync(IndexName, "doc-x", null, 0, new float[] { 1.0f, 0.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc-y", null, 0, new float[] { 0.0f, 1.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc-xy", null, 0, new float[] { 0.7f, 0.7f, 0.0f }, cancellationToken: Ct);

        IReadOnlyList<AIVectorSearchResult> results = await _store.SearchAsync(IndexName, new float[] { 0.9f, 0.1f, 0.0f }, topK: 3, cancellationToken: Ct);

        Assert.Equal(3, results.Count);
        Assert.Equal("doc-x", results[0].DocumentId);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task SearchAsync_FiltersByCulture()
    {
        await _store.UpsertAsync(IndexName, "doc1", "en", 0, new float[] { 1.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc1", "da", 0, new float[] { 0.0f, 1.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc2", "en", 0, new float[] { 0.5f, 0.5f }, cancellationToken: Ct);

        IReadOnlyList<AIVectorSearchResult> results = await _store.SearchAsync(IndexName, new float[] { 1.0f, 0.0f }, culture: "en", topK: 10, cancellationToken: Ct);

        Assert.Equal(2, results.Count); // doc1 en + doc2 en
        Assert.All(results, r => Assert.Contains(r.DocumentId, new[] { "doc1", "doc2" }));
    }

    [Fact]
    public async Task SearchAsync_NoCultureFilter_ReturnsInvariantOnly()
    {
        await _store.UpsertAsync(IndexName, "doc1", "en", 0, new float[] { 1.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc1", "da", 0, new float[] { 0.0f, 1.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc2", null, 0, new float[] { 0.5f, 0.5f }, cancellationToken: Ct);

        IReadOnlyList<AIVectorSearchResult> results = await _store.SearchAsync(IndexName, new float[] { 1.0f, 0.0f }, topK: 10, cancellationToken: Ct);

        AIVectorSearchResult result = Assert.Single(results); // Only invariant doc2
        Assert.Equal("doc2", result.DocumentId);
    }

    [Fact]
    public async Task SearchAsync_WithCulture_ReturnsMatchingAndInvariant()
    {
        await _store.UpsertAsync(IndexName, "doc1", "en", 0, new float[] { 1.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc1", "da", 0, new float[] { 0.0f, 1.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc2", null, 0, new float[] { 0.5f, 0.5f }, cancellationToken: Ct);

        IReadOnlyList<AIVectorSearchResult> results = await _store.SearchAsync(IndexName, new float[] { 1.0f, 0.0f }, culture: "en", topK: 10, cancellationToken: Ct);

        Assert.Equal(2, results.Count); // en doc1 + invariant doc2
    }

    [Fact]
    public async Task SearchAsync_EmptyIndex_ReturnsEmpty()
    {
        IReadOnlyList<AIVectorSearchResult> results = await _store.SearchAsync(IndexName, new float[] { 1.0f, 0.0f }, cancellationToken: Ct);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_TopKLimitsResults()
    {
        await _store.UpsertAsync(IndexName, "doc1", null, 0, new float[] { 1.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc2", null, 0, new float[] { 0.0f, 1.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc3", null, 0, new float[] { 0.5f, 0.5f }, cancellationToken: Ct);

        IReadOnlyList<AIVectorSearchResult> results = await _store.SearchAsync(IndexName, new float[] { 1.0f, 0.0f }, topK: 2, cancellationToken: Ct);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchAsync_IncludesMetadata()
    {
        var metadata = new Dictionary<string, object> { ["type"] = "article" };
        await _store.UpsertAsync(IndexName, "doc1", null, 0, new float[] { 1.0f, 0.0f }, metadata, Ct);

        IReadOnlyList<AIVectorSearchResult> results = await _store.SearchAsync(IndexName, new float[] { 1.0f, 0.0f }, cancellationToken: Ct);

        Assert.NotNull(results[0].Metadata);
        Assert.Equal("article", results[0].Metadata!["type"]);
    }

    [Fact]
    public async Task ResetAsync_ClearsAllDocuments()
    {
        await _store.UpsertAsync(IndexName, "doc1", null, 0, new float[] { 1.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc2", "en", 0, new float[] { 0.0f, 1.0f }, cancellationToken: Ct);

        await _store.ResetAsync(IndexName, Ct);

        Assert.Equal(0, await _store.GetDocumentCountAsync(IndexName, Ct));
    }

    [Fact]
    public async Task GetDocumentCountAsync_NonExistentIndex_ReturnsZero()
    {
        Assert.Equal(0, await _store.GetDocumentCountAsync("nonexistent", Ct));
    }

    [Fact]
    public async Task GetVectorsByDocumentAsync_ReturnsStoredVectorsOrderedByChunk()
    {
        await _store.UpsertAsync(IndexName, "doc1", "en", 2, new float[] { 0.5f, 0.5f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc1", "en", 0, new float[] { 1.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc1", "en", 1, new float[] { 0.0f, 1.0f }, cancellationToken: Ct);

        IReadOnlyList<AIVectorEntry> results = await _store.GetVectorsByDocumentAsync(IndexName, "doc1", "en", Ct);

        Assert.Equal([0, 1, 2], results.Select(r => r.ChunkIndex));
    }

    [Fact]
    public async Task GetVectorsByDocumentAsync_FiltersByCulture()
    {
        await _store.UpsertAsync(IndexName, "doc1", "en", 0, new float[] { 1.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc1", "da", 0, new float[] { 0.0f, 1.0f }, cancellationToken: Ct);

        IReadOnlyList<AIVectorEntry> results = await _store.GetVectorsByDocumentAsync(IndexName, "doc1", "en", Ct);

        AIVectorEntry result = Assert.Single(results);
        Assert.Equal("en", result.Culture);
    }

    [Fact]
    public async Task GetVectorsByDocumentAsync_NoCultureFilter_ReturnsAll()
    {
        await _store.UpsertAsync(IndexName, "doc1", "en", 0, new float[] { 1.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync(IndexName, "doc1", "da", 0, new float[] { 0.0f, 1.0f }, cancellationToken: Ct);

        IReadOnlyList<AIVectorEntry> results = await _store.GetVectorsByDocumentAsync(IndexName, "doc1", cancellationToken: Ct);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetVectorsByDocumentAsync_NonExistentDocument_ReturnsEmpty()
    {
        IReadOnlyList<AIVectorEntry> results = await _store.GetVectorsByDocumentAsync(IndexName, "nonexistent", cancellationToken: Ct);

        Assert.Empty(results);
    }

    [Fact]
    public async Task MultipleIndexes_AreIsolated()
    {
        await _store.UpsertAsync("index-a", "doc1", null, 0, new float[] { 1.0f, 0.0f }, cancellationToken: Ct);
        await _store.UpsertAsync("index-b", "doc1", null, 0, new float[] { 0.0f, 1.0f }, cancellationToken: Ct);

        Assert.Equal(1, await _store.GetDocumentCountAsync("index-a", Ct));
        Assert.Equal(1, await _store.GetDocumentCountAsync("index-b", Ct));

        await _store.ResetAsync("index-a", Ct);

        Assert.Equal(0, await _store.GetDocumentCountAsync("index-a", Ct));
        Assert.Equal(1, await _store.GetDocumentCountAsync("index-b", Ct));
    }
}
