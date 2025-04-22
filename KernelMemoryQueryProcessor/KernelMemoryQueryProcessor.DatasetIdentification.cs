using Microsoft.KernelMemory;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text;
using System.Text.Json;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

namespace AI_RAG_Examples_KM
{
    public partial class KernelMemoryQueryProcessor
    {
        // Identifies which dataset the question is most likely about
        private async Task<string> IdentifyDatasetAsync(string question)
        {
            Console.WriteLine("--- Identifying Dataset ---");
            return await GetDatasetNameAsync(question);
        }

        // New helper method to get dataset name
        private async Task<string> GetDatasetNameAsync(string question)
        {
            try
            {
                TabularFilterHelper filterHelper;
                if (_tabularMemoryDb != null)
                {
                    Console.WriteLine("INFO: Using DI-injected tabularMemoryDb for TabularFilterHelper (reflection skipped).");
                    filterHelper = new TabularFilterHelper(_tabularMemoryDb);
                }
                else
                {
                    filterHelper = new TabularFilterHelper(_memory, _indexName);
                }

                // Fetch all schemas, not just names
                var schemas = await filterHelper.ListSchemasAsync();

                if (schemas.Count > 0)
                {
                    // Build detailed schema context
                    var sb = new StringBuilder();
                    foreach (var schema in schemas)
                    {
                        sb.AppendLine($"Dataset: {schema.DatasetName}");
                        if (schema.Columns != null && schema.Columns.Count > 0)
                        {
                            sb.AppendLine("  Fields:");
                            foreach (var col in schema.Columns)
                            {
                                sb.Append($"    - {col.NormalizedName}");
                                if (col.CommonValues != null && col.CommonValues.Count > 0)
                                {
                                    var examples = string.Join(", ", col.CommonValues.Take(3).Select(v => $"\"{v}\""));
                                    sb.Append($" (e.g., {examples})");
                                }
                                sb.AppendLine();
                            }
                        }
                        sb.AppendLine();
                    }
                    string detailedSchemaContext = sb.ToString();

                    string datasetPromptTemplate =
@"You are analyzing a user query to determine which dataset (schema) it is most likely referring to.

Available datasets and their fields:
{{$detailedSchemas}}

User query: {{$query}}

Analyze the query and determine which dataset(s) it is most likely referring to. Consider both the dataset name and the available schema field names and example values.
Return a JSON array of up to 3 candidate dataset names, ordered from most to least likely. If no dataset seems relevant, return an empty array [].

FORMAT:
Output MUST be a pure, correctly formatted JSON array of strings.

EXAMPLES:
User query: Show me all servers with a Server Purpose of 'Corelight Network monitoring sensor'.
Available datasets: [""ServerInventory"", ""NetworkDevices"", ""ApplicationList""]
Output: [""ServerInventory""]

User query: List all network devices and their IP addresses.
Available datasets: [""ServerInventory"", ""NetworkDevices"", ""ApplicationList""]
Output: [""NetworkDevices""]

User query: What applications are running on server X?
Available datasets: [""ServerInventory"", ""NetworkDevices"", ""ApplicationList""]
Output: [""ApplicationList""]

User query: Tell me about the system architecture.
Available datasets: [""ServerInventory"", ""NetworkDevices"", ""ApplicationList""]
Output: []

User query: {{$query}}
Output:
";

                    var datasetFunction = _kernel.CreateFunctionFromPrompt(
                        datasetPromptTemplate,
                        new OpenAIPromptExecutionSettings { Temperature = 0.0 });

                    var datasetResult = await _kernel.InvokeAsync(datasetFunction, new KernelArguments
                    {
                        ["detailedSchemas"] = detailedSchemaContext,
                        ["query"] = question
                    });

                    var datasetArrayJson = datasetResult.GetValue<string>()?.Trim();
                    if (!string.IsNullOrWhiteSpace(datasetArrayJson))
                    {
                        try
                        {
                            // Try to parse as a JSON array and return the first element
                            var datasetArray = JsonSerializer.Deserialize<List<string>>(datasetArrayJson);
                            if (datasetArray != null && datasetArray.Count > 0)
                            {
                                var datasetName = datasetArray[0];
                                if (!string.IsNullOrEmpty(datasetName) && !datasetName.Equals("none", StringComparison.OrdinalIgnoreCase))
                                {
                                    Console.WriteLine($"Identified dataset: {datasetName}");
                                    return datasetName;
                                }
                            }
                        }
                        catch
                        {
                            // Fallback: treat as a single string (strip markdown if present)
                            var cleaned = datasetArrayJson
                                .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
                                .Replace("```", "", StringComparison.OrdinalIgnoreCase)
                                .Trim();
                            if (cleaned.StartsWith("["))
                            {
                                try
                                {
                                    var datasetArray = JsonSerializer.Deserialize<List<string>>(cleaned);
                                    if (datasetArray != null && datasetArray.Count > 0)
                                    {
                                        var datasetName = datasetArray[0];
                                        if (!string.IsNullOrEmpty(datasetName) && !datasetName.Equals("none", StringComparison.OrdinalIgnoreCase))
                                        {
                                            Console.WriteLine($"Identified dataset: {datasetName}");
                                            return datasetName;
                                        }
                                    }
                                }
                                catch { }
                            }
                            if (!string.IsNullOrEmpty(cleaned) && !cleaned.Equals("none", StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine($"Identified dataset: {cleaned}");
                                return cleaned;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during dataset identification: {ex.Message}");
            }
            return string.Empty;
        }
    }
}
