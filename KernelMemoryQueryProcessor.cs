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

        public KernelMemoryQueryProcessor(
            IKernelMemory memory,
            Kernel kernel,
            string indexName,
            AzureOpenAIConfig azureOpenAITextConfig)
        {
            _memory = memory;
            _kernel = kernel;
            _indexName = indexName;
            _azureOpenAITextConfig = azureOpenAITextConfig;
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

        public async Task AskTabularQuestionAsync(string question)
        {
            // --- Dataset Identification Step ---
            Console.WriteLine("--- Identifying Dataset ---");
            
            string datasetName = string.Empty;
            
            try
            {
                // Create a TabularFilterHelper to access schema functionality
                var filterHelper = new TabularFilterHelper(_memory);
                
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
                    // Re-use or create filter helper instance
                    var schemaHelper = new TabularFilterHelper(_memory);
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

            // Updated prompt template to include schema info
            string filterPromptTemplate = @"
        Analyze the user's question to identify specific criteria that can be used to filter tabular data or apply tags, based ONLY on the provided schema fields.

        Available Schema Fields (use these exact keys):
        {{$schemaInfo}}
        {{!-- End of Schema Info --}}

        RULES FOR OUTPUT KEYS:
        1. Use the EXACT normalized keys provided in the schema info above (e.g., ""data.server_purpose"", ""project""). Do NOT invent new keys.
        2. If a criterion refers to a structured data column, use the corresponding ""data.*"" key from the schema.
        3. If a criterion refers to a general category or context, use the corresponding standard tag key from the schema (e.g., ""project"").
        4. Extract the specific value mentioned for the criterion.
        5. Format the output STRICTLY as a JSON object containing the identified key-value pairs.
        6. If no specific filter criteria matching the schema are found, output an empty JSON object: {}

        FORMAT:
        DO NOT use backticks in the response. Output MUST be a pure, correctly formatted JSON structure.

        EXAMPLES:
        {{!-- Examples are less critical now schema is provided, but can still help --}}
        Question: Show me production servers. {{!-- Assuming 'environment' is a tag or 'data.environment' is in schema --}}
        Output: {""data.environment"": ""Production""}

        Question: List all servers for the 'Phoenix' project. {{!-- Assuming 'project' is a tag --}}
        Output: {""project"": ""Phoenix""}

        Question: What is the server purpose for VAXVNAGG01? {{!-- Assuming 'data.server_name' is in schema --}}
        Output: {""data.server_name"": ""VAXVNAGG01""}

        Question: Tell me about the system architecture.
        Output: {}

        Question: {{ $input }}
        Output:
        ";

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
                    // Deserialize directly, assuming keys are now correctly normalized by the LLM
                    var filterDict = JsonSerializer.Deserialize<Dictionary<string, string>>(filterJson);
                    if (filterDict != null && filterDict.Count > 0)
                    {
                        // Convert string values to object values for validation
                        var paramDict = filterDict.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value);
                        
                        // If we have a dataset name, validate parameters against schema
                        if (!string.IsNullOrEmpty(datasetName))
                        {
                            try
                            {
                                var filterHelper = new TabularFilterHelper(_memory);
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
                                foreach (var kvp in filterDict)
                                {
                                    if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                                    {
                                        generatedFilter.Add(kvp.Key, kvp.Value);
                                    }
                                }
                            }
                        }
                        else
                        {
                            // No dataset identified, use unvalidated filter
                            generatedFilter = new MemoryFilter();
                            foreach (var kvp in filterDict)
                            {
                                if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                                {
                                    generatedFilter.Add(kvp.Key, kvp.Value);
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
            // Pass the generated filter (if any) to AskAsync
            // Note: AskAsync might not have a 'limit' param exposed directly for getting *all* results.
            //  For comprehensive retrieval based *only* on filters, SearchAsync might be better,
            //  followed by a separate LLM call to synthesize the answer from the search results.
           
            //var answer = await memory.AskAsync(question, index: IndexName, filter: generatedFilter);
            var answer = await _memory.AskAsync(question, index: _indexName, filter: generatedFilter);
         
            foreach (var x in answer.RelevantSources)
            {
                // Attempt to get the original file name if availablestring
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
         
        Kernel Memory Answer: {answer.Result}
         
        Kernel Memory Sources:
        {sources}
        ---
         
        ";
        /*
        Only Respond with information from Kernel Memory including RelevantSources.
         
        If you don't know, then respond with 'This information is not part of our internal documents.'
         
        If you do know then Start with 'According to our internal documents'. Reply with the best answer.
         
        Make sure to include the document name and reference id if available from the sources provided.
        */
            // Re-use the kernel instance, ensure the MemoryPlugin is imported if needed for tool calls in the final prompt// var plugin = new MemoryPlugin(memory, defaultIndex: IndexName, waitForIngestionToComplete: true);// kernel.ImportPluginFromObject(plugin, "SEOmemory"); // Only if SK_Prompt uses tools from MemoryPlugin
         
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
