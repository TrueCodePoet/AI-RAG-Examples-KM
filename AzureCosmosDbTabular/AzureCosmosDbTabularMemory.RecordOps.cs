// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryStorage;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Azure Cosmos DB implementation of <see cref="IMemoryDb"/> for tabular data.
/// </summary>
internal sealed partial class AzureCosmosDbTabularMemory
{
    /// <inheritdoc/>
    public async Task<string> UpsertAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        // Extract dataset name from tags if available
        string? datasetName = null;
        if (record.Tags.TryGetValue("dataset_name", out var datasetNames)) 
        {
                    // Check the type of datasetNames using GetType()
                    object datasetNamesObj = datasetNames;
                    Type datasetNamesType = datasetNamesObj.GetType();
                    
                    if (datasetNamesType == typeof(string))
                    {
                        // Handle string type
                        datasetName = (string)datasetNamesObj;
                    }
                    else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(datasetNamesType) && 
                             datasetNamesType != typeof(string)) // strings are IEnumerable<char> so we exclude them
                    {
                        // Handle any collection type
                        var enumerable = (System.Collections.IEnumerable)datasetNamesObj;
                        var enumerator = enumerable.GetEnumerator();
                        
                        if (enumerator.MoveNext() && enumerator.Current != null)
                        {
                            datasetName = enumerator.Current.ToString();
                        }
                    }
                    else if (datasetNamesObj != null)
                    {
                        datasetName = datasetNamesObj.ToString();
                    }
        }

        // Extract source file name from tags if available
        string sourceFileName = "excel_import";
        if (record.Payload.TryGetValue("file", out var sourceFiles) && 
            sourceFiles != null)
        {
                    // Check the type of sourceFiles using GetType()
                    object sourceFilesObj = sourceFiles;
                    Type sourceFilesType = sourceFilesObj.GetType();
                    
                    if (sourceFilesType == typeof(string))
                    {
                        // Handle string type
                        string fileName = (string)sourceFilesObj;
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            sourceFileName = fileName;
                        }
                    }
                    else if (sourceFilesType == typeof(List<string>) || 
                             (typeof(IList<string>).IsAssignableFrom(sourceFilesType) && sourceFilesType != typeof(string)))
                    {
                        // Handle IList<string> type
                        var filesList = (IList<string>)sourceFilesObj;
                        if (filesList.Count > 0)
                        {
                            sourceFileName = filesList[0];
                        }
                    }
                    else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(sourceFilesType) && 
                             sourceFilesType != typeof(string))
                    {
                        // Handle any other collection type
                        var enumerable = (System.Collections.IEnumerable)sourceFilesObj;
                        var enumerator = enumerable.GetEnumerator();
                        
                        if (enumerator.MoveNext() && enumerator.Current != null)
                        {
                            sourceFileName = enumerator.Current.ToString();
                        }
                    }
                    else if (sourceFilesObj != null)
                    {
                        // Handle any other type by converting to string
                        string stringValue = sourceFilesObj.ToString();
                        if (stringValue != null)
                        {
                            sourceFileName = stringValue;
                        }
                    }
        }

        // Variables to store schema ID and import batch ID
        string schemaId = string.Empty;
        string importBatchId = string.Empty;
        
        // Extract schema ID and import batch ID from the text field in the payload
        string recordText = string.Empty;
        if (record.Payload.TryGetValue("text", out var recordTextObj) && recordTextObj is string textValue && !string.IsNullOrEmpty(textValue))
        {
            recordText = textValue;
            
            // Extract schema ID from text
            int schemaIdStart = recordText.IndexOf("schema_id is ");
            if (schemaIdStart >= 0)
            {
                schemaIdStart += "schema_id is ".Length;
                int schemaIdEnd = recordText.IndexOf(".", schemaIdStart);
                if (schemaIdEnd > schemaIdStart)
                {
                    schemaId = recordText.Substring(schemaIdStart, schemaIdEnd - schemaIdStart);
                    Console.WriteLine($"UpsertAsync: Extracted schema ID={schemaId} from text");
                }
            }
            
            // Extract import batch ID from text
            int importBatchIdStart = recordText.IndexOf("import_batch_id is ");
            if (importBatchIdStart >= 0)
            {
                importBatchIdStart += "import_batch_id is ".Length;
                int importBatchIdEnd = recordText.IndexOf(".", importBatchIdStart);
                if (importBatchIdEnd > importBatchIdStart)
                {
                    importBatchId = recordText.Substring(importBatchIdStart, importBatchIdEnd - importBatchIdStart);
                    Console.WriteLine($"UpsertAsync: Extracted import batch ID={importBatchId} from text");
                }
            }
        }
        
        // Fallback: Check for schema ID and import batch ID in the record's payload
        if (string.IsNullOrEmpty(schemaId) && 
            record.Payload.TryGetValue("schema_id", out var schemaIdValue) && 
            schemaIdValue is string schemaIdStr && 
            !string.IsNullOrEmpty(schemaIdStr))
        {
            schemaId = schemaIdStr;
            Console.WriteLine($"UpsertAsync: Found schema ID {schemaId} in record payload");
        }
        
        if (string.IsNullOrEmpty(importBatchId) && 
            record.Payload.TryGetValue("import_batch_id", out var importBatchIdValue) && 
            importBatchIdValue is string importBatchIdStr && 
            !string.IsNullOrEmpty(importBatchIdStr))
        {
            importBatchId = importBatchIdStr;
            Console.WriteLine($"UpsertAsync: Found import batch ID {importBatchId} in record payload");
        }

        // Extract tabular data from the custom field
        Dictionary<string, object> tabularData = new();
        if (record.Payload.TryGetValue("__custom_tabular_data", out var customTabularDataObj) && 
            customTabularDataObj is string customTabularDataStr)
        {
            try
            {
                tabularData = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    customTabularDataStr, 
                    AzureCosmosDbTabularConfig.DefaultJsonSerializerOptions) ?? new Dictionary<string, object>();
                
                // Check if schema ID and import batch ID are in the deserialized tabular data
                if (string.IsNullOrEmpty(schemaId) && tabularData.TryGetValue("schema_id", out var tabularSchemaId))
                {
    // Handle different types using GetType()
    object schemaIdObj = tabularSchemaId;
    Type schemaIdType = schemaIdObj.GetType();
    
    if (schemaIdType == typeof(string))
    {
        // Handle string type
        schemaId = (string)schemaIdObj;
        Console.WriteLine($"Extracted schema ID from tabular data: {schemaId}");
    }
    else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(schemaIdType) && 
             schemaIdType != typeof(string)) // strings are IEnumerable<char> so we exclude them
    {
        // Handle any collection type
        var enumerable = (System.Collections.IEnumerable)schemaIdObj;
        var enumerator = enumerable.GetEnumerator();
        
        if (enumerator.MoveNext() && enumerator.Current != null)
        {
            schemaId = enumerator.Current.ToString()!;
            Console.WriteLine($"Extracted schema ID from tabular data list: {schemaId}");
        }
    }
    else if (schemaIdObj != null)
    {
        // Handle any other type by converting to string
        schemaId = schemaIdObj.ToString();
        Console.WriteLine($"Extracted and converted schema ID from tabular data: {schemaId}");
    }
                }
                
                if (string.IsNullOrEmpty(importBatchId) && tabularData.TryGetValue("import_batch_id", out var tabularImportBatchId))
                {
                    // Handle different types using GetType()
                    object importBatchIdObj = tabularImportBatchId;
                    Type importBatchIdType = importBatchIdObj.GetType();
                    
                    if (importBatchIdType == typeof(string))
                    {
                        // Handle string type
                        importBatchId = (string)importBatchIdObj;
                        Console.WriteLine($"Extracted import batch ID from tabular data: {importBatchId}");
                    } 
                    else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(importBatchIdType) && 
                             importBatchIdType != typeof(string)) // strings are IEnumerable<char> so we exclude them
                    {
                        // Handle any collection type
                        var enumerable = (System.Collections.IEnumerable)importBatchIdObj;
                        var enumerator = enumerable.GetEnumerator();
                        
                        if (enumerator.MoveNext() && enumerator.Current != null)
                        {
                            importBatchId = enumerator.Current.ToString()!;
                            Console.WriteLine($"Extracted import batch ID from tabular data list: {importBatchId}");
                        }
                    }
                    else if (importBatchIdObj != null)
                    {
                        // Handle any other type by converting to string
                        importBatchId = importBatchIdObj.ToString();
                        Console.WriteLine($"Extracted and converted import batch ID from tabular data: {importBatchId}");
                    }
                }
            }
            catch (Exception ex)
            {
                this._logger.LogWarning(ex, "Failed to deserialize custom tabular data for record {Id}", record.Id);
            }
        }

        // Check if we need to extract schema information
        if (this._config.EnableSchemaManagement && 
            this._config.ExtractSchemaOnImport && 
            !string.IsNullOrEmpty(datasetName) && 
            tabularData.Count > 0)
        {
        // Create or update schema and get its ID and import batch ID
            (schemaId, importBatchId) = await this.CreateOrUpdateSchemaAsync(
                datasetName, 
                sourceFileName, 
                tabularData, 
                index, 
                cancellationToken).ConfigureAwait(false);
            
            // Log the schema ID and import batch ID for debugging
            this._logger.LogDebug("Created/updated schema with ID {SchemaId} and import batch ID {ImportBatchId}", 
                schemaId, importBatchId);
        }

        // Log the schema ID and import batch ID before creating the record
        Console.WriteLine($"UpsertAsync: About to create record with schemaId={schemaId}, importBatchId={importBatchId}");
        
        // Create a source dictionary with worksheet and row information
        Dictionary<string, string> sourceInfo = new();
        
        // Extract source information from the text field in the payload
        // We already have the text in recordText, so we can reuse it
        if (!string.IsNullOrEmpty(recordText))
        {
            // Check if text is in the format "Record from worksheet SheetName, row 123: ..."
            if (recordText.StartsWith("Record from worksheet"))
            {
                int worksheetStart = recordText.IndexOf("Record from worksheet ") + "Record from worksheet ".Length;
                int rowStart = recordText.IndexOf(", row ");
                if (rowStart > worksheetStart)
                {
                    string worksheet = recordText.Substring(worksheetStart, rowStart - worksheetStart);
                    
                    int rowEnd = recordText.IndexOf(":", rowStart);
                    if (rowEnd > rowStart)
                    {
                        string rowStr = recordText.Substring(rowStart + ", row ".Length, rowEnd - (rowStart + ", row ".Length));
                        if (int.TryParse(rowStr, out int rowNum))
                        {
                            sourceInfo["_worksheet"] = worksheet;
                            sourceInfo["_rowNumber"] = rowNum.ToString();
                            Console.WriteLine($"UpsertAsync: Extracted worksheet={worksheet}, rowNumber={rowNum} from text");
                        }
                    }
                }
            }
        }
        
        // Try to extract from metadata if not found in text
        if (!sourceInfo.ContainsKey("_worksheet") && record.Payload.TryGetValue("worksheetName", out var worksheetNameObj) && worksheetNameObj is string worksheetName)
        {
            sourceInfo["_worksheet"] = worksheetName;
            Console.WriteLine($"UpsertAsync: Extracted worksheet={worksheetName} from metadata");
        }
        
        if (!sourceInfo.ContainsKey("_rowNumber") && record.Payload.TryGetValue("rowNumber", out var rowNumberObj) && rowNumberObj is string rowNumber)
        {
            sourceInfo["_rowNumber"] = rowNumber;
            Console.WriteLine($"UpsertAsync: Extracted rowNumber={rowNumber} from metadata");
        }
        
        // Add schema ID and import batch ID to source info
        if (!string.IsNullOrEmpty(schemaId))
        {
            sourceInfo["schema_id"] = schemaId;
        }
        
        if (!string.IsNullOrEmpty(importBatchId))
        {
            sourceInfo["import_batch_id"] = importBatchId;
        }
        
        // Create the Cosmos DB record from the memory record, including schema ID and import batch ID
        var memoryRecord = AzureCosmosDbTabularMemoryRecord.FromMemoryRecord(
            record, 
            data: tabularData,
            source: sourceInfo,
            schemaId: schemaId, 
            importBatchId: importBatchId);
            
        // Ensure schema ID and import batch ID are in the payload
        // Since we can't modify SchemaId and ImportBatchId properties after initialization (they use init accessors),
        // we need to ensure they're properly set during initialization via FromMemoryRecord
        if (!string.IsNullOrEmpty(schemaId) && !memoryRecord.Payload.ContainsKey("schema_id"))
        {
            Console.WriteLine($"UpsertAsync: Adding schema_id to payload: {schemaId}");
            memoryRecord.Payload["schema_id"] = schemaId;
        }
        
        if (!string.IsNullOrEmpty(importBatchId) && !memoryRecord.Payload.ContainsKey("import_batch_id"))
        {
            Console.WriteLine($"UpsertAsync: Adding import_batch_id to payload: {importBatchId}");
            memoryRecord.Payload["import_batch_id"] = importBatchId;
        }
        
        // Log the final state of the record before saving to the database
        Console.WriteLine($"UpsertAsync: Final record state - SchemaId={memoryRecord.SchemaId}, ImportBatchId={memoryRecord.ImportBatchId}");

        // Save the record to the database
        var result = await this._cosmosClient
            .GetDatabase(this._databaseName)
            .GetContainer(index)
            .UpsertItemAsync(
                memoryRecord,
                memoryRecord.GetPartitionKey(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
                
        Console.WriteLine($"UpsertAsync: Record saved to database with ID {result.Resource.Id}");

        return result.Resource.Id;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<(MemoryRecord, double)> GetSimilarListAsync(
        string index,
        string text,
        ICollection<MemoryFilter>? filters = null,
        double minRelevance = 0,
        int limit = 1,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Generate the embedding for the query text
        var queryEmbedding = await this._embeddingGenerator.GenerateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);

        // Process filters to extract both standard tag filters and structured data filters
        var (whereCondition, filterParameters) = this.ProcessFilters("c", filters);

        // Construct the vector search query using the root path
        string vectorFieldQueryPath = $"c.{AzureCosmosDbTabularMemoryRecord.VectorField}"; // Reverted path c.embedding
        var sql = $"""
                   SELECT TOP @limit
                     {AzureCosmosDbTabularMemoryRecord.Columns("c", withEmbeddings)}, VectorDistance({vectorFieldQueryPath}, @queryEmbedding) AS SimilarityScore
                   FROM c
                   {whereCondition}
                   ORDER BY VectorDistance({vectorFieldQueryPath}, @queryEmbedding) ASC
                   """; // ASC order because lower distance means higher similarity

        var queryDefinition = new QueryDefinition(sql)
            .WithParameter("@limit", limit)
            .WithParameter("@queryEmbedding", queryEmbedding.Data.ToArray()); // Pass embedding data array as parameter

        // Add filter parameters
        foreach (var (name, value) in filterParameters)
        {
            queryDefinition = queryDefinition.WithParameter(name, value);
        }

        this._logger.LogTrace("Executing vector search query: {Query}", queryDefinition.QueryText);

        // Execute the query
        using var feedIterator = this._cosmosClient
            .GetDatabase(this._databaseName)
            .GetContainer(index)
            .GetItemQueryIterator<TabularMemoryRecordResult>(queryDefinition);

        while (feedIterator.HasMoreResults)
        {
            FeedResponse<TabularMemoryRecordResult> response;
            try
            {
                response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (CosmosException ex) when (ex.Message.Contains("VectorDistance"))
            {
                // Provide a more specific error if VectorDistance fails (e.g., index not configured correctly)
                this._logger.LogError(ex, "Vector search failed. Ensure the vector index path '/{VectorField}' is correctly configured on container '{Index}'.", AzureCosmosDbTabularMemoryRecord.VectorField, index); // Updated log message path
                // Re-throw or handle as appropriate, here we break the loop
                yield break;
            }

            foreach (var memoryRecord in response)
            {
                // Convert Cosine distance (0 to 2) to relevance score (1 to 0)
                // Cosine Similarity = (2 - Cosine Distance) / 2
                double cosineDistance = memoryRecord.SimilarityScore;
                double relevanceScore = (2.0 - cosineDistance) / 2.0;

                this._logger.LogDebug("ID: {Id}, Distance: {Distance}, Relevance: {Relevance}", memoryRecord.Id, cosineDistance, relevanceScore);

                if (relevanceScore >= minRelevance)
                {
                    yield return (memoryRecord.ToMemoryRecord(withEmbeddings), relevanceScore);
                }
                else
                {
                    this._logger.LogDebug("ID: {Id} filtered out by minRelevance ({Relevance} < {MinRelevance})", memoryRecord.Id, relevanceScore, minRelevance);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<MemoryRecord> GetListAsync(
        string index,
        ICollection<MemoryFilter>? filters = null,
        int limit = 1,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Process filters to extract both standard tag filters and structured data filters
        var (whereCondition, parameters) = this.ProcessFilters("c", filters);

        var sql = $"""
                   SELECT Top @limit
                     {AzureCosmosDbTabularMemoryRecord.Columns("c", withEmbeddings)}
                   FROM
                     c
                   {whereCondition}
                   """; // Using @limit instead of @topN

        var queryDefinition = new QueryDefinition(sql)
            .WithParameter("@limit", limit);

        // Add all parameters from the filters
        foreach (var (name, value) in parameters)
        {
            queryDefinition.WithParameter(name, value);
        }

        using var feedIterator = this._cosmosClient
            .GetDatabase(this._databaseName)
            .GetContainer(index)
            .GetItemQueryIterator<AzureCosmosDbTabularMemoryRecord>(queryDefinition);

        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var record in response)
            {
                yield return record.ToMemoryRecord(withEmbeddings); // Pass withEmbeddings
            }
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            // Use the encoded ID for deletion as stored in Cosmos DB
            var encodedId = AzureCosmosDbTabularMemoryRecord.EncodeId(record.Id);

            await this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainer(index)
                .DeleteItemAsync<AzureCosmosDbTabularMemoryRecord>(
                    encodedId, // Use encoded ID
                    new PartitionKey(record.GetFileId()), // Use original File ID for partition key
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            this._logger.LogDebug("Deleted record {Id} from index {Index}", encodedId, index);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            this._logger.LogTrace("Record {Id} (encoded: {EncodedId}) not found in index {Index}, nothing to delete", record.Id, AzureCosmosDbTabularMemoryRecord.EncodeId(record.Id), index);
        }
    }
}
