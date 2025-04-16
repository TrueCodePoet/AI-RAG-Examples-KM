// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
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
    public async Task CreateIndexAsync(string index, int vectorSize, CancellationToken cancellationToken = default)
    {
        this._logger.LogInformation("CreateIndexAsync: Attempting to create or ensure container '{Index}' in database '{Database}' with vector size {VectorSize}", index, this._databaseName, vectorSize);

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

        try
        {
            var containerResponse = await databaseResponse.Database.CreateContainerIfNotExistsAsync(
                containerProperties,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            this._logger.LogInformation("CreateIndexAsync: Created/Ensured container '{Index}' in database '{Database}' with Vector Index Path '{VectorPath}'",
                index, this._databaseName, vectorFieldPath); // Log the correct path used
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "CreateIndexAsync: Error creating container '{Index}' in database '{Database}'", index, this._databaseName);
            throw;
        }
    }

   // Checks if a container (index) exists in the database.
    public async Task<bool> IndexExistsAsync(string containerName, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = this._cosmosClient.GetDatabase(this._databaseName);
            var container = database.GetContainer(containerName);
            var response = await container.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.StatusCode == System.Net.HttpStatusCode.OK;
        }
        catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error checking existence of container {ContainerName}", containerName);
            throw;
        }
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
}
