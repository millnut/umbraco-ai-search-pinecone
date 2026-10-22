# Contributing Guidelines

Contributions to this package are most welcome!

There is no test site in the solution. To try the package in a real site, pack it and install it there (see below). The design and the reasoning behind it are in [docs/vector-store-notes.md](../docs/vector-store-notes.md).

## Building

```bash
# From the repository root
dotnet build src/Umbraco.Community.AI.Search.Pinecone.slnx
dotnet test --project src/Umbraco.Community.AI.Search.Pinecone.Tests
```

## Testing in a site

Pack with a version that has never been used before, because NuGet's global cache keeps serving the first build of any version:

```bash
VERSION="0.1.0-local.$(date +%Y%m%d%H%M)"
dotnet pack src/Umbraco.Community.AI.Search.Pinecone/Umbraco.Community.AI.Search.Pinecone.csproj \
  -c Release /p:Version=$VERSION -o /path/to/a/local/nuget/source
```

In the site, add that folder as a NuGet source, reference `Umbraco.Community.AI.Search.Pinecone` at `$VERSION`, and restore. The site also needs `Umbraco.AI`, `Umbraco.AI.Search` and an embedding provider. Then follow the set-up steps in the README.
