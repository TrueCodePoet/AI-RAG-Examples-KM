# AI-RAG-Examples-KM

A reference implementation for testing Retrieval-Augmented Generation (RAG) using Microsoft's Kernel Memory framework, with a focus on tabular data processing and Azure Cosmos DB integration.

## Overview

This project demonstrates how to implement RAG with preserved tabular data structure using Microsoft's Kernel Memory. It focuses on processing Excel files while maintaining their structured nature, enabling natural language queries against tabular data stored in Azure Cosmos DB.

## Key Features

- Blob storage document processing pipeline
- Excel file tabular data extraction and structure preservation
- Natural language querying with filter generation
- Integration with Azure OpenAI for embeddings and text generation
- Cosmos DB Tabular storage implementation for efficient structured data retrieval

## Configuration

Configure the application by updating the `appsettings.json` file with your Azure service credentials:

```json
{
  "KernelMemory": {
    "Services": {
      "AzureOpenAIText": {
        "Deployment": "your-text-model-deployment",
        "ApiKey": "your-api-key",
        "Endpoint": "https://your-endpoint.openai.azure.com/"
      },
      "AzureOpenAIEmbedding": {
        "ApiKey": "your-api-key",
        "Endpoint": "https://your-endpoint.openai.azure.com/",
        "Deployment": "your-embedding-model-deployment"
      },
      "AzureCosmosDbTabular": {
        "Endpoint": "https://your-cosmos-account.documents.azure.com:443/",
        "APIKey": "your-cosmos-key"
      }
    }
  },
  "AzureBlobStorage": {
    "BlobContainerUrl": "https://youraccount.blob.core.windows.net/your-container",
    "SasToken": "your-sas-token"
  }
}
```

## Rate Limiting and Performance

### Understanding Azure OpenAI Rate Limiting

When processing large Excel files, you may see warnings like:

```
warn: Microsoft.KernelMemory.AI.AzureOpenAI.Internals.ClientSequentialRetryPolicy[0]
      Header Retry-After found, value 17
warn: Microsoft.KernelMemory.AI.AzureOpenAI.Internals.ClientSequentialRetryPolicy[0]
      Delay extracted from HTTP response: 17000 msecs
```

These warnings indicate:

1. **Rate Limiting**: Your application is hitting Azure OpenAI's rate limits. When processing Excel files, the system generates embeddings for each row/chunk, which can result in many API calls in a short period.

2. **Retry Mechanism**: The client is correctly handling these responses by:
   - Detecting the 429 (Too Many Requests) response
   - Reading the Retry-After header (e.g., 17 seconds)
   - Automatically waiting before retrying

3. **Normal Behavior**: This is expected behavior when processing large amounts of data. The system is working as designed by respecting Azure OpenAI's rate limits.

### How to Reduce Rate Limiting Issues

1. **Increase Azure OpenAI Quota**: If possible, request a higher quota for your Azure OpenAI deployment.

2. **Add Rate Limiting Configuration**: You can add rate limiting settings to your appsettings.json:

```json
"KernelMemory": {
  "Services": {
    "AzureOpenAIText": {
      "MaxRetries": 10,
      "MaxTokensPerMinute": 60000,
      "MaxRequestsPerMinute": 1000,
      ...
    },
    "AzureOpenAIEmbedding": {
      "MaxRetries": 10,
      "MaxTokensPerMinute": 60000,
      "MaxRequestsPerMinute": 1000,
      ...
    }
  }
}
```

3. **Batch Processing**: Consider processing files in smaller batches with delays between batches.

4. **Reduce Concurrency**: If processing multiple files concurrently, consider reducing the concurrency level.

## Known Issues and Solutions

### PivotTable Structure Issues

Excel files containing PivotTables may cause errors during processing due to limitations in the ClosedXML library. The application now includes enhanced error handling for PivotTable structures:

- Added a `SkipPivotTables` configuration option (defaulted to true)
- Improved error detection for PivotTable and XML structure issues
- Better error recovery with detailed logging

### Schema Storage

The AzureCosmosDbTabular implementation now stores schema information in the same container as the vector data:

- Uses a single container for both data and schema by default
- Maintains the relationship between schemas and their corresponding data
- Simplifies configuration and deployment

## Usage

The main program demonstrates:

1. Processing documents from Azure Blob Storage
2. Processing Excel files from a local directory
3. Querying the data using natural language

Example query:
```csharp
string Question = @"Give me a list of all server names with a Server Purpose of 'Corelight Network monitoring sensor'.";
await queryProcessor.AskTabularQuestionAsync(Question);
```

## Requirements

- .NET 8.0 SDK
- Azure subscription with:
  - Azure OpenAI service
  - Azure Cosmos DB
  - Azure Blob Storage (optional)
