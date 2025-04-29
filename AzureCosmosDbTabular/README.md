# Azure Cosmos DB Tabular Data Connector for Kernel Memory

This extension provides integration between [Microsoft Kernel Memory](https://github.com/microsoft/kernel-memory) and [Azure Cosmos DB](https://azure.microsoft.com/en-us/products/cosmos-db/) for storing and querying tabular data (Excel, CSV, JSON) with structured query capabilities.

It includes a specialized Excel decoder (`TabularExcelDecoder`) that preserves the tabular structure of Excel files when importing them into Kernel Memory.

## Features

- Store tabular data (rows from Excel, CSV, JSON) as individual records in Azure Cosmos DB.
- Perform structured queries against tabular data fields using special filter syntax (`data.FieldName`).
- Retrieve records based on vector similarity using Azure Cosmos DB's native vector search capabilities (`VectorDistance` function with Cosine distance). This allows for semantic search over the tabular data content.
- Fully implements the `IMemoryDb` interface.
- Includes an optional specialized Excel decoder (`TabularExcelDecoder`) that:
  - Preserves column-row relationships
  - Maintains data types (numbers, dates, booleans)
  - Creates one document per row for precise querying
  - Uses column headers as field names
  - Includes metadata about the source (worksheet name, row number)

## Requirements

- Azure Cosmos DB account with NoSQL API
- .NET 8.0 or later

## Configuration

To use this extension, you need to provide:

1. Azure Cosmos DB endpoint URL
2. Azure Cosmos DB API key
3. (Optional) Database name (defaults to "memory")

## Usage

### Basic Setup

```csharp
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

// Create a memory builder with Azure Cosmos DB Tabular
var memory = new KernelMemoryBuilder()
    .WithAzureOpenAITextEmbeddingGeneration(
        endpoint: "https://your-openai-endpoint.com",
        apiKey: "your-openai-api-key",
        deploymentName: "your-embedding-deployment-name")
    .WithAzureCosmosDbTabularMemory(
        endpoint: "https://your-cosmosdb-account.documents.azure.com:443/",
        apiKey: "your-cosmosdb-api-key")
    .Build();
```

### Advanced Configuration

```csharp
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

// Create a custom configuration
var cosmosConfig = new AzureCosmosDbTabularConfig
{
    Endpoint = "https://your-cosmosdb-account.documents.azure.com:443/",
    APIKey = "your-cosmosdb-api-key",
    DatabaseName = "your-database-name"
};

// Create a memory builder with the custom configuration
var memory = new KernelMemoryBuilder()
    .WithAzureOpenAITextEmbeddingGeneration(
        endpoint: "https://your-openai-endpoint.com",
        apiKey: "your-openai-api-key",
        deploymentName: "your-embedding-deployment-name")
    .WithAzureCosmosDbTabularMemory(cosmosConfig)
    .Build();
```

## Excel File Processing

This extension includes a specialized Excel decoder (`TabularExcelDecoder`) that preserves the tabular structure of Excel files when importing them into Kernel Memory. Unlike the standard Excel decoder that flattens data to text, this decoder:

- Preserves column-row relationships
- Maintains data types (numbers, dates, booleans)
- Creates one document per row for precise querying
- Uses column headers as field names
- Includes metadata about the source (worksheet name, row number)

### Configuring the Excel Decoder

When setting up your Kernel Memory instance, you can configure the TabularExcelDecoder:

```csharp
// Create a memory builder with Azure Cosmos DB Tabular and Excel decoder
var memory = new KernelMemoryBuilder()
    .WithAzureOpenAITextEmbeddingGeneration(
        endpoint: "https://your-openai-endpoint.com",
        apiKey: "your-openai-api-key",
        deploymentName: "your-embedding-deployment-name")
    .WithAzureCosmosDbTabularMemory(
        endpoint: "https://your-cosmosdb-account.documents.azure.com:443/",
        apiKey: "your-cosmosdb-api-key")
    .WithTabularExcelDecoder(config => {
        // Configure Excel parsing options
        config.UseFirstRowAsHeader = true;
        config.PreserveDataTypes = true;
        config.ProcessAllWorksheets = true;
        // Optionally specify which worksheets to process
        // config.WorksheetsToProcess = new List<string> { "Sheet1", "Data" };
    })
    .Build();
```

### Importing Excel Files

Import Excel files the same way you would import any document:

```csharp
// Import an Excel file
await memory.ImportDocumentAsync("servers.xlsx", documentId: "servers-inventory");
```

Each row in the Excel file will be stored as a separate document in Cosmos DB, with:
- Column headers as field names
- Cell values preserved with their original data types
- Metadata about the source (worksheet name, row number)

### Advanced Excel Processing

For complex Excel files with multiple worksheets or special formatting:

```csharp
// Configure the TabularExcelDecoder with advanced options
var memory = new KernelMemoryBuilder()
    .WithAzureOpenAITextEmbeddingGeneration(/* config */)
    .WithAzureCosmosDbTabularMemory(/* config */)
    .WithTabularExcelDecoder(config => {
        // Use a specific row as header (0-based index)
        config.UseFirstRowAsHeader = true;
        config.HeaderRowIndex = 2; // Use the 3rd row as header
        
        // Process only specific worksheets
        config.ProcessAllWorksheets = false;
        config.WorksheetsToProcess = new List<string> { "Servers", "Network" };
        
        // Data type handling
        config.PreserveDataTypes = true;
        config.DateFormat = "yyyy-MM-dd";
        config.TimeFormat = "HH:mm:ss";
        
        // Column naming
        config.NormalizeHeaderNames = true; // Convert spaces to underscores, etc.
        config.DefaultColumnPrefix = "Field"; // For columns without headers
        
        // Row/column filtering
        config.SkipEmptyRows = true;
        config.SkipHiddenRows = true;
        config.SkipHiddenColumns = true;
    })
    .Build();
```

## Storing Tabular Data

When storing tabular data manually (without using the Excel decoder), you need to include the data as key-value pairs in the memory record's payload:

```csharp
// Example: Storing a row from an Excel spreadsheet
var data = new Dictionary<string, object>
{
    { "ServerName", "SVR01" },
    { "Environment", "Production" },
    { "Location", "East US" },
    { "Status", "Running" }
};

var sourceInfo = new Dictionary<string, string>
{
    { "SheetName", "Servers" },
    { "RowNumber", "5" }
};

// Create a memory record with the tabular data
var record = new MemoryRecord
{
    Id = Guid.NewGuid().ToString(),
    Payload = new Dictionary<string, object>
    {
    { "tabular_data", JsonSerializer.Serialize(data) },
    // The `source_info` key maps to the `source` field in the stored document
    { "source_info", JsonSerializer.Serialize(sourceInfo) } 
}
};

// Add tags if needed
record.Tags.Add("type", "server");
record.Tags.Add("department", "IT");

// Store the record
await memory.SaveAsync(record);
```

## Querying Tabular Data

This extension supports querying data in multiple ways:

1.  **Vector Similarity Search**: Perform semantic searches using vector embeddings via the `GetSimilarListAsync` method. This leverages Azure Cosmos DB's native vector search.
2.  **Structured Field Queries**: Filter records based on specific field values within the tabular data using the special `data.` prefix in filter tags (e.g., `filter.Add("data.Environment", "Production")`).
3.  **Hybrid Search**: Combine vector similarity search with structured field queries and standard tag filters within the same `GetSimilarListAsync` call for powerful, targeted retrieval.

### Structured Field Queries

To query tabular data fields, use the special `data.` prefix in your filter tags:

```csharp
// Query for all servers in the Production environment
var filter = new MemoryFilter();
filter.Add("data.Environment", "Production");

// Execute the query
var results = await memory.SearchAsync(filter: filter);
```

You can combine multiple field conditions:

```csharp
// Query for Production servers in East US
var filter = new MemoryFilter();
filter.Add("data.Environment", "Production");
filter.Add("data.Location", "East US");

// Execute the query
var results = await memory.SearchAsync(filter: filter);
```

You can also combine standard tags with data field filters:

```csharp
// Query for Production servers in the IT department
var filter = new MemoryFilter();
filter.Add("data.Environment", "Production");
filter.Add("department", "IT");

// Execute the query
var results = await memory.SearchAsync(filter: filter);
```

## Advanced Filtering and Fuzzy Matching

### Fuzzy Match Configuration

The tabular extension supports fuzzy matching for structured data fields using either `LIKE` or `CONTAINS` operators, as well as case-insensitive matching. These are fully configurable via `appsettings.json`:

```json
{
  "KernelMemory": {
    "Services": {
      "AzureCosmosDbTabular": {
        "Endpoint": "...",
        "APIKey": "...",
        "FuzzyMatch": {
          "Enabled": true,
          "Operator": "LIKE", // or "CONTAINS"
          "CaseInsensitive": true,
          "MinimumLength": 3
        }
      }
    }
  }
}
```

- **Operator:** `"LIKE"` uses SQL LIKE patterns (with `%` and `_` wildcards). `"CONTAINS"` uses the Cosmos DB CONTAINS function for substring matching.
- **CaseInsensitive:** If true, all comparisons are done in lower case.
- **MinimumLength:** Minimum string length for fuzzy matching to apply.

### Filter Logic

- **AND/OR Logic:** Multiple keys in a filter are combined as AND conditions. Arrays for a key (e.g., `"project": ["phoenix", "austin"]`) are combined as OR conditions.
- **Explicit LIKE Patterns:** If a filter value contains `%` or `_`, it is treated as a LIKE pattern regardless of the fuzzy match setting.
- **Tag Filters:** Tag filters use EXISTS subqueries for case-insensitive matching.
- **Hybrid Filters:** You can combine tag filters and data field filters in the same query.

### Examples

#### Exact Match

```csharp
var filter = new MemoryFilter();
filter.Add("data.Environment", "Production"); // Exact match
```

#### Fuzzy Match (LIKE)

```csharp
// With FuzzyMatch.Operator = "LIKE"
var filter = new MemoryFilter();
filter.Add("data.ServerName", "SVR"); // Will match any server name containing "SVR"
```

#### Fuzzy Match (CONTAINS)

```csharp
// With FuzzyMatch.Operator = "CONTAINS"
var filter = new MemoryFilter();
filter.Add("data.Location", "East"); // Will match any location containing "East"
```

#### Explicit LIKE Pattern

```csharp
var filter = new MemoryFilter();
filter.Add("data.ServerName", "%SVR%"); // Treated as LIKE pattern
```

#### OR Condition (Array)

```csharp
var filter = new MemoryFilter();
filter.Add("data.Environment", new[] { "Production", "Staging" }); // Matches either value
```

#### Tag Filter (Case-Insensitive)

```csharp
var filter = new MemoryFilter();
filter.Add("department", "IT"); // Tag filter, case-insensitive
```

#### Hybrid Filter

```csharp
var filter = new MemoryFilter();
filter.Add("data.Environment", "Production");
filter.Add("department", "IT");
```

### Query Execution

```csharp
var results = await memory.SearchAsync(filter: filter);
```

### Notes

- All structured data field filters must use the `data.` prefix (e.g., `data.Environment`).
- Field names are normalized to snake_case for querying.
- Arrays for a key are ORed; multiple keys are ANDed.
- Fuzzy matching applies only to string fields and when enabled/configured.
- Tag filters are always case-insensitive.

## Batch Ingestion and Performance

### Batch Insert Support (CustomTabularIngestion)

The custom ingestion pipeline now supports batch inserts using Cosmos DB's `TransactionalBatch` API. This can drastically speed up ingestion for large files, especially when all rows share the same partition key (file name).

- **BatchSize property:** Set `BatchSize` on your `CustomTabularIngestion` instance. If `BatchSize == 1`, single inserts are used (default). If `BatchSize > 1`, records are inserted in batches of the specified size.
- **Recommended batch size:** Start with 50. Adjust up or down based on document size and RU/s budget. Cosmos DB limits batches to 100 operations or 2MB per batch.
- **Usage example:**
  ```csharp
  var customIngestion = new CustomTabularIngestion(...);
  customIngestion.BatchSize = 50; // Enable batching
  await customIngestion.ImportTabularDocumentCustomAsync(...);
  ```

### Best Practices and Lessons Learned

- **Unique IDs per Row:** Each row/chunk must have a unique `Id` (e.g., `Guid.NewGuid().ToString()`). If the same document ID is reused for all rows, only one record will be stored per document, and previous rows will be overwritten.
- **Partition Key:** All rows from the same file should use the same partition key (typically the file name) to enable efficient batch operations.
- **Batching:** Use batching for large ingestions to maximize throughput and minimize RU/s consumption.
- **Metadata:** Always include metadata such as worksheet name, row number, schema ID, and import batch ID for traceability and advanced querying.
- **Error Handling:** Monitor for 413 (Payload Too Large) errors and reduce batch size if needed.

### Example: Enabling Batch Ingestion

```csharp
var customIngestion = new CustomTabularIngestion(...);
customIngestion.BatchSize = 50; // Use batch size of 50 for ingestion
await customIngestion.ImportTabularDocumentCustomAsync("myfile.xlsx", "my-dataset");
```

## Implementation Details

The extension creates a database (default name "memory", configurable via `DatabaseName`) in your Azure Cosmos DB account. Each memory index is stored as a separate container within this database.

**Vector Search Implementation:** This connector utilizes Azure Cosmos DB's native vector search capabilities.
- When an index is created (`CreateIndexAsync`), a vector index policy (Flat index, Cosine distance) is automatically configured on the `/embedding` path, assuming the standard serialization of Kernel Memory's `Embedding` type.
- Similarity searches (`GetSimilarListAsync`) use the `VectorDistance` function in Cosmos DB queries comparing against the `c.embedding` field to perform efficient vector comparisons.

Memory records are stored with the following structure:
- `id`: The original `MemoryRecord.Id`, Base64 encoded for compatibility with Cosmos DB ID constraints.
- `file`: The file identifier derived from `MemoryRecord.Id`, used as the **partition key** for the container.
- `tags`: Collection of metadata tags.
- `embedding`: Vector representation of the memory content, indexed for vector search.
- `data`: Tabular data extracted from the `tabular_data` payload field, stored as key-value pairs.
- `source`: Source information extracted from the `source_info` payload field (e.g., sheet name, row number).
- `payload`: The original payload dictionary associated with the memory record (excluding `tabular_data` and `source_info` if they were processed).

## AI-Driven Filtering with TabularFilterHelper

This extension now includes a `TabularFilterHelper` class that provides AI-driven filtering capabilities. This helper class can:

1. Discover available fields in the database
2. Get the most common values for a field
3. Generate filters based on field and value

### Basic Usage

```csharp
// Create the TabularFilterHelper
var filterHelper = new TabularFilterHelper(memory);

// Get available fields
var fields = await filterHelper.GetFilterableFieldsAsync("your-index");
Console.WriteLine("Available fields:");
foreach (var fieldType in fields.Keys)
{
    Console.WriteLine($"  {fieldType}:");
    foreach (var fieldName in fields[fieldType])
    {
        Console.WriteLine($"    - {fieldName}");
    }
}

// Get top values for a field
var topValues = await filterHelper.GetTopFieldValuesAsync("your-index", "data", "environment");
Console.WriteLine("Top values for environment:");
foreach (var (value, count) in topValues)
{
    Console.WriteLine($"  - {value} ({count} occurrences)");
}

// Generate a filter
var filter = filterHelper.GenerateFilter("data", "environment", "Production");
var results = await memory.SearchAsync(filter: filter);
```

### Integration with LLMs for Natural Language Queries

The `TabularFilterHelper` class is designed to be used with LLMs to translate natural language queries into structured filters. Here's an example of how to integrate it with Semantic Kernel:

```csharp
// Create a kernel for LLM-based filtering
var kernel = Kernel.CreateBuilder()
    .AddAzureOpenAIChatCompletion(
        "gpt-4",
        "https://your-openai-service.openai.azure.com/",
        "your-openai-api-key")
    .Build();

// Create a custom function to translate natural language to filters
string promptTemplate = @"
You are helping translate a user's question into a database filter.

Available fields:
{{$fields}}

User question: {{$question}}

Identify the most relevant field and value to filter on.
Return your answer in JSON format with these properties:
- fieldType: either 'tag' or 'data'
- fieldName: the exact name of the field
- value: the value to filter for

JSON response:";

// Get available fields
var filterHelper = new TabularFilterHelper(memory);
var fields = await filterHelper.GetFilterableFieldsAsync("your-index");

// Build a description of available fields
var fieldsList = new StringBuilder();
foreach (var fieldType in fields.Keys)
{
    fieldsList.AppendLine($"  {fieldType}:");
    foreach (var fieldName in fields[fieldType])
    {
        fieldsList.AppendLine($"    - {fieldName}");
    }
}

// Create the function
var translateFunction = kernel.CreateFunctionFromPrompt(promptTemplate);

// Process a user query
string userQuery = "Show me all production servers";
var result = await kernel.InvokeAsync(translateFunction, new KernelArguments
{
    ["fields"] = fieldsList.ToString(),
    ["question"] = userQuery
});

// Parse the JSON response
var response = JsonSerializer.Deserialize<FilterResponse>(result.GetValue<string>());

// Generate the filter
var filter = filterHelper.GenerateFilter(response.FieldType, response.FieldName, response.Value);

// Execute the query
var searchResults = await memory.SearchAsync(filter: filter);

// Helper class for JSON deserialization
class FilterResponse
{
    public string FieldType { get; set; } = "data";
    public string FieldName { get; set; } = "";
    public string Value { get; set; } = "";
}
```

## Implementation Details

The extension creates a database (default name "memory", configurable via `DatabaseName`) in your Azure Cosmos DB account. Each memory index is stored as a separate container within this database.

**Vector Search Implementation:** This connector utilizes Azure Cosmos DB's native vector search capabilities.
- When an index is created (`CreateIndexAsync`), a vector index policy (Flat index, Cosine distance) is automatically configured on the `/embedding` path, assuming the standard serialization of Kernel Memory's `Embedding` type.
- Similarity searches (`GetSimilarListAsync`) use the `VectorDistance` function in Cosmos DB queries comparing against the `c.embedding` field to perform efficient vector comparisons.

Memory records are stored with the following structure:
- `id`: The original `MemoryRecord.Id`, Base64 encoded for compatibility with Cosmos DB ID constraints.
- `file`: The file identifier derived from `MemoryRecord.Id`, used as the **partition key** for the container.
- `tags`: Collection of metadata tags.
- `embedding`: Vector representation of the memory content, indexed for vector search.
- `data`: Tabular data extracted from the `tabular_data` payload field, stored as key-value pairs.
- `source`: Source information extracted from the `source_info` payload field (e.g., sheet name, row number).
- `payload`: The original payload dictionary associated with the memory record (excluding `tabular_data` and `source_info` if they were processed).
