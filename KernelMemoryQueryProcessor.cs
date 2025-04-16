using Microsoft.KernelMemory;
using Microsoft.SemanticKernel;
using Microsoft.KernelMemory.MemoryStorage; // For TagCollection
using Microsoft.SemanticKernel.Connectors.AzureOpenAI; // For AzureOpenAIPromptExecutionSettings
using Microsoft.KernelMemory.AI.AzureOpenAI; // For AzureOpenAIConfig
using System.Text.Json; // For JsonSerializer
using Microsoft.SemanticKernel.Connectors.OpenAI; // For MemoryFilter
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular; // For TabularFilterHelper, TabularDataSchema
using Microsoft.Extensions.DependencyInjection; // For GetRequiredService
using System.Text; // For StringBuilder

namespace AI_RAG_Examples_KM
{
public class KernelMemoryQueryProcessor
{
    private readonly IKernelMemory _memory;
    private readonly Kernel _kernel;
    private readonly string _indexName;
    private readonly AzureOpenAIConfig _azureOpenAITextConfig;
    private readonly IMemoryDb? _tabularMemoryDb;
    private readonly bool _skipDatasetIdentification;
    private readonly string _fuzzyMatchOperator;

    public KernelMemoryQueryProcessor(
        IKernelMemory memory,
        Kernel kernel,
        string indexName,
        AzureOpenAIConfig azureOpenAITextConfig,
        IMemoryDb? tabularMemoryDb = null,
        string fuzzyMatchOperator = "CONTAINS")
    {
        _memory = memory;
        _kernel = kernel;
        _indexName = indexName;
        _azureOpenAITextConfig = azureOpenAITextConfig;
        _tabularMemoryDb = tabularMemoryDb;
        _fuzzyMatchOperator = fuzzyMatchOperator;

        // If we don't have a valid tabularMemoryDb instance, we'll skip the dataset identification step
        _skipDatasetIdentification = _tabularMemoryDb == null;
        if (_skipDatasetIdentification)
        {
            Console.WriteLine("WARNING: No valid tabularMemoryDb instance provided - dataset identification will be skipped");
            Console.WriteLine("This is expected when reflection cannot find the database instance in MemoryServerless implementation");
            Console.WriteLine("Will proceed without schema-based validation. Use direct parameter passing when possible.");
        }
    }

        public async Task AskQuestionAsync(string question)
        {
            if (false)
            {
                var answer = await _memory.SearchAsync(question, index: _indexName);
         
                //Console.WriteLine($"Answer: {answer.Results}");
         
                // Display referenced documents
                if (answer.Results != null && answer.Results.Count > 0)
                {
                    Console.WriteLine("Referenced Documents:");
                    foreach (var doc in answer.Results)
                    {
                        Console.WriteLine($" {doc.Index} - {doc.SourceName} ({doc.SourceUrl})");
                    }
                }
                else
                {
                    Console.WriteLine("No referenced documents found.");
                }
            }
         
            if (true)
            {
                string sources = "";
                var answer = await _memory.AskAsync(question, index: _indexName);
                foreach (var x in answer.RelevantSources)
                {
                    sources += $"  - {x.SourceName}  - {x.Link} [{x.Partitions.First().LastUpdate:D}]" + Environment.NewLine;
                }
         
                var SK_Prompt = $@"
                        Question to Kernel Memory: {question}
         
                        Kernel Memory Answer: {answer.Result}
         
                        Kernel Memory Sources:
                        {sources}
         
                        ---
                        Only Respond with information from Kernel Memory including RelevantSources.  
                       
                        If you don't know, then respond with 'This information is not part of our internal documents.'
         
                        If you do know then Start with 'According to our internal documents'.  Reply with the best answer.
         
                        Make sure to inlude the document name and reference id if available.
                        ";
         
                var plugin = new MemoryPlugin(_memory, defaultIndex: _indexName, waitForIngestionToComplete: true);
                var localKernel = KernelInitializer.InitializeKernel(_azureOpenAITextConfig);
                localKernel.ImportPluginFromObject(plugin, "SEOmemory");
                Microsoft.SemanticKernel.Connectors.AzureOpenAI.AzureOpenAIPromptExecutionSettings settings = new()
                {
                    ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions  //ToolCallBehavior.AutoInvokeKernelFunctions,
         
                };
         
                /*
         
                SK_Prompt = @"
                        Question to Kernel Memory: {{$input}}
         
                        Kernel Memory Answer: {{SEOmemory.search query=$input}}
         
                        Only Respond with information from Kernel Memory including RelevantSources.  
                       
                        If you don't know then respond with 'This information is not part of our internal documents.'
         
                        If you do know then Start with 'According to our internal documents'.  Reply with the best answer.  Make sure your answer is complete!
         
                        Make sure to inlude the document name and reference id if available.
                        ";
                */
         
                KernelArguments arguments = new KernelArguments(settings)
                            {
                                { "input", question },
                            };
         
                var response = await localKernel.InvokePromptAsync(SK_Prompt, arguments);
         
                Console.WriteLine(response.GetValue<string>());
                Console.ReadLine();
            }
        }

        public async Task AskTabularQuestionAsync(string question, int? resultLimit = null)
        {
            // --- Dataset Identification Step ---
            Console.WriteLine("--- Identifying Dataset ---");
            
            string datasetName = string.Empty;
            
            try
            {
                // Create a TabularFilterHelper with the memory instance provided in constructor if available
                TabularFilterHelper filterHelper;
                if (_tabularMemoryDb != null)
                {
                    filterHelper = new TabularFilterHelper(_tabularMemoryDb);
                }
                else
                {
                    // Fall back to the original approach with reflection
                    filterHelper = new TabularFilterHelper(_memory, _indexName);
                }
                
                // Get list of available datasets
                var datasetList = await filterHelper.ListDatasetNamesAsync();
                
                if (datasetList.Count > 0)
                {
                    // Use LLM to identify the most relevant dataset
                    string datasetPromptTemplate = @"
You are analyzing a user query to determine which dataset it is most likely referring to.

Available datasets:
{{$datasets}}

User query: {{$query}}

Analyze the query and determine which dataset it is most likely referring to.
Return ONLY the name of the most relevant dataset. If no dataset seems relevant, return 'none'.

Dataset:";

                    var datasetFunction = _kernel.CreateFunctionFromPrompt(
                        datasetPromptTemplate,
                        new OpenAIPromptExecutionSettings { Temperature = 0.0 });
                    
                    var datasetResult = await _kernel.InvokeAsync(datasetFunction, new KernelArguments
                    {
                        ["datasets"] = string.Join("\n", datasetList.Select(d => $"- {d}")),
                        ["query"] = question
                    });
                    
                    datasetName = datasetResult.GetValue<string>()?.Trim();
                    
                    if (string.IsNullOrEmpty(datasetName) || datasetName.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        datasetName = string.Empty;
                    }
                    else
                    {
                        Console.WriteLine($"Identified dataset: {datasetName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during dataset identification: {ex.Message}");
                datasetName = string.Empty; // Ensure datasetName is empty on error
            }

            // --- Fetch Schema Info (if dataset identified) ---
            TabularDataSchema? schema = null;
            string formattedSchemaInfo = "No schema information available for filter generation."; // Default
            if (!string.IsNullOrEmpty(datasetName))
            {
                Console.WriteLine($"--- Fetching Schema for: {datasetName} ---");
                try
                {
                    // Create a TabularFilterHelper with the memory instance provided in constructor if available
                    TabularFilterHelper schemaHelper;
                    if (_tabularMemoryDb != null)
                    {
                        schemaHelper = new TabularFilterHelper(_tabularMemoryDb);
                    }
                    else
                    {
                        // Fall back to the original approach with reflection
                        schemaHelper = new TabularFilterHelper(_memory, _indexName);
                    }
                    schema = await schemaHelper.GetSchemaAsync(datasetName);
                    if (schema != null)
                    {
                        formattedSchemaInfo = FormatSchemaForPrompt(schema);
                        Console.WriteLine($"Schema Info Found:\n{formattedSchemaInfo}");
                    }
                    else
                    {
                         Console.WriteLine($"Schema object not found for dataset: {datasetName}");
                         formattedSchemaInfo = $"Schema object not found for dataset '{datasetName}'. Cannot provide specific field info.";
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error fetching schema for dataset {datasetName}: {ex.Message}");
                    formattedSchemaInfo = $"Error fetching schema for dataset '{datasetName}'. Cannot provide specific field info.";
                    // Continue without schema info, LLM will have less context
                }
            }
            else
            {
                 Console.WriteLine("No dataset identified, skipping schema fetch.");
            }

            // --- Filter Generation Step ---
            Console.WriteLine("--- Generating Filters ---");

            // Updated prompt template to include schema info and operator-specific instructions
            string filterPromptTemplate;
            if (_fuzzyMatchOperator.Equals("LIKE", StringComparison.OrdinalIgnoreCase))
            {
                filterPromptTemplate = @"
        Analyze the user's question to identify specific criteria that can be used to filter tabular data or apply tags, based ONLY on the provided schema fields.

        IMPORTANT: For string fields, generate partial (fuzzy) values that will be used with a SQL LIKE operator for matching. Use the '%' character as a wildcard at the start, end, or within values (e.g., '%corelight%', 'phoenix%', '%sensor'). You may generate multiple patterns if appropriate. Matching is case-insensitive.

        For AND logic, include multiple keys in the JSON object (all must match). For OR logic, use an array of values for a key (any value may match).

        Available Schema Fields (use these exact keys):
        {{$schemaInfo}}

        RULES FOR OUTPUT KEYS:
        1. Use the EXACT normalized keys provided in the schema info above (e.g., ""data.server_purpose"", ""project""). Do NOT invent new keys.
        2. If a criterion refers to a structured data column, use the corresponding ""data.*"" key from the schema.
        3. If a criterion refers to a general category or context, use the corresponding standard tag key from the schema (e.g., ""project"").
        4. Extract the specific value mentioned for the criterion. For string fields, use a substring or keyword (not the full value) for fuzzy search, and use '%' as a wildcard.
        5. For OR logic, use an array of patterns for a key: { ""data.server_purpose"": [""%corelight%"", ""%sensor%""] }
        6. Format the output STRICTLY as a JSON object containing the identified key-value pairs.
        7. If no specific filter criteria matching the schema are found, output an empty JSON object: {}

        FORMAT:
        DO NOT use backticks in the response. Output MUST be a pure, correctly formatted JSON structure.

        EXAMPLES:
        Question: Show me production servers.
        Output: {""data.environment"": ""%product%""}

        Question: List all servers for the 'Phoenix' or 'Austin' project.
        Output: {""project"": [""phoenix%"", ""austin%""]}

        Question: What is the server purpose for VAXVNAGG01?
        Output: {""data.server_name"": ""%vaxv%""}

        Question: Tell me about the system architecture.
        Output: {}

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
        {{$schemaInfo}}

        RULES FOR OUTPUT KEYS:
        1. Use the EXACT normalized keys provided in the schema info above (e.g., ""data.server_purpose"", ""project""). Do NOT invent new keys.
        2. If a criterion refers to a structured data column, use the corresponding ""data.*"" key from the schema.
        3. If a criterion refers to a general category or context, use the corresponding standard tag key from the schema (e.g., ""project"").
        4. Extract the specific value mentioned for the criterion. For string fields, use a substring or keyword (not the full value) for fuzzy search.
        5. For OR logic, use an array of patterns for a key: { ""data.server_purpose"": [""corelight"", ""sensor""] }
        6. Format the output STRICTLY as a JSON object containing the identified key-value pairs.
        7. If no specific filter criteria matching the schema are found, output an empty JSON object: {}

        FORMAT:
        DO NOT use backticks in the response. Output MUST be a pure, correctly formatted JSON structure.

        EXAMPLES:
        Question: Show me production servers.
        Output: {""data.environment"": ""product""}

        Question: List all servers for the 'Phoenix' or 'Austin' project.
        Output: {""project"": [""phoenix"", ""austin""]}

        Question: What is the server purpose for VAXVNAGG01?
        Output: {""data.server_name"": ""vaxv""}

        Question: Tell me about the system architecture.
        Output: {}

        Question: {{$input}}
        Output:
        ";
            }

            var generateFiltersFunction = _kernel.CreateFunctionFromPrompt(
                filterPromptTemplate, functionName: "GenerateNormalizedFilters",
                description: "Analyzes a user question and generates normalized structured filters as JSON.",
                executionSettings: new AzureOpenAIPromptExecutionSettings { Temperature = 0.0 }
            );
         
            MemoryFilter? generatedFilter = null;
            List<string> warnings = new List<string>();
            
            try
            {
                // Inject schema info into the arguments
                var filterArguments = new KernelArguments()
                {
                    { "input", question },
                    { "schemaInfo", formattedSchemaInfo }
                };

                var filterResult = await _kernel.InvokeAsync(generateFiltersFunction, filterArguments);
                var filterJson = filterResult.GetValue<string>()?.Trim();
                Console.WriteLine($"LLM Normalized Filter Suggestion: {filterJson}"); // Debug output

                if (!string.IsNullOrWhiteSpace(filterJson) && filterJson != "{}")
                {
                    // Deserialize as Dictionary<string, object> to handle both string and array values
                    var filterDict = JsonSerializer.Deserialize<Dictionary<string, object>>(filterJson);
                    if (filterDict != null && filterDict.Count > 0)
                    {
                        // Convert values to object or List<object> for validation
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
                                // Optionally handle other types (numbers, bools) if needed
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

                        // If we have a dataset name, validate parameters against schema
                        if (!string.IsNullOrEmpty(datasetName))
                        {
                            try
                            {
                                // Create a TabularFilterHelper with the memory instance provided in constructor if available
                                TabularFilterHelper filterHelper;
                                if (_tabularMemoryDb != null)
                                {
                                    filterHelper = new TabularFilterHelper(_tabularMemoryDb);
                                }
                                else
                                {
                                    // Fall back to the original approach with reflection
                                    filterHelper = new TabularFilterHelper(_memory, _indexName);
                                }
                                var result = await filterHelper.GenerateValidatedFilterAsync(
                                    datasetName, paramDict);

                                generatedFilter = result.Filter;
                                warnings.AddRange(result.Warnings);

                                if (result.Warnings.Count > 0)
                                {
                                    Console.WriteLine("Validation warnings:");
                                    foreach (var warning in result.Warnings)
                                    {
                                        Console.WriteLine($"  - {warning}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error during parameter validation: {ex.Message}");

                                // Fall back to unvalidated filter
                                generatedFilter = new MemoryFilter();
                                foreach (var kvp in paramDict)
                                {
                                    if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
                                    {
                                        if (kvp.Value is string s)
                                        {
                                            generatedFilter.Add(kvp.Key, s);
                                        }
                                        else if (kvp.Value is List<string> slist)
                                        {
                                            generatedFilter.Add(kvp.Key, slist);
                                        }
                                        else if (kvp.Value is IEnumerable<object> objList)
                                        {
                                            var strList = objList.Select(x => x?.ToString() ?? "").ToList();
                                            generatedFilter.Add(kvp.Key, strList);
                                        }
                                        else
                                        {
                                            generatedFilter.Add(kvp.Key, kvp.Value.ToString() ?? "");
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            // No dataset identified, use unvalidated filter
                            generatedFilter = new MemoryFilter();
                            foreach (var kvp in paramDict)
                            {
                                if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
                                {
                                    if (kvp.Value is string s)
                                    {
                                        generatedFilter.Add(kvp.Key, s);
                                    }
                                    else if (kvp.Value is List<string> slist)
                                    {
                                        generatedFilter.Add(kvp.Key, slist);
                                    }
                                    else if (kvp.Value is IEnumerable<object> objList)
                                    {
                                        var strList = objList.Select(x => x?.ToString() ?? "").ToList();
                                        generatedFilter.Add(kvp.Key, strList);
                                    }
                                    else
                                    {
                                        generatedFilter.Add(kvp.Key, kvp.Value.ToString() ?? "");
                                    }
                                }
                            }
                        }

                        Console.WriteLine($"Applied Filter: {JsonSerializer.Serialize(generatedFilter)}"); // Debug output
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during filter generation or parsing: {ex.Message}");
                generatedFilter = null;
            }
         
            // --- Kernel Memory Ask Step ---    
            Console.WriteLine("\n--- Asking Kernel Memory ---");
            string sources = "";

            // Set a high limit (or no limit) for retrieving results from the database
            int dbQueryLimit = resultLimit.HasValue && resultLimit.Value > 0 ? Math.Max(resultLimit.Value, 100) : 100;
            Console.WriteLine($"Database query limit configured to: {dbQueryLimit}"); 
            
            // Use SearchAsync instead of AskAsync to get more control over the results
            // We'll still use AskAsync for the synthesis but get the raw data first
            var searchResults = await _memory.SearchAsync(
                question, 
                index: _indexName, 
                filter: generatedFilter, 
                limit: dbQueryLimit // SearchAsync does support the limit parameter
            );
            
            Console.WriteLine($"Search returned {searchResults.Results.Count} results");
            
            // --- Format all raw search results for prompt injection ---
            string allRawResults = "";
            foreach (var doc in searchResults.Results)
            {
                string sourceName = doc.SourceName;
                string docId = doc.DocumentId ?? "N/A";
                string lastUpdate = doc.Partitions.FirstOrDefault()?.LastUpdate.ToString("d") ?? "N/A";
                string link = doc.Link ?? "";
                // Collect all partition texts for this record
                var partitionTexts = doc.Partitions != null
                    ? string.Join("\n      ", doc.Partitions.Select(p => p.Text))
                    : "";
                allRawResults += $"  - {sourceName} (ID: {docId}) Link: {link} [LastUpdate: {lastUpdate}]\n    Partition Texts:\n      {partitionTexts}\n";
            }
            
            Console.WriteLine("--- First query was for raw search data. Now executing AskAsync for answer synthesis. ---");
            
            // Investigation: The two queries with different limits come from:
            // 1. Our explicit SearchAsync above (limit: dbQueryLimit = 1000)
            // 2. The internal SearchAsync called by AskAsync below (which defaults to 5)
            
            // The mystery is now solved! We can't avoid this because:
            // 1. SearchAsync is called by us with limit=1000
            // 2. AskAsync internally calls SearchAsync again with its default limit=5
            
            // Use the standard AskAsync method with just the filter
            var answer = await _memory.AskAsync(
                question, 
                index: _indexName, 
                filter: generatedFilter
            );
            
            // Note: Even though AskAsync's internal search uses limit=5,
            // the searchResults variable above contains all 1000 results,
            // so we still have access to all the data for debugging
            
            // Apply result limit if specified (client-side filtering)
            var relevantSources = answer.RelevantSources;
            if (resultLimit.HasValue && resultLimit.Value > 0 && relevantSources.Count > resultLimit.Value)
            {
                Console.WriteLine($"Limiting displayed results to {resultLimit.Value} (from {relevantSources.Count} total)");
                relevantSources = relevantSources.Take(resultLimit.Value).ToList();
            }
            else
            {
                Console.WriteLine($"Total results retrieved: {relevantSources.Count}");
            }
         
            foreach (var x in relevantSources)
            {
                // Attempt to get the original file name if available
                string sourceName = x.SourceName;
                List<string?> fileTagValue = null;
                x.Partitions.FirstOrDefault()?.Tags.TryGetValue("file", out fileTagValue);
         
                foreach (var partition in x.Partitions)
                {
                    if (partition.Tags != null &&
                        partition.Tags.TryGetValue("file", out var fileTagValues) &&
                        fileTagValues is not null &&
                        fileTagValues.Count > 0)
                    {
                        sourceName = fileTagValues.First() ?? sourceName;
                        break;
                    }
                }
         
                sources += $"  - {sourceName} (Partition: {x.DocumentId ?? "N/A"}) Link: {x.Link} [{x.Partitions.First().LastUpdate:D}]" + Environment.NewLine;
            }
         
            // --- Final Answer Synthesis Step ---
            Console.WriteLine("\n--- Synthesizing Final Answer ---");
            var skPrompt = $@"
        Question to Kernel Memory: {question}

        Kernel Memory Documents:
            Start Documents -----------
            {allRawResults}
            End Documents -----------

        Kernel Memory Answer: {answer.Result}
         
        Kernel Memory Sources:
        {sources}
        ---
         
        Only Respond with information from Kernel Memory including RelevantSources.
         
        If you don't know, then respond with 'This information is not part of our internal documents.'
         
        If you do know then Start with 'According to our internal documents'. Reply with the best answer.
         
        Make sure to include the document name and reference id if available from the sources provided.
        ";
         
            Microsoft.SemanticKernel.Connectors.AzureOpenAI.AzureOpenAIPromptExecutionSettings settings = new()
            {
                // ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions // Enable if SK_Prompt uses tools
            };
         
            KernelArguments arguments = new KernelArguments(settings)
                        {
                            { "input", question }, // Pass original question again if needed by the prompt
                        };
         
            var response = await _kernel.InvokePromptAsync(skPrompt, arguments);
         
            Console.WriteLine("\n--- Final Response ---");
            Console.WriteLine(response.GetValue<string>());
            Console.WriteLine("\nPress Enter to exit.");
            Console.ReadLine();
        }

        // Helper method to format schema information for the LLM prompt
        private string FormatSchemaForPrompt(TabularDataSchema schema)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Structured Data Fields (Prefix keys with 'data.'):");
            if (schema.Columns != null && schema.Columns.Any())
            {
                foreach (var col in schema.Columns.OrderBy(c => c.NormalizedName))
                {
                    // Use the *normalized* name for the key the LLM should generate
                    string key = $"data.{col.NormalizedName}";
                    sb.Append($"- {key}");
                    if (col.CommonValues != null && col.CommonValues.Any())
                    {
                        // Limit examples shown for brevity
                        var examples = col.CommonValues.Take(5).Select(v => $"\"{v}\"");
                        sb.Append($" (e.g., {string.Join(", ", examples)})");
                    }
                    sb.AppendLine();
                }
            }
            else
            {
                sb.AppendLine("  (No specific column data available)");
            }

            // Add common tag fields (these could potentially be dynamic in a future version)
            sb.AppendLine("\nCommon Tag Fields (Use keys directly):");
            sb.AppendLine("- project");
            sb.AppendLine("- application");
            sb.AppendLine("- environment");
            sb.AppendLine("- status");
            // Add any other known standard tags if applicable

            return sb.ToString();
        }
    }
}
