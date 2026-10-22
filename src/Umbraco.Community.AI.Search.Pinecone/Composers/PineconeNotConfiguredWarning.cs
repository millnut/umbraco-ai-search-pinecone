using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Community.AI.Search.Pinecone.Options;

namespace Umbraco.Community.AI.Search.Pinecone.Composers;

/// <summary>
/// Logs at startup that the Pinecone store isn't in use because its settings are missing.
/// </summary>
internal sealed class PineconeNotConfiguredWarning : INotificationHandler<UmbracoApplicationStartingNotification>
{
    private readonly ILogger<PineconeNotConfiguredWarning> _logger;

    public PineconeNotConfiguredWarning(ILogger<PineconeNotConfiguredWarning> logger) => _logger = logger;

    public void Handle(UmbracoApplicationStartingNotification notification)
        => _logger.LogWarning(
            "Pinecone vector store is installed but not configured: set {Section}:ApiKey and {Section}:IndexName. " +
            "Umbraco AI Search is using its database vector store until then.",
            PineconeVectorStoreOptions.SectionName,
            PineconeVectorStoreOptions.SectionName);
}
