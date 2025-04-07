﻿using Microsoft.KernelMemory;
using Microsoft.SemanticKernel;
using Azure.Storage.Blobs;
using System.Text.RegularExpressions;
using Microsoft.KernelMemory.MemoryStorage; // For TagCollection
using AI_RAG_Examples_KM; // Use the namespace defined in KernelSetup.cs
using Microsoft.KernelMemory.AI.AzureOpenAI; // For AzureOpenAIConfig
using Microsoft.SemanticKernel.Connectors.AzureOpenAI; // For AzureOpenAIPromptExecutionSettings
using System.Text.Json; // For JsonSerializer
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;
using Microsoft.SemanticKernel.Connectors.OpenAI; // For MemoryFilter

//const string IndexName = "sc-seoaichat-sv-index-DSS-KernelMemory";
const string IndexName = "sc-seoaichat-sv-index-DSS-KernelMemory-Tabular";
 
// Define the local directory path where blobs will be saved
string LocalDownloadPath = @"D:\Temp\BLOB";
 
// Load configuration using the helper class
var appConfig = KernelInitializer.LoadConfiguration();
 
// Initialize Kernel and Memory using the helper class
var kernel = KernelInitializer.InitializeKernel(appConfig.AzureOpenAITextConfig);
var memory = KernelInitializer.InitializeMemory(
    appConfig.AzureOpenAITextConfig, 
    appConfig.AzureOpenAIEmbeddingConfig, 
    appConfig.CosmosDbSettings,
    IndexName); // Pass the index name to use as dataset name
 
// Create an instance of BlobStorageProcessor
var fileProcessor = new BlobStorageProcessor(
    memory, 
    appConfig.BlobStorageSettings, 
    IndexName, 
    LocalDownloadPath);

// Create an instance of KernelMemoryQueryProcessor
var queryProcessor = new KernelMemoryQueryProcessor(
    memory,
    kernel,
    IndexName,
    appConfig.AzureOpenAITextConfig);

// This section indexes from the blob storage
await fileProcessor.ProcessBlobsFromStorageAsync();
 
// This section indexes from local File system
await fileProcessor.ProcessFilesFromLocalDirectoryAsync(fileExtensionPattern: "*.xlsx");
 
// This section allows you to query Kernel Memory directly
await QueryKernelMemory();

// Query Kernel Memory
async Task QueryKernelMemory()
{
    string Question = @"Give me a list of all server names with a Server Purpose of 'Corelight Network monitoring sensor'.
    It is important to return the full list. I expect that their are over 40.";

    Console.WriteLine($"Searching for: {Question}");
    await queryProcessor.AskTabularQuestionAsync(Question);
}

// Configuration class defined at the bottom
public class CosmosDbSettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string APIKey { get; set; } = string.Empty;
    //public string DatabaseName { get; set; } = "memory"; // Default value
}

// Configuration class for Blob Storage - MOVED TO KernelSetup.cs
// public class BlobStorageSettings
// {
//     public string BlobContainerUrl { get; set; } = string.Empty;
//     public string SasToken { get; set; } = string.Empty;
// }
