# Active Context: AI-RAG-Examples-KM

## Current Focus
The project is currently focused on implementing and testing the tabular data processing capabilities of the RAG system. Specifically, the work centers around:

1. Developing the Azure Cosmos DB Tabular implementation for efficient storage and retrieval of structured data
2. Enhancing the Excel file processing to preserve tabular structure and data types
3. Implementing AI-driven filter generation for natural language queries against structured data
4. Testing the end-to-end workflow with sample Excel files containing server information

## Recent Changes

### Tabular Data Processing
- Implemented TabularExcelDecoder for specialized Excel file processing
- Added support for preserving data types and structure during ingestion
- Created normalized text representation of tabular data for embedding generation
- Enhanced error handling for PivotTable structures in Excel files

### Cosmos DB Integration
- Developed AzureCosmosDbTabularMemory implementation for structured data storage
- Added support for efficient filtering on data fields
- Implemented specialized indexing policies for tabular data
- Modified schema storage to use the same container as vector data

### Query Processing
- Enhanced KernelMemoryQueryProcessor with filter generation capabilities
- Added support for translating natural language to structured filters
- Implemented specialized response formatting for tabular data queries

## Next Steps

### Short-term Tasks
1. **Testing with Larger Datasets**: Test the system with larger Excel files to evaluate performance and accuracy
2. **Filter Generation Improvements**: Enhance the filter generation prompt to handle more complex queries
3. **Error Handling**: Improve error handling for edge cases in Excel processing
4. **Documentation**: Add more detailed documentation on the tabular data processing capabilities

### Medium-term Goals
1. **Support for Additional File Formats**: Extend the tabular processing to other structured formats (CSV, JSON, etc.)
2. **Performance Optimization**: Optimize the processing pipeline for large datasets
3. **User Interface**: Develop a simple UI for demonstrating the query capabilities
4. **Batch Processing**: Add support for batch processing of multiple files

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

### Open Questions
1. **Scaling Strategy**: How to efficiently scale the system for very large datasets?
2. **Cost Optimization**: What strategies can be employed to minimize Azure costs while maintaining performance?
3. **Query Precision**: How to balance between natural language flexibility and query precision?
4. **Data Type Preservation**: What's the best approach for handling complex data types in Excel files?
5. **Helper Dependencies**: The `TabularFilterHelper` uses reflection to access internal Kernel Memory fields, which could break with future KM updates. Is there a more robust way to achieve this?

### Current Challenges
1. **Filter Accuracy**: The filter generation sometimes misinterprets field names or values in complex queries.
2. **Excel Format Variations**: Different Excel formatting styles can affect the quality of data extraction.
3. **Memory Usage**: Processing large Excel files can be memory-intensive.
4. **Response Formatting**: Ensuring responses are well-formatted and user-friendly for tabular data queries.
5. **Rate Limiting**: Azure OpenAI rate limits can slow down processing of large datasets.
6. **Excel PivotTable Handling**: Excel files with PivotTables can cause processing errors, now addressed with improved error detection and handling.
7. **Type Compatibility**: Fixed issues with TabularExcelDecoder where internal/public type accessibility conflicts prevented proper schema extraction.
8. **Schema Storage**: Fixed issue where TabularExcelDecoder's `_memory` field was null, preventing schema from being saved. Implemented a two-part solution:
   - Enhanced dependency injection to properly resolve the IMemoryDb instance
   - Added a WithMemory method to TabularExcelDecoder to allow setting the memory instance after creation
   - Used reflection to find and update TabularExcelDecoder instances in the pipeline at runtime

9. **Interface-based Design**: Improved code by using the IMemoryDb interface instead of the concrete AzureCosmosDbTabularMemory class:
   - Modified TabularExcelDecoder to accept IMemoryDb instead of AzureCosmosDbTabularMemory
   - Updated Program.cs to work with IMemoryDb for better abstraction
   - This change improves maintainability and allows for easier swapping of memory implementations

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
| Filter Generation | Implemented | AI-driven approach with room for improvement |
| Query Processing | Implemented | Basic functionality with specialized formatting |
| Schema Management | Implemented | Schema extraction, storage, and validation in the same container as data |
| UI/API | Not Started | Planned for future development |
