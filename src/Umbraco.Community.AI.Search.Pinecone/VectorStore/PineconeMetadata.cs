using System.Globalization;
using System.Text.Json;

namespace Umbraco.Community.AI.Search.Pinecone.VectorStore;

/// <summary>
/// Converts metadata between Umbraco AI Search and Pinecone, which only stores strings, numbers, bools and
/// lists of strings.
/// </summary>
internal static class PineconeMetadata
{
    // Keys the store writes itself. They win over the indexer's metadata of the same name.
    public const string DocumentIdKey = "documentId";
    public const string CultureKey = "culture";
    public const string ChunkIndexKey = "chunkIndex";

    /// <summary>
    /// Converts the indexer's metadata to Pinecone values. Nulls are dropped, because Pinecone doesn't accept
    /// them, and anything without a Pinecone equivalent is stored as text.
    /// </summary>
    public static Dictionary<string, object> ToPinecone(IDictionary<string, object>? metadata)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (metadata is null)
        {
            return result;
        }

        foreach ((var key, var value) in metadata)
        {
            if (ToPineconeValue(value) is { } converted)
            {
                result[key] = converted;
            }
        }

        return result;
    }

    /// <summary>
    /// Converts Pinecone metadata back: strings, doubles, bools and string arrays.
    /// </summary>
    public static Dictionary<string, object> FromPinecone(IDictionary<string, JsonElement>? metadata)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (metadata is null)
        {
            return result;
        }

        foreach ((var key, JsonElement value) in metadata)
        {
            if (FromJson(value) is { } converted)
            {
                result[key] = converted;
            }
        }

        return result;
    }

    private static object? ToPineconeValue(object? value) => value switch
    {
        null => null,
        string or bool => value,
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
            => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        JsonElement element => FromJson(element),
        IEnumerable<string> strings => strings.ToArray(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static object? FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText()).ToArray(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };
}
