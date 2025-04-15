// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Text;
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
    /// <summary>
    /// Processes memory filters to extract both standard tag filters and structured data filters.
    /// </summary>
    /// <param name="alias">The alias for the SQL query.</param>
    /// <param name="filters">The filters to process.</param>
    /// <returns>A tuple containing the WHERE clause and the parameters.</returns>
    private (string, IReadOnlyCollection<Tuple<string, object>>) ProcessFilters(string alias, ICollection<MemoryFilter>? filters = null)
    {
        if (filters is null || !filters.Any(f => !f.IsEmpty()))
        {
            return (string.Empty, Array.Empty<Tuple<string, object>>());
        }

        var parameters = new List<Tuple<string, object>>();
        var outerBuilder = new StringBuilder(); // For OR between filters
        bool firstOuterFilter = true;

        foreach (var filter in filters)
        {
            if (filter.IsEmpty()) { continue; }

            var innerBuilder = new StringBuilder(); // For AND within a filter
            bool firstInnerCondition = true;

            foreach (var pair in filter)
            {
                if (pair.Value is null) { continue; } // Skip null values

                if (!firstInnerCondition)
                {
                    innerBuilder.Append(" AND ");
                }

                // Store the original value for logging
                object originalValue = pair.Value;

                if (pair.Key.StartsWith("data.", StringComparison.Ordinal))
                {
                    // Normalize the field name (convert camelCase to snake_case)
                    string normalizedKey = NormalizeFieldName(pair.Key);

                    // Handle structured data filter
                    string fieldName = normalizedKey.Substring(5);
                    if (!string.IsNullOrEmpty(fieldName))
                    {
                        // Check the type of pair.Value using GetType()
                        object valueObj = pair.Value;
                        Type valueType = valueObj.GetType();
                        
                        if (valueType == typeof(string))
                        {
                            // For string values
                            string stringValue = (string)valueObj;
                            string paramName = $"@p_{parameters.Count}";
                            
                            // Determine whether to use fuzzy matching based on config and string length
                            bool useFuzzyMatch = this._config.FuzzyMatch.Enabled && 
                                              stringValue.Length >= this._config.FuzzyMatch.MinimumLength;
                            
                            // Create the parameter value based on the operator and case sensitivity
                            string paramValue = stringValue;
                            if (useFuzzyMatch && this._config.FuzzyMatch.Operator.Equals("LIKE", StringComparison.OrdinalIgnoreCase))
                            {
                                // For LIKE operator, convert spaces to % and add wildcards at start/end
                                paramValue = "%" + stringValue.Replace(" ", "%") + "%";
                            }
                            
                            // Apply case conversion for case-insensitive matching if configured
                            if (this._config.FuzzyMatch.CaseInsensitive)
                            {
                                paramValue = paramValue.ToLowerInvariant();
                            }
                            
                            // Add parameter
                            parameters.Add(Tuple.Create<string, object>(paramName, paramValue));

                            // Build the appropriate condition based on matching settings
                            if (useFuzzyMatch)
                            {
                                if (this._config.FuzzyMatch.Operator.Equals("LIKE", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Using LIKE operator for pattern matching
                                    if (this._config.FuzzyMatch.CaseInsensitive)
                                    {
                                        innerBuilder.Append($"LOWER({alias}.{AzureCosmosDbTabularMemoryRecord.DataField}.{fieldName}) LIKE {paramName}");
                                    }
                                    else
                                    {
                                        innerBuilder.Append($"{alias}.{AzureCosmosDbTabularMemoryRecord.DataField}.{fieldName} LIKE {paramName}");
                                    }
                                    this._logger.LogDebug("Using LIKE pattern matching for field {FieldName} with value {OriginalValue}", fieldName, originalValue);
                                }
                                else
                                {
                                    // Default is CONTAINS for substring matching
                                    if (this._config.FuzzyMatch.CaseInsensitive)
                                    {
                                        innerBuilder.Append($"CONTAINS(LOWER({alias}.{AzureCosmosDbTabularMemoryRecord.DataField}.{fieldName}), {paramName})");
                                    }
                                    else
                                    {
                                        innerBuilder.Append($"CONTAINS({alias}.{AzureCosmosDbTabularMemoryRecord.DataField}.{fieldName}, {paramName})");
                                    }
                                    this._logger.LogDebug("Using CONTAINS fuzzy matching for field {FieldName} with value {OriginalValue}", fieldName, originalValue);
                                }
                            }
                            else
                            {
                                // For non-fuzzy matching, use equality with case sensitivity setting
                                if (this._config.FuzzyMatch.CaseInsensitive)
                                {
                                    innerBuilder.Append($"LOWER({alias}.{AzureCosmosDbTabularMemoryRecord.DataField}.{fieldName}) = {paramName}");
                                }
                                else
                                {
                                    innerBuilder.Append($"{alias}.{AzureCosmosDbTabularMemoryRecord.DataField}.{fieldName} = {paramName}");
                                }
                                this._logger.LogDebug("Using exact matching for field {FieldName} with value {OriginalValue}", fieldName, originalValue);
                            }
                        }
                        else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(valueType) && 
                                 valueType != typeof(string)) // strings are IEnumerable<char> so we exclude them
                        {
                            // For collection of string values - handle as OR condition with each valid item
                            innerBuilder.Append("(");
                            bool firstValue = true;
                            
                            // Handle any collection type
                            var enumerable = (System.Collections.IEnumerable)valueObj;
                            foreach (var item in enumerable)
                            {
                                if (item != null)
                                {
                                    string paramName = $"@p_{parameters.Count}";
                                    parameters.Add(Tuple.Create<string, object>(paramName, item.ToString().ToLowerInvariant()));
                                    
                                    if (!firstValue)
                                    {
                                        innerBuilder.Append(" OR ");
                                    }
                                    
                                    innerBuilder.Append($"LOWER({alias}.{AzureCosmosDbTabularMemoryRecord.DataField}.{fieldName}) = {paramName}");
                                    firstValue = false;
                                }
                            }
                            
                            // Only add closing parenthesis if at least one value was processed
                            if (!firstValue)
                            {
                                innerBuilder.Append(")");
                            }
                            else
                            {
                                // Skip this condition if no valid values in collection
                                continue;
                            }
                        }
                        else
                        {
                            // For non-string values
                            string paramName = $"@p_{parameters.Count}";
                            parameters.Add(Tuple.Create<string, object>(paramName, pair.Value));
                            innerBuilder.Append($"{alias}.{AzureCosmosDbTabularMemoryRecord.DataField}.{fieldName} = {paramName}");
                        }
                        
                        firstInnerCondition = false;

                        // Log the normalization if it happened
                        if (normalizedKey != pair.Key)
                        {
                            this._logger.LogDebug("Normalized field name: {OriginalKey} -> {NormalizedKey}", pair.Key, normalizedKey);
                        }
                    }
                    else
                    {
                        this._logger.LogWarning("Invalid structured data filter key found: {Key}", pair.Key);
                    }
                }
                else // Handle standard tag filter
                {
                    // For tags, we need to handle both string and List<string?> comparisons
                    // Tag values in CosmosDB are stored as arrays, so we use EXISTS with a subquery
                    
                    // Check the type of pair.Value using GetType()
                    object valueObj = pair.Value;
                    Type valueType = valueObj.GetType();
                    
                    if (valueType == typeof(string))
                    {
                        // Single string value for tag
                        string stringValue = (string)valueObj;
                        string paramName = $"@p_{parameters.Count}";
                        parameters.Add(Tuple.Create<string, object>(paramName, stringValue.ToLowerInvariant()));
                        
                        // Case-insensitive tag matching
                        innerBuilder.Append($"EXISTS(SELECT VALUE t FROM t IN {alias}.{AzureCosmosDbTabularMemoryRecord.TagsField}[\"{pair.Key}\"] WHERE LOWER(t) = {paramName})");
                        firstInnerCondition = false;
                    }
                    else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(valueType) && 
                             valueType != typeof(string)) // strings are IEnumerable<char> so we exclude them
                    {
                        // Collection of strings for tag - create OR condition for each value
                        innerBuilder.Append("(");
                        bool firstValue = true;
                        
                        // Handle any collection type
                        var enumerable = (System.Collections.IEnumerable)valueObj;
                        foreach (var item in enumerable)
                        {
                            if (item != null)
                            {
                                string paramName = $"@p_{parameters.Count}";
                                parameters.Add(Tuple.Create<string, object>(paramName, item.ToString().ToLowerInvariant()));
                                
                                if (!firstValue)
                                {
                                    innerBuilder.Append(" OR ");
                                }
                                
                                innerBuilder.Append($"EXISTS(SELECT VALUE t FROM t IN {alias}.{AzureCosmosDbTabularMemoryRecord.TagsField}[\"{pair.Key}\"] WHERE LOWER(t) = {paramName})");
                                firstValue = false;
                            }
                        }
                        
                        // Only add closing parenthesis if at least one value was processed
                        if (!firstValue)
                        {
                            innerBuilder.Append(")");
                            firstInnerCondition = false;
                        }
                        else
                        {
                            // Skip this condition if no valid values in collection
                            continue;
                        }
                    }
                    else
                    {
                        // For non-string values (e.g., numbers, booleans)
                        string paramName = $"@p_{parameters.Count}";
                        parameters.Add(Tuple.Create<string, object>(paramName, pair.Value));
                        
                        innerBuilder.Append($"EXISTS(SELECT VALUE t FROM t IN {alias}.{AzureCosmosDbTabularMemoryRecord.TagsField}[\"{pair.Key}\"] WHERE t = {paramName})");
                        firstInnerCondition = false;
                    }
                }
            }

            // Only add this filter's conditions if it generated any valid conditions
            if (!firstInnerCondition) // means innerBuilder is not empty
            {
                if (!firstOuterFilter)
                {
                    outerBuilder.Append(" OR ");
                }
                outerBuilder.Append('(').Append(innerBuilder).Append(')');
                firstOuterFilter = false;
            }
        }

        if (firstOuterFilter) // means outerBuilder is empty (no valid filters found)
        {
            return (string.Empty, Array.Empty<Tuple<string, object>>());
        }

        // Return the complete WHERE clause
        return ($"WHERE {outerBuilder}", parameters);
    }

    /// <summary>
    /// Gets a record by its unique ID and partition key.
    /// </summary>
    /// <param name="index">The container/index name.</param>
    /// <param name="id">The record's original (decoded) ID.</param>
    /// <param name="partitionKey">The partition key (File field).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The record if found, or null.</returns>
    public async Task<AzureCosmosDbTabularMemoryRecord?> GetByIdAsync(
        string index,
        string id,
        string partitionKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var encodedId = AzureCosmosDbTabularMemoryRecord.EncodeId(id);
            var response = await this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainer(index)
                .ReadItemAsync<AzureCosmosDbTabularMemoryRecord>(
                    encodedId, new PartitionKey(partitionKey), cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            this._logger.LogWarning("Record with ID {Id} not found in index {Index}", id, index);
            return null;
        }
    }

    /// <summary>
    /// Gets the filterable fields from the index.
    /// </summary>
    /// <param name="index">The index name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A dictionary of field types and their available field names.</returns>
    public async Task<Dictionary<string, HashSet<string>>> GetFilterableFieldsAsync(
        string index,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["tags"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["data"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

        // Query for a sample of documents to extract field names
        var sql = "SELECT TOP 100 c.tags, c.data FROM c";
        var queryDefinition = new QueryDefinition(sql);

        try
        {
            using var feedIterator = this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainer(index)
                .GetItemQueryIterator<dynamic>(queryDefinition);

            while (feedIterator.HasMoreResults)
            {
                var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);

                foreach (var item in response)
                {
                    // Extract tag keys
                    if (item.tags != null)
                    {
                        foreach (var tagKey in ((IDictionary<string, object>)item.tags).Keys)
                        {
                            result["tags"].Add(tagKey);
                        }
                    }

                    // Extract data field keys
                    if (item.data != null)
                    {
                        foreach (var dataKey in ((IDictionary<string, object>)item.data).Keys)
                        {
                            result["data"].Add(dataKey);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error getting filterable fields from index {Index}", index);
        }

        return result;
    }

    /// <summary>
    /// Gets the top values for a specific field.
    /// </summary>
    /// <param name="index">The index name.</param>
    /// <param name="fieldType">The field type (tag or data).</param>
    /// <param name="fieldName">The field name.</param>
    /// <param name="limit">The maximum number of values to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of field values and their occurrence counts.</returns>
    public async Task<List<(string Value, int Count)>> GetTopFieldValuesAsync(
        string index,
        string fieldType,
        string fieldName,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var result = new List<(string Value, int Count)>();

        try
        {
            // For tag fields we need a different approach since they're arrays
            if (fieldType.Equals("tag", StringComparison.OrdinalIgnoreCase))
            {
                // For tags, we need to unnest the array to count values
                var tagSql = $@"
                    SELECT VALUE t FROM c
                    JOIN t IN c.{AzureCosmosDbTabularMemoryRecord.TagsField}[""{fieldName}""]
                    WHERE IS_DEFINED(c.{AzureCosmosDbTabularMemoryRecord.TagsField}[""{fieldName}""])
                ";

                var unnestQuery = new QueryDefinition(tagSql);
                
                // First fetch all tag values
                var tagValues = new Dictionary<string, int>();
                
                using var tagIterator = this._cosmosClient
                    .GetDatabase(this._databaseName)
                    .GetContainer(index)
                    .GetItemQueryIterator<string>(unnestQuery);
                
                // Count occurrences of each value
                while (tagIterator.HasMoreResults)
                {
                    var tagResponse = await tagIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                    
                    foreach (var tagValue in tagResponse)
                    {
                        if (tagValue != null)
                        {
                            string valueStr = tagValue.ToString();
                            if (tagValues.ContainsKey(valueStr))
                            {
                                tagValues[valueStr]++;
                            }
                            else
                            {
                                tagValues[valueStr] = 1;
                            }
                        }
                    }
                }
                
                // Sort by count and take the top 'limit' values
                result = tagValues
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(limit)
                    .Select(kvp => (kvp.Key, kvp.Value))
                    .ToList();
            }
            else
            {
                // For data fields, we can use traditional grouping
                string fieldPath = $"c.{AzureCosmosDbTabularMemoryRecord.DataField}.{fieldName}";
                
                // Query for the top values of the specified field
                var sql = $@"
                    SELECT {fieldPath} as value, COUNT(1) as count
                    FROM c
                    WHERE IS_DEFINED({fieldPath})
                    GROUP BY {fieldPath}
                    ORDER BY count DESC
                    OFFSET 0 LIMIT {limit}
                ";

                var queryDefinition = new QueryDefinition(sql);

                using var feedIterator = this._cosmosClient
                    .GetDatabase(this._databaseName)
                    .GetContainer(index)
                    .GetItemQueryIterator<dynamic>(queryDefinition);

                while (feedIterator.HasMoreResults)
                {
                    var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);

                    foreach (var item in response)
                    {
                        if (item.value != null)
                        {
                            string valueStr = item.value.ToString() ?? "";
                            int count = (int)item.count;
                            result.Add((valueStr, count));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error getting top values for field {FieldType}.{FieldName} from index {Index}",
                fieldType, fieldName, index);
        }

        return result;
    }
}
