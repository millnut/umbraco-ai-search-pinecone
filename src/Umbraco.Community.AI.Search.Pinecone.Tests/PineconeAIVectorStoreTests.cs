using System.Text.Json.Nodes;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.Community.AI.Search.Pinecone.Client;
using Umbraco.Community.AI.Search.Pinecone.Options;
using Umbraco.Community.AI.Search.Pinecone.Tests.Fakes;
using Umbraco.Community.AI.Search.Pinecone.VectorStore;

namespace Umbraco.Community.AI.Search.Pinecone.Tests;

public class PineconeAIVectorStoreTests
{
    private const string IndexName = TestStore.IndexName;
    private const string DocumentId = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

    private readonly FakePinecone _pinecone = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Upsert_writes_one_record_with_the_documented_id_and_metadata()
    {
        var metadata = new Dictionary<string, object>
        {
            ["objectType"] = "Document",
            ["chunkIndex"] = 99, // the store's own value wins
            ["totalChunks"] = 3,
            ["accessIds"] = "a,b",
        };

        await TestStore.Create(_pinecone).UpsertAsync(IndexName, DocumentId, "en-US", 1, new float[] { 0.1f, 0.2f }, metadata, Ct);

        JsonNode upsert = Assert.Single(_pinecone.RequestsTo("/vectors/upsert")).Json!;
        Assert.Equal(IndexName, upsert["namespace"]!.GetValue<string>());
        JsonNode record = Assert.Single(upsert["vectors"]!.AsArray())!;
        Assert.Equal($"{DocumentId}#en-us#1", record["id"]!.GetValue<string>());
        Assert.Equal([0.1f, 0.2f], record["values"]!.AsArray().Select(v => v!.GetValue<float>()));
        Assert.Equal(
            JsonNode.Parse($$"""
                {"objectType":"Document","chunkIndex":1,"totalChunks":3,"accessIds":"a,b","documentId":"{{DocumentId}}","culture":"en-us"}
                """),
            record["metadata"],
            JsonNode.DeepEquals);
    }

    [Fact]
    public async Task Invariant_records_use_the_invariant_token()
    {
        await TestStore.Create(_pinecone).UpsertAsync(IndexName, DocumentId, null, 0, new float[] { 1f }, cancellationToken: Ct);

        StoredRecordAssert(_pinecone, $"{DocumentId}#invariant#0", "invariant");
    }

    [Fact]
    public async Task Namespace_prefix_is_put_in_front_of_the_index_name()
    {
        PineconeVectorStoreOptions options = TestStore.DefaultOptions();
        options.NamespacePrefix = "prod-";

        await TestStore.Create(_pinecone, options).UpsertAsync(IndexName, DocumentId, null, 0, new float[] { 1f }, cancellationToken: Ct);

        Assert.Equal(["prod-" + IndexName], _pinecone.Namespaces.Keys);
    }

    [Fact]
    public async Task Search_filters_on_culture_and_asks_for_metadata_only()
    {
        PineconeAIVectorStore store = TestStore.Create(_pinecone);

        await store.SearchAsync(IndexName, new float[] { 1f, 0f }, "en-US", 5, Ct);
        await store.SearchAsync(IndexName, new float[] { 1f, 0f }, null, 20_000, Ct);

        var queries = _pinecone.RequestsTo("/query").Select(r => r.Json!).ToList();
        Assert.Equal(JsonNode.Parse("""{"culture":{"$in":["en-us","invariant"]}}"""), queries[0]["filter"], JsonNode.DeepEquals);
        Assert.Equal(JsonNode.Parse("""{"culture":{"$eq":"invariant"}}"""), queries[1]["filter"], JsonNode.DeepEquals);
        Assert.Equal(5, queries[0]["topK"]!.GetValue<int>());
        Assert.Equal(PineconeDataClient.MaxTopK, queries[1]["topK"]!.GetValue<int>());
        Assert.True(queries[0]["includeMetadata"]!.GetValue<bool>());
        Assert.False(queries[0]["includeValues"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Search_matches_cultures_whatever_their_case()
    {
        PineconeAIVectorStore store = TestStore.Create(_pinecone);
        await store.UpsertAsync(IndexName, DocumentId, "en-US", 0, new float[] { 1f, 0f }, cancellationToken: Ct);

        IReadOnlyList<AIVectorSearchResult> results = await store.SearchAsync(IndexName, new float[] { 1f, 0f }, "EN-us", cancellationToken: Ct);

        Assert.Equal(DocumentId, Assert.Single(results).DocumentId);
    }

    [Fact]
    public async Task Search_returns_the_metadata_the_searcher_reads_unchanged()
    {
        PineconeAIVectorStore store = TestStore.Create(_pinecone);
        var metadata = new Dictionary<string, object> { ["objectType"] = "Media", ["accessIds"] = "g1,g2", ["totalChunks"] = 2 };
        await store.UpsertAsync(IndexName, DocumentId, null, 0, new float[] { 1f, 0f }, metadata, Ct);

        AIVectorSearchResult result = Assert.Single(await store.SearchAsync(IndexName, new float[] { 1f, 0f }, cancellationToken: Ct));

        Assert.Equal("Media", result.Metadata!["objectType"]);
        Assert.Equal("g1,g2", result.Metadata["accessIds"]);
        Assert.Equal(2.0, result.Metadata["totalChunks"]);
        Assert.Equal(1.0, result.Score, 6);
    }

    [Fact]
    public async Task Search_with_no_results_wanted_makes_no_request()
    {
        Assert.Empty(await TestStore.Create(_pinecone).SearchAsync(IndexName, new float[] { 1f }, topK: 0, cancellationToken: Ct));
        Assert.Empty(_pinecone.Requests);
    }

    [Fact]
    public async Task Delete_document_follows_pagination_and_leaves_other_documents()
    {
        PineconeAIVectorStore store = TestStore.Create(_pinecone);
        const string other = "3fa85f64-5717-4562-b3fc-2c963f66afa7";
        for (var i = 0; i < 250; i++)
        {
            await store.UpsertAsync(IndexName, DocumentId, i % 2 == 0 ? "en" : null, i, new float[] { 1f }, cancellationToken: Ct);
        }

        await store.UpsertAsync(IndexName, other, null, 0, new float[] { 1f }, cancellationToken: Ct);

        await store.DeleteDocumentAsync(IndexName, DocumentId, Ct);

        Assert.Equal([$"{other}#invariant#0"], _pinecone.Namespaces[IndexName].Keys);
        Assert.Equal(3, _pinecone.RequestsTo("/vectors/list").Count());
        Assert.All(_pinecone.RequestsTo("/vectors/list"), r => Assert.Equal($"{DocumentId}#", r.Query["prefix"].Single()));
        Assert.Single(_pinecone.RequestsTo("/vectors/delete"));
    }

    [Fact]
    public async Task Delete_with_no_culture_removes_invariant_records_only()
    {
        PineconeAIVectorStore store = TestStore.Create(_pinecone);
        await store.UpsertAsync(IndexName, DocumentId, null, 0, new float[] { 1f }, cancellationToken: Ct);
        await store.UpsertAsync(IndexName, DocumentId, "en", 0, new float[] { 1f }, cancellationToken: Ct);

        await store.DeleteAsync(IndexName, DocumentId, null, Ct);

        Assert.Equal([$"{DocumentId}#en#0"], _pinecone.Namespaces[IndexName].Keys);
    }

    [Fact]
    public async Task Deleting_from_an_empty_namespace_sends_no_delete()
    {
        await TestStore.Create(_pinecone).DeleteDocumentAsync(IndexName, DocumentId, Ct);

        Assert.Single(_pinecone.RequestsTo("/vectors/list"));
        Assert.Empty(_pinecone.RequestsTo("/vectors/delete"));
    }

    [Fact]
    public async Task Get_vectors_returns_values_cultures_and_metadata()
    {
        PineconeAIVectorStore store = TestStore.Create(_pinecone);
        await store.UpsertAsync(IndexName, DocumentId, "en", 1, new float[] { 0f, 1f }, new Dictionary<string, object> { ["objectType"] = "Document" }, Ct);
        await store.UpsertAsync(IndexName, DocumentId, null, 0, new float[] { 1f, 0f }, cancellationToken: Ct);

        IReadOnlyList<AIVectorEntry> entries = await store.GetVectorsByDocumentAsync(IndexName, DocumentId, cancellationToken: Ct);

        Assert.Collection(
            entries,
            invariant =>
            {
                Assert.Null(invariant.Culture);
                Assert.Equal(0, invariant.ChunkIndex);
                Assert.Equal([1f, 0f], invariant.Vector.ToArray());
            },
            english =>
            {
                Assert.Equal("en", english.Culture);
                Assert.Equal(1, english.ChunkIndex);
                Assert.Equal(DocumentId, english.DocumentId);
                Assert.Equal("Document", english.Metadata!["objectType"]);
            });
    }

    [Fact]
    public async Task Get_vectors_fetches_in_pages()
    {
        PineconeAIVectorStore store = TestStore.Create(_pinecone);
        for (var i = 0; i < 150; i++)
        {
            await store.UpsertAsync(IndexName, DocumentId, null, i, new float[] { 1f }, cancellationToken: Ct);
        }

        IReadOnlyList<AIVectorEntry> entries = await store.GetVectorsByDocumentAsync(IndexName, DocumentId, cancellationToken: Ct);

        Assert.Equal(Enumerable.Range(0, 150), entries.Select(e => e.ChunkIndex));
        Assert.Equal([100, 50], _pinecone.RequestsTo("/vectors/fetch").Select(r => r.Query["ids"].Count()));
    }

    [Fact]
    public async Task Reset_of_a_namespace_that_does_not_exist_succeeds()
    {
        await TestStore.Create(_pinecone).ResetAsync(IndexName, Ct);

        Assert.Single(_pinecone.RequestsTo($"/namespaces/{IndexName}"));
    }

    [Fact]
    public async Task Wrong_dimension_fails_with_a_clear_message_before_writing()
    {
        _pinecone.Dimension = 1024;

        PineconeException exception = await Assert.ThrowsAsync<PineconeException>(
            () => TestStore.Create(_pinecone).UpsertAsync(IndexName, DocumentId, null, 0, new float[1536], cancellationToken: Ct));

        Assert.Contains("has 1024 dimensions, but the embedding profile produces 1536", exception.Message);
        Assert.Empty(_pinecone.RequestsTo("/vectors/upsert"));
    }

    [Fact]
    public async Task A_metric_other_than_cosine_fails_with_a_clear_message()
    {
        _pinecone.Metric = "dotproduct";

        PineconeException exception = await Assert.ThrowsAsync<PineconeException>(
            () => TestStore.Create(_pinecone).SearchAsync(IndexName, new float[] { 1f }, cancellationToken: Ct));

        Assert.Contains("uses the dotproduct metric", exception.Message);
    }

    [Fact]
    public async Task The_index_is_checked_once()
    {
        _pinecone.Dimension = 2;
        PineconeAIVectorStore store = TestStore.Create(_pinecone);

        await store.UpsertAsync(IndexName, DocumentId, null, 0, new float[] { 1f, 0f }, cancellationToken: Ct);
        await store.UpsertAsync(IndexName, DocumentId, null, 1, new float[] { 1f, 0f }, cancellationToken: Ct);
        await store.SearchAsync(IndexName, new float[] { 1f, 0f }, cancellationToken: Ct);

        Assert.Single(_pinecone.RequestsTo("/describe_index_stats"));
    }

    private static void StoredRecordAssert(FakePinecone pinecone, string id, string culture)
    {
        FakePinecone.StoredRecord record = pinecone.Namespaces[IndexName][id];
        Assert.Equal(culture, record.Metadata["culture"]!.GetValue<string>());
    }
}
