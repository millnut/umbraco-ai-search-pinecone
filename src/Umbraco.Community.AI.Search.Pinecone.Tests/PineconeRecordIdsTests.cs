using Umbraco.Community.AI.Search.Pinecone.VectorStore;

namespace Umbraco.Community.AI.Search.Pinecone.Tests;

public class PineconeRecordIdsTests
{
    private const string DocumentId = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

    [Theory]
    [InlineData("en-US", 0, DocumentId + "#en-us#0")]
    [InlineData(null, 12, DocumentId + "#invariant#12")]
    [InlineData("", 1, DocumentId + "#invariant#1")]
    public void Create(string? culture, int chunkIndex, string expected)
        => Assert.Equal(expected, PineconeRecordIds.Create(DocumentId, culture, chunkIndex));

    [Fact]
    public void Prefixes_end_with_a_separator()
    {
        Assert.Equal(DocumentId + "#", PineconeRecordIds.DocumentPrefix(DocumentId));
        Assert.Equal(DocumentId + "#da-dk#", PineconeRecordIds.CulturePrefix(DocumentId, "da-DK"));
        Assert.Equal(DocumentId + "#invariant#", PineconeRecordIds.CulturePrefix(DocumentId, null));
    }

    [Theory]
    [InlineData("invariant", null)]
    [InlineData(null, null)]
    [InlineData("en-us", "en-us")]
    public void CultureFromToken(string? token, string? expected)
        => Assert.Equal(expected, PineconeRecordIds.CultureFromToken(token));
}
