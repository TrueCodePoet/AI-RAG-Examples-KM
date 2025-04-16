// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Azure Cosmos DB implementation of <see cref="IMemoryDb"/> for tabular data.
/// </summary>
internal sealed partial class AzureCosmosDbTabularMemory
{
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
                //await this.CreateIndexAsync(containerName, 0, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Use the first available index or create a default one
                var indexes = await this.GetIndexesAsync(cancellationToken).ConfigureAwait(false);
                containerName = indexes.Any() ? indexes.First() : "default";
                
                // Ensure the container exists
                //if (!indexes.Any())
                //{
                //    await this.CreateIndexAsync(containerName, 0, cancellationToken).ConfigureAwait(false);
                //}
            }
            
            // This will catch if index needs to be created
            if (! await this.IndexExistsAsync(containerName,cancellationToken).ConfigureAwait(false)){
                await this.CreateIndexAsync(containerName, 0, cancellationToken).ConfigureAwait(false);
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
}
