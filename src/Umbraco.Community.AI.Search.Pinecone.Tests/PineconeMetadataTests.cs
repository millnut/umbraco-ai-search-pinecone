using System.Text.Json;
using Umbraco.Community.AI.Search.Pinecone.VectorStore;

namespace Umbraco.Community.AI.Search.Pinecone.Tests;

public class PineconeMetadataTests
{
    [Fact]
    public void Converts_values_to_pinecone_types()
    {
        Dictionary<string, object> converted = PineconeMetadata.ToPinecone(new Dictionary<string, object>
        {
            ["text"] = "a",
            ["int"] = 3,
            ["long"] = 4L,
            ["decimal"] = 1.5m,
            ["flag"] = true,
            ["list"] = new List<string> { "x", "y" },
            ["guid"] = Guid.Empty,
            ["null"] = null!,
        });

        Assert.Equal("a", converted["text"]);
        Assert.Equal(3.0, converted["int"]);
        Assert.Equal(4.0, converted["long"]);
        Assert.Equal(1.5, converted["decimal"]);
        Assert.Equal(true, converted["flag"]);
        Assert.Equal(["x", "y"], (string[])converted["list"]);
        Assert.Equal(Guid.Empty.ToString(), converted["guid"]);
        Assert.False(converted.ContainsKey("null"));
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var original = new Dictionary<string, object> { ["text"] = "a", ["number"] = 2, ["flag"] = false, ["list"] = new[] { "x" } };
        var json = JsonSerializer.Serialize(PineconeMetadata.ToPinecone(original));

        Dictionary<string, object> read = PineconeMetadata.FromPinecone(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

        Assert.Equal("a", read["text"]);
        Assert.Equal(2.0, read["number"]);
        Assert.Equal(false, read["flag"]);
        Assert.Equal(["x"], (string[])read["list"]);

        // Metadata read back can be written again unchanged, e.g. by code that copies entries.
        Assert.Equal(json, JsonSerializer.Serialize(PineconeMetadata.ToPinecone(read)));
    }
}
