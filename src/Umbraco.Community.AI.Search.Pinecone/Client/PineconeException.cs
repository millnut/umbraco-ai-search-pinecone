using System.Net;

namespace Umbraco.Community.AI.Search.Pinecone.Client;

/// <summary>
/// A request to Pinecone failed, or the index can't be used for Umbraco AI Search.
/// </summary>
public sealed class PineconeException : Exception
{
    public PineconeException(string message, HttpStatusCode? statusCode = null)
        : base(message)
        => StatusCode = statusCode;

    /// <summary>
    /// The HTTP status Pinecone returned, or null when the problem was found before or after the request.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }
}
