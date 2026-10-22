using Microsoft.Extensions.Logging.Abstractions;
using Umbraco.Community.AI.Search.Pinecone.Client;
using Umbraco.Community.AI.Search.Pinecone.Options;
using Umbraco.Community.AI.Search.Pinecone.Tests.Fakes;
using Umbraco.Community.AI.Search.Pinecone.VectorStore;

namespace Umbraco.Community.AI.Search.Pinecone.Tests;

/// <summary>
/// Builds the store and client against a <see cref="FakePinecone"/>.
/// </summary>
internal static class TestStore
{
    public const string IndexName = "test-index";

    public static PineconeVectorStoreOptions DefaultOptions() => new()
    {
        ApiKey = FakePinecone.ApiKey,
        IndexName = IndexName,
    };

    /// <summary>
    /// The waits the client asked for between retries. It doesn't actually wait.
    /// </summary>
    public static PineconeDataClient CreateClient(FakePinecone pinecone, PineconeVectorStoreOptions? options = null, List<TimeSpan>? delays = null)
        => new(new FakeHttpClientFactory(pinecone), new TestOptionsMonitor<PineconeVectorStoreOptions>(options ?? DefaultOptions()), NullLogger<PineconeDataClient>.Instance)
        {
            Delay = (delay, _) =>
            {
                delays?.Add(delay);
                return Task.CompletedTask;
            },
        };

    public static PineconeAIVectorStore Create(FakePinecone pinecone, PineconeVectorStoreOptions? options = null)
    {
        options ??= DefaultOptions();
        return new PineconeAIVectorStore(
            CreateClient(pinecone, options),
            new TestOptionsMonitor<PineconeVectorStoreOptions>(options),
            NullLogger<PineconeAIVectorStore>.Instance);
    }
}
