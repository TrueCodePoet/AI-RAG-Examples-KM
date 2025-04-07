// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Represents the schema of a tabular dataset.
/// </summary>
public class TabularDataSchema
{
    /// <summary>
    /// Gets or sets the unique identifier for the schema.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets or sets the name of the dataset.
    /// </summary>
    [JsonPropertyName("datasetName")]
    public string DatasetName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source file name.
    /// </summary>
    [JsonPropertyName("sourceFile")]
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the import date.
    /// </summary>
    [JsonPropertyName("importDate")]
    public DateTime ImportDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the columns in the schema.
    /// </summary>
    [JsonPropertyName("columns")]
    public List<SchemaColumn> Columns { get; set; } = new List<SchemaColumn>();

    /// <summary>
    /// Gets or sets additional metadata for the schema.
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    
    /// <summary>
    /// Gets or sets the file identifier (used as partition key).
    /// This must match the partition key field name used in AzureCosmosDbTabularMemoryRecord.
    /// </summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets the partition key for this schema.
    /// </summary>
    /// <returns>The partition key.</returns>
    internal Microsoft.Azure.Cosmos.PartitionKey GetPartitionKey() => new(File);
}

/// <summary>
/// Represents a column in a tabular data schema.
/// </summary>
public class SchemaColumn
{
    /// <summary>
    /// Gets or sets the original name of the column.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the normalized name of the column (e.g., snake_case).
    /// </summary>
    [JsonPropertyName("normalizedName")]
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the data type of the column (e.g., string, number, boolean, date).
    /// </summary>
    [JsonPropertyName("dataType")]
    public string DataType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the column is required.
    /// </summary>
    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; set; } = false;

    /// <summary>
    /// Gets or sets a description of the column.
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a list of common values found in the column.
    /// </summary>
    [JsonPropertyName("commonValues")]
    public List<string> CommonValues { get; set; } = new List<string>();
}
