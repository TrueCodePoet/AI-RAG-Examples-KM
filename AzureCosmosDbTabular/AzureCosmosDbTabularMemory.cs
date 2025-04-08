// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.MemoryStorage;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Result class for memory records with similarity score.
/// </summary>
internal sealed class TabularMemoryRecordResult : AzureCosmosDbTabularMemoryRecord
{
    /// <summary>
    /// Gets or sets the similarity score.
    /// </summary>
    public double SimilarityScore { get; init; }
}

/// <summary>
/// Azure Cosmos DB implementation of <see cref="IMemoryDb"/> for tabular data.
/// </summary>
internal sealed class AzureCosmosDbTabularMemory : IMemoryDb
{
    private readonly CosmosClient _cosmosClient;
    private readonly ITextEmbeddingGenerator _embeddingGenerator;
    private readonly ILogger _logger;
    private readonly AzureCosmosDbTabularConfig _config;
    private readonly string _databaseName;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureCosmosDbTabularMemory"/> class.
    /// </summary>
    /// <param name="cosmosClient">The Cosmos DB client.</param>
    /// <param name="embeddingGenerator">The text embedding generator.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">The configuration.</param>
    public AzureCosmosDbTabularMemory(
        CosmosClient cosmosClient,
        ITextEmbeddingGenerator embeddingGenerator,
        ILogger<AzureCosmosDbTabularMemory> logger,
        AzureCosmosDbTabularConfig config)
    {
        this._cosmosClient = cosmosClient;
        this._embeddingGenerator = embeddingGenerator;
        this._logger = logger;
        this._config = config;
        this._databaseName = config.DatabaseName;
    }

    /// <inheritdoc/>
    public async Task CreateIndexAsync(string index, int vectorSize, CancellationToken cancellationToken = default)
    {
        var databaseResponse = await this._cosmosClient
            .CreateDatabaseIfNotExistsAsync(this._databaseName, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Schema information is now stored in the same container as the data

        var containerProperties = AzureCosmosDbTabularConfig.GetContainerProperties(index);

        // Define the vector field path
        string vectorFieldPath = $"/{AzureCosmosDbTabularMemoryRecord.VectorField}"; // "/embedding"

        // Define the vector embedding policy for the container
        var embeddings = new List<Microsoft.Azure.Cosmos.Embedding> // Specify the correct namespace
        {
            new()
            {
                Path = vectorFieldPath,
                DataType = VectorDataType.Float32,
                DistanceFunction = DistanceFunction.Cosine,
                Dimensions = vectorSize,
            }
        };
        containerProperties.VectorEmbeddingPolicy = new VectorEmbeddingPolicy(new Collection<Microsoft.Azure.Cosmos.Embedding>(embeddings)); // Specify the correct namespace

        // Remove any default exclusion for the vector path or its children
        var exclusionToRemove = containerProperties.IndexingPolicy.ExcludedPaths.FirstOrDefault(p => p.Path == vectorFieldPath + "/*");
        if (exclusionToRemove != null)
        {
            containerProperties.IndexingPolicy.ExcludedPaths.Remove(exclusionToRemove);
        }

        // Ensure the specific vector path is included if using wildcard includes
        if (!containerProperties.IndexingPolicy.IncludedPaths.Any(p => p.Path == vectorFieldPath + "/?"))
        {
            if (!containerProperties.IndexingPolicy.IncludedPaths.Any(p => p.Path == "/*"))
            {
                containerProperties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = vectorFieldPath + "/?" });
            }
        }

        // Add the vector index definition using the correct structure and path
        containerProperties.IndexingPolicy.VectorIndexes.Clear(); // Clear potentially incorrect definitions
        containerProperties.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath
        {
            Path = vectorFieldPath, // Reverted Path: Targeting root embedding property
            Type = VectorIndexType.QuantizedFlat // Switched to QuantizedFlat to support higher dimensions (e.g., 1536)
        });

        var containerResponse = await databaseResponse.Database.CreateContainerIfNotExistsAsync(
            containerProperties,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        this._logger.LogInformation("Created/Ensured container {Index} in database {Database} with Vector Index Path {VectorPath}",
            index, this._databaseName, vectorFieldPath); // Log the correct path used
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<string>> GetIndexesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<string>();
        try
        {
            using var feedIterator = this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainerQueryIterator<ContainerProperties>("SELECT * FROM c"); // Query properties to check index later if needed

            while (feedIterator.HasMoreResults)
            {
                var next = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                foreach (var containerProperties in next.Resource)
                {
                    if (!string.IsNullOrEmpty(containerProperties?.Id))
                    {
                        result.Add(containerProperties.Id);
                    }
                }
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            this._logger.LogWarning("Database {Database} not found.", this._databaseName);
            // Database doesn't exist, so no indexes exist. Return empty list.
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task DeleteIndexAsync(string index, CancellationToken cancellationToken = default)
    {
        try
        {
            await this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainer(index)
                .DeleteContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            this._logger.LogWarning("Index {Index} or Database {Database} not found for deletion.", index, this._databaseName);
            // If it doesn't exist, consider the operation successful.
        }
    }

    /// <inheritdoc/>
    public async Task<string> UpsertAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        // Extract dataset name from tags if available
        string? datasetName = null;
        if (record.Tags.TryGetValue("dataset_name", out var datasetNames) && 
            datasetNames != null && 
            datasetNames.Count > 0)
        {
            datasetName = datasetNames[0];
        }

        // Extract source file name from tags if available
        string sourceFileName = "excel_import";
        if (record.Payload.TryGetValue("file", out var sourceFiles) && 
            sourceFiles != null)
        {
            // Check if sourceFiles is a collection
            if (sourceFiles is IList<string> filesList && filesList.Count > 0)
            {
                sourceFileName = filesList[0];
            }
            else if (sourceFiles is string fileName && !string.IsNullOrEmpty(fileName))
            {
                sourceFileName = fileName;
            }
            else if (sourceFiles.ToString() != null)
            {
                sourceFileName = sourceFiles.ToString();
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

        // Extract tabular data from the record
        Dictionary<string, object> tabularData = new();
        if (record.Payload.TryGetValue("tabular_data", out var tabularDataObj) && 
            tabularDataObj is string tabularDataStr)
        {
            try
            {
                tabularData = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    tabularDataStr, 
                    AzureCosmosDbTabularConfig.DefaultJsonSerializerOptions) ?? new Dictionary<string, object>();
                
                // Check if schema ID and import batch ID are in the deserialized tabular data
                if (string.IsNullOrEmpty(schemaId) && tabularData.TryGetValue("schema_id", out var tabularSchemaId))
                {
                    if (tabularSchemaId is string tabularSchemaIdStr)
                    {
                        schemaId = tabularSchemaIdStr;
                        Console.WriteLine($"Extracted schema ID from tabular data: {schemaId}");
                    }
                    else if (tabularSchemaId != null)
                    {
                        schemaId = tabularSchemaId.ToString();
                        Console.WriteLine($"Extracted and converted schema ID from tabular data: {schemaId}");
                    }
                }
                
                if (string.IsNullOrEmpty(importBatchId) && tabularData.TryGetValue("import_batch_id", out var tabularImportBatchId))
                {
                    if (tabularImportBatchId is string tabularImportBatchIdStr)
                    {
                        importBatchId = tabularImportBatchIdStr;
                        Console.WriteLine($"Extracted import batch ID from tabular data: {importBatchId}");
                    }
                    else if (tabularImportBatchId != null)
                    {
                        importBatchId = tabularImportBatchId.ToString();
                        Console.WriteLine($"Extracted and converted import batch ID from tabular data: {importBatchId}");
                    }
                }
            }
            catch (Exception ex)
            {
                this._logger.LogWarning(ex, "Failed to deserialize tabular data for record {Id}", record.Id);
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

    /// <summary>
    /// Creates a new schema for the given dataset and returns its ID and import batch ID.
    /// </summary>
    /// <param name="datasetName">The dataset name.</param>
    /// <param name="sourceFileName">The source file name.</param>
    /// <param name="data">The tabular data to extract schema from.</param>
    /// <param name="indexName">The index name to store the schema in.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the schema ID and import batch ID.</returns>
    private async Task<(string SchemaId, string ImportBatchId)> CreateOrUpdateSchemaAsync(
        string datasetName,
        string sourceFileName,
        Dictionary<string, object> data,
        string indexName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Always create a new schema with a unique ID for each import
            var schema = TabularDataSchema.Create(datasetName, sourceFileName);
            
            // Get existing schema to copy column information if available
            var existingSchema = await this.GetSchemaAsync(datasetName, cancellationToken).ConfigureAwait(false);
            
            if (existingSchema != null)
            {
                // Copy column information from existing schema
                foreach (var column in existingSchema.Columns)
                {
                    // Only add if not already in the new schema
                    if (!schema.Columns.Any(c => string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        schema.Columns.Add(column);
                    }
                }
            }

            // Add or update column information from current data
            foreach (var kvp in data)
            {
                // Check if column already exists in the schema
                var existingColumn = schema.Columns.FirstOrDefault(c => 
                    string.Equals(c.Name, kvp.Key, StringComparison.OrdinalIgnoreCase));
                    
                if (existingColumn != null)
                {
                    // Update existing column with new value
                    string valueStr = kvp.Value?.ToString() ?? string.Empty;
                    if (!existingColumn.CommonValues.Contains(valueStr))
                    {
                        existingColumn.CommonValues.Add(valueStr);
                        if (existingColumn.CommonValues.Count > 10)
                        {
                            existingColumn.CommonValues.RemoveAt(0); // Keep only the 10 most recent values
                        }
                    }
                    
                    // Verify data type consistency
                    string inferredType = InferDataType(kvp.Value);
                    if (existingColumn.DataType != inferredType)
                    {
                        // If types don't match, use the more general type (string)
                        if (existingColumn.DataType != "string" && inferredType != "string")
                        {
                            existingColumn.DataType = "string";
                        }
                    }
                }
                else
                {
                    // Add new column
                    var column = new SchemaColumn
                    {
                        Name = kvp.Key,
                        NormalizedName = NormalizeColumnName(kvp.Key),
                        DataType = InferDataType(kvp.Value),
                        CommonValues = new List<string> { kvp.Value?.ToString() ?? string.Empty }
                    };

                    schema.Columns.Add(column);
                }
            }

            // Store the new schema
            await this.StoreSchemaAsync(schema, indexName, cancellationToken).ConfigureAwait(false);
            
            this._logger.LogInformation("Created new schema {SchemaId} for dataset {DatasetName} with import batch {ImportBatchId}", 
                schema.Id, datasetName, schema.ImportBatchId);
                
            return (schema.Id, schema.ImportBatchId);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error creating schema for dataset {DatasetName}", datasetName);
            return (string.Empty, string.Empty);
        }
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

    /// <summary>
    /// Processes memory filters to extract both standard tag filters and structured data filters.
    /// </summary>
    /// <param name="alias">The alias for the SQL query.</param>
    /// <param name="filters">The filters to process.</param>
    /// <returns>A tuple containing the WHERE clause and the parameters.</returns>
    private (string, IReadOnlyCollection<Tuple<string, object>>) ProcessFilters(string alias, ICollection<MemoryFilter>? filters = null)
    {
        if (filters is null || !filters.Any(f => !f.IsEmpty()))
        {
            return (string.Empty, Array.Empty<Tuple<string, object>>());
        }

        var parameters = new List<Tuple<string, object>>();
        var outerBuilder = new StringBuilder(); // For OR between filters
        bool firstOuterFilter = true;

        foreach (var filter in filters)
        {
            if (filter.IsEmpty()) { continue; }

            var innerBuilder = new StringBuilder(); // For AND within a filter
            bool firstInnerCondition = true;

            foreach (var pair in filter)
            {
                if (pair.Value is null) { continue; } // Skip null values

                if (!firstInnerCondition)
                {
                    innerBuilder.Append(" AND ");
                }

                // Use a consistent parameter naming scheme
                string paramName = $"@p_{parameters.Count}";
                parameters.Add(Tuple.Create<string, object>(paramName, pair.Value));

                if (pair.Key.StartsWith("data.", StringComparison.Ordinal))
                {
                    // Normalize the field name (convert camelCase to snake_case)
                    string normalizedKey = NormalizeFieldName(pair.Key);

                    // Handle structured data filter
                    string fieldName = normalizedKey.Substring(5);
                    if (!string.IsNullOrEmpty(fieldName))
                    {
                        // Use bracket notation for field names that might contain special characters
                        innerBuilder.Append($"{alias}.{AzureCosmosDbTabularMemoryRecord.DataField}[\"{fieldName}\"] = {paramName}");
                        firstInnerCondition = false;

                        // Log the normalization if it happened
                        if (normalizedKey != pair.Key)
                        {
                            this._logger.LogDebug("Normalized field name: {OriginalKey} -> {NormalizedKey}", pair.Key, normalizedKey);
                        }
                    }
                    else
                    {
                        this._logger.LogWarning("Invalid structured data filter key found: {Key}", pair.Key);
                        // Remove the parameter added for the invalid key
                        parameters.RemoveAt(parameters.Count - 1);
                    }
                }
                else // Handle standard tag filter
                {
                    // Use bracket notation for tag keys that might contain special characters
                    innerBuilder.Append($"ARRAY_CONTAINS({alias}.{AzureCosmosDbTabularMemoryRecord.TagsField}[\"{pair.Key}\"], {paramName})");
                    firstInnerCondition = false;
                }
            }

            // Only add this filter's conditions if it generated any valid conditions
            if (!firstInnerCondition) // means innerBuilder is not empty
            {
                if (!firstOuterFilter)
                {
                    outerBuilder.Append(" OR ");
                }
                outerBuilder.Append('(').Append(innerBuilder).Append(')');
                firstOuterFilter = false;
            }
        }

        if (firstOuterFilter) // means outerBuilder is empty (no valid filters found)
        {
            return (string.Empty, Array.Empty<Tuple<string, object>>());
        }

        // Return the complete WHERE clause
        return ($"WHERE {outerBuilder}", parameters);
    }

    /// <summary>
    /// Normalizes field names from camelCase to snake_case.
    /// </summary>
    /// <param name="fieldName">The field name to normalize.</param>
    /// <returns>The normalized field name.</returns>
    private string NormalizeFieldName(string fieldName)
    {
        if (!fieldName.StartsWith("data.")) return fieldName;

        // Extract the part after "data."
        string field = fieldName.Substring(5);

        // Convert camelCase or PascalCase to snake_case
        // e.g., "serverPurpose" → "server_purpose"
        string snakeCase = System.Text.RegularExpressions.Regex.Replace(
            field,
            "(?<=[a-z])(?=[A-Z])",
            "_"
        ).ToLowerInvariant();

        return "data." + snakeCase;
    }

    /// <summary>
    /// Gets the filterable fields from the index.
    /// </summary>
    /// <param name="index">The index name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A dictionary of field types and their available field names.</returns>
    public async Task<Dictionary<string, HashSet<string>>> GetFilterableFieldsAsync(
        string index,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["tags"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["data"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

        // Query for a sample of documents to extract field names
        var sql = "SELECT TOP 100 c.tags, c.data FROM c";
        var queryDefinition = new QueryDefinition(sql);

        try
        {
            using var feedIterator = this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainer(index)
                .GetItemQueryIterator<dynamic>(queryDefinition);

            while (feedIterator.HasMoreResults)
            {
                var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);

                foreach (var item in response)
                {
                    // Extract tag keys
                    if (item.tags != null)
                    {
                        foreach (var tagKey in ((IDictionary<string, object>)item.tags).Keys)
                        {
                            result["tags"].Add(tagKey);
                        }
                    }

                    // Extract data field keys
                    if (item.data != null)
                    {
                        foreach (var dataKey in ((IDictionary<string, object>)item.data).Keys)
                        {
                            result["data"].Add(dataKey);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error getting filterable fields from index {Index}", index);
        }

        return result;
    }

    // CreateSchemaContainerAsync method removed as schemas are now stored in the same container as data

    /// <summary>
    /// Extracts schema information from a record and stores it.
    /// </summary>
    /// <param name="datasetName">The dataset name.</param>
    /// <param name="data">The data to extract schema from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ExtractAndStoreSchemaAsync(
        string datasetName,
        Dictionary<string, object> data,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if schema already exists
            var existingSchema = await this.GetSchemaAsync(datasetName, cancellationToken).ConfigureAwait(false);
            
            if (existingSchema != null)
            {
                // Update existing schema with new column information
                await this.UpdateSchemaAsync(existingSchema, data, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Create new schema
            var schema = new TabularDataSchema
            {
                DatasetName = datasetName,
                ImportDate = DateTime.UtcNow,
                Columns = new List<SchemaColumn>()
            };

            // Extract column information
            foreach (var kvp in data)
            {
                var column = new SchemaColumn
                {
                    Name = kvp.Key,
                    NormalizedName = NormalizeColumnName(kvp.Key),
                    DataType = InferDataType(kvp.Value),
                    CommonValues = new List<string> { kvp.Value?.ToString() ?? string.Empty }
                };

                schema.Columns.Add(column);
            }

            // Store schema - pass the index name as null to use the default container selection logic
            await this.StoreSchemaAsync(schema, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error extracting and storing schema for dataset {DatasetName}", datasetName);
        }
    }

    /// <summary>
    /// Updates an existing schema with new column information.
    /// </summary>
    /// <param name="schema">The existing schema.</param>
    /// <param name="data">The new data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task UpdateSchemaAsync(
        TabularDataSchema schema,
        Dictionary<string, object> data,
        CancellationToken cancellationToken = default)
    {
        bool schemaChanged = false;

        // Update existing columns and add new ones
        foreach (var kvp in data)
        {
            var existingColumn = schema.Columns.FirstOrDefault(c => 
                string.Equals(c.Name, kvp.Key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.NormalizedName, NormalizeColumnName(kvp.Key), StringComparison.OrdinalIgnoreCase));

            if (existingColumn != null)
            {
                // Update common values if this is a new value
                string valueStr = kvp.Value?.ToString() ?? string.Empty;
                if (!existingColumn.CommonValues.Contains(valueStr))
                {
                    existingColumn.CommonValues.Add(valueStr);
                    if (existingColumn.CommonValues.Count > 10)
                    {
                        existingColumn.CommonValues.RemoveAt(0); // Keep only the 10 most recent values
                    }
                    schemaChanged = true;
                }

                // Verify data type consistency
                string inferredType = InferDataType(kvp.Value);
                if (existingColumn.DataType != inferredType)
                {
                    // If types don't match, use the more general type (string)
                    if (existingColumn.DataType != "string" && inferredType != "string")
                    {
                        existingColumn.DataType = "string";
                        schemaChanged = true;
                    }
                }
            }
            else
            {
                // Add new column
                var newColumn = new SchemaColumn
                {
                    Name = kvp.Key,
                    NormalizedName = NormalizeColumnName(kvp.Key),
                    DataType = InferDataType(kvp.Value),
                    CommonValues = new List<string> { kvp.Value?.ToString() ?? string.Empty }
                };

                schema.Columns.Add(newColumn);
                schemaChanged = true;
            }
        }

        // Only update if schema changed
        if (schemaChanged)
        {
            schema.ImportDate = DateTime.UtcNow; // Update timestamp
            await this.StoreSchemaAsync(schema, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stores a schema in the appropriate container.
    /// </summary>
    /// <param name="schema">The schema to store.</param>
    /// <param name="indexName">Optional specific index name to use for storage. If not provided, will use configuration settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schema ID.</returns>
    public async Task<string> StoreSchemaAsync(
        TabularDataSchema schema,
        string? indexName = null,
        CancellationToken cancellationToken = default)
    {
        if (!this._config.EnableSchemaManagement)
        {
            return string.Empty;
        }

        try
        {
            // Add a special tag to identify this as a schema document
            if (schema.Metadata == null)
            {
                schema.Metadata = new Dictionary<string, string>();
            }
            schema.Metadata["document_type"] = "schema";
            
            // Ensure the ID is prefixed to avoid collisions with regular documents
            // We no longer overwrite the ID with a generic one based on dataset name
            // This preserves the unique ID generated in TabularDataSchema.Create()
            
            // Set the file property to match the source file name (used as partition key)
            // This allows efficient querying by source file while maintaining unique IDs
            if (string.IsNullOrEmpty(schema.File))
            {
                schema.File = schema.SourceFile;
                Console.WriteLine($"Setting schema File property to source file: {schema.SourceFile}");
            }

            // Determine which container to use for schema storage
            string containerName;
            
            if (!string.IsNullOrEmpty(indexName))
            {
                // Use the explicitly provided index name
                containerName = indexName;
            }
            else if (!string.IsNullOrEmpty(this._config.SchemaContainerName))
            {
                // Use the dedicated schema container from config
                containerName = this._config.SchemaContainerName;
                
                // Ensure the container exists
                await this.CreateIndexAsync(containerName, 0, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Use the first available index or create a default one
                var indexes = await this.GetIndexesAsync(cancellationToken).ConfigureAwait(false);
                containerName = indexes.Any() ? indexes.First() : "default";
                
                // Ensure the container exists
                if (!indexes.Any())
                {
                    await this.CreateIndexAsync(containerName, 0, cancellationToken).ConfigureAwait(false);
                }
            }
            
            // Store schema in the determined container
            var result = await this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainer(containerName)
                .UpsertItemAsync(
                    schema,
                    schema.GetPartitionKey(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            this._logger.LogInformation("Stored schema for dataset {DatasetName} in container {Container}", 
                schema.DatasetName, containerName);
            return result.Resource.Id;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error storing schema for dataset {DatasetName}", schema.DatasetName);
            return string.Empty;
        }
    }

    /// <summary>
    /// Gets a schema by dataset name from any available index container.
    /// </summary>
    /// <param name="datasetName">The dataset name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schema, or null if not found.</returns>
    public async Task<TabularDataSchema?> GetSchemaAsync(
        string datasetName,
        CancellationToken cancellationToken = default)
    {
        if (!this._config.EnableSchemaManagement)
        {
            return null;
        }

        try
        {
            // Get all available indexes
            var indexes = await this.GetIndexesAsync(cancellationToken).ConfigureAwait(false);
            
            // If no indexes exist, return null
            if (!indexes.Any())
            {
                return null;
            }
            
            // Query for schema by dataset name and document_type
            var queryDefinition = new QueryDefinition(
                "SELECT * FROM c WHERE c.datasetName = @datasetName AND c.metadata.document_type = 'schema'")
                .WithParameter("@datasetName", datasetName);

            // Try to find the schema in any of the available indexes
            foreach (var index in indexes)
            {
                try
                {
                    using var feedIterator = this._cosmosClient
                        .GetDatabase(this._databaseName)
                        .GetContainer(index)
                        .GetItemQueryIterator<TabularDataSchema>(queryDefinition);

                    if (feedIterator.HasMoreResults)
                    {
                        var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                        var schema = response.FirstOrDefault();
                        if (schema != null)
                        {
                            return schema;
                        }
                    }
                }
                catch (Exception ex)
                {
                    this._logger.LogWarning(ex, "Error searching for schema in container {Container}", index);
                    // Continue to the next container
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error getting schema for dataset {DatasetName}", datasetName);
            return null;
        }
    }

    /// <summary>
    /// Gets schemas by source file name from any available index container.
    /// </summary>
    /// <param name="sourceFileName">The source file name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of schemas for the specified source file.</returns>
    public async Task<List<TabularDataSchema>> GetSchemasBySourceFileAsync(
        string sourceFileName,
        CancellationToken cancellationToken = default)
    {
        var result = new List<TabularDataSchema>();
        
        if (!this._config.EnableSchemaManagement)
        {
            return result;
        }

        try
        {
            // Get all available indexes
            var indexes = await this.GetIndexesAsync(cancellationToken).ConfigureAwait(false);
            
            // If no indexes exist, return empty list
            if (!indexes.Any())
            {
                return result;
            }
            
            // Query for schema by source file name and document_type
            var queryDefinition = new QueryDefinition(
                "SELECT * FROM c WHERE c.sourceFile = @sourceFile AND c.metadata.document_type = 'schema'")
                .WithParameter("@sourceFile", sourceFileName);

            // Try to find schemas in any of the available indexes
            foreach (var index in indexes)
            {
                try
                {
                    using var feedIterator = this._cosmosClient
                        .GetDatabase(this._databaseName)
                        .GetContainer(index)
                        .GetItemQueryIterator<TabularDataSchema>(queryDefinition);

                    while (feedIterator.HasMoreResults)
                    {
                        var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                        result.AddRange(response);
                    }
                }
                catch (Exception ex)
                {
                    this._logger.LogWarning(ex, "Error searching for schemas by source file in container {Container}", index);
                    // Continue to the next container
                }
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error getting schemas for source file {SourceFileName}", sourceFileName);
        }

        return result;
    }

    /// <summary>
    /// Gets a schema by ID from any available index container.
    /// </summary>
    /// <param name="schemaId">The schema ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schema, or null if not found.</returns>
    public async Task<TabularDataSchema?> GetSchemaByIdAsync(
        string schemaId,
        CancellationToken cancellationToken = default)
    {
        if (!this._config.EnableSchemaManagement)
        {
            return null;
        }

        try
        {
            // Get all available indexes
            var indexes = await this.GetIndexesAsync(cancellationToken).ConfigureAwait(false);
            
            // If no indexes exist, return null
            if (!indexes.Any())
            {
                return null;
            }
            
            // Query for schema by ID and document_type
            var queryDefinition = new QueryDefinition(
                "SELECT * FROM c WHERE c.id = @id AND c.metadata.document_type = 'schema'")
                .WithParameter("@id", schemaId);

            // Try to find the schema in any of the available indexes
            foreach (var index in indexes)
            {
                try
                {
                    using var feedIterator = this._cosmosClient
                        .GetDatabase(this._databaseName)
                        .GetContainer(index)
                        .GetItemQueryIterator<TabularDataSchema>(queryDefinition);

                    if (feedIterator.HasMoreResults)
                    {
                        var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                        var schema = response.FirstOrDefault();
                        if (schema != null)
                        {
                            return schema;
                        }
                    }
                }
                catch (Exception ex)
                {
                    this._logger.LogWarning(ex, "Error searching for schema by ID in container {Container}", index);
                    // Continue to the next container
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error getting schema for ID {SchemaId}", schemaId);
            return null;
        }
    }

    /// <summary>
    /// Lists all available schemas from all index containers.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of schemas.</returns>
    public async Task<List<TabularDataSchema>> ListSchemasAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<TabularDataSchema>();

        if (!this._config.EnableSchemaManagement)
        {
            return result;
        }

        try
        {
            // Get all available indexes
            var indexes = await this.GetIndexesAsync(cancellationToken).ConfigureAwait(false);
            
            // If no indexes exist, return empty list
            if (!indexes.Any())
            {
                return result;
            }
            
            // Query for all schema documents
            var queryDefinition = new QueryDefinition(
                "SELECT * FROM c WHERE c.metadata.document_type = 'schema'");

            // Search in all available indexes
            foreach (var index in indexes)
            {
                try
                {
                    using var feedIterator = this._cosmosClient
                        .GetDatabase(this._databaseName)
                        .GetContainer(index)
                        .GetItemQueryIterator<TabularDataSchema>(queryDefinition);

                    while (feedIterator.HasMoreResults)
                    {
                        var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                        result.AddRange(response);
                    }
                }
                catch (Exception ex)
                {
                    this._logger.LogWarning(ex, "Error listing schemas in container {Container}", index);
                    // Continue to the next container
                }
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error listing schemas");
        }

        return result;
    }

    /// <summary>
    /// Lists all available dataset names.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of dataset names.</returns>
    public async Task<List<string>> ListDatasetNamesAsync(
        CancellationToken cancellationToken = default)
    {
        var schemas = await this.ListSchemasAsync(cancellationToken).ConfigureAwait(false);
        return schemas.Select(s => s.DatasetName).ToList();
    }

    /// <summary>
    /// Validates filter parameters against a schema.
    /// </summary>
    /// <param name="datasetName">The dataset name.</param>
    /// <param name="parameters">The parameters to validate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the validated parameters and any warnings.</returns>
    public async Task<(Dictionary<string, object> ValidatedParameters, List<string> Warnings)> ValidateParametersAsync(
        string datasetName,
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken = default)
    {
        var validatedParameters = new Dictionary<string, object>();
        var warnings = new List<string>();

        if (!this._config.EnableSchemaManagement)
        {
            return (parameters, new List<string> { "Schema management is disabled. Parameters not validated." });
        }

        // Get schema for the dataset
        var schema = await this.GetSchemaAsync(datasetName, cancellationToken).ConfigureAwait(false);
        if (schema == null)
        {
            return (parameters, new List<string> { $"No schema found for dataset '{datasetName}'. Parameters not validated." });
        }

        // Validate each parameter
        foreach (var param in parameters)
        {
            string key = param.Key;
            object value = param.Value;

            // Handle data. prefix
            string fieldName = key;
            if (key.StartsWith("data.", StringComparison.Ordinal))
            {
                fieldName = key.Substring(5);
            }

            // Find matching column in schema
            var column = schema.Columns.FirstOrDefault(c => 
                string.Equals(c.Name, fieldName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.NormalizedName, fieldName, StringComparison.OrdinalIgnoreCase));

            if (column == null)
            {
                warnings.Add($"Field '{fieldName}' not found in schema for dataset '{datasetName}'.");
                validatedParameters[key] = value; // Add anyway, but with warning
                continue;
            }

            // Validate value against column type
            if (ValidateValue(value, column.DataType))
            {
                // Use the normalized name from the schema
                string normalizedKey = key.StartsWith("data.", StringComparison.Ordinal)
                    ? $"data.{column.NormalizedName}"
                    : column.NormalizedName;

                validatedParameters[normalizedKey] = value;
            }
            else
            {
                warnings.Add($"Value '{value}' is not valid for field '{fieldName}' of type '{column.DataType}'.");
                validatedParameters[key] = value; // Add anyway, but with warning
            }
        }

        return (validatedParameters, warnings);
    }

    /// <summary>
    /// Validates a value against a data type.
    /// </summary>
    /// <param name="value">The value to validate.</param>
    /// <param name="dataType">The data type.</param>
    /// <returns>True if the value is valid for the data type, false otherwise.</returns>
    private bool ValidateValue(object value, string dataType)
    {
        if (value == null)
        {
            return true; // Null is valid for any type
        }

        string valueStr = value.ToString() ?? string.Empty;

        switch (dataType.ToLowerInvariant())
        {
            case "string":
                return true; // Any value is valid as a string

            case "number":
            case "integer":
                return double.TryParse(valueStr, out _);

            case "boolean":
                return bool.TryParse(valueStr, out _);

            case "date":
            case "datetime":
                return DateTime.TryParse(valueStr, out _);

            default:
                return true; // Unknown type, assume valid
        }
    }

    /// <summary>
    /// Infers the data type of a value.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The inferred data type.</returns>
    private string InferDataType(object? value)
    {
        if (value == null)
        {
            return "string";
        }

        if (value is bool)
        {
            return "boolean";
        }

        if (value is int or long or float or double or decimal)
        {
            return "number";
        }

        if (value is DateTime)
        {
            return "date";
        }

        string valueStr = value.ToString() ?? string.Empty;

        if (bool.TryParse(valueStr, out _))
        {
            return "boolean";
        }

        if (int.TryParse(valueStr, out _) || double.TryParse(valueStr, out _))
        {
            return "number";
        }

        if (DateTime.TryParse(valueStr, out _))
        {
            return "date";
        }

        return "string";
    }

    /// <summary>
    /// Normalizes a column name to snake_case.
    /// </summary>
    /// <param name="columnName">The column name to normalize.</param>
    /// <returns>The normalized column name.</returns>
    private static string NormalizeColumnName(string columnName)
    {
        // Convert camelCase or PascalCase to snake_case
        string snakeCase = Regex.Replace(
            columnName,
            "(?<=[a-z])(?=[A-Z])",
            "_"
        ).ToLowerInvariant();

        // Replace spaces and other non-alphanumeric characters with underscores
        snakeCase = Regex.Replace(snakeCase, "[^a-z0-9]", "_");

        // Remove consecutive underscores
        while (snakeCase.Contains("__"))
        {
            snakeCase = snakeCase.Replace("__", "_");
        }

        // Trim underscores from start and end
        return snakeCase.Trim('_');
    }

    /// <summary>
    /// Gets all records that belong to a specific schema.
    /// </summary>
    /// <param name="index">The index name.</param>
    /// <param name="schemaId">The schema ID.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="withEmbeddings">Whether to include embeddings in the results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An async enumerable of memory records.</returns>
    public async IAsyncEnumerable<MemoryRecord> GetRecordsBySchemaIdAsync(
        string index,
        string schemaId,
        int limit = 100,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sql = $"""
                   SELECT TOP @limit
                     {AzureCosmosDbTabularMemoryRecord.Columns("c", withEmbeddings)}
                   FROM c
                   WHERE c.schemaId = @schemaId
                   """;

        var queryDefinition = new QueryDefinition(sql)
            .WithParameter("@limit", limit)
            .WithParameter("@schemaId", schemaId);

        using var feedIterator = this._cosmosClient
            .GetDatabase(this._databaseName)
            .GetContainer(index)
            .GetItemQueryIterator<AzureCosmosDbTabularMemoryRecord>(queryDefinition);

        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var record in response)
            {
                yield return record.ToMemoryRecord(withEmbeddings);
            }
        }
    }

    /// <summary>
    /// Gets all records that belong to a specific import batch.
    /// </summary>
    /// <param name="index">The index name.</param>
    /// <param name="importBatchId">The import batch ID.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="withEmbeddings">Whether to include embeddings in the results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An async enumerable of memory records.</returns>
    public async IAsyncEnumerable<MemoryRecord> GetRecordsByImportBatchIdAsync(
        string index,
        string importBatchId,
        int limit = 100,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sql = $"""
                   SELECT TOP @limit
                     {AzureCosmosDbTabularMemoryRecord.Columns("c", withEmbeddings)}
                   FROM c
                   WHERE c.importBatchId = @importBatchId
                   """;

        var queryDefinition = new QueryDefinition(sql)
            .WithParameter("@limit", limit)
            .WithParameter("@importBatchId", importBatchId);

        using var feedIterator = this._cosmosClient
            .GetDatabase(this._databaseName)
            .GetContainer(index)
            .GetItemQueryIterator<AzureCosmosDbTabularMemoryRecord>(queryDefinition);

        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var record in response)
            {
                yield return record.ToMemoryRecord(withEmbeddings);
            }
        }
    }

    /// <summary>
    /// Gets the schema for a specific record.
    /// </summary>
    /// <param name="index">The index name.</param>
    /// <param name="recordId">The record ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schema, or null if not found.</returns>
    public async Task<TabularDataSchema?> GetSchemaForRecordAsync(
        string index,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // First, get the record to find its schema ID
            var encodedId = AzureCosmosDbTabularMemoryRecord.EncodeId(recordId);
            var queryDefinition = new QueryDefinition(
                "SELECT c.schemaId FROM c WHERE c.id = @id")
                .WithParameter("@id", encodedId);

            using var feedIterator = this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainer(index)
                .GetItemQueryIterator<dynamic>(queryDefinition);

            if (feedIterator.HasMoreResults)
            {
                var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                var record = response.FirstOrDefault();
                
                if (record != null && record.schemaId != null)
                {
                    string schemaId = record.schemaId.ToString();
                    
                    // Now get the schema by ID
                    var schemaQueryDefinition = new QueryDefinition(
                        "SELECT * FROM c WHERE c.id = @id")
                        .WithParameter("@id", schemaId);

                    // Try to find the schema in any of the available indexes
                    var indexes = await this.GetIndexesAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var idx in indexes)
                    {
                        try
                        {
                            using var schemaIterator = this._cosmosClient
                                .GetDatabase(this._databaseName)
                                .GetContainer(idx)
                                .GetItemQueryIterator<TabularDataSchema>(schemaQueryDefinition);

                            if (schemaIterator.HasMoreResults)
                            {
                                var schemaResponse = await schemaIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                                var schema = schemaResponse.FirstOrDefault();
                                if (schema != null)
                                {
                                    return schema;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            this._logger.LogWarning(ex, "Error searching for schema in container {Container}", idx);
                            // Continue to the next container
                        }
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error getting schema for record {RecordId}", recordId);
            return null;
        }
    }

    /// <summary>
    /// Gets the top values for a specific field.
    /// </summary>
    /// <param name="index">The index name.</param>
    /// <param name="fieldType">The field type (tag or data).</param>
    /// <param name="fieldName">The field name.</param>
    /// <param name="limit">The maximum number of values to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of field values and their occurrence counts.</returns>
    public async Task<List<(string Value, int Count)>> GetTopFieldValuesAsync(
        string index,
        string fieldType,
        string fieldName,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var result = new List<(string Value, int Count)>();

        // Determine the field path based on the field type
        string fieldPath = fieldType.Equals("tag", StringComparison.OrdinalIgnoreCase)
            ? $"c.{AzureCosmosDbTabularMemoryRecord.TagsField}[\"{fieldName}\"]"
            : $"c.{AzureCosmosDbTabularMemoryRecord.DataField}[\"{fieldName}\"]";

        // Query for the top values of the specified field
        var sql = $@"
            SELECT {fieldPath} as value, COUNT(1) as count
            FROM c
            WHERE IS_DEFINED({fieldPath})
            GROUP BY {fieldPath}
            ORDER BY count DESC
            OFFSET 0 LIMIT {limit}
        ";

        var queryDefinition = new QueryDefinition(sql);

        try
        {
            using var feedIterator = this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainer(index)
                .GetItemQueryIterator<dynamic>(queryDefinition);

            while (feedIterator.HasMoreResults)
            {
                var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);

                foreach (var item in response)
                {
                    string value = item.value.ToString();
                    int count = (int)item.count;

                    result.Add((value, count));
                }
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error getting top values for field {FieldType}.{FieldName} from index {Index}",
                fieldType, fieldName, index);
        }

        return result;
    }
}
