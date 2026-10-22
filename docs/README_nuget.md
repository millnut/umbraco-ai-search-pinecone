# Pinecone Vector Store for Umbraco AI Search

[![Downloads](https://img.shields.io/nuget/dt/Umbraco.Community.AI.Search.Pinecone?color=cc9900)](https://www.nuget.org/packages/Umbraco.Community.AI.Search.Pinecone/)
[![NuGet](https://img.shields.io/nuget/vpre/Umbraco.Community.AI.Search.Pinecone?color=0273B3)](https://www.nuget.org/packages/Umbraco.Community.AI.Search.Pinecone)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A vector store for [Umbraco AI Search](https://www.nuget.org/packages/Umbraco.AI.Search) that keeps your content's embeddings in a [Pinecone](https://www.pinecone.io) serverless index, instead of the CMS database.

It works with any Umbraco AI embedding provider, such as OpenAI or [Pinecone's own](https://www.nuget.org/packages/Umbraco.Community.AI.Pinecone).

This is a community package. It is not made or supported by Umbraco HQ or Pinecone.

Requires Umbraco 17 (.NET 10) and Umbraco AI Search 17.0.4 or later.

## Install

```bash
dotnet add package Umbraco.Community.AI.Search.Pinecone
```

## Set up

1. In the Pinecone console, create a serverless **dense** index with the **cosine** metric, using custom settings, **without choosing an embedding model**. An index made from a model preset stores documents, not vectors, and the package refuses it. The dimension must match your default embedding profile, for example 1536 for OpenAI's `text-embedding-3-small` or 1024 for Pinecone's `llama-text-embed-v2`.
2. Put your Pinecone API key and index name in configuration. Keep the key in an appsettings file you don't commit to source control, or in the environment variable `Umbraco__AI__Search__Pinecone__ApiKey`:

   ```json
   {
     "Umbraco": {
       "AI": {
         "Search": {
           "Pinecone": {
             "ApiKey": "your-pinecone-api-key",
             "IndexName": "umbraco-search"
           }
         }
       }
     }
   }
   ```

3. Restart the site, then rebuild the `UmbAI_Search` index from the backoffice.

Until the API key and index name are both set, Umbraco AI Search keeps using its database store, and a warning is logged at startup.

## Configuration

All settings live under `Umbraco:AI:Search:Pinecone`.

| Setting | Meaning |
|---|---|
| `ApiKey` | Required. Your Pinecone API key. |
| `IndexName` | Required. An existing serverless index that uses the cosine metric. The package never creates indexes. |
| `IndexHost` | Optional. The index's host from the Pinecone console, which saves looking it up by name. Must use `https`. |
| `NamespacePrefix` | Optional. Put in front of the namespace name, e.g. `site-a-` gives `site-a-UmbAI_Search`, so sites with the same owner can share one index. It doesn't keep them apart from each other: see [Security and data](#security-and-data). |

If you change the embedding model or its dimensions, create a new index with the new dimension and rebuild.

## Known limitations

- Pinecone is eventually consistent: a change can take a few seconds to show in search results and in the index's document count.
- If an editor saves the same content twice within a second or two, chunks from the first save can occasionally be left behind. A rebuild removes them.
- Umbraco AI Search still creates its database table for vectors. It stays empty while this store is in use.

## Security and data

### The API key

- Anyone with the key can read, change or delete everything in the indexes it reaches. Keep it out of source control: use an environment variable, user secrets or an uncommitted appsettings file. The package never logs it.
- Give the key no more access than it needs. The store reads and writes records in one index, and looks up the index's details once. If you set `IndexHost`, it doesn't need the lookup.
- Use a separate Pinecone project and key for each environment. Namespaces aren't a security boundary: a development site's key that can reach your production index can read or delete its data.

### Protected content

Umbraco AI Search indexes members-only pages too, and hides them from people who can't see them by checking the access list stored with each record. Someone who can change records in Pinecone could remove that list, and protected pages would then show in anyone's search results. Only titles and links show, not page content. That's another reason to protect the key.

### What is sent to Pinecone

For each published page and media item, including members-only ones, Pinecone stores:

- the embedding vectors of its text, in chunks. Embeddings can be partly turned back into text, so treat them as you would the content.
- its key, culture, type (`Document` or `Media`) and chunk position.
- for protected content, the access list: member group keys and, for pages protected for individual members, **member keys**.

The text itself isn't stored in Pinecone, but your embedding provider receives it to make the vectors.

### Personal data and GDPR

- If your pages hold personal data, or you protect pages for individual members, Pinecone is processing personal data on your behalf. Check Pinecone's data processing agreement and your privacy notice.
- Pinecone's free Starter plan only runs in AWS `us-east-1`, in the United States. Paid plans let you choose a region, including in the EU.
- Unpublishing or deleting content removes its vectors. Rarely, a few chunks can be left behind (see Known limitations), and a deleted member's key stays in the access list until the pages it was on are indexed again. To remove everything, rebuild the `UmbAI_Search` index, or delete the namespace or index in Pinecone.

### Cost and abuse

Each search makes one Pinecone query, and one call to your embedding provider. If your site has a public search page, rate-limit it, so nobody can use up your Pinecone or embedding quota. The package adds no endpoints of its own.
