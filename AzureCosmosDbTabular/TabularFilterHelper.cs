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
using AI_RAG_Examples_KM; // For MemoryHelper

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Helper class for AI-driven filtering of tabular data.
/// </summary>
public class TabularFilterHelper
{
    private readonly IKernelMemory _memory;
    private readonly ILogger<TabularFilterHelper> _logger;
    private readonly string _indexName;

    /// <summary>
    /// Initializes a new instance of the <see cref="TabularFilterHelper"/> class.
    /// </summary>
    /// <param name="memory">The kernel memory instance.</param>
    /// <param name="indexName">The index name to use for operations.</param>
    /// <param name="logger">Optional logger.</param>
    public TabularFilterHelper(
        IKernelMemory memory,
        string indexName = "",
        ILogger<TabularFilterHelper>? logger = null)
    {
        this._memory = memory;
        this._indexName = indexName;
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
        try
        {
            // Start by trying to get the memory DB instance using the helper method from Program.cs
            var memoryDb = MemoryHelper.GetMemoryDbFromKernelMemory(this._memory);
            if (memoryDb != null)
            {
                // Check if it's already an AzureCosmosDbTabularMemory
                if (memoryDb is AzureCosmosDbTabularMemory tabularMemoryDb)
                {
                    this._logger.LogInformation("Successfully obtained AzureCosmosDbTabularMemory instance directly");
                    return tabularMemoryDb;
                }
                
                // If it's an IMemoryDb but not AzureCosmosDbTabularMemory, try to cast it
                // This might happen if we're using a proxy or decorator pattern
                try
                {
                    // Try to cast using dynamic to bypass compile-time type checking
                    dynamic dynamicMemoryDb = memoryDb;
                    AzureCosmosDbTabularMemory castedMemoryDb = dynamicMemoryDb;
                    this._logger.LogInformation("Successfully cast IMemoryDb to AzureCosmosDbTabularMemory using dynamic");
                    return castedMemoryDb;
                }
                catch (Exception castEx)
                {
                    // If dynamic casting fails, log and continue to next method
                    this._logger.LogWarning("Could not cast IMemoryDb to AzureCosmosDbTabularMemory: {ErrorType}, {ErrorMessage}", 
                        castEx.GetType().Name, castEx.Message);
                }
            }
            else
            {
                this._logger.LogWarning("MemoryHelper.GetMemoryDbFromKernelMemory returned null");
            }
            
            // Try multiple reflection approaches to find the memory DB
            
            // 1. Try the standard field name first
            try
            {
                var memoryDbField = this._memory.GetType().GetField("_memoryDb", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (memoryDbField != null)
                {
                    var reflectedMemoryDb = memoryDbField.GetValue(this._memory);
                    if (reflectedMemoryDb is AzureCosmosDbTabularMemory reflectedTabularMemoryDb)
                    {
                        this._logger.LogInformation("Found AzureCosmosDbTabularMemory instance via _memoryDb field reflection");
                        return reflectedTabularMemoryDb;
                    }
                    else if (reflectedMemoryDb != null)
                    {
                        this._logger.LogWarning("_memoryDb field contains a different type: {ActualType}", reflectedMemoryDb.GetType().FullName);
                    }
                }
                else
                {
                    this._logger.LogWarning("Could not find _memoryDb field in memory object");
                }
            }
            catch (Exception ex)
            {
                this._logger.LogWarning("Error accessing _memoryDb field: {Error}", ex.Message);
            }
            
            // 2. Try looking through all private fields for any AzureCosmosDbTabularMemory
            try
            {
                foreach (var field in this._memory.GetType().GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    try
                    {
                        var value = field.GetValue(this._memory);
                        if (value is AzureCosmosDbTabularMemory tabularMemoryDb)
                        {
                            this._logger.LogInformation("Found AzureCosmosDbTabularMemory instance in field {FieldName}", field.Name);
                            return tabularMemoryDb;
                        }
                    }
                    catch (Exception) { /* Ignore individual field access errors */ }
                }
            }
            catch (Exception ex)
            {
                this._logger.LogWarning("Error scanning fields for AzureCosmosDbTabularMemory: {Error}", ex.Message);
            }

            // 3. Try the orchestrator
            try
            {
                var orchestratorProp = this._memory.GetType().GetProperty("Orchestrator");
                if (orchestratorProp != null)
                {
                    var orchestrator = orchestratorProp.GetValue(this._memory);
                    var orchestratorType = orchestrator?.GetType();
                    
                    // Try to find _memoryDb in orchestrator
                    var orchestratorMemoryDbField = orchestratorType?.GetField("_memoryDb", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (orchestratorMemoryDbField != null)
                    {
                        var orchestratorMemoryDb = orchestratorMemoryDbField.GetValue(orchestrator);
                        if (orchestratorMemoryDb is AzureCosmosDbTabularMemory tabularMemoryDb)
                        {
                            this._logger.LogInformation("Found AzureCosmosDbTabularMemory instance in orchestrator._memoryDb");
                            return tabularMemoryDb;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                this._logger.LogWarning("Error accessing orchestrator: {Error}", ex.Message);
            }

            // We can't easily create a new instance because of the required dependencies
            // (CosmosClient, ITextEmbeddingGenerator, etc.)
            if (!string.IsNullOrEmpty(this._indexName))
            {
                this._logger.LogError(
                    "Could not locate an existing AzureCosmosDbTabularMemory instance. " +
                    "Cannot create a fallback instance due to dependency requirements. " +
                    "Make sure TabularFilterHelper is being created with the same IKernelMemory " +
                    "instance used throughout the application.");
            }

            // If we get here, we haven't found a valid instance and can't create one
            throw new InvalidOperationException(
                "Could not find or create an AzureCosmosDbTabularMemory instance. Provide an index name in the constructor to enable fallback instance creation.");
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error accessing the memory DB instance");
            throw new InvalidOperationException("Could not access the memory DB instance.", ex);
        }
    }
}
