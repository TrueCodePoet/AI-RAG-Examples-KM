using Microsoft.KernelMemory;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text.Json;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

namespace AI_RAG_Examples_KM
{
    public partial class KernelMemoryQueryProcessor
    {
        // Generates filter templates from the user question based on schema information
        private async Task<List<MemoryFilter>> GenerateFiltersAsync(string question, string formattedSchemaInfo, string datasetName)
        {
            Console.WriteLine("--- Generating Filters ---");
            List<MemoryFilter> allFilters = new List<MemoryFilter>();
            List<string> warnings = new List<string>();
            
            try
            {
                // Create filter prompt template based on fuzzy match operator
                string filterPromptTemplate;
                if (_fuzzyMatchOperator.Equals("LIKE", StringComparison.OrdinalIgnoreCase))
                {
                    filterPromptTemplate = @"
        Analyze the user's question to identify up to 5 different filter templates that could be used to filter tabular data or apply tags, based ONLY on the provided schema fields.

        IMPORTANT: For string fields, generate partial (fuzzy) values that will be used with a SQL LIKE operator for matching. Use the '%' character as a wildcard at the start, end, or within values (e.g., '%corelight%', 'phoenix%', '%sensor'). You may generate multiple patterns if appropriate. Matching is case-insensitive.

        For AND logic, include multiple keys in the JSON object (all must match). For OR logic, use an array of values for a key (any value may match).

        Return your answer as a JSON array of up to 5 filter templates. Each template should be a JSON object as described below.

        Available Schema Fields (use these exact keys):
        {formattedSchemaInfo}

        RULES FOR OUTPUT KEYS:
        1. Use the EXACT normalized keys provided in the schema info above (e.g., ""data.server"", ""project""). Do NOT invent new keys.
        2. If a criterion refers to a structured data column, use the corresponding ""data.*"" key from the schema.
        3. If a criterion refers to a general category or context, use the corresponding standard tag key from the schema (e.g., ""project"").
        4. Extract the specific value mentioned for the criterion. For string fields, use a substring or keyword (not the full value) for fuzzy search, and use '%' as a wildcard.
        5. For OR logic, use an array of patterns for a key: { ""data.purpose"": [""%corelight%"", ""%sensor%""] }
        6. Focus on return as many potential records as you can while narrowing as much as you can.  be optimistic not pessimistic.
        7. If no specific filter criteria matching the schema are found, output an empty JSON object: {}
        8. Do Not inject filters that are not part of the question.  example.  Do not suggest a date range or project type if it was not asked for.

        FORMAT:
        Format the output STRICTLY as a JSON array of up to 5 filter templates (each a JSON object as described above).
        DO NOT use backticks in the response. Output MUST be a pure, correctly formatted JSON array.

        EXAMPLES:
        Question: Show me production servers.
        Output: [{""data.environment"": ""%product%""}]

        Question: List all servers for the 'Phoenix' or 'Austin' project.
        Output: [{""project"": [""phoenix%"", ""austin%""]}]

        Question: What is the server purpose for VAXVNAGG01?
        Output: [{""data.name"": ""%vaxv%""}]

        Question: Tell me about the system architecture.
        Output: [{}]

        Question: {{$input}}
        Output:
        ";
                }
                else // CONTAINS or other substring operator
                {
                    filterPromptTemplate = @"
        Analyze the user's question to identify specific criteria that can be used to filter tabular data or apply tags, based ONLY on the provided schema fields.

        IMPORTANT: For string fields, generate partial (fuzzy) values that will be used with a CONTAINS operator for matching. Use a substring or keyword that is likely to appear in the relevant field. Matching is case-insensitive.

        For AND logic, include multiple keys in the JSON object (all must match). For OR logic, use an array of values for a key (any value may match).

        Available Schema Fields (use these exact keys):
        {formattedSchemaInfo}

        RULES FOR OUTPUT KEYS:
        1. Use the EXACT normalized keys provided in the schema info above (e.g., ""data.server"", ""project""). Do NOT invent new keys.
        2. If a criterion refers to a structured data column, use the corresponding ""data.*"" key from the schema.
        3. If a criterion refers to a general category or context, use the corresponding standard tag key from the schema (e.g., ""project"").
        4. Extract the specific value mentioned for the criterion. For string fields, use a substring or keyword (not the full value) for fuzzy search.
        5. For OR logic, use an array of patterns for a key: { ""data.purpose"": [""corelight"", ""sensor""] }
        6. Focus on return as many potential records as you can while narrowing as much as you can.  be optimistic not pessimistic.
        7. If no specific filter criteria matching the schema are found, output an empty JSON object: {}
 
        FORMAT:
        Format the output STRICTLY as a JSON object containing the identified key-value pairs.
        DO NOT use backticks in the response. Output MUST be a pure, correctly formatted JSON structure.

        EXAMPLES:
        Question: Show me production servers.
        Output: {""data.environment"": ""product""}

        Question: List all servers for the 'Phoenix' or 'Austin' project.
        Output: {""project"": [""phoenix"", ""austin""]}

        Question: What is the purpose for VAXVNAGG01?
        Output: {""data.name"": ""vaxv""}

        Question: Tell me about the system architecture.
        Output: {}

        Question: {{$input}}
        Output:
        ";
                }
                filterPromptTemplate = filterPromptTemplate.Replace("{formattedSchemaInfo}", formattedSchemaInfo);

                Console.WriteLine($"[DEBUG] Prompt Template :\n{filterPromptTemplate}");
                
                // Create and invoke the filter function
                var generateFiltersFunction = _kernel.CreateFunctionFromPrompt(
                    filterPromptTemplate, 
                    functionName: "GenerateNormalizedFilters",
                    description: "Analyzes a user question and generates normalized structured filters as JSON.",
                    executionSettings: new AzureOpenAIPromptExecutionSettings { Temperature = 0.0 }
                );
                
                var filterArguments = new KernelArguments()
                {
                    { "input", question },
                    { "schemaInfo", formattedSchemaInfo }
                };

                var filterResult = await _kernel.InvokeAsync(generateFiltersFunction, filterArguments);
                var filterJson = filterResult.GetValue<string>()?.Trim();
                Console.WriteLine($"LLM Normalized Filter Suggestion: {filterJson}"); // Debug output

                // Process the filter JSON
                if (!string.IsNullOrWhiteSpace(filterJson) && filterJson != "{}")
                {
                    // Try to parse as an array of filter templates
                    try
                    {
                        var filterTemplates = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(filterJson);
                        if (filterTemplates != null && filterTemplates.Count > 0)
                        {
                            foreach (var filterDict in filterTemplates)
                            {
                                if (filterDict == null || filterDict.Count == 0) continue;
                                var paramDict = new Dictionary<string, object>();
                                foreach (var kvp in filterDict)
                                {
                                    if (kvp.Value is JsonElement elem)
                                    {
                                        if (elem.ValueKind == JsonValueKind.String)
                                        {
                                            paramDict[kvp.Key] = elem.GetString();
                                        }
                                        else if (elem.ValueKind == JsonValueKind.Array)
                                        {
                                            var list = new List<string>();
                                            foreach (var item in elem.EnumerateArray())
                                            {
                                                if (item.ValueKind == JsonValueKind.String)
                                                    list.Add(item.GetString());
                                            }
                                            paramDict[kvp.Key] = list;
                                        }
                                    }
                                    else if (kvp.Value is string s)
                                    {
                                        paramDict[kvp.Key] = s;
                                    }
                                    else if (kvp.Value is IEnumerable<object> objList)
                                    {
                                        paramDict[kvp.Key] = objList.ToList();
                                    }
                                    else
                                    {
                                        paramDict[kvp.Key] = kvp.Value;
                                    }
                                }

                                // Validate parameters against schema if available
                                MemoryFilter filter = null;
                                if (!string.IsNullOrEmpty(datasetName))
                                {
                                    try
                                    {
                                        TabularFilterHelper filterHelper;
                                        if (_tabularMemoryDb != null)
                                        {
                                            filterHelper = new TabularFilterHelper(_tabularMemoryDb);
                                        }
                                        else
                                        {
                                            filterHelper = new TabularFilterHelper(_memory, _indexName);
                                        }
                                        var result = await filterHelper.GenerateValidatedFilterAsync(datasetName, paramDict);
                                        filter = result.Filter;
                                        warnings.AddRange(result.Warnings);
                                    }
                                    catch
                                    {
                                        filter = null;
                                    }
                                }
                                if (filter == null)
                                {
                                    filter = new MemoryFilter();
                                    foreach (var kvp in paramDict)
                                    {
                                        if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
                                        {
                                            if (kvp.Value is string s)
                                            {
                                                filter.Add(kvp.Key, s);
                                            }
                                            else if (kvp.Value is List<string> slist)
                                            {
                                                filter.Add(kvp.Key, slist);
                                            }
                                            else if (kvp.Value is IEnumerable<object> objList)
                                            {
                                                var strList = objList.Select(x => x?.ToString() ?? "").ToList();
                                                filter.Add(kvp.Key, strList);
                                            }
                                            else
                                            {
                                                filter.Add(kvp.Key, kvp.Value.ToString() ?? "");
                                            }
                                        }
                                    }
                                }
                                allFilters.Add(filter);
                            }
                        }
                    }
                    catch
                    {
                        // Fallback: try to parse as a single filter template (object)
                        try
                        {
                            var filterDict = JsonSerializer.Deserialize<Dictionary<string, object>>(filterJson);
                            if (filterDict != null && filterDict.Count > 0)
                            {
                                var paramDict = new Dictionary<string, object>();
                                foreach (var kvp in filterDict)
                                {
                                    if (kvp.Value is JsonElement elem)
                                    {
                                        if (elem.ValueKind == JsonValueKind.String)
                                        {
                                            paramDict[kvp.Key] = elem.GetString();
                                        }
                                        else if (elem.ValueKind == JsonValueKind.Array)
                                        {
                                            var list = new List<string>();
                                            foreach (var item in elem.EnumerateArray())
                                            {
                                                if (item.ValueKind == JsonValueKind.String)
                                                    list.Add(item.GetString());
                                            }
                                            paramDict[kvp.Key] = list;
                                        }
                                    }
                                    else if (kvp.Value is string s)
                                    {
                                        paramDict[kvp.Key] = s;
                                    }
                                    else if (kvp.Value is IEnumerable<object> objList)
                                    {
                                        paramDict[kvp.Key] = objList.ToList();
                                    }
                                    else
                                    {
                                        paramDict[kvp.Key] = kvp.Value;
                                    }
                                }
                                MemoryFilter filter = null;
                                if (!string.IsNullOrEmpty(datasetName))
                                {
                                    try
                                    {
                                        TabularFilterHelper filterHelper;
                                        if (_tabularMemoryDb != null)
                                        {
                                            filterHelper = new TabularFilterHelper(_tabularMemoryDb);
                                        }
                                        else
                                        {
                                            filterHelper = new TabularFilterHelper(_memory, _indexName);
                                        }
                                        var result = await filterHelper.GenerateValidatedFilterAsync(datasetName, paramDict);
                                        filter = result.Filter;
                                        warnings.AddRange(result.Warnings);
                                    }
                                    catch
                                    {
                                        filter = null;
                                    }
                                }
                                if (filter == null)
                                {
                                    filter = new MemoryFilter();
                                    foreach (var kvp in paramDict)
                                    {
                                        if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
                                        {
                                            if (kvp.Value is string s)
                                            {
                                                filter.Add(kvp.Key, s);
                                            }
                                            else if (kvp.Value is List<string> slist)
                                            {
                                                filter.Add(kvp.Key, slist);
                                            }
                                            else if (kvp.Value is IEnumerable<object> objList)
                                            {
                                                var strList = objList.Select(x => x?.ToString() ?? "").ToList();
                                                filter.Add(kvp.Key, strList);
                                            }
                                            else
                                            {
                                                filter.Add(kvp.Key, kvp.Value.ToString() ?? "");
                                            }
                                        }
                                    }
                                }
                                allFilters.Add(filter);
                            }
                        }
                        catch
                        {
                            // Unable to parse as valid JSON
                            Console.WriteLine("Failed to parse filter JSON");
                        }
                    }
                }

                // If no filters were parsed, fallback to an empty filter
                if (allFilters.Count == 0)
                {
                    allFilters.Add(new MemoryFilter());
                }
                
                return allFilters;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during filter generation or parsing: {ex.Message}");
                // If no filters were created due to error, add an empty one as fallback
                if (allFilters.Count == 0)
                {
                    allFilters.Add(new MemoryFilter());
                }
                return allFilters;
            }
        }
    }
}
