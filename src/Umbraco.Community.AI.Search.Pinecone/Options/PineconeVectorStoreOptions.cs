namespace Umbraco.Community.AI.Search.Pinecone.Options;

/// <summary>
/// Settings bound from the <c>Umbraco:AI:Search:Pinecone</c> section of configuration.
/// </summary>
public sealed class PineconeVectorStoreOptions
{
    public const string SectionName = "Umbraco:AI:Search:Pinecone";

    /// <summary>
    /// The Pinecone API key. Keep it out of source control: use an environment variable
    /// (<c>Umbraco__AI__Search__Pinecone__ApiKey</c>), user secrets or an uncommitted appsettings file.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The name of an existing serverless index that uses the cosine metric, with the same dimension
    /// as the default embedding profile. The package never creates indexes.
    /// </summary>
    public string? IndexName { get; set; }

    /// <summary>
    /// The index's host, from the Pinecone console. Optional: when set, the index isn't looked up by name.
    /// </summary>
    public string? IndexHost { get; set; }

    /// <summary>
    /// Put in front of each Umbraco index name to make the Pinecone namespace, e.g. <c>prod-</c> gives
    /// <c>prod-UmbAI_Search</c>. Lets several sites or environments share one index.
    /// </summary>
    public string NamespacePrefix { get; set; } = string.Empty;

    /// <summary>
    /// True when the settings the store can't run without are present.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(IndexName);
}
