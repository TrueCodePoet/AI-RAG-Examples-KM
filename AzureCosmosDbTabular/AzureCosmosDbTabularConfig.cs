// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Configuration for Azure Cosmos DB Tabular Data connector.
/// </summary>
public sealed class AzureCosmosDbTabularConfig
{
    /// <summary>
    /// Azure Cosmos DB endpoint URL.
    /// </summary>
    [Required] public required string Endpoint { get; init; }

    /// <summary>
    /// Azure Cosmos DB API key.
    /// </summary>
    public string? APIKey { get; init; }

    /// <summary>
    /// Name of the database to use. Defaults to "memory".
    /// </summary>
    public string DatabaseName { get; init; } = "memory";

    /// <summary>
    /// Whether to enable schema management. Defaults to true.
    /// </summary>
    public bool EnableSchemaManagement { get; init; } = true;

    /// <summary>
    /// Name of the container to use for schema storage. Defaults to "schemas".
    /// </summary>
    public string SchemaContainerName { get; init; } = "schemas";

    /// <summary>
    /// Whether to extract schema information during document processing. Defaults to true.
    /// </summary>
    public bool ExtractSchemaOnImport { get; init; } = true;

    /// <summary>
    /// Default JSON serializer options.
    /// </summary>
    internal static readonly JsonSerializerOptions DefaultJsonSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Gets the container properties for the specified container ID.
    /// </summary>
    /// <param name="containerId">The container ID.</param>
    /// <param name="isSchemaContainer">Whether this is a schema container.</param>
    /// <returns>The container properties.</returns>
    internal static ContainerProperties GetContainerProperties(string containerId, bool isSchemaContainer = false)
    {
        // For schema containers, use dataset_name as the partition key
        var partitionKeyPath = isSchemaContainer 
            ? "/datasetName" 
            : $"/{AzureCosmosDbTabularMemoryRecord.FileField}";

        var properties = new ContainerProperties(
            containerId,
            partitionKeyPath);

        // Include all paths in the indexing policy
        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });

        // Exclude the vector field from indexing
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = $"/{AzureCosmosDbTabularMemoryRecord.VectorField}/*" });

        // Ensure the data field is indexed for efficient querying
        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = $"/{AzureCosmosDbTabularMemoryRecord.DataField}/*" });

        return properties;
    }

    // Static constructor removed to address CA1810
}
