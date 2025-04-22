using Microsoft.KernelMemory;
using Microsoft.SemanticKernel;
using Microsoft.KernelMemory.AI.AzureOpenAI;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;

namespace AI_RAG_Examples_KM
{
    public partial class KernelMemoryQueryProcessor
    {
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
                
                // Create settings without referencing ToolCallBehavior directly
                var settings = new AzureOpenAIPromptExecutionSettings();
                
                KernelArguments arguments = new KernelArguments(settings)
                {
                    { "input", question },
                };
         
                var response = await localKernel.InvokePromptAsync(SK_Prompt, arguments);
         
                Console.WriteLine(response.GetValue<string>());
                Console.ReadLine();
            }
        }
    }
}
