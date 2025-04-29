# Technical Context: AI-RAG-Examples-KM

## Technology Stack

### Core Framework
- **.NET 8.0**: The project is built on .NET 8.0, utilizing its latest features and performance improvements.
- **Microsoft Kernel Memory (v0.98.250324.1)**: The foundation framework for RAG implementation, providing orchestration, pipeline management, and memory abstractions.
- **Microsoft Semantic Kernel**: Used for AI operations, prompt management, and function calling.

### Cloud Services
- **Azure OpenAI**: Provides embedding generation and text completion capabilities.
  - Used for both text generation and embedding creation
  - Configured separately for each function
- **Azure Cosmos DB**: Serves as the persistent storage layer for memory records.
  - Stores both vector embeddings and structured data
  - Enables efficient querying of tabular information
- **Azure Blob Storage**: Source for documents to be processed and indexed.
  - Configured with container URL and SAS token for access

### Data Processing
- **ClosedXML**: Library for Excel file processing, used by the TabularExcelDecoder.
- **System.Text.Json**: Used for JSON serialization and deserialization throughout the application.

### Configuration
- **Microsoft.Extensions.Configuration**: Used for loading and binding configuration from appsettings.json.
- **dotenv.net (v3.2.1)**: Optional support for environment variable loading.

## Development Environment

### Required Tools
- **Visual Studio 2022** or **Visual Studio Code** with C# extensions
- **.NET 8.0 SDK**
- **Azure subscription** for accessing Azure OpenAI and Cosmos DB services

### Local Setup
1. Clone the repository
2. Configure appsettings.json with Azure service credentials
3. Ensure local directory exists for blob downloads (default: D:\Temp\BLOB)
4. Build and run the application

### Extension Development Workflow
- The primary development of Kernel Memory extensions (like the Cosmos DB connectors) occurs in the `X:\AI\Kernel-Memory-Clone\kernel-memory\extensions` directory.
- Developed extensions are then copied into the current project (`x:/AI/AI-RAG-KM/AI-RAG-Examples-KM`) for testing and integration within this specific RAG example application.
- Key extensions developed in this workflow include:
  - `AzureCosmosDb`: Standard vector store implementation.
  - `AzureCosmosDbTabular`: Specialized vector store with structured data querying capabilities (used in this project).
  - `TabularExcelDecoder`: Custom decoder for Excel files, tightly coupled with `AzureCosmosDbTabular`.

### Configuration Files
- **appsettings.json**: Contains all configuration for Azure services and application settings
  ```json
  {
    "KernelMemory": {
      "Services": {
        "AzureOpenAIText": {
          "Deployment": "",
          "ApiKey": "",
          "Endpoint": ""
        },
        "AzureOpenAIEmbedding": {
          "ApiKey": "",
          "Endpoint": "",
          "Deployment": ""
        },
        "AzureAISearch": {
          "ApiKey": "",
          "Endpoint": "",
          "IndexName": ""
        },
        "AzureCosmosDbTabular": {
          "Endpoint": "",
          "APIKey": ""
        }
      }
    },
    "AzureBlobStorage": {
      "BlobContainerUrl": "",
      "SasToken": ""
    }
  }
  ```

## Component Architecture

### Memory Access Patterns
- **Reflection-Based Access**: The `MemoryHelper` class uses reflection to access internal components of the Kernel Memory framework:
  - Used to extract the `IMemoryDb` instance from `IKernelMemory` instances
  - Enables the TabularFilterHelper to perform schema-based operations
  - Has reliability issues with different implementations like `MemoryServerless`
  - Will be replaced by Dependency Injection in future versions
- **Planned Dependency Injection**: Future refactoring will use DI to directly resolve and pass required service instances:
  - `KernelInitializer.InitializeMemory` will return both `IKernelMemory` and `IMemoryDb` instances
  - Components will receive direct references to required services
  - Eliminates dependency on internal field structures that may change

## Technical Constraints

### Azure OpenAI Limitations
- Requires valid Azure OpenAI service with appropriate model deployments
- API rate limits may apply depending on the service tier
- Token limits for context windows based on the model used

### Cosmos DB Considerations
- Costs scale with storage and request units.
- Provisioned throughput needs to be sufficient for vector operations.
- Query complexity affects performance and cost.
- **Indexing for Tabular Data:** The `AzureCosmosDbTabular` extension requires the Cosmos DB container's indexing policy to explicitly include the `/data/*` path to enable efficient filtering on structured fields. The vector path (`/embedding/*`) should typically be excluded from standard indexing as it's handled by the vector index policy.

### Memory Requirements
- Processing large Excel files may require significant memory
- Vector operations can be memory-intensive
- Consider available RAM when processing large datasets

### Storage Requirements
- Local storage needed for temporary blob downloads
- Cosmos DB storage scales with the number and size of documents processed

## Dependencies

### NuGet Packages
- **Microsoft.KernelMemory** (v0.98.250324.1)
- **Microsoft.KernelMemory.Abstractions** (v0.98.250324.1)
- **Microsoft.KernelMemory.Core** (v0.98.250324.1)
- **Microsoft.SemanticKernel.Connectors.AzureCosmosDBNoSQL** (v1.44.0-preview)
- **dotenv.net** (v3.2.1)

### Implicit Dependencies
- **ClosedXML**: Used by TabularExcelDecoder for Excel file processing
- **Microsoft.Azure.Cosmos**: Used for Cosmos DB operations
- **Azure.Storage.Blobs**: Used for blob storage operations

## Extension Points

### Custom Decoders
The system can be extended with additional decoders for different file formats by implementing the IContentDecoder interface.

### Pipeline Customization
The memory pipeline can be customized by adding, removing, or replacing handlers:
```csharp
memory.Orchestrator.AddHandler<CustomHandler>("custom_step");
```

### Filter Generation
The filter generation prompt can be modified to support different filtering patterns or additional field types.

### Query Processing
The query processing can be extended with additional strategies or post-processing steps.

## Performance Considerations

### Optimizations
- Batch ingestion with TransactionalBatch in CustomTabularIngestion (BatchSize property, recommended 50)
- Unique ID assignment per row to prevent overwrites in Cosmos DB
- Efficient partitioning strategies for large documents
- Cosmos DB indexing policies for tabular data
- Caching of frequently accessed data

### Scaling
- Horizontal scaling through multiple instances
- Vertical scaling for memory-intensive operations
- Cosmos DB throughput provisioning based on workload

### Monitoring
- Track Azure OpenAI token usage
- Monitor Cosmos DB request units
- Observe memory consumption during processing
