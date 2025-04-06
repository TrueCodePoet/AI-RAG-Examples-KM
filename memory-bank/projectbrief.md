# Project Brief: AI-RAG-Examples-KM

## Overview
AI-RAG-Examples-KM is a .NET application that implements Retrieval-Augmented Generation (RAG) using Microsoft's Kernel Memory framework. The project focuses on testing and demonstrating various RAG implementations, with a particular emphasis on processing and querying tabular data stored in Azure Cosmos DB.

## Core Objectives
1. Demonstrate how to ingest and process documents from Azure Blob Storage
2. Implement specialized handling for tabular data (particularly Excel files)
3. Showcase natural language querying capabilities against structured data
4. Provide a reference implementation for Azure Cosmos DB integration with Kernel Memory

## Key Features
- Blob storage document processing pipeline
- Excel file tabular data extraction and preservation
- Natural language querying with filter generation
- Integration with Azure OpenAI for embeddings and text generation
- Cosmos DB Tabular storage implementation for efficient structured data retrieval

## Target Use Cases
- Querying structured data (like Excel spreadsheets) using natural language
- Building knowledge bases from document repositories
- Implementing RAG systems with preserved tabular data structure
- Creating AI assistants that can reference internal structured documents

## Success Criteria
- Successfully process and index documents from blob storage
- Maintain tabular data structure during processing
- Generate accurate responses to natural language queries about tabular data
- Demonstrate efficient filtering and retrieval of relevant information
