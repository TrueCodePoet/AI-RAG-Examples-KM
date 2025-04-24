// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Text.Json; // Added for JsonSerializer
using System.Text.Json.Serialization;
using Microsoft.KernelMemory.MemoryStorage;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Result class for memory records with similarity score.
/// </summary>
internal sealed class TabularMemoryRecordResult
{
    // Copy properties from AzureCosmosDbTabularMemoryRecord
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("payload")]
    public required Dictionary<string, object> Payload { get; init; } = [];

    [JsonPropertyName("tags")]
    public TagCollection Tags { get; init; } = [];

    [JsonPropertyName("embedding")]
    public float[] Vector { get; init; } = Array.Empty<float>();

    [JsonPropertyName("data")]
    public Dictionary<string, object> Data { get; init; } = [];

    [JsonPropertyName("source")]
    public Dictionary<string, string> Source { get; init; } = [];

    [JsonPropertyName("schemaId")]
    public string SchemaId { get; init; } = string.Empty;

    [JsonPropertyName("importBatchId")]
    public string ImportBatchId { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the similarity score.
    /// </summary>
    [JsonPropertyName("SimilarityScore")] // Keep this name for query mapping
    public double SimilarityScore { get; init; }

    /// <summary>
    /// Converts this result to a memory record.
    /// </summary>
    /// <param name="withEmbedding">Whether to include the embedding.</param>
    /// <returns>The memory record.</returns>
    internal MemoryRecord ToMemoryRecord(bool withEmbedding = true)
    {
        var id = AzureCosmosDbTabularMemoryRecord.DecodeId(Id); // Use static method
        var memoryRecord = new MemoryRecord
        {
            Id = id,
            Payload = Payload,
            Tags = Tags
        };

        // Add the tabular data to the payload
        if (Data.Count > 0)
        {
            memoryRecord.Payload["tabular_data"] = JsonSerializer.Serialize(Data, AzureCosmosDbTabularConfig.DefaultJsonSerializerOptions);
        }

        // Add the source information to the payload
        if (Source.Count > 0)
        {
            memoryRecord.Payload["source_info"] = JsonSerializer.Serialize(Source, AzureCosmosDbTabularConfig.DefaultJsonSerializerOptions);
        }

        // Add schema ID and import batch ID to the payload
        if (!string.IsNullOrEmpty(this.SchemaId))
        {
            memoryRecord.Payload["schema_id"] = this.SchemaId;
        }

        if (!string.IsNullOrEmpty(this.ImportBatchId))
        {
            memoryRecord.Payload["import_batch_id"] = this.ImportBatchId;
        }

        if (withEmbedding && this.Vector.Length > 0)
        {
            memoryRecord.Vector = new Embedding(this.Vector);
        }

        return memoryRecord;
    }
}
