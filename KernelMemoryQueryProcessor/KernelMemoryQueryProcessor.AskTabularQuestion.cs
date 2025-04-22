using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AI_RAG_Examples_KM
{
    public partial class KernelMemoryQueryProcessor
    {
        public async Task AskTabularQuestionAsync(string question, int? resultLimit = null)
        {
            // --- Fetch ALL Schema Info ---
            var (schemas, formattedSchemaInfo) = await GetAllSchemasInfoAsync();

            // --- Dataset Identification Step ---
            string datasetName = await IdentifyDatasetAsync(question);

            // --- Fetch Schema Info (if dataset identified) ---
            TabularDataSchema? schema = null;
            formattedSchemaInfo = "No schema information available for filter generation."; // Default
            (schema, formattedSchemaInfo) = await GetSchemaInfoAsync(datasetName);
            Console.WriteLine($"[DEBUG] Injected schema info for prompt:\n{formattedSchemaInfo}");

            // --- Filter Generation Step ---
            var allFilters = await GenerateFiltersAsync(question, formattedSchemaInfo, datasetName);
            
            // --- Kernel Memory Ask Step (Search) ---
            var relevantSources = await ExecuteTabularSearchAsync(question, allFilters, resultLimit ?? 100);

            // --- Final Answer Synthesis Step ---
            string answer = await SynthesizeTabularAnswerAsync(question, relevantSources, resultLimit);
            
            Console.WriteLine("\nPress Enter to exit.");
            Console.ReadLine();
        }
    }
}
