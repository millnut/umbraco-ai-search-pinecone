using Microsoft.Extensions.DependencyInjection;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.Community.AI.Search.Pinecone.Composers;
using Umbraco.Community.AI.Search.Pinecone.Options;
using Umbraco.Community.AI.Search.Pinecone.VectorStore;

namespace Umbraco.Community.AI.Search.Pinecone.Tests;

public class PineconeVectorStoreComposerTests
{
    [Fact]
    public void ReplaceVectorStore_LeavesOnlyThePineconeStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAIVectorStore, InMemoryAIVectorStore>();

        PineconeVectorStoreComposer.ReplaceVectorStore(services);

        ServiceDescriptor descriptor = Assert.Single(services, d => d.ServiceType == typeof(IAIVectorStore));
        Assert.Equal(typeof(PineconeAIVectorStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Theory]
    [InlineData("key", "index", true)]
    [InlineData("key", null, false)]
    [InlineData(null, "index", false)]
    [InlineData(" ", "index", false)]
    [InlineData("key", "", false)]
    public void IsConfigured_NeedsApiKeyAndIndexName(string? apiKey, string? indexName, bool expected)
        => Assert.Equal(expected, new PineconeVectorStoreOptions { ApiKey = apiKey, IndexName = indexName }.IsConfigured);
}
