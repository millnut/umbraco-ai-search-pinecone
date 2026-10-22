using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.AI.Search.Startup;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Community.AI.Search.Pinecone.Client;
using Umbraco.Community.AI.Search.Pinecone.Options;
using Umbraco.Community.AI.Search.Pinecone.VectorStore;

namespace Umbraco.Community.AI.Search.Pinecone.Composers;

/// <summary>
/// Replaces Umbraco AI Search's database vector store with the Pinecone one, when Pinecone is configured.
/// </summary>
[ComposeAfter(typeof(UmbracoAISearchComposer))]
public sealed class PineconeVectorStoreComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        IConfigurationSection section = builder.Config.GetSection(PineconeVectorStoreOptions.SectionName);
        builder.Services.AddOptions<PineconeVectorStoreOptions>().Bind(section);

        // Read once here rather than through IOptions because it decides what gets registered at all.
        if ((section.Get<PineconeVectorStoreOptions>() ?? new PineconeVectorStoreOptions()).IsConfigured)
        {
            // PineconeDataClient retries transient failures itself; this caps each attempt.
            builder.Services.AddHttpClient(PineconeConstants.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
            builder.Services.AddSingleton<PineconeDataClient>();
            ReplaceVectorStore(builder.Services);
        }
        else
        {
            // Leave the database store in place so search keeps working, and say why.
            builder.AddNotificationHandler<UmbracoApplicationStartingNotification, PineconeNotConfiguredWarning>();
        }
    }

    /// <summary>
    /// Removes every <see cref="IAIVectorStore"/> registration and adds the Pinecone store. Runs after
    /// Umbraco AI Search's composer, so its database store is already registered.
    /// </summary>
    internal static void ReplaceVectorStore(IServiceCollection services)
    {
        services.RemoveAll<IAIVectorStore>();
        services.AddSingleton<IAIVectorStore, PineconeAIVectorStore>();
    }
}
