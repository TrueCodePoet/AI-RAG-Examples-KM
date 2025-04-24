using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular.DataFormats;
using Microsoft.KernelMemory.MemoryStorage;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.DataFormats; // Added for IContentDecoder

public class CustomTabularIngestion
{
    private readonly IMemoryDb _memoryDb;
    private readonly CosmosClient _cosmosClient;
    private readonly ITextEmbeddingGenerator _embeddingGenerator;
    private readonly ILogger _logger;
    private readonly string _databaseName;
    private readonly string _indexName;

    public CustomTabularIngestion(
        IMemoryDb memoryDb,
        CosmosClient cosmosClient,
        ITextEmbeddingGenerator embeddingGenerator,
        ILogger logger,
        string databaseName,
        string indexName)
    {
        _memoryDb = memoryDb;
        _cosmosClient = cosmosClient;
        _embeddingGenerator = embeddingGenerator;
        _logger = logger;
        _databaseName = databaseName;
        _indexName = indexName;
    }

    public async Task ImportTabularDocumentCustomAsync(
        string filePath,
        string datasetName,
        CancellationToken cancellationToken = default)
    {
        // Determine file type and use the appropriate decoder
        IContentDecoder decoder = filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? new TabularCsvDecoder()
            : (IContentDecoder)new TabularExcelDecoder();

        // Decode the file into chunks (rows)
        var fileContent = await decoder.DecodeAsync(filePath, cancellationToken);
        int totalRows = fileContent.Sections.Count;
        int successCount = 0;
        int failCount = 0;

        Console.WriteLine($"[CustomIngestion] Starting custom ingestion for {filePath} ({totalRows} rows)");

        // Ensure the index/container exists before upserting
        try
        {
            // Assuming a fixed vector size for now, ideally get from config or embedding generator
            const int vectorSize = 1536; 
            await _memoryDb.CreateIndexAsync(_indexName, vectorSize, cancellationToken);
            Console.WriteLine($"[CustomIngestion] Ensured index '{_indexName}' exists.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CustomIngestion] Failed to create or ensure index '{IndexName}' exists. Aborting ingestion.", _indexName);
            Console.WriteLine($"[CustomIngestion] ERROR: Failed to create index '{_indexName}'. Aborting.");
            return; // Stop ingestion if index creation fails
        }

        foreach (var chunk in fileContent.Sections)
        {
            try
            {
                // Generate embedding for the chunk text
                var embedding = await _embeddingGenerator.GenerateEmbeddingAsync(chunk.Content, cancellationToken);

                // Build the MemoryRecord (mimic SDK structure)
                var record = new MemoryRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    Payload = new Dictionary<string, object>(chunk.Metadata.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value))
                    {
                        ["text"] = chunk.Content
                    },
                    Vector = embedding.Data,
                    Tags = new TagCollection()
                };

                // Upsert to Cosmos DB
                var cosmosRecord = AzureCosmosDbTabularMemoryRecord.FromMemoryRecord(
                    record,
                    data: null,
                    source: chunk.Metadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString() ?? string.Empty),
                    schemaId: chunk.Metadata.ContainsKey("schema_id") ? chunk.Metadata["schema_id"] : null,
                    importBatchId: chunk.Metadata.ContainsKey("import_batch_id") ? chunk.Metadata["import_batch_id"] : null
                );

                await _cosmosClient
                    .GetDatabase(_databaseName)
                    .GetContainer(_indexName)
                    .UpsertItemAsync(
                        cosmosRecord,
                        cosmosRecord.GetPartitionKey(),
                        cancellationToken: cancellationToken);

                successCount++;
            }
            catch (Exception ex)
            {
                failCount++;
                var rowNum = chunk.Metadata.TryGetValue("rowNumber", out var rn) ? rn : "?";
                _logger.LogError(ex, "[CustomIngestion] Failed to process row {RowNumber}: {Error}", rowNum, ex.Message);
                Console.WriteLine($"[CustomIngestion] ERROR: Failed to process row {rowNum}: {ex.GetType().Name} - {ex.Message}");
            }
        }

        Console.WriteLine($"[CustomIngestion] Complete. Success: {successCount}, Failed: {failCount}, Total: {totalRows}");
    }
}
