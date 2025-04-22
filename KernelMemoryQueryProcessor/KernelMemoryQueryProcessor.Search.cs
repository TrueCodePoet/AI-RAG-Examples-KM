using Microsoft.KernelMemory;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AI_RAG_Examples_KM
{
    public partial class KernelMemoryQueryProcessor
    {
        // Execute search across filters
        private async Task<List<Citation>> ExecuteTabularSearchAsync(string question, List<MemoryFilter> filters, int limit)
        {
            Console.WriteLine("\n--- Asking Kernel Memory ---");
            
            // Set a high limit (or no limit) for retrieving results from the database
            int dbQueryLimit = limit > 0 ? limit : 100;
            Console.WriteLine($"Database query limit configured to: {dbQueryLimit}"); 
            
            // Use SearchAsync for each filter template and aggregate results
            var allResults = new List<Citation>();
            foreach (var filter in filters)
            {
                var searchResults = await _memory.SearchAsync(
                    question,
                    index: _indexName,
                    filter: filter,
                    limit: dbQueryLimit
                );
                if (searchResults?.Results != null)
                    allResults.AddRange(searchResults.Results);
            }
            
            // Deduplicate results by DocumentId (or use another unique property if needed)
            var relevantSources = allResults
                .GroupBy(r => r.DocumentId)
                .Select(g => g.First())
                .ToList();

            Console.WriteLine($"Aggregated search returned {relevantSources.Count} unique results from {filters.Count} filter templates");
            
            return relevantSources;
        }
    }
}
