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

    /// <summary>
    /// Batch size for ingestion. If 1, disables batching. If >1, enables batching.
    /// </summary>
    public int BatchSize { get; set; } = 1;

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
        // Ensure we pass the memory instance and dataset name to the decoder for schema extraction
        var tabularMemory = _memoryDb as Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular.AzureCosmosDbTabularMemory;
        var loggerFactory = _logger is ILogger loggerObj && loggerObj is ILoggerFactory lf ? lf : null;

        IContentDecoder decoder;
        if (filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            decoder = new Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular.DataFormats.TabularCsvDecoder(
                null, // use default config
                tabularMemory,
                loggerFactory
            ).WithDatasetName(datasetName);
        }
        else
        {
            decoder = new Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular.DataFormats.TabularExcelDecoder(
                null, // use default config
                tabularMemory,
                loggerFactory
            ).WithDatasetName(datasetName);
        }

        // Decode the file into chunks (rows) and get the schema
        FileContent fileContent;
        TabularDataSchema? schema;
        // Use File.OpenRead to get a stream, then call the internal method
        using (var fileStream = File.OpenRead(filePath))
        {
            if (decoder is TabularCsvDecoder csvDecoder)
            {
                (fileContent, schema) = await csvDecoder.DecodeStreamInternalAsync(fileStream, filePath, cancellationToken);
            }
            else if (decoder is TabularExcelDecoder excelDecoder)
            {
                (fileContent, schema) = await excelDecoder.DecodeStreamInternalAsync(fileStream, filePath, cancellationToken);
            }
            else
            {
                // Fallback for generic IContentDecoder (though unlikely for tabular)
                // This path won't get the schema
                fileContent = await decoder.DecodeAsync(fileStream, cancellationToken);
                schema = null; 
            }
        }

        // Store the schema if available
        string schemaId = string.Empty;
        string importBatchId = string.Empty;
        if (schema != null)
        {
            // Use the tabularMemory variable already declared above
            if (tabularMemory != null)
            {
                schemaId = await tabularMemory.StoreSchemaAsync(schema, _indexName, cancellationToken);
                importBatchId = schema.ImportBatchId ?? string.Empty;
                Console.WriteLine($"[CustomIngestion] Stored schema with ID: {schemaId}, Batch ID: {importBatchId}");
            }
            else
            {
                Console.WriteLine("[CustomIngestion] WARNING: _memoryDb is not AzureCosmosDbTabularMemory, cannot store schema.");
                importBatchId = schema.ImportBatchId ?? string.Empty;
            }
        }

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

        // Prepare all records first
        var cosmosRecords = new List<AzureCosmosDbTabularMemoryRecord>();
        foreach (var chunk in fileContent.Sections)
        {
            // Ensure schema_id and importBatchId are present in metadata
            if (!chunk.Metadata.ContainsKey("schema_id") && !string.IsNullOrEmpty(schemaId))
            {
                chunk.Metadata["schema_id"] = schemaId;
            }
            if (!chunk.Metadata.ContainsKey("importBatchId") && !string.IsNullOrEmpty(importBatchId))
            {
                chunk.Metadata["importBatchId"] = importBatchId;
            }

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
                importBatchId: chunk.Metadata.ContainsKey("importBatchId") ? chunk.Metadata["importBatchId"] : importBatchId
            );

            cosmosRecords.Add(cosmosRecord);
        }

        // Group by partition key (file name)
        var groups = new Dictionary<string, List<AzureCosmosDbTabularMemoryRecord>>();
        foreach (var rec in cosmosRecords)
        {
            if (!groups.ContainsKey(rec.File))
                groups[rec.File] = new List<AzureCosmosDbTabularMemoryRecord>();
            groups[rec.File].Add(rec);
        }

        foreach (var group in groups)
        {
            var partitionKey = group.Key;
            var records = group.Value;
            if (BatchSize > 1)
            {
                // Batch insert using TransactionalBatch
                for (int i = 0; i < records.Count; i += BatchSize)
                {
                    var batch = records.GetRange(i, Math.Min(BatchSize, records.Count - i));
                    var container = _cosmosClient.GetDatabase(_databaseName).GetContainer(_indexName);
                    var transactionalBatch = container.CreateTransactionalBatch(new PartitionKey(partitionKey));
                    foreach (var rec in batch)
                    {
                        transactionalBatch.UpsertItem(rec);
                    }
                    var response = await transactionalBatch.ExecuteAsync(cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        successCount += batch.Count;
                    }
                    else
                    {
                        failCount += batch.Count;
                        _logger.LogError("[CustomIngestion] TransactionalBatch failed for partition {PartitionKey}: {StatusCode}", partitionKey, response.StatusCode);
                    }
                }
            }
            else
            {
                // Single insert (current behavior)
                foreach (var rec in records)
                {
                    try
                    {
                        await _cosmosClient
                            .GetDatabase(_databaseName)
                            .GetContainer(_indexName)
                            .UpsertItemAsync(
                                rec,
                                rec.GetPartitionKey(),
                                cancellationToken: cancellationToken);

                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        _logger.LogError(ex, "[CustomIngestion] Failed to process record for partition {PartitionKey}: {Error}", partitionKey, ex.Message);
                    }
                }
            }
        }

        // Update final summary log to include the batch ID
        Console.WriteLine($"[CustomIngestion] Complete. Success: {successCount}, Failed: {failCount}, Total: {totalRows}, Batch ID: {importBatchId}");
        if (!string.IsNullOrEmpty(importBatchId))
        {
             Console.WriteLine($"To validate in Cosmos DB, run:");
             Console.WriteLine($"SELECT COUNT(1) FROM c WHERE c.importBatchId = '{importBatchId}'");
        }
    }
}
