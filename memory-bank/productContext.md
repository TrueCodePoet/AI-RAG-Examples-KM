# Product Context: AI-RAG-Examples-KM

## Purpose
AI-RAG-Examples-KM serves as a reference implementation and testing ground for Retrieval-Augmented Generation (RAG) systems built with Microsoft's Kernel Memory framework. It specifically addresses the challenge of maintaining structured data relationships when implementing RAG with tabular data sources like Excel spreadsheets.

## Problem Statement
Traditional RAG systems often lose the structured nature of tabular data during processing, treating spreadsheets as flat text documents. This results in:
1. Loss of column-row relationships
2. Inability to filter by specific data fields
3. Reduced precision when querying structured information
4. Difficulty in maintaining data types (numbers, dates, etc.)

## Solution Approach
This project demonstrates a specialized approach to RAG that:
1. Preserves tabular structure during document processing
2. Maintains data types and relationships
3. Enables field-specific filtering and querying
4. Leverages Azure Cosmos DB's capabilities for efficient storage and retrieval
5. Uses AI to translate natural language queries into structured filters

## User Experience Goals
- Users should be able to ask natural language questions about tabular data
- Queries like "Show me all servers with a Server Purpose of 'Corelight Network monitoring sensor'" should return precise results
- The system should understand field names and values without requiring exact syntax
- Responses should be comprehensive and accurate, leveraging the preserved structure of the data
- The implementation should be extensible to different types of structured data

## Integration Points
- Azure Blob Storage for document sources
- Azure Cosmos DB for structured memory storage
- Azure OpenAI for embeddings and text generation
- Microsoft Kernel Memory for orchestration and pipeline management
- Local file system for temporary storage and processing

## Intended Workflow
1. Documents are ingested from Azure Blob Storage or local files
2. Specialized decoders process tabular data (particularly Excel files)
3. Data is stored in Azure Cosmos DB with preserved structure
4. Users submit natural language queries
5. Queries are analyzed to extract potential filters
6. Relevant information is retrieved and synthesized into coherent responses
7. Results are presented with source attribution

## Success Metrics
- Accuracy of responses to structured data queries
- Preservation of data relationships and types
- Performance of filter generation from natural language
- Scalability with large datasets
- Extensibility to different data formats
