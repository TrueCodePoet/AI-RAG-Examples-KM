# Azure Cosmos DB Connector for Kernel Memory

This extension provides integration between [Microsoft Kernel Memory](https://github.com/microsoft/kernel-memory) and [Azure Cosmos DB](https://azure.microsoft.com/en-us/products/cosmos-db/) for storing and retrieving memory records.

## Features

- Store memory records (including embeddings) in Azure Cosmos DB NoSQL containers.
- Retrieve records based on vector similarity using Azure Cosmos DB's native vector search capabilities (`VectorDistance` function with Cosine distance).
- Filter memory records based on tags.
- Fully implements the `IMemoryDb` interface.

## Requirements

- Azure Cosmos DB account with NoSQL API
- .NET 8.0 or later

## Configuration

To use this extension, you need to provide:

1. Azure Cosmos DB endpoint URL
2. Azure Cosmos DB API key

## Usage

### Basic Setup

```csharp
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDb;

// Create a memory builder with Azure Cosmos DB
var memory = new KernelMemoryBuilder()
    .WithAzureOpenAITextEmbeddingGeneration(
        endpoint: "https://your-openai-endpoint.com",
        apiKey: "your-openai-api-key",
        deploymentName: "your-embedding-deployment-name")
    .WithAzureCosmosDbMemory(
        endpoint: "https://your-cosmosdb-account.documents.azure.com:443/",
        apiKey: "your-cosmosdb-api-key")
    .Build();
```

### Advanced Configuration

```csharp
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDb;

// Create a custom configuration
var cosmosConfig = new AzureCosmosDbConfig
{
    Endpoint = "https://your-cosmosdb-account.documents.azure.com:443/",
    APIKey = "your-cosmosdb-api-key"
};

// Create a memory builder with the custom configuration
var memory = new KernelMemoryBuilder()
    .WithAzureOpenAITextEmbeddingGeneration(
        endpoint: "https://your-openai-endpoint.com",
        apiKey: "your-openai-api-key",
        deploymentName: "your-embedding-deployment-name")
    .WithAzureCosmosDbMemory(cosmosConfig)
    .Build();
```

## Implementation Details

The extension creates a database named "memory" (hardcoded) in your Azure Cosmos DB account. Each memory index is stored as a separate container within this database.

**Vector Search Implementation:** This connector utilizes Azure Cosmos DB's native vector search capabilities.
- When an index is created (`CreateIndexAsync`), a vector index policy (Flat index, Cosine distance) is automatically configured on the `/embedding` path, assuming the standard serialization of Kernel Memory's `Embedding` type.
- Similarity searches (`GetSimilarListAsync`) use the `VectorDistance` function in Cosmos DB queries comparing against the `c.embedding` field to perform efficient vector comparisons.

Memory records are stored with the following structure:
- `id`: The original `MemoryRecord.Id`, Base64 encoded for compatibility with Cosmos DB ID constraints.
- `file`: The file identifier derived from `MemoryRecord.Id`, used as the **partition key** for the container.
- `tags`: Collection of metadata tags.
- `embedding`: Vector representation of the memory content, indexed for vector search.
- `payload`: Additional data associated with the memory record.

## Best Practices and Lessons Learned

- **Unique IDs per Record:** Always assign a unique `Id` to each `MemoryRecord`. If the same document ID is reused, only one record will be stored per document, and previous records will be overwritten.
- **Partition Key:** Use a meaningful partition key (e.g., file/document name or logical grouping) to enable efficient queries and scaling.
- **Batching:** While the standard extension does not natively support Cosmos DB `TransactionalBatch`, you can parallelize inserts for higher throughput. For large-scale ingestion, consider batching records by partition key and using parallel tasks.
- **Error Handling:** Monitor for rate limiting (HTTP 429) and implement retry logic or reduce concurrency if needed.
- **Metadata:** Include relevant metadata in the `tags` and `payload` fields for advanced querying and traceability.

**Note:** For advanced tabular data scenarios (Excel/CSV), see the `AzureCosmosDbTabular` extension, which supports structured data, schema management, and batch ingestion.
