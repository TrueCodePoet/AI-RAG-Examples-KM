# Active Context: AI-RAG-Examples-KM

## Current Focus
The project is currently focused on implementing and testing the tabular data processing capabilities of the RAG system. Specifically, the work centers around:

1. Developing the Azure Cosmos DB Tabular implementation for efficient storage and retrieval of structured data
2. Enhancing the Excel file processing to preserve tabular structure and data types
3. Implementing AI-driven filter generation for natural language queries against structured data
4. Testing the end-to-end workflow with sample Excel files containing server information

## Recent Changes

### Code Refactoring
- Refactored the large `AzureCosmosDbTabularMemory.cs` file into multiple partial class files for better maintainability:
  - Created `AzureCosmosDbTabularMemory.cs` - Core class definition, constructor, and fields
  - Created `TabularMemoryRecordResult.cs` - Internal result class
  - Created `AzureCosmosDbTabularMemory.Index.cs` - Index management methods
  - Created `AzureCosmosDbTabularMemory.RecordOps.cs` - Record operations (Upsert, Get, Delete)
  - Created `AzureCosmosDbTabularMemory.Query.cs` - Query processing and filtering
  - Created `AzureCosmosDbTabularMemory.SchemaStorage.cs` - Schema storage and retrieval
  - Created `AzureCosmosDbTabularMemory.SchemaOperations.cs` - Schema creation and manipulation
  - Created `AzureCosmosDbTabularMemory.SchemaRecord.cs` - Record-schema relationships
  - Created `AzureCosmosDbTabularMemory.Utilities.cs` - Helper methods like normalization and type inference
- Refactored the large `KernelMemoryQueryProcessor.cs` file into multiple partial class files for better organization:
  - Created `KernelMemoryQueryProcessor.cs` - Core class with fields and constructor
  - Created `KernelMemoryQueryProcessor.AskQuestion.cs` - Regular question-answering logic
  - Created `KernelMemoryQueryProcessor.AskTabularQuestion.cs` - Tabular data question-answering
  - Created `KernelMemoryQueryProcessor.DatasetIdentification.cs` - Dataset identification functionality
  - Created `KernelMemoryQueryProcessor.SchemaInfo.cs` - Schema information retrieval and formatting
  - Created `KernelMemoryQueryProcessor.FilterGeneration.cs` - Filter generation for queries
  - Created `KernelMemoryQueryProcessor.Search.cs` - Search execution across filters
  - Created `KernelMemoryQueryProcessor.Synthesis.cs` - Answer synthesis from search results
- Fixed compatibility issues with Semantic Kernel version in the AskQuestion implementation
- This restructuring improves code organization and maintainability while preserving the same functionality

### Tabular Data Processing
- Implemented TabularExcelDecoder for specialized Excel file processing
- Added support for preserving data types and structure during ingestion
- Created normalized text representation of tabular data for embedding generation
- Enhanced error handling for PivotTable structures in Excel files
- Fixed schema generation to prioritize the provided file path over workbook metadata for accurate source file names
- **Added batch ingestion support in CustomTabularIngestion:** BatchSize property enables Cosmos DB TransactionalBatch for high-throughput ingestion. Default is 1 (no batching); set to 50 for optimal performance.
- **Ensured unique IDs per row:** Each row/chunk is assigned a unique Guid-based ID to prevent overwriting in Cosmos DB.
- **Documented ingestion pitfalls:** If the same document ID is reused for all rows, only one record is stored per document, and previous rows are overwritten. This was a key lesson learned and is now avoided in all ingestion pipelines.

### Cosmos DB Integration
- Developed AzureCosmosDbTabularMemory implementation for structured data storage
- Added support for efficient filtering on data fields
- Implemented specialized indexing policies for tabular data
- Modified schema storage to use the same container as vector data
- Fixed source dictionary extraction to properly parse text field for metadata
- Enhanced schema ID and import batch ID extraction from text field
- Fixed data dictionary population to properly deserialize tabular_data field
  - Refined the `ParseSentenceFormat` method in `AzureCosmosDbTabularMemoryRecord` to correctly handle the text format and avoid parsing artifacts
  - Implemented `TabularCsvDecoder` modeled after `TabularExcelDecoder` to support CSV file ingestion.
  - Ensured `TabularCsvDecoder` generates the exact same "text" sentence format as the Excel decoder for parser compatibility.
  - Added logging to `TabularCsvDecoder` to track lines read vs. rows added for diagnosing potential row skipping issues.
  - Resolved `System.FormatException` during record retrieval by ensuring all record IDs are Base64 encoded using `EncodeId` before storage (verified in `FromMemoryRecord`).
- **Performance optimization:** Batch ingestion with TransactionalBatch is now available for high-throughput scenarios. Batch size is configurable; 50 is recommended for most cases.
- **Best practices:** Always use unique IDs per row, group by partition key for batching, and monitor for Cosmos DB 413 errors to adjust batch size.

### Query Processing
- Enhanced KernelMemoryQueryProcessor with filter generation capabilities
- Implemented dataset identification using TabularFilterHelper and LLM to determine the relevant dataset for a query
- Added support for translating natural language to structured filters with JSON output format
- Implemented schema-based filter validation against the identified dataset's schema
- Implemented specialized response formatting for tabular data queries
- **Added result limiting feature to control the number of sources displayed in responses**
  - Added resultLimit parameter to AskTabularQuestionAsync method
  - Implemented logic to limit displayed sources while still using all sources for the answer
  - Modified Program.cs to use a result limit of 10 for tabular queries

### Dependency Injection for MemoryDb (Major Refactor)
- **Replaced reflection-based IMemoryDb access with DI-based approach**:
  - `KernelInitializer.InitializeMemory` now returns both `IKernelMemory` and `IMemoryDb` directly.
  - `Program.cs` and all consumers now receive the `IMemoryDb` instance via DI, not reflection.
  - `BlobStorageProcessor` and `KernelMemoryQueryProcessor` are now explicitly constructed with the DI-injected `IMemoryDb` instance.
  - A runtime log message confirms when the DI path is used and reflection is skipped.
  - Reflection-based fallback is only used if DI is not available, and will be removed after full validation.
  - This change makes the system robust to future changes in Kernel Memory internals and improves maintainability.
  - **Lesson learned (2025-04-17):** The KernelMemoryBuilder API does not expose the main IServiceCollection, so we used reflection to access the private `_memoryServiceCollection` field and build a ServiceProvider to extract the IMemoryDb instance for DI. This workaround is necessary until the upstream library exposes a public property or method for this purpose. The approach is now working and enables full schema-aware DI in the application.

### Fuzzy Matching, AND/OR Query Logic, and Dynamic AI Prompting
- **FuzzyMatch configuration is now fully supported and configurable via appsettings.json**:
  - `CosmosDbSettings` now includes a `FuzzyMatch` property (with `Enabled`, `Operator`, etc.), bound from config.
  - The fuzzy match operator (`LIKE` or `CONTAINS`) is passed from config to `KernelMemoryQueryProcessor`.
  - The AI filter generation prompt is dynamically constructed to match the configured operator, with operator-specific instructions and examples.
- **AND/OR logic in filters is robustly supported**:
  - Multiple keys in the filter JSON are combined as AND conditions.
  - Arrays for a key (e.g., `"project": ["phoenix", "austin"]`) are combined as OR conditions in the backend SQL.
  - The AI agent is instructed to use arrays for OR and multiple keys for AND.
- **Backend (AzureCosmosDbTabularMemory.Query.cs) supports both AND and OR logic**:
  - Arrays in filters are translated to SQL OR conditions.
  - All logic is case-insensitive and works for both LIKE and CONTAINS operators.
- **Result**: The system is now highly flexible, maintainable, and can be reconfigured for different fuzzy matching strategies and query logic without code changes.

### TabularFilterHelper Improvements
- **Completely redesigned TabularFilterHelper to use Reflection API** instead of dynamic typing
  - Fixed "Cannot use a collection of dynamic type in an asynchronous foreach" errors
  - Improved robustness by using proper method invocation through reflection
  - Added extensive error handling for method lookup and invocation failures
  - Maintained the same API surface for backward compatibility
  - Enhanced the constructor to better handle dependency injection

## Next Steps

### Short-term Tasks
- **(Done)** Refactor Memory Initialization: `KernelInitializer.InitializeMemory` now uses DI to resolve the `IMemoryDb` instance and returns it alongside `IKernelMemory`. `Program.cs` uses this resolved instance and no longer uses the `MemoryHelper` reflection logic.
- **(Done)** Fix filter value type preservation for LIKE/CONTAINS/OR logic: Updated `TabularFilterHelper.GenerateValidatedFilterAsync` to preserve array values as `List<string>` (not stringified), and updated query logic in `AzureCosmosDbTabularMemory.Query.cs` to robustly handle all `IEnumerable` types for OR/array cases, applying LIKE/CONTAINS/exact as appropriate.
2. **Testing with Larger Datasets**: Test the system with larger Excel **and CSV** files to evaluate performance and accuracy, **specifically checking for row skipping issues in CSVs**.
3. **Filter Generation Improvements**: Enhance the filter generation prompt to handle more complex queries.
4. **Error Handling**: Continue improving error handling for edge cases in Excel and CSV processing.
5. **Documentation**: Add more detailed documentation on the tabular data processing capabilities (Excel & CSV).
6. **Result Presentation**: Further enhance the result limiting feature with pagination or summarization options.

### Medium-term Goals
1. **Support for Additional File Formats**: Extend the tabular processing to other structured formats (JSON, etc.). **(CSV support added)**
2. **Performance Optimization**: Optimize the processing pipeline for large datasets.
3. **User Interface**: Develop a simple UI for demonstrating the query capabilities.
4. **Batch Processing**: Add support for batch processing of multiple files.

### Long-term Vision
1. **Advanced Filtering**: Implement more sophisticated filtering capabilities (ranges, fuzzy matching, etc.)
2. **Schema Inference**: Automatically infer and apply schema to unstructured data
3. **Cross-document Queries**: Support queries that span multiple documents or data sources
4. **Integration with Other Systems**: Provide connectors for common data systems

## Active Decisions and Considerations

### Architecture Decisions
1. **Cosmos DB vs. Vector Databases**: Decided to use Cosmos DB for its combination of document storage and vector capabilities, despite potential cost implications.
2. **Custom Excel Processing**: Chose to implement a specialized Excel decoder rather than using the standard one to better preserve tabular structure.
3. **Filter Generation Approach**: Opted for an AI-driven approach to filter generation rather than a rule-based system for greater flexibility.
4. **Single Container for Schema and Data**: Decided to store schema information in the same container as the data for simplicity and improved data locality.
5. **Text Field Parsing**: Implemented a robust approach to extract all data (metadata and row data) from the text field using a combination of regex for the prefix and manual string splitting/parsing for the data section.
6. **Partial Class Organization**: Used C# partial classes to organize the large AzureCosmosDbTabularMemory implementation into logical groupings while maintaining a single cohesive class.
7. **Filename Priority for Schema Generation**: Modified the `ExtractSchemaFromWorkbook` method to prioritize the provided file path over workbook metadata (Title property) when determining the source file name.
8. **Dependency Injection for Service Resolution**: Now using DI to provide `IMemoryDb` directly to all consumers. Reflection-based access (`MemoryHelper`) is deprecated and only present for backward compatibility. This makes the system robust to changes in Kernel Memory internals and improves maintainability.
9. **Result Limiting Approach**: Implemented result limiting post-query to allow the LLM to still have access to all information while controlling what's displayed to the user.
10. **Modular Code Organization**: Adopted C# partial classes to split large classes like KernelMemoryQueryProcessor into smaller, focused files while maintaining cohesion and logical grouping of functionality.

### Open Questions
1. **Scaling Strategy**: How to efficiently scale the system for very large datasets?
2. **Cost Optimization**: What strategies can be employed to minimize Azure costs while maintaining performance?
3. **Query Precision**: How to balance between natural language flexibility and query precision?
4. **Data Type Preservation**: What's the best approach for handling complex data types in Excel files?
5. **Helper Dependencies**: With the new reflection-based approach, TabularFilterHelper is more robust, but is this the best long-term solution or should we push for API changes in Kernel Memory?
6. **Result Presentation**: What's the most effective way to present large result sets to users? Pagination, summarization, clustering?

### Current Challenges
1. **Filter Accuracy**: The filter generation sometimes misinterprets field names or values in complex queries.
2. **Excel Format Variations**: Different Excel formatting styles can affect the quality of data extraction.
3. **Memory Usage**: Processing large Excel files can be memory-intensive.
4. **Response Formatting**: Ensuring responses are well-formatted and user-friendly for tabular data queries.
5. **Rate Limiting**: Azure OpenAI rate limits can slow down processing of large datasets.
6. **Vector Field Name Mismatch**: Fixed discrepancy between the field name in constants (embedding) and actual JSON property (vector).
   - Was causing vector queries to fail as they were looking for the wrong field
   - Solution: Updated all SQL queries to use the correct field name (c.vector)
   - Also updated the vector index path in container creation
7. **VectorDistance Sorting Constraints**: Discovered that Cosmos DB does not support ASC/DESC directives with VectorDistance.
   - Was causing vector search queries to fail with BadRequest errors
   - Solution: Removed ASC directive from ORDER BY clauses
   - Added comments explaining that VectorDistance automatically sorts from most similar to least similar
8. **Limited Query Results**: Previous implementation only supported fixed-size result sets.
   - Solution: Modified queries to support limit=0 (unlimited results)
   - Only include TOP @limit in SQL when limit > 0
   - Only add @limit parameter when actually using a limit
   - Changed default limit from 1 to 5 in all query methods for better usability
9. **TabularFilterHelper Dynamic Type Issues**: Fixed issues with using dynamic types in asynchronous contexts.
   - Problem: "Cannot use a collection of dynamic type in an asynchronous foreach" errors
   - Solution: Redesigned TabularFilterHelper to use .NET Reflection API for method invocation
   - Added extensive error handling for method lookup and invocation
   - This approach avoids type casting while maintaining compatibility with the API
10. **Result Set Size Control**: Added the ability to limit displayed results.
    - Problem: Large result sets could be overwhelming to users
    - Solution: Added resultLimit parameter to AskTabularQuestionAsync
    - Implemented post-query filtering of displayed sources
    - Updated Program.cs to use a default limit of 10 for tabular queries

11. **IMemoryDb Access Failure**: The `TabularFilterHelper` cannot reliably access the underlying `AzureCosmosDbTabularMemory` instance when initialized only with `IKernelMemory`. The reflection-based approach (looking for a private `_memoryDb` field) fails because the `MemoryServerless` implementation doesn't store the instance that way.
    - **Impact**: Queries requiring schema access (like dataset identification) fail.
    - **Status**: Partially fixed. Implemented a centralized `MemoryHelper` class and made `TabularFilterHelper` and `KernelMemoryQueryProcessor` more resilient through graceful fallbacks when reflection fails. Long-term fix will use DI instead of reflection.
    
12. **Fault Tolerance in Schema Operations**: The system now has improved fault tolerance when schema operations fail due to reflection errors.
    - Key improvements:
      - Created a dedicated `MemoryHelper` class to centralize reflection code
      - Updated `TabularFilterHelper` to return empty results instead of throwing exceptions when _memoryDb is null
      - Modified `KernelMemoryQueryProcessor` to detect missing memoryDb and provide clear warnings
      - Added skip flags to bypass dataset identification when unavailable
      - Added detailed diagnostics about reflection failures
13. **(Resolved)** `System.FormatException`: 'Input is not a valid Base-64 string' during record retrieval.
    - **Cause:** Record `Id` was not encoded using `EncodeId` before storage.
    - **Solution:** Ensured `EncodeId` is used consistently when creating `AzureCosmosDbTabularMemoryRecord` instances (verified in `FromMemoryRecord`).
14. **(Monitoring)** Potential CSV Row Skipping: Initial tests showed fewer rows imported from CSV than expected.
    - **Cause:** Potentially incorrect `HeaderRowIndex` config or blank lines in the CSV.
    - **Mitigation:** Added detailed logging (`totalLines`, `totalRowsAdded`) to `TabularCsvDecoder` to help diagnose during further testing.

## Rate Limiting Considerations

When processing large Excel files, the system may encounter rate limiting from Azure OpenAI services. This manifests as warning logs like:

```
warn: Microsoft.KernelMemory.AI.AzureOpenAI.Internals.ClientSequentialRetryPolicy[0]
      Header Retry-After found, value 17
warn: Microsoft.KernelMemory.AI.AzureOpenAI.Internals.ClientSequentialRetryPolicy[0]
      Delay extracted from HTTP response: 17000 msecs
```

These warnings indicate:

1. The application is hitting Azure OpenAI's rate limits due to the high volume of embedding generation requests.
2. The client is correctly implementing a retry mechanism with backoff based on the Retry-After header.
3. This is expected behavior when processing large datasets and not an error condition.

Potential solutions include:
- Increasing Azure OpenAI quota
- Adding rate limiting configuration in appsettings.json
- Implementing batch processing with delays
- Reducing concurrency when processing multiple files

## Integration Status

| Component | Status | Notes |
|-----------|--------|-------|
| Azure OpenAI | Implemented | Configured for both text and embedding generation |
| Azure Cosmos DB | Implemented | Custom implementation for tabular data with unified schema storage |
| Azure Blob Storage | Implemented | Basic functionality for document ingestion |
| Excel Processing | Implemented | Custom decoder for tabular data preservation with PivotTable handling |
| CSV Processing   | Implemented | Custom decoder modeled after Excel decoder, requires exact text format match |
| Filter Generation | Implemented | AI-driven approach with room for improvement |
| Query Processing | Implemented | Basic functionality with specialized formatting and result limiting |
| Schema Management | Implemented | Schema extraction, storage, and validation in the same container as data |
| UI/API | Not Started | Planned for future development |
