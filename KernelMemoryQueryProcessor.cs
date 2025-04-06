using Microsoft.KernelMemory;
using Microsoft.SemanticKernel;
using Microsoft.KernelMemory.MemoryStorage; // For TagCollection
using Microsoft.SemanticKernel.Connectors.AzureOpenAI; // For AzureOpenAIPromptExecutionSettings
using Microsoft.KernelMemory.AI.AzureOpenAI; // For AzureOpenAIConfig
using System.Text.Json; // For JsonSerializer
using Microsoft.SemanticKernel.Connectors.OpenAI; // For MemoryFilter

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
            // --- Filter Generation Step ---
            Console.WriteLine("--- Generating Filters ---");
         
            string filterPromptTemplate = @"
        Analyze the user's question to identify specific criteria that can be used to filter tabular data or apply tags.
         
        RULES FOR OUTPUT KEYS:
        1. If a criterion refers to a structured data column (like environment, status, server name, server purpose), prefix the key with ""data."".
        2. Normalize the structured data key:
            - Convert the natural name (e.g., ""Server Purpose"", ""Operating System"") to lowercase.
            - Replace spaces and other non-alphanumeric characters (except underscore) with underscores.
            - Example: ""Server Purpose"" becomes ""data.server_purpose"".
            - Example: ""Operating System"" becomes ""data.operating_system"".
        3. If a criterion refers to a general category or context (like application, project), use it directly as a standard tag key (lowercase). Example: ""application"".
        4. Extract the specific value mentioned for the criterion.
        5. Format the output STRICTLY as a JSON object containing the identified key-value pairs.
        6. If no specific filter criteria are found, output an empty JSON object: {}
         
        FORMAT:
        DO NOT use backticks in the response. Output MUST be a pure, correctly formatted JSON structure.
         
        EXAMPLES:
        Question: Show me production servers.
        Output: {""data.environment"": ""Production""}
         
        Question: List all servers for the 'Phoenix' project.
        Output: {""project"": ""Phoenix""}
         
        Question: Give me all production servers from TAMI application
        Output: {""data.environment"": ""Production"", ""application"": ""TAMI""}
         
        Question: What servers are running Windows Server 2019 in the East US location?
        Output: {""data.operating_system"": ""Windows Server 2019"", ""data.location"": ""East US""}
         
        Question: What is the server purpose for VAXVNAGG01?
        Output: {""data.server_name"": ""VAXVNAGG01""}
         
        Question: Tell me about the system architecture.
        Output: {}
         
        Question: {{ $input }}
        Output:
        ";
         
            // --- Filter Generation Step ---
            Console.WriteLine("--- Generating Filters ---");
         
            var generateFiltersFunction = _kernel.CreateFunctionFromPrompt(
                filterPromptTemplate, functionName: "GenerateNormalizedFilters",
                description: "Analyzes a user question and generates normalized structured filters as JSON.",
                executionSettings: new AzureOpenAIPromptExecutionSettings { Temperature = 0.0 }
            );
         
            MemoryFilter? generatedFilter = null;
            try
            {
                var filterResult = await _kernel.InvokeAsync(generateFiltersFunction, new() { { "input", question } });
                var filterJson = filterResult.GetValue<string>()?.Trim();
                Console.WriteLine($"LLM Normalized Filter Suggestion: {filterJson}"); // Debug output
         
                if (!string.IsNullOrWhiteSpace(filterJson) && filterJson != "{}")
                {
                    // Deserialize directly, assuming keys are now correctly normalized by the LLM
                    var filterDict = JsonSerializer.Deserialize<Dictionary<string, string>>(filterJson);
                    if (filterDict != null && filterDict.Count > 0)
                    {
                        generatedFilter = new MemoryFilter();
                        foreach (var kvp in filterDict)
                        {
                            if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                            {
                                // Add directly - no C# normalization needed
                                generatedFilter.Add(kvp.Key, kvp.Value);
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
    }
}
