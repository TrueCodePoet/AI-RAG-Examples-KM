// Copyright (c) Microsoft. All rights reserved.

using System;

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
