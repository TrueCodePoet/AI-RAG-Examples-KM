// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.MemoryStorage;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Helper class for AI-driven filtering of tabular data.
/// </summary>
public class TabularFilterHelper
{
    private readonly IKernelMemory _memory;
    private readonly ILogger<TabularFilterHelper> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TabularFilterHelper"/> class.
    /// </summary>
    /// <param name="memory">The kernel memory instance.</param>
    /// <param name="logger">Optional logger.</param>
    public TabularFilterHelper(
        IKernelMemory memory,
        ILogger<TabularFilterHelper>? logger = null)
    {
        this._memory = memory;
        this._logger = logger ?? Microsoft.KernelMemory.Diagnostics.DefaultLogger.Factory.CreateLogger<TabularFilterHelper>();
    }

    /// <summary>
    /// Gets the filterable fields from the index.
    /// </summary>
    /// <param name="indexName">The index name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A dictionary of field types and their available field names.</returns>
    public async Task<Dictionary<string, HashSet<string>>> GetFilterableFieldsAsync(
        string indexName,
        CancellationToken cancellationToken = default)
    {
        // Get the memory DB instance
        var memoryDb = GetTabularMemoryDb();

        // Get the filterable fields
        return await memoryDb.GetFilterableFieldsAsync(indexName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the top values for a specific field.
    /// </summary>
    /// <param name="indexName">The index name.</param>
    /// <param name="fieldType">The field type (tag or data).</param>
    /// <param name="fieldName">The field name.</param>
    /// <param name="limit">The maximum number of values to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of field values and their occurrence counts.</returns>
    public async Task<List<(string Value, int Count)>> GetTopFieldValuesAsync(
        string indexName,
        string fieldType,
        string fieldName,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        // Get the memory DB instance
        var memoryDb = GetTabularMemoryDb();

        // Get the top field values
        return await memoryDb.GetTopFieldValuesAsync(indexName, fieldType, fieldName, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a schema by dataset name.
    /// </summary>
    /// <param name="datasetName">The dataset name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schema, or null if not found.</returns>
    public async Task<TabularDataSchema?> GetSchemaAsync(
        string datasetName,
        CancellationToken cancellationToken = default)
    {
        // Get the memory DB instance
        var memoryDb = GetTabularMemoryDb();

        // Get the schema
        return await memoryDb.GetSchemaAsync(datasetName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists all available schemas.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of schemas.</returns>
    public async Task<List<TabularDataSchema>> ListSchemasAsync(
        CancellationToken cancellationToken = default)
    {
        // Get the memory DB instance
        var memoryDb = GetTabularMemoryDb();

        // List schemas
        return await memoryDb.ListSchemasAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists all available dataset names.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of dataset names.</returns>
    public async Task<List<string>> ListDatasetNamesAsync(
        CancellationToken cancellationToken = default)
    {
        // Get the memory DB instance
        var memoryDb = GetTabularMemoryDb();

        // List dataset names
        return await memoryDb.ListDatasetNamesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets schemas by source file name.
    /// </summary>
    /// <param name="sourceFileName">The source file name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of schemas for the specified source file.</returns>
    public async Task<List<TabularDataSchema>> GetSchemasBySourceFileAsync(
        string sourceFileName,
        CancellationToken cancellationToken = default)
    {
        // Get the memory DB instance
        var memoryDb = GetTabularMemoryDb();

        // Get schemas by source file
        return await memoryDb.GetSchemasBySourceFileAsync(sourceFileName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a schema by ID.
    /// </summary>
    /// <param name="schemaId">The schema ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schema, or null if not found.</returns>
    public async Task<TabularDataSchema?> GetSchemaByIdAsync(
        string schemaId,
        CancellationToken cancellationToken = default)
    {
        // Get the memory DB instance
        var memoryDb = GetTabularMemoryDb();

        // Get schema by ID
        return await memoryDb.GetSchemaByIdAsync(schemaId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets all records that belong to a specific schema.
    /// </summary>
    /// <param name="indexName">The index name.</param>
    /// <param name="schemaId">The schema ID.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="withEmbeddings">Whether to include embeddings in the results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of memory records.</returns>
    public async Task<List<MemoryRecord>> GetRecordsBySchemaIdAsync(
        string indexName,
        string schemaId,
        int limit = 100,
        bool withEmbeddings = false,
        CancellationToken cancellationToken = default)
    {
        // Get the memory DB instance
        var memoryDb = GetTabularMemoryDb();

        // Get records by schema ID
        var result = new List<MemoryRecord>();
        await foreach (var record in memoryDb.GetRecordsBySchemaIdAsync(
            indexName, schemaId, limit, withEmbeddings, cancellationToken))
        {
            result.Add(record);
        }
        return result;
    }

    /// <summary>
    /// Gets all records that belong to a specific import batch.
    /// </summary>
    /// <param name="indexName">The index name.</param>
    /// <param name="importBatchId">The import batch ID.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="withEmbeddings">Whether to include embeddings in the results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of memory records.</returns>
    public async Task<List<MemoryRecord>> GetRecordsByImportBatchIdAsync(
        string indexName,
        string importBatchId,
        int limit = 100,
        bool withEmbeddings = false,
        CancellationToken cancellationToken = default)
    {
        // Get the memory DB instance
        var memoryDb = GetTabularMemoryDb();

        // Get records by import batch ID
        var result = new List<MemoryRecord>();
        await foreach (var record in memoryDb.GetRecordsByImportBatchIdAsync(
            indexName, importBatchId, limit, withEmbeddings, cancellationToken))
        {
            result.Add(record);
        }
        return result;
    }

    /// <summary>
    /// Gets the schema for a specific record.
    /// </summary>
    /// <param name="indexName">The index name.</param>
    /// <param name="recordId">The record ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schema, or null if not found.</returns>
    public async Task<TabularDataSchema?> GetSchemaForRecordAsync(
        string indexName,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        // Get the memory DB instance
        var memoryDb = GetTabularMemoryDb();

        // Get schema for record
        return await memoryDb.GetSchemaForRecordAsync(indexName, recordId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates parameters against a schema.
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
        // Get the memory DB instance
        var memoryDb = GetTabularMemoryDb();

        // Validate parameters
        return await memoryDb.ValidateParametersAsync(datasetName, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Generates a validated filter based on parameters and schema.
    /// </summary>
    /// <param name="datasetName">The dataset name.</param>
    /// <param name="parameters">The parameters to validate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the generated filter and any warnings.</returns>
    public async Task<(MemoryFilter Filter, List<string> Warnings)> GenerateValidatedFilterAsync(
        string datasetName,
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken = default)
    {
        // Validate parameters against schema
        var (validatedParameters, warnings) = await ValidateParametersAsync(
            datasetName, parameters, cancellationToken).ConfigureAwait(false);

        // Create filter with validated parameters
        var filter = new MemoryFilter();
        foreach (var param in validatedParameters)
        {
            filter.Add(param.Key, param.Value?.ToString() ?? string.Empty);
        }

        return (filter, warnings);
    }

    /// <summary>
    /// Generates a filter based on field and value.
    /// </summary>
    /// <param name="fieldType">The field type (tag or data).</param>
    /// <param name="fieldName">The field name.</param>
    /// <param name="value">The value to filter for.</param>
    /// <returns>The generated memory filter.</returns>
    public MemoryFilter GenerateFilter(string fieldType, string fieldName, string value)
    {
        var filter = new MemoryFilter();

        if (fieldType == "tag")
        {
            filter.Add(fieldName, value);
        }
        else // data field
        {
            filter.Add($"data.{fieldName}", value);
        }

        return filter;
    }

    /// <summary>
    /// Generates a filter based on field and value.
    /// </summary>
    /// <param name="fieldType">The field type (tag or data).</param>
    /// <param name="fieldName">The field name.</param>
    /// <param name="value">The value to filter for.</param>
    /// <returns>The generated memory filter.</returns>
    public MemoryFilter GenerateObjectFilter(string fieldType, string fieldName, object value)
    {
        var filter = new MemoryFilter();
        string stringValue = value?.ToString() ?? string.Empty;

        if (fieldType == "tag")
        {
            filter.Add(fieldName, stringValue);
        }
        else // data field
        {
            filter.Add($"data.{fieldName}", stringValue);
        }

        return filter;
    }

    /// <summary>
    /// Gets the tabular memory DB instance.
    /// </summary>
    /// <returns>The tabular memory DB instance.</returns>
    private AzureCosmosDbTabularMemory GetTabularMemoryDb()
    {
        // Get the memory DB instance using reflection
        var memoryDbField = this._memory.GetType().GetField("_memoryDb", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (memoryDbField == null)
        {
            throw new InvalidOperationException("Could not access the memory DB instance.");
        }

        var memoryDb = memoryDbField.GetValue(this._memory);
        if (memoryDb is not AzureCosmosDbTabularMemory tabularMemoryDb)
        {
            throw new InvalidOperationException("The memory DB is not an AzureCosmosDbTabularMemory instance.");
        }

        return tabularMemoryDb;
    }
}
