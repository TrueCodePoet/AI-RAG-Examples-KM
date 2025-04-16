// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
        // The schema ID in the database might be stored as a list of strings
        // For the query, we need to handle this by using a nested query to check for the value
        var sql = $"""
                   SELECT TOP @limit
                     {AzureCosmosDbTabularMemoryRecord.Columns("c", withEmbeddings)}
                   FROM c
                   WHERE (IS_NULL(c.metadata.document_type) OR c.metadata.document_type != 'schema')
                     AND ((IS_STRING(c.schemaId) AND c.schemaId = @schemaId)
                      OR (IS_ARRAY(c.schemaId) AND EXISTS(SELECT VALUE t FROM t IN c.schemaId WHERE t = @schemaId)))
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
        // The import batch ID in the database might be stored as a list of strings
        // For the query, we need to handle this by using a nested query to check for the value
        var sql = $"""
                   SELECT TOP @limit
                     {AzureCosmosDbTabularMemoryRecord.Columns("c", withEmbeddings)}
                   FROM c
                   WHERE (IS_NULL(c.metadata.document_type) OR c.metadata.document_type != 'schema')
                     AND ((IS_STRING(c.importBatchId) AND c.importBatchId = @importBatchId)
                      OR (IS_ARRAY(c.importBatchId) AND EXISTS(SELECT VALUE t FROM t IN c.importBatchId WHERE t = @importBatchId)))
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
                    // Handle different types of schemaId values
                    string schemaId;
                    
                    // Get the actual type of the schemaId object
                    object schemaIdObj = record.schemaId;
                    Type schemaIdType = schemaIdObj.GetType();
                    
                    if (schemaIdType == typeof(string))
                    {
                        // Handle string type
                        schemaId = (string)schemaIdObj;
                        this._logger.LogDebug("SchemaId is a string: {SchemaId}", schemaId);
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
                            this._logger.LogDebug("Extracted schema ID from collection: {SchemaId}", schemaId);
                        }
                        else
                        {
                            this._logger.LogWarning("Schema ID collection is empty for record {RecordId}", recordId);
                            return null;
                        }
                    }
                    else
                    {
                        // Handle any other type by converting to string
                        schemaId = schemaIdObj.ToString();
                        this._logger.LogDebug("Using schema ID as string (type: {Type}): {SchemaId}", 
                            schemaIdType.Name, schemaId);
                    }
                    
                    // Now get the schema by ID
                    return await this.GetSchemaByIdAsync(schemaId, cancellationToken).ConfigureAwait(false);
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
}
