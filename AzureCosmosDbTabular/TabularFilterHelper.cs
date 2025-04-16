// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    // Core properties
    private readonly ILogger<TabularFilterHelper> _logger;
    private readonly IMemoryDb _memoryDb;
    private readonly string _indexName;
    private readonly IKernelMemory? _memory;

    /// <summary>
    /// Initializes a new instance of the <see cref="TabularFilterHelper"/> class.
    /// </summary>
    /// <param name="memoryDb">The memory database instance (must be AzureCosmosDbTabularMemory).</param>
    /// <param name="logger">Optional logger.</param>
    public TabularFilterHelper(
        IMemoryDb memoryDb,
        ILogger<TabularFilterHelper>? logger = null)
    {
        // Verify it's the right type
        if (IsTabularMemoryDb(memoryDb))
        {
            this._memoryDb = memoryDb;
            this._logger = logger ?? Microsoft.KernelMemory.Diagnostics.DefaultLogger.Factory.CreateLogger<TabularFilterHelper>();
        }
        else
        {
            throw new ArgumentException(
                "The provided IMemoryDb must be an AzureCosmosDbTabularMemory instance.", nameof(memoryDb));
        }
    }
    
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
        Console.WriteLine("===== TABULAR FILTER HELPER DETAILED DIAGNOSTICS =====");
        Console.WriteLine($"Creating TabularFilterHelper with IKernelMemory type: {memory.GetType().FullName}");
        Console.WriteLine($"Index name provided: '{indexName}'");
        
        this._memory = memory;
        this._indexName = indexName;
        this._logger = logger ?? Microsoft.KernelMemory.Diagnostics.DefaultLogger.Factory.CreateLogger<TabularFilterHelper>();
        
        // Attempt to initialize MemoryDb from IKernelMemory
        try
        {
            Console.WriteLine("Calling MemoryHelper.GetMemoryDbFromKernelMemory to extract IMemoryDb instance...");
            var memoryDb = MemoryHelper.GetMemoryDbFromKernelMemory(memory);
            
            Console.WriteLine($"GetMemoryDbFromKernelMemory returned: {(memoryDb == null ? "NULL" : memoryDb.GetType().FullName)}");
            
            if (memoryDb != null)
            {
                var isTabular = IsTabularMemoryDb(memoryDb);
                Console.WriteLine($"Is returned memoryDb a tabular memory? {isTabular}");
                
                if (isTabular)
                {
                    Console.WriteLine("SUCCESS: Valid AzureCosmosDbTabularMemory instance found!");
                    this._memoryDb = memoryDb;
                }
                else
                {
                    Console.WriteLine($"WARNING: memoryDb is not a tabular memory, it's: {memoryDb.GetType().FullName}");
                    // Instead of throwing, create a stub implementation that will return empty results
                    Console.WriteLine("Creating TabularFilterHelper with limited functionality - schema operations will return empty results");
                    // Leave _memoryDb as null
                }
            }
            else
            {
                Console.WriteLine("WARNING: GetMemoryDbFromKernelMemory returned NULL");
                Console.WriteLine("Creating TabularFilterHelper with limited functionality - schema operations will return empty results");
                // Leave _memoryDb as null
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EXCEPTION when extracting memoryDb: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            Console.WriteLine("Creating TabularFilterHelper with limited functionality - schema operations will return empty results");
            // Leave _memoryDb as null - don't throw
        }
        finally
        {
            Console.WriteLine("===== END TABULAR FILTER HELPER DIAGNOSTICS =====");
        }
    }

    /// <summary>
    /// Checks if the given IMemoryDb is an AzureCosmosDbTabularMemory instance.
    /// </summary>
    private bool IsTabularMemoryDb(IMemoryDb memoryDb)
    {
        var type = memoryDb.GetType();
        Console.WriteLine($"IsTabularMemoryDb checking type: {type.FullName}");
        
        // Check by type name (case-insensitive)
        bool containsTabularByName = type.FullName?.IndexOf("AzureCosmosDbTabularMemory", StringComparison.OrdinalIgnoreCase) >= 0;
        
        // Check by full inheritance chain
        bool isCastableToTabular = false;
        try {
            isCastableToTabular = memoryDb is AzureCosmosDbTabularMemory;
            Console.WriteLine($"Direct cast check result: {isCastableToTabular}");
        }
        catch (Exception ex) {
            Console.WriteLine($"Exception during cast check: {ex.Message}");
        }
        
        // Check by interface implementation
        var interfaces = type.GetInterfaces();
        bool implementsTabularInterfaces = interfaces.Any(i => 
            i.FullName?.Contains("ITabularMemoryDb") == true || 
            i.FullName?.Contains("TabularMemoryInterface") == true);
        
        Console.WriteLine($"Type check results: ByName={containsTabularByName}, ByCast={isCastableToTabular}, ByInterface={implementsTabularInterfaces}");
        
        // Accept if any check passes
        return containsTabularByName || isCastableToTabular || implementsTabularInterfaces;
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
        if (_memoryDb == null)
        {
            Console.WriteLine("WARNING: _memoryDb is null, returning empty result for GetFilterableFieldsAsync");
            return new Dictionary<string, HashSet<string>>();
        }
        
        try
        {
            // Get the GetFilterableFieldsAsync method from the concrete type
            var method = _memoryDb.GetType().GetMethod("GetFilterableFieldsAsync");
            if (method == null)
            {
                throw new InvalidOperationException($"Method 'GetFilterableFieldsAsync' not found in type {_memoryDb.GetType().FullName}");
            }

            // Invoke the method
            var task = method.Invoke(_memoryDb, new object[] { indexName, cancellationToken }) as Task<Dictionary<string, HashSet<string>>>;
            if (task == null)
            {
                throw new InvalidOperationException("Failed to invoke GetFilterableFieldsAsync method");
            }

            return await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR in GetFilterableFieldsAsync: {ex.Message}");
            return new Dictionary<string, HashSet<string>>();
        }
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
        // Get the GetTopFieldValuesAsync method from the concrete type
        var method = _memoryDb.GetType().GetMethod("GetTopFieldValuesAsync");
        if (method == null)
        {
            throw new InvalidOperationException($"Method 'GetTopFieldValuesAsync' not found in type {_memoryDb.GetType().FullName}");
        }

        // Invoke the method
        var task = method.Invoke(_memoryDb, new object[] { indexName, fieldType, fieldName, limit, cancellationToken }) as Task<List<(string, int)>>;
        if (task == null)
        {
            throw new InvalidOperationException("Failed to invoke GetTopFieldValuesAsync method");
        }

        return await task.ConfigureAwait(false);
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
        // Get the GetSchemaAsync method from the concrete type
        var method = _memoryDb.GetType().GetMethod("GetSchemaAsync");
        if (method == null)
        {
            throw new InvalidOperationException($"Method 'GetSchemaAsync' not found in type {_memoryDb.GetType().FullName}");
        }

        // Invoke the method
        var task = method.Invoke(_memoryDb, new object[] { datasetName, cancellationToken }) as Task<TabularDataSchema?>;
        if (task == null)
        {
            throw new InvalidOperationException("Failed to invoke GetSchemaAsync method");
        }

        return await task.ConfigureAwait(false);
    }

    /// <summary>
    /// Lists all available schemas.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of schemas.</returns>
    public async Task<List<TabularDataSchema>> ListSchemasAsync(
        CancellationToken cancellationToken = default)
    {
        // Get the ListSchemasAsync method from the concrete type
        var method = _memoryDb.GetType().GetMethod("ListSchemasAsync");
        if (method == null)
        {
            throw new InvalidOperationException($"Method 'ListSchemasAsync' not found in type {_memoryDb.GetType().FullName}");
        }

        // Invoke the method
        var task = method.Invoke(_memoryDb, new object[] { cancellationToken }) as Task<List<TabularDataSchema>>;
        if (task == null)
        {
            throw new InvalidOperationException("Failed to invoke ListSchemasAsync method");
        }

        return await task.ConfigureAwait(false);
    }

    /// <summary>
    /// Lists all available dataset names.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of dataset names.</returns>
    public async Task<List<string>> ListDatasetNamesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_memoryDb == null)
        {
            Console.WriteLine("WARNING: _memoryDb is null, returning empty result for ListDatasetNamesAsync");
            return new List<string>();
        }

        try
        {
            // Get the ListDatasetNamesAsync method from the concrete type
            var method = _memoryDb.GetType().GetMethod("ListDatasetNamesAsync");
            if (method == null)
            {
                Console.WriteLine($"ERROR: Method 'ListDatasetNamesAsync' not found in type {_memoryDb.GetType().FullName}");
                return new List<string>();
            }

            // Invoke the method
            var task = method.Invoke(_memoryDb, new object[] { cancellationToken }) as Task<List<string>>;
            if (task == null)
            {
                Console.WriteLine("ERROR: Failed to invoke ListDatasetNamesAsync method");
                return new List<string>();
            }

            return await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR in ListDatasetNamesAsync: {ex.Message}");
            return new List<string>();
        }
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
        // Get the GetSchemasBySourceFileAsync method from the concrete type
        var method = _memoryDb.GetType().GetMethod("GetSchemasBySourceFileAsync");
        if (method == null)
        {
            throw new InvalidOperationException($"Method 'GetSchemasBySourceFileAsync' not found in type {_memoryDb.GetType().FullName}");
        }

        // Invoke the method
        var task = method.Invoke(_memoryDb, new object[] { sourceFileName, cancellationToken }) as Task<List<TabularDataSchema>>;
        if (task == null)
        {
            throw new InvalidOperationException("Failed to invoke GetSchemasBySourceFileAsync method");
        }

        return await task.ConfigureAwait(false);
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
        // Get the GetSchemaByIdAsync method from the concrete type
        var method = _memoryDb.GetType().GetMethod("GetSchemaByIdAsync");
        if (method == null)
        {
            throw new InvalidOperationException($"Method 'GetSchemaByIdAsync' not found in type {_memoryDb.GetType().FullName}");
        }

        // Invoke the method
        var task = method.Invoke(_memoryDb, new object[] { schemaId, cancellationToken }) as Task<TabularDataSchema?>;
        if (task == null)
        {
            throw new InvalidOperationException("Failed to invoke GetSchemaByIdAsync method");
        }

        return await task.ConfigureAwait(false);
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
        // This method returns IAsyncEnumerable, so we need special handling
        var method = _memoryDb.GetType().GetMethod("GetRecordsBySchemaIdAsync");
        if (method == null)
        {
            throw new InvalidOperationException($"Method 'GetRecordsBySchemaIdAsync' not found in type {_memoryDb.GetType().FullName}");
        }

        // Invoke the method to get the IAsyncEnumerable
        // We'll manually enumerate it and convert to a List
        var result = new List<MemoryRecord>();
        var asyncEnumerable = method.Invoke(_memoryDb, new object[] { indexName, schemaId, limit, withEmbeddings, cancellationToken });
        
        if (asyncEnumerable == null)
        {
            throw new InvalidOperationException("Failed to invoke GetRecordsBySchemaIdAsync method");
        }

        // Get the GetAsyncEnumerator method from IAsyncEnumerable<MemoryRecord>
        var enumeratorMethod = asyncEnumerable.GetType().GetMethod("GetAsyncEnumerator");
        if (enumeratorMethod == null)
        {
            throw new InvalidOperationException("Could not get GetAsyncEnumerator method");
        }
        
        // Get the enumerator
        var enumerator = enumeratorMethod.Invoke(asyncEnumerable, new object[] { cancellationToken });
        if (enumerator == null)
        {
            throw new InvalidOperationException("Failed to get async enumerator");
        }
        
        // Get the MoveNextAsync and Current methods
        var moveNextMethod = enumerator.GetType().GetMethod("MoveNextAsync");
        var currentProperty = enumerator.GetType().GetProperty("Current");
        
        if (moveNextMethod == null || currentProperty == null)
        {
            throw new InvalidOperationException("Could not get MoveNextAsync method or Current property");
        }
        
        // Enumerate the results manually
        while (true)
        {
            // Call MoveNextAsync and await the result
            var moveNextTask = moveNextMethod.Invoke(enumerator, Array.Empty<object>()) as Task<bool>;
            if (moveNextTask == null)
            {
                throw new InvalidOperationException("Failed to invoke MoveNextAsync");
            }
            
            bool hasNext = await moveNextTask.ConfigureAwait(false);
            if (!hasNext)
            {
                break;
            }
            
            // Get the current item
            var current = currentProperty.GetValue(enumerator) as MemoryRecord;
            if (current != null)
            {
                result.Add(current);
            }
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
        // This method returns IAsyncEnumerable, so we need special handling
        var method = _memoryDb.GetType().GetMethod("GetRecordsByImportBatchIdAsync");
        if (method == null)
        {
            throw new InvalidOperationException($"Method 'GetRecordsByImportBatchIdAsync' not found in type {_memoryDb.GetType().FullName}");
        }

        // Invoke the method to get the IAsyncEnumerable
        // We'll manually enumerate it and convert to a List
        var result = new List<MemoryRecord>();
        var asyncEnumerable = method.Invoke(_memoryDb, new object[] { indexName, importBatchId, limit, withEmbeddings, cancellationToken });
        
        if (asyncEnumerable == null)
        {
            throw new InvalidOperationException("Failed to invoke GetRecordsByImportBatchIdAsync method");
        }

        // Get the GetAsyncEnumerator method from IAsyncEnumerable<MemoryRecord>
        var enumeratorMethod = asyncEnumerable.GetType().GetMethod("GetAsyncEnumerator");
        if (enumeratorMethod == null)
        {
            throw new InvalidOperationException("Could not get GetAsyncEnumerator method");
        }
        
        // Get the enumerator
        var enumerator = enumeratorMethod.Invoke(asyncEnumerable, new object[] { cancellationToken });
        if (enumerator == null)
        {
            throw new InvalidOperationException("Failed to get async enumerator");
        }
        
        // Get the MoveNextAsync and Current methods
        var moveNextMethod = enumerator.GetType().GetMethod("MoveNextAsync");
        var currentProperty = enumerator.GetType().GetProperty("Current");
        
        if (moveNextMethod == null || currentProperty == null)
        {
            throw new InvalidOperationException("Could not get MoveNextAsync method or Current property");
        }
        
        // Enumerate the results manually
        while (true)
        {
            // Call MoveNextAsync and await the result
            var moveNextTask = moveNextMethod.Invoke(enumerator, Array.Empty<object>()) as Task<bool>;
            if (moveNextTask == null)
            {
                throw new InvalidOperationException("Failed to invoke MoveNextAsync");
            }
            
            bool hasNext = await moveNextTask.ConfigureAwait(false);
            if (!hasNext)
            {
                break;
            }
            
            // Get the current item
            var current = currentProperty.GetValue(enumerator) as MemoryRecord;
            if (current != null)
            {
                result.Add(current);
            }
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
        // Get the GetSchemaForRecordAsync method from the concrete type
        var method = _memoryDb.GetType().GetMethod("GetSchemaForRecordAsync");
        if (method == null)
        {
            throw new InvalidOperationException($"Method 'GetSchemaForRecordAsync' not found in type {_memoryDb.GetType().FullName}");
        }

        // Invoke the method
        var task = method.Invoke(_memoryDb, new object[] { indexName, recordId, cancellationToken }) as Task<TabularDataSchema?>;
        if (task == null)
        {
            throw new InvalidOperationException("Failed to invoke GetSchemaForRecordAsync method");
        }

        return await task.ConfigureAwait(false);
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
        // Get the ValidateParametersAsync method from the concrete type
        var method = _memoryDb.GetType().GetMethod("ValidateParametersAsync");
        if (method == null)
        {
            throw new InvalidOperationException($"Method 'ValidateParametersAsync' not found in type {_memoryDb.GetType().FullName}");
        }

        // Invoke the method
        var task = method.Invoke(_memoryDb, new object[] { datasetName, parameters, cancellationToken }) as Task<(Dictionary<string, object>, List<string>)>;
        if (task == null)
        {
            throw new InvalidOperationException("Failed to invoke ValidateParametersAsync method");
        }

        return await task.ConfigureAwait(false);
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
            if (param.Value is string s)
            {
                filter.Add(param.Key, s);
            }
            else if (param.Value is IEnumerable<string> stringList)
            {
                filter.Add(param.Key, stringList.ToList());
            }
            else if (param.Value is IEnumerable<object> objList && !(param.Value is string))
            {
                var strList = objList.Select(x => x?.ToString() ?? string.Empty).ToList();
                filter.Add(param.Key, strList);
            }
            else if (param.Value != null)
            {
                filter.Add(param.Key, param.Value.ToString() ?? string.Empty);
            }
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
}
