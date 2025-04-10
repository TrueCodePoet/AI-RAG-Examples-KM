// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Azure Cosmos DB implementation of <see cref="IMemoryDb"/> for tabular data.
/// </summary>
internal sealed partial class AzureCosmosDbTabularMemory
{
    /// <summary>
    /// Creates a new schema for the given dataset and returns its ID and import batch ID.
    /// </summary>
    /// <param name="datasetName">The dataset name.</param>
    /// <param name="sourceFileName">The source file name.</param>
    /// <param name="data">The tabular data to extract schema from.</param>
    /// <param name="indexName">The index name to store the schema in.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the schema ID and import batch ID.</returns>
    private async Task<(string SchemaId, string ImportBatchId)> CreateOrUpdateSchemaAsync(
        string datasetName,
        string sourceFileName,
        Dictionary<string, object> data,
        string indexName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Always create a new schema with a unique ID for each import
            var schema = TabularDataSchema.Create(datasetName, sourceFileName);
            
            // Get existing schema to copy column information if available
            var existingSchema = await this.GetSchemaAsync(datasetName, cancellationToken).ConfigureAwait(false);
            
            if (existingSchema != null)
            {
                // Copy column information from existing schema
                foreach (var column in existingSchema.Columns)
                {
                    // Only add if not already in the new schema
                    if (!schema.Columns.Any(c => string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        schema.Columns.Add(column);
                    }
                }
            }

            // Add or update column information from current data
            foreach (var kvp in data)
            {
                // Check if column already exists in the schema
                var existingColumn = schema.Columns.FirstOrDefault(c => 
                    string.Equals(c.Name, kvp.Key, StringComparison.OrdinalIgnoreCase));
                    
                if (existingColumn != null)
                {
                    // Update existing column with new value
                    string valueStr = kvp.Value?.ToString() ?? string.Empty;
                    if (!existingColumn.CommonValues.Contains(valueStr))
                    {
                        existingColumn.CommonValues.Add(valueStr);
                        if (existingColumn.CommonValues.Count > 10)
                        {
                            existingColumn.CommonValues.RemoveAt(0); // Keep only the 10 most recent values
                        }
                    }
                    
                    // Verify data type consistency
                    string inferredType = InferDataType(kvp.Value);
                    if (existingColumn.DataType != inferredType)
                    {
                        // If types don't match, use the more general type (string)
                        if (existingColumn.DataType != "string" && inferredType != "string")
                        {
                            existingColumn.DataType = "string";
                        }
                    }
                }
                else
                {
                    // Add new column
                    var column = new SchemaColumn
                    {
                        Name = kvp.Key,
                        NormalizedName = NormalizeColumnName(kvp.Key),
                        DataType = InferDataType(kvp.Value),
                        CommonValues = new List<string> { kvp.Value?.ToString() ?? string.Empty }
                    };

                    schema.Columns.Add(column);
                }
            }

            // Store the new schema
            await this.StoreSchemaAsync(schema, indexName, cancellationToken).ConfigureAwait(false);
            
            this._logger.LogInformation("Created new schema {SchemaId} for dataset {DatasetName} with import batch {ImportBatchId}", 
                schema.Id, datasetName, schema.ImportBatchId);
                
            return (schema.Id, schema.ImportBatchId);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error creating schema for dataset {DatasetName}", datasetName);
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Extracts schema information from a record and stores it.
    /// </summary>
    /// <param name="datasetName">The dataset name.</param>
    /// <param name="data">The data to extract schema from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ExtractAndStoreSchemaAsync(
        string datasetName,
        Dictionary<string, object> data,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if schema already exists
            var existingSchema = await this.GetSchemaAsync(datasetName, cancellationToken).ConfigureAwait(false);
            
            if (existingSchema != null)
            {
                // Update existing schema with new column information
                await this.UpdateSchemaAsync(existingSchema, data, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Create new schema
            var schema = new TabularDataSchema
            {
                DatasetName = datasetName,
                ImportDate = DateTime.UtcNow,
                Columns = new List<SchemaColumn>()
            };

            // Extract column information
            foreach (var kvp in data)
            {
                var column = new SchemaColumn
                {
                    Name = kvp.Key,
                    NormalizedName = NormalizeColumnName(kvp.Key),
                    DataType = InferDataType(kvp.Value),
                    CommonValues = new List<string> { kvp.Value?.ToString() ?? string.Empty }
                };

                schema.Columns.Add(column);
            }

            // Store schema - pass the index name as null to use the default container selection logic
            await this.StoreSchemaAsync(schema, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error extracting and storing schema for dataset {DatasetName}", datasetName);
        }
    }

    /// <summary>
    /// Updates an existing schema with new column information.
    /// </summary>
    /// <param name="schema">The existing schema.</param>
    /// <param name="data">The new data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task UpdateSchemaAsync(
        TabularDataSchema schema,
        Dictionary<string, object> data,
        CancellationToken cancellationToken = default)
    {
        bool schemaChanged = false;

        // Update existing columns and add new ones
        foreach (var kvp in data)
        {
            var existingColumn = schema.Columns.FirstOrDefault(c => 
                string.Equals(c.Name, kvp.Key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.NormalizedName, NormalizeColumnName(kvp.Key), StringComparison.OrdinalIgnoreCase));

            if (existingColumn != null)
            {
                // Update common values if this is a new value
                string valueStr = kvp.Value?.ToString() ?? string.Empty;
                if (!existingColumn.CommonValues.Contains(valueStr))
                {
                    existingColumn.CommonValues.Add(valueStr);
                    if (existingColumn.CommonValues.Count > 10)
                    {
                        existingColumn.CommonValues.RemoveAt(0); // Keep only the 10 most recent values
                    }
                    schemaChanged = true;
                }

                // Verify data type consistency
                string inferredType = InferDataType(kvp.Value);
                if (existingColumn.DataType != inferredType)
                {
                    // If types don't match, use the more general type (string)
                    if (existingColumn.DataType != "string" && inferredType != "string")
                    {
                        existingColumn.DataType = "string";
                        schemaChanged = true;
                    }
                }
            }
            else
            {
                // Add new column
                var newColumn = new SchemaColumn
                {
                    Name = kvp.Key,
                    NormalizedName = NormalizeColumnName(kvp.Key),
                    DataType = InferDataType(kvp.Value),
                    CommonValues = new List<string> { kvp.Value?.ToString() ?? string.Empty }
                };

                schema.Columns.Add(newColumn);
                schemaChanged = true;
            }
        }

        // Only update if schema changed
        if (schemaChanged)
        {
            schema.ImportDate = DateTime.UtcNow; // Update timestamp
            await this.StoreSchemaAsync(schema, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Validates filter parameters against a schema.
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
        var validatedParameters = new Dictionary<string, object>();
        var warnings = new List<string>();

        if (!this._config.EnableSchemaManagement)
        {
            return (parameters, new List<string> { "Schema management is disabled. Parameters not validated." });
        }

        // Get schema for the dataset
        var schema = await this.GetSchemaAsync(datasetName, cancellationToken).ConfigureAwait(false);
        if (schema == null)
        {
            return (parameters, new List<string> { $"No schema found for dataset '{datasetName}'. Parameters not validated." });
        }

        // Validate each parameter
        foreach (var param in parameters)
        {
            string key = param.Key;
            object value = param.Value;

            // Handle data. prefix
            string fieldName = key;
            if (key.StartsWith("data.", StringComparison.Ordinal))
            {
                fieldName = key.Substring(5);
            }

            // Find matching column in schema
            var column = schema.Columns.FirstOrDefault(c => 
                string.Equals(c.Name, fieldName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.NormalizedName, fieldName, StringComparison.OrdinalIgnoreCase));

            if (column == null)
            {
                warnings.Add($"Field '{fieldName}' not found in schema for dataset '{datasetName}'.");
                validatedParameters[key] = value; // Add anyway, but with warning
                continue;
            }

            // Validate value against column type
            if (ValidateValue(value, column.DataType))
            {
                // Use the normalized name from the schema
                string normalizedKey = key.StartsWith("data.", StringComparison.Ordinal)
                    ? $"data.{column.NormalizedName}"
                    : column.NormalizedName;

                validatedParameters[normalizedKey] = value;
            }
            else
            {
                warnings.Add($"Value '{value}' is not valid for field '{fieldName}' of type '{column.DataType}'.");
                validatedParameters[key] = value; // Add anyway, but with warning
            }
        }

        return (validatedParameters, warnings);
    }
}
