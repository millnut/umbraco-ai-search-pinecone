namespace Umbraco.Community.AI.Search.Pinecone.VectorStore;

/// <summary>
/// Builds record IDs of the form <c>{documentId}#{culture}#{chunkIndex}</c>.
/// </summary>
/// <remarks>
/// The ID is the Db stores' unique key, so upserting a chunk again overwrites it. Ending each prefix with
/// <c>#</c> lets Pinecone's list-by-prefix find one document's records, all cultures or one, without
/// matching a longer ID that starts the same way. Umbraco's document IDs are GUIDs, which never contain <c>#</c>.
/// </remarks>
internal static class PineconeRecordIds
{
    /// <summary>
    /// Stands in for a null culture, in IDs and in the <c>culture</c> metadata, because Pinecone can't store
    /// a null and filtering on a missing field isn't documented. No culture code can clash with it.
    /// </summary>
    public const string Invariant = "invariant";

    /// <summary>
    /// The culture as stored: lower-cased so lookups don't depend on how the caller wrote it, or
    /// <see cref="Invariant"/> for invariant content.
    /// </summary>
    public static string CultureToken(string? culture)
        => string.IsNullOrEmpty(culture) ? Invariant : culture.ToLowerInvariant();

    /// <summary>
    /// The culture to hand back to Umbraco: null for invariant content.
    /// </summary>
    public static string? CultureFromToken(string? token)
        => string.IsNullOrEmpty(token) || token == Invariant ? null : token;

    public static string Create(string documentId, string? culture, int chunkIndex)
        => $"{documentId}#{CultureToken(culture)}#{chunkIndex}";

    /// <summary>
    /// Matches every record of a document, in every culture.
    /// </summary>
    public static string DocumentPrefix(string documentId) => $"{documentId}#";

    /// <summary>
    /// Matches a document's records in one culture, or its invariant records when <paramref name="culture"/> is null.
    /// </summary>
    public static string CulturePrefix(string documentId, string? culture) => $"{documentId}#{CultureToken(culture)}#";
}
