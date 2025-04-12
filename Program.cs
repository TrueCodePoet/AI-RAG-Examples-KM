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

// Define index names for both pipeline types
const string TabularIndexName = "sc-seoaichat-sv-index-DSS-KernelMemory-Tabular";
const string StandardIndexName = "sc-seoaichat-sv-index-DSS-KernelMemory-Standard";
 
// Define the local directory path where blobs will be saved
string LocalDownloadPath = @"D:\Temp\BLOB";
 
// Load configuration using the helper class
var appConfig = KernelInitializer.LoadConfiguration();
 
// Initialize Kernel for both pipelines to share
var kernel = KernelInitializer.InitializeKernel(appConfig.AzureOpenAITextConfig);

// Initialize both memory pipelines
Console.WriteLine("Initializing dual memory pipelines...");

// 1. Tabular Memory Pipeline
var tabularMemory = KernelInitializer.InitializeMemory(
    appConfig.AzureOpenAITextConfig, 
    appConfig.AzureOpenAIEmbeddingConfig, 
    appConfig.CosmosDbTabularSettings,
    appConfig.CosmosDbStandardSettings,
    useTabularPipeline: true,
    TabularIndexName);

// 2. Standard Memory Pipeline
var standardMemory = KernelInitializer.InitializeMemory(
    appConfig.AzureOpenAITextConfig, 
    appConfig.AzureOpenAIEmbeddingConfig, 
    appConfig.CosmosDbTabularSettings,
    appConfig.CosmosDbStandardSettings,
    useTabularPipeline: false,
    StandardIndexName);

// Set up TabularExcelDecoder for the tabular pipeline
var tabularMemoryDb = MemoryHelper.GetMemoryDbFromKernelMemory(tabularMemory);
if (tabularMemoryDb != null)
{
    Console.WriteLine("Successfully obtained IMemoryDb instance for tabular pipeline");
    
    // Find and update TabularExcelDecoder instances in the pipeline
    MemoryHelper.SetMemoryOnTabularExcelDecoders(tabularMemory, tabularMemoryDb);
}
else
{
    Console.WriteLine("Warning: Could not obtain IMemoryDb instance for tabular pipeline");
}

// Create BlobStorageProcessor instances for each pipeline
var tabularFileProcessor = new BlobStorageProcessor(
    tabularMemory, 
    appConfig.BlobStorageSettings, 
    TabularIndexName, 
    LocalDownloadPath);

var standardFileProcessor = new BlobStorageProcessor(
    standardMemory, 
    appConfig.BlobStorageSettings, 
    StandardIndexName, 
    LocalDownloadPath);

// Create query processors for both pipelines
var tabularQueryProcessor = new KernelMemoryQueryProcessor(
    tabularMemory,
    kernel,
    TabularIndexName,
    appConfig.AzureOpenAITextConfig,
    tabularMemoryDb); // Pass the memory DB directly to avoid reflection

var standardQueryProcessor = new KernelMemoryQueryProcessor(
    standardMemory,
    kernel,
    StandardIndexName,
    appConfig.AzureOpenAITextConfig);

// This section demonstrates processing the same files through both pipelines
Console.WriteLine("\n*** Processing files through both pipelines ***");

// Option 1: Process files from blob storage 
// await tabularFileProcessor.ProcessBlobsFromStorageAsync();
// await standardFileProcessor.ProcessBlobsFromStorageAsync();

// Option 2: Process files from local file system
Console.WriteLine("\n*** Processing through Tabular Pipeline ***");
await tabularFileProcessor.ProcessFilesFromLocalDirectoryAsync(fileExtensionPattern: "*.xlsx");

Console.WriteLine("\n*** Processing through Standard Pipeline ***");
await standardFileProcessor.ProcessFilesFromLocalDirectoryAsync(fileExtensionPattern: "*.xlsx");

// This section allows you to query both Kernel Memory indexes
await QueryKernelMemory();

// Query both Kernel Memory pipelines
async Task QueryKernelMemory()
{
    string Question = @"Give me a list of all server names with a Server Purpose of 'Corelight Network monitoring sensor'.
    It is important to return the full list. I expect that there are over 40.";

    Console.WriteLine($"\n*** Searching Tabular Index for: {Question} ***");
    // Pass a result limit of 10 to limit the displayed results
    await tabularQueryProcessor.AskTabularQuestionAsync(Question, resultLimit: 10);
    
    Console.WriteLine($"\n*** Searching Standard Index for: {Question} ***");
    await standardQueryProcessor.AskQuestionAsync(Question);
    
    // In a production system, you might want to:
    // 1. Query both indexes in parallel
    // 2. Compare or combine results
    // 3. Present a unified answer
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

// Helper class for memory-related operations
internal static class MemoryHelper
{
    // Helper method to get the IMemoryDb instance from the memory object
    internal static IMemoryDb? GetMemoryDbFromKernelMemory(IKernelMemory memory)
    {
        try
        {
            // Use reflection to access the internal _memoryDb field
            var memoryType = memory.GetType();
            var memoryDbField = memoryType.GetField("_memoryDb", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (memoryDbField != null)
            {
                var memoryDb = memoryDbField.GetValue(memory);
                
                // Check if it's an IMemoryDb
                if (memoryDb is IMemoryDb memoryDbInstance)
                {
                    return memoryDbInstance;
                }
                else
                {
                    Console.WriteLine($"Memory DB is not IMemoryDb, it's {memoryDb?.GetType().FullName ?? "null"}");
                }
            }
            else
            {
                Console.WriteLine("Could not find _memoryDb field in memory object");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting memory DB: {ex.Message}");
        }
        
        return null;
    }

    // Helper method to find and update TabularExcelDecoder instances in the pipeline
    internal static void SetMemoryOnTabularExcelDecoders(IKernelMemory memory, IMemoryDb memoryDb)
    {
        // Cast the memoryDb to AzureCosmosDbTabularMemory
        var azureCosmosDbTabularMemory = memoryDb as AzureCosmosDbTabularMemory;
        if (azureCosmosDbTabularMemory == null)
        {
            Console.WriteLine("Warning: memoryDb is not an instance of AzureCosmosDbTabularMemory");
            return;
        }
        
        SetMemoryOnTabularExcelDecodersInternal(memory, azureCosmosDbTabularMemory);
    }
    
    // Helper method to find and update TabularExcelDecoder instances in the pipeline
    private static void SetMemoryOnTabularExcelDecodersInternal(IKernelMemory memory, AzureCosmosDbTabularMemory memoryDb)
    {
        try
        {
            // Use reflection to access the internal pipeline handlers
            var memoryType = memory.GetType();
            var orchestratorProperty = memoryType.GetProperty("Orchestrator");
            
            if (orchestratorProperty != null)
            {
                var orchestrator = orchestratorProperty.GetValue(memory);
                var orchestratorType = orchestrator?.GetType();
                
                // Get the handlers dictionary
                var handlersField = orchestratorType?.GetField("_handlers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (handlersField != null)
                {
                    var handlers = handlersField.GetValue(orchestrator) as System.Collections.Generic.Dictionary<string, object>;
                    
                    if (handlers != null)
                    {
                        // Look for TextExtractionHandler which contains the content decoders
                        foreach (var handler in handlers.Values)
                        {
                            var handlerType = handler.GetType();
                            
                            // Check if this is the TextExtractionHandler
                            if (handlerType.Name == "TextExtractionHandler")
                            {
                                // Get the _decoders field
                                var decodersField = handlerType.GetField("_decoders", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                if (decodersField != null)
                                {
                                    var decoders = decodersField.GetValue(handler) as System.Collections.Generic.List<Microsoft.KernelMemory.DataFormats.IContentDecoder>;
                                    
                                    if (decoders != null)
                                    {
                                        // Find TabularExcelDecoder instances
                                        foreach (var decoder in decoders)
                                        {
                                            if (decoder.GetType().FullName?.Contains("TabularExcelDecoder") == true)
                                            {
                                                // Use the WithMemory method to create a new instance with memory set
                                                var withMemoryMethod = decoder.GetType().GetMethod("WithMemory");
                                                if (withMemoryMethod != null)
                                                {
                                                    // Create a new decoder instance with memory set
                                                    var newDecoder = withMemoryMethod.Invoke(decoder, new object[] { memoryDb });
                                                    
                                                    // Replace the old decoder with the new one in the list
                                                    int index = decoders.IndexOf(decoder);
                                                    if (index >= 0)
                                                    {
                                                        decoders[index] = (Microsoft.KernelMemory.DataFormats.IContentDecoder)newDecoder;
                                                        Console.WriteLine("Successfully replaced TabularExcelDecoder instance with new one that has memory set");
                                                    }
                                                    else
                                                    {
                                                        Console.WriteLine("Could not find decoder in the list to replace it");
                                                    }
                                                }
                                                else
                                                {
                                                    Console.WriteLine("Could not find WithMemory method on TabularExcelDecoder");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting memory on TabularExcelDecoder: {ex.Message}");
        }
    }
}
