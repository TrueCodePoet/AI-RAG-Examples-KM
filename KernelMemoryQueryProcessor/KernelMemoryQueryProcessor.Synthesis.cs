using Microsoft.KernelMemory;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;

namespace AI_RAG_Examples_KM
{
    public partial class KernelMemoryQueryProcessor
    {
        // Synthesize final answer from search results
        private async Task<string> SynthesizeTabularAnswerAsync(string question, List<Citation> relevantSources, int? resultLimit)
        {
            Console.WriteLine("\n--- Synthesizing Final Answer ---");
            
            // Format all raw search results for prompt injection
            string allRawResults = "";
            int resultCount = 0;
            foreach (var doc in relevantSources)
            {
                resultCount++;
                string sourceName = doc.SourceName;
                string docId = doc.DocumentId ?? "N/A";
                string lastUpdate = doc.Partitions?.FirstOrDefault()?.LastUpdate.ToString("d") ?? "N/A";
                string link = doc.Link ?? "";
                var partitionTexts = doc.Partitions != null
                    ? string.Join("\n      ", doc.Partitions.Select(p => p.Text))
                    : "";
                allRawResults += $"  - {sourceName} (ID: {docId}) Link: {link} [LastUpdate: {lastUpdate}]\n    Partition Texts:\n   ROW: {resultCount} : {partitionTexts}\n";
            }

            // Apply result limit if specified
            if (resultLimit.HasValue && resultLimit.Value > 0 && relevantSources.Count > resultLimit.Value)
            {
                Console.WriteLine($"Limiting displayed results to {resultLimit.Value} (from {relevantSources.Count} total)");
                relevantSources = relevantSources.Take(resultLimit.Value).ToList();
            }
            else
            {
                Console.WriteLine($"Total results retrieved: {relevantSources.Count}");
            }

            // Format sources for the prompt
            string sources = "";
            foreach (var x in relevantSources)
            {
                string sourceName = x.SourceName;
                List<string?> fileTagValue = null;
                x.Partitions?.FirstOrDefault()?.Tags?.TryGetValue("file", out fileTagValue);

                if (x.Partitions != null)
                {
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
                }

                sources += $"  - {sourceName} (Partition: {x.DocumentId ?? "N/A"}) Link: {x.Link} [{x.Partitions?.FirstOrDefault()?.LastUpdate:D}]" + Environment.NewLine;
            }

            // Create the prompt for answer synthesis
            var skPrompt = $@"
        Question to Kernel Memory: {question}

        Kernel Memory Documents:
            Start Documents -----------
            {allRawResults}
            End Documents -----------

        Kernel Memory Answer: [Results synthesized from top {relevantSources.Count} records]
         
        Kernel Memory Sources:
        {sources}
        ---
         
        Only Respond with information from Kernel Memory including RelevantSources.

        DO NOT FABRICATE RESULTS.
        You are given row-level results. Your task is to return only the rows that fully match the query conditions.
        For tabular data queries, it is essential to evaluate the entire row and include only those where all conditions are satisfied.
        You are provided the complete row—return data only if the entire row meets the specified criteria.
        If the row does not meet the criteria, do not include it in your response.
        If the row is not relevant, do not include it in your response.


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
            string answer = response.GetValue<string>();
            
            Console.WriteLine("\n--- Final Response ---");
            Console.WriteLine(answer);
            
            return answer;
        }
    }
}
