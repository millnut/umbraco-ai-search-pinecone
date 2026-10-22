using System.Net;
using Umbraco.Community.AI.Search.Pinecone.Client;
using Umbraco.Community.AI.Search.Pinecone.Options;
using Umbraco.Community.AI.Search.Pinecone.Tests.Fakes;

namespace Umbraco.Community.AI.Search.Pinecone.Tests;

public class PineconeDataClientTests
{
    private readonly FakePinecone _pinecone = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sends_the_api_key_and_version_on_every_request()
    {
        PineconeDataClient client = TestStore.CreateClient(_pinecone);

        await client.DescribeIndexStatsAsync(Ct);

        Assert.Equal(2, _pinecone.Requests.Count);
        Assert.All(_pinecone.Requests, r =>
        {
            Assert.Equal(FakePinecone.ApiKey, r.ApiKey);
            Assert.Equal(PineconeDataClient.ApiVersion, r.ApiVersion);
        });
    }

    [Fact]
    public async Task Looks_up_the_host_once()
    {
        PineconeDataClient client = TestStore.CreateClient(_pinecone);

        await client.DescribeIndexStatsAsync(Ct);
        await client.ListIdsAsync("ns", "doc#", Ct);

        RecordedRequestAssert(Assert.Single(_pinecone.Requests, r => r.Host == "api.pinecone.io"), $"/indexes/{TestStore.IndexName}");
        Assert.All(_pinecone.Requests.Skip(1), r => Assert.Equal(FakePinecone.Host, r.Host));
    }

    [Theory]
    [InlineData(FakePinecone.Host)]
    [InlineData("https://" + FakePinecone.Host + "/")]
    public async Task A_configured_host_skips_the_lookup(string host)
    {
        PineconeVectorStoreOptions options = TestStore.DefaultOptions();
        options.IndexHost = host;

        await TestStore.CreateClient(_pinecone, options).DescribeIndexStatsAsync(Ct);

        Assert.Equal(FakePinecone.Host, Assert.Single(_pinecone.Requests).Host);
    }

    [Theory]
    [InlineData("http://" + FakePinecone.Host)]
    [InlineData("ftp://" + FakePinecone.Host)]
    public async Task A_configured_host_must_use_https(string host)
    {
        PineconeVectorStoreOptions options = TestStore.DefaultOptions();
        options.IndexHost = host;

        PineconeException exception = await Assert.ThrowsAsync<PineconeException>(
            () => TestStore.CreateClient(_pinecone, options).DescribeIndexStatsAsync(Ct));

        Assert.Contains("must use https", exception.Message);
        Assert.Empty(_pinecone.Requests);
    }

    [Fact]
    public async Task A_configured_host_keeps_only_the_host()
    {
        PineconeVectorStoreOptions options = TestStore.DefaultOptions();
        options.IndexHost = $"https://{FakePinecone.Host}/some/path?x=1";

        await TestStore.CreateClient(_pinecone, options).DescribeIndexStatsAsync(Ct);

        Assert.Equal("/describe_index_stats", Assert.Single(_pinecone.Requests).Path);
    }

    [Fact]
    public async Task Pagination_that_never_ends_is_stopped()
    {
        _pinecone.EndlessPagination = true;
        PineconeDataClient client = TestStore.CreateClient(_pinecone);
        await client.UpsertAsync("ns", [Vector("doc#invariant#0")], Ct);

        PineconeException exception = await Assert.ThrowsAsync<PineconeException>(() => client.ListIdsAsync("ns", "doc#", Ct));

        Assert.Contains($"more than {PineconeDataClient.MaxListPages} pages", exception.Message);
        Assert.Equal(PineconeDataClient.MaxListPages, _pinecone.RequestsTo("/vectors/list").Count());
    }

    [Fact]
    public async Task A_missing_index_says_so()
    {
        _pinecone.IndexExists = false;

        PineconeException exception = await Assert.ThrowsAsync<PineconeException>(
            () => TestStore.CreateClient(_pinecone).DescribeIndexStatsAsync(Ct));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Contains($"Pinecone index '{TestStore.IndexName}' was not found", exception.Message);
    }

    [Fact]
    public async Task An_index_that_is_not_ready_says_so_and_is_looked_up_again_next_time()
    {
        _pinecone.IndexReady = false;
        PineconeDataClient client = TestStore.CreateClient(_pinecone);

        PineconeException exception = await Assert.ThrowsAsync<PineconeException>(() => client.DescribeIndexStatsAsync(Ct));
        Assert.Contains("isn't ready (state: Initializing)", exception.Message);

        _pinecone.IndexReady = true;
        await client.DescribeIndexStatsAsync(Ct);

        Assert.Equal(2, _pinecone.Requests.Count(r => r.Host == "api.pinecone.io"));
    }

    [Fact]
    public async Task A_document_schema_index_is_rejected_with_a_clear_message()
    {
        _pinecone.DocumentSchema = true;

        PineconeException exception = await Assert.ThrowsAsync<PineconeException>(
            () => TestStore.CreateClient(_pinecone).DescribeIndexStatsAsync(Ct));

        Assert.Contains("has a document schema (fields: chunk_text, title)", exception.Message);
        Assert.DoesNotContain(_pinecone.Requests, r => r.Host == FakePinecone.Host);
    }

    [Fact]
    public async Task A_rejected_key_is_reported_without_the_key()
    {
        PineconeVectorStoreOptions options = TestStore.DefaultOptions();
        options.ApiKey = "wrong-secret-key";

        PineconeException exception = await Assert.ThrowsAsync<PineconeException>(
            () => TestStore.CreateClient(_pinecone, options).DescribeIndexStatsAsync(Ct));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Contains("rejected the API key", exception.Message);
        Assert.DoesNotContain("wrong-secret-key", exception.Message);
    }

    [Fact]
    public async Task A_missing_key_fails_before_any_request()
    {
        PineconeVectorStoreOptions options = TestStore.DefaultOptions();
        options.ApiKey = null;

        await Assert.ThrowsAsync<PineconeException>(() => TestStore.CreateClient(_pinecone, options).DescribeIndexStatsAsync(Ct));

        Assert.Empty(_pinecone.Requests);
    }

    [Fact]
    public async Task Server_errors_are_retried_then_reported_with_status_and_detail()
    {
        _pinecone.FailWith = HttpStatusCode.InternalServerError;

        PineconeException exception = await Assert.ThrowsAsync<PineconeException>(
            () => TestStore.CreateClient(_pinecone).UpsertAsync("ns", [Vector("a")], Ct));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Contains("upsert records", exception.Message);
        Assert.Contains("Something went wrong", exception.Message);
        Assert.Equal(PineconeDataClient.MaxAttempts, _pinecone.RequestsTo("/vectors/upsert").Count());
    }

    [Fact]
    public async Task Rate_limits_are_retried_after_the_wait_pinecone_asks_for()
    {
        var delays = new List<TimeSpan>();
        var tooMany = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("Too many requests") };
        tooMany.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
        _pinecone.FailNext.Enqueue(tooMany);
        _pinecone.FailNext.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await TestStore.CreateClient(_pinecone, delays: delays).UpsertAsync("ns", [Vector("a")], Ct);

        Assert.Equal(3, _pinecone.RequestsTo("/vectors/upsert").Count());
        Assert.Equal(TimeSpan.FromSeconds(2), delays[0]);
        Assert.InRange(delays[1], TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1250));
        Assert.Single(_pinecone.Namespaces["ns"]);
    }

    [Fact]
    public async Task Client_errors_are_not_retried()
    {
        _pinecone.FailNext.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Vector dimension 3 does not match") });

        PineconeException exception = await Assert.ThrowsAsync<PineconeException>(
            () => TestStore.CreateClient(_pinecone).UpsertAsync("ns", [Vector("a")], Ct));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Single(_pinecone.RequestsTo("/vectors/upsert"));
    }

    private static PineconeVector Vector(string id) => new(id, [1f], new Dictionary<string, object>());

    [Fact]
    public async Task Ids_with_a_hash_are_escaped_in_the_url()
    {
        PineconeDataClient client = TestStore.CreateClient(_pinecone);
        await client.UpsertAsync("ns", [new PineconeVector("doc#en#0", [1f], new Dictionary<string, object>())], Ct);

        IReadOnlyList<PineconeRecord> records = await client.FetchAsync("ns", ["doc#en#0"], Ct);
        IReadOnlyList<string> ids = await client.ListIdsAsync("ns", "doc#en#", Ct);

        Assert.Equal("doc#en#0", Assert.Single(records).Id);
        Assert.Equal(["doc#en#0"], ids);
    }

    [Fact]
    public async Task Deletes_are_sent_in_batches_of_a_thousand()
    {
        PineconeDataClient client = TestStore.CreateClient(_pinecone);
        var ids = Enumerable.Range(0, 2500).Select(i => $"id-{i}").ToList();
        await client.UpsertAsync("ns", ids.Select(id => new PineconeVector(id, [1f], new Dictionary<string, object>())).ToList(), Ct);

        await client.DeleteAsync("ns", ids, Ct);

        Assert.Equal([1000, 1000, 500], _pinecone.RequestsTo("/vectors/delete").Select(r => r.Json!["ids"]!.AsArray().Count));
        Assert.Empty(_pinecone.Namespaces["ns"]);
    }

    private static void RecordedRequestAssert(FakePinecone.RecordedRequest request, string path)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(path, request.Path);
    }
}
