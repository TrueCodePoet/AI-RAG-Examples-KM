using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;
using System.Text;

namespace AI_RAG_Examples_KM
{
    public partial class KernelMemoryQueryProcessor
    {
        // New helper method to fetch all schemas and formatted schema info
        private async Task<(List<TabularDataSchema>, string)> GetAllSchemasInfoAsync()
        {
            List<TabularDataSchema> schemas = new List<TabularDataSchema>();
            string formattedSchemaInfo = "No schema information available for filter generation.";
            try
            {
                TabularFilterHelper schemaHelper;
                if (_tabularMemoryDb != null)
                {
                    Console.WriteLine("INFO: Using DI-injected tabularMemoryDb for TabularFilterHelper (reflection skipped). Using all schemas.");
                    schemaHelper = new TabularFilterHelper(_tabularMemoryDb);
                }
                else
                {
                    schemaHelper = new TabularFilterHelper(_memory, _indexName);
                }
                schemas = await schemaHelper.ListSchemasAsync();
                if (schemas != null && schemas.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var schema in schemas)
                    {
                        sb.AppendLine($"Dataset: {schema.DatasetName}");
                        sb.AppendLine(FormatSchemaForPrompt(schema));
                        sb.AppendLine();
                    }
                    formattedSchemaInfo = sb.ToString();
                    Console.WriteLine($"[DEBUG] All Schemas Info for prompt:\n{formattedSchemaInfo}");
                }
                else
                {
                    Console.WriteLine("No schema objects found.");
                    formattedSchemaInfo = "No schema objects found. Cannot provide specific field info.";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching all schemas: {ex.Message}");
                formattedSchemaInfo = $"Error fetching all schemas. Cannot provide specific field info.";
            }
            return (schemas, formattedSchemaInfo);
        }

        // New helper method to fetch schema and formatted schema info
        private async Task<(TabularDataSchema?, string)> GetSchemaInfoAsync(string datasetName)
        {
            TabularDataSchema? schema = null;
            string formattedSchemaInfo = "No schema information available for filter generation."; // Default
            if (!string.IsNullOrEmpty(datasetName))
            {
                Console.WriteLine($"--- Fetching Schema for: {datasetName} ---");
                try
                {
                    TabularFilterHelper schemaHelper;
                    if (_tabularMemoryDb != null)
                    {
                        Console.WriteLine("INFO: Using DI-injected tabularMemoryDb for TabularFilterHelper (reflection skipped).");
                        schemaHelper = new TabularFilterHelper(_tabularMemoryDb);
                    }
                    else
                    {
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
                }
            }
            else
            {
                Console.WriteLine("No dataset identified, skipping schema fetch.");
            }
            return (schema, formattedSchemaInfo);
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
