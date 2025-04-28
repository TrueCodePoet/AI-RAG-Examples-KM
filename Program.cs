﻿using Microsoft.KernelMemory;
using Microsoft.SemanticKernel;
using System.Diagnostics;
using Azure.Storage.Blobs;
using System.Text.RegularExpressions;
using Microsoft.KernelMemory.MemoryStorage; // For TagCollection
using AI_RAG_Examples_KM; // Use the namespace defined in KernelSetup.cs
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular; // For MemoryHelper
using Microsoft.KernelMemory.AI.AzureOpenAI; // For AzureOpenAIConfig
using Microsoft.SemanticKernel.Connectors.AzureOpenAI; // For AzureOpenAIPromptExecutionSettings
using System.Text.Json; // For JsonSerializer
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;
using Microsoft.SemanticKernel.Connectors.OpenAI; // For MemoryFilter
using Microsoft.KernelMemory.AI; // Added for ITextEmbeddingGenerator

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
var (tabularMemory, tabularMemoryDb) = KernelInitializer.InitializeMemory(
    appConfig.AzureOpenAITextConfig,
    appConfig.AzureOpenAIEmbeddingConfig,
    appConfig.CosmosDbTabularSettings,
    appConfig.CosmosDbStandardSettings,
    useTabularPipeline: true,
    TabularIndexName);

// 2. Standard Memory Pipeline
var (standardMemory, standardMemoryDb) = KernelInitializer.InitializeMemory(
    appConfig.AzureOpenAITextConfig,
    appConfig.AzureOpenAIEmbeddingConfig,
    appConfig.CosmosDbTabularSettings,
    appConfig.CosmosDbStandardSettings,
    useTabularPipeline: false,
    StandardIndexName);

// Detailed verification of tabularMemoryDb before using it
Console.WriteLine("\n=== VERIFICATION OF tabularMemoryDb INSTANCE ===");
if (tabularMemoryDb != null)
{
    Console.WriteLine($"• tabularMemoryDb Type: {tabularMemoryDb.GetType().FullName}");
    Console.WriteLine($"• Is AzureCosmosDbTabularMemory: {tabularMemoryDb.GetType().FullName?.Contains("AzureCosmosDbTabularMemory") == true}");
    Console.WriteLine($"• Implements IMemoryDb: {tabularMemoryDb is IMemoryDb}");
    var isAzureCosmosDbTabularMemory = tabularMemoryDb is AzureCosmosDbTabularMemory;
    Console.WriteLine($"• Direct cast to AzureCosmosDbTabularMemory: {isAzureCosmosDbTabularMemory}");
    var containsTabularIgnoreCase = tabularMemoryDb.GetType().FullName?.IndexOf("TabularMemory", StringComparison.OrdinalIgnoreCase) >= 0;
    Console.WriteLine($"• Name contains 'TabularMemory' (case insensitive): {containsTabularIgnoreCase}");
    Console.WriteLine("Successfully obtained IMemoryDb instance for tabular pipeline");
    // If you still need to update TabularExcelDecoder instances, do so here (optional)
    // MemoryHelper.SetMemoryOnTabularExcelDecoders(tabularMemory, tabularMemoryDb);
}
else
{
    Console.WriteLine("Warning: Could not obtain IMemoryDb instance for tabular pipeline");
}
Console.WriteLine("=== END VERIFICATION ===\n");

// Create BlobStorageProcessor instances for each pipeline
var tabularFileProcessor = new BlobStorageProcessor(
    tabularMemory,
    appConfig.BlobStorageSettings,
    TabularIndexName,
    LocalDownloadPath,
    tabularMemoryDb);


// --- Custom Excel Ingestion Pipeline Example ---
if (tabularMemoryDb is AzureCosmosDbTabularMemory tabularDb)
{
    // Extract dependencies from tabularDb using reflection
    var cosmosClient = (Microsoft.Azure.Cosmos.CosmosClient?)typeof(AzureCosmosDbTabularMemory)
        .GetField("_cosmosClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?.GetValue(tabularDb);
    var logger = (Microsoft.Extensions.Logging.ILogger?)typeof(AzureCosmosDbTabularMemory)
        .GetField("_logger", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?.GetValue(tabularDb);
    var embeddingGenerator = (ITextEmbeddingGenerator?)typeof(AzureCosmosDbTabularMemory)
        .GetField("_embeddingGenerator", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?.GetValue(tabularDb) as Microsoft.KernelMemory.AI.ITextEmbeddingGenerator; // Correct cast

    if (cosmosClient != null && logger != null && embeddingGenerator != null)
    {
        var customIngestion = new CustomTabularIngestion(
            memoryDb: tabularDb,
            cosmosClient: cosmosClient,
            embeddingGenerator: embeddingGenerator,
            logger: logger,
            databaseName: appConfig.CosmosDbTabularSettings.DatabaseName,
            indexName: TabularIndexName
        );
        customIngestion.BatchSize = 50; // Set the batch size for ingestion
        
        // Create a BlobStorageProcessor that uses the custom ingestion pipeline
        var customTabularFileProcessor = new BlobStorageProcessor(
            tabularMemory,
            appConfig.BlobStorageSettings,
            TabularIndexName,
            LocalDownloadPath,
            tabularMemoryDb,
            customTabularIngestion: customIngestion
        );

        // Example: Ingest Excel files using the custom pipeline
        // await customTabularFileProcessor.ProcessFilesFromLocalDirectoryAsync(fileExtensionPattern: "*.xlsx");

        // --- SharePoint Ingestion Section ---
        // Example SharePoint config values (replace with your actual config or load from appsettings.json)
        string sharepointTenantId = "YOUR_TENANT_ID";
        string sharepointClientId = "YOUR_CLIENT_ID";
        string sharepointClientSecret = "YOUR_CLIENT_SECRET";
        string sharepointSiteId = "YOUR_SITE_ID";
        string sharepointDriveId = "YOUR_DRIVE_ID";
        string sharepointDownloadPath = @"D:\Temp\SHAREPOINT";

        var sharePointProcessor = new SharePointFileProcessor(
            sharepointTenantId,
            sharepointClientId,
            sharepointClientSecret,
            sharepointSiteId,
            sharepointDriveId,
            sharepointDownloadPath,
            customIngestion
        );

        // Example: Ingest all Excel files from SharePoint
        // await sharePointProcessor.ProcessFilesFromSharePointAsync(fileExtensionPattern: "*.xlsx");
        // Example: Ingest all CSV files from SharePoint
        // await sharePointProcessor.ProcessFilesFromSharePointAsync(fileExtensionPattern: "*.csv");
    }
    else
    {
        Console.WriteLine("CustomTabularIngestion: Could not resolve all dependencies from tabularMemoryDb.");
    }
}

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
    tabularMemoryDb,
    appConfig.CosmosDbTabularSettings.FuzzyMatch.Operator); // Pass the fuzzy match operator for prompt construction

var standardQueryProcessor = new KernelMemoryQueryProcessor(
    standardMemory,
    kernel,
    StandardIndexName,
    appConfig.AzureOpenAITextConfig);


ClearCoreSdkListeners(); // Clear listeners to avoid excessive logging
Console.WriteLine("Cleared Core SDK listeners.");

// This section demonstrates processing the same files through both pipelines
Console.WriteLine("\n*** Processing files through both pipelines ***");

// Option 1: Process files from blob storage 
// await tabularFileProcessor.ProcessBlobsFromStorageAsync();
// await standardFileProcessor.ProcessBlobsFromStorageAsync();

// Option 2: Process files from local file system
Console.WriteLine("\n*** Processing through Tabular Pipeline ***");
await tabularFileProcessor.ProcessFilesFromLocalDirectoryAsync(fileExtensionPattern: "*.xlsx");

Console.WriteLine("\n*** Processing through Tabular Pipeline ***");
await tabularFileProcessor.ProcessFilesFromLocalDirectoryAsync(fileExtensionPattern: "*.csv");


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
    await tabularQueryProcessor.AskTabularQuestionAsync(Question, resultLimit: 100);
    
    Console.WriteLine($"\n*** Searching Standard Index for: {Question} ***");
    await standardQueryProcessor.AskQuestionAsync(Question);
    
    // In a production system, you might want to:
    // 1. Query both indexes in parallel
    // 2. Compare or combine results
    // 3. Present a unified answer
}

 static void ClearCoreSdkListeners() 
 { 
     Type defaultTrace = Type.GetType("Microsoft.Azure.Cosmos.Core.Trace.DefaultTrace,Microsoft.Azure.Cosmos.Direct"); 
     TraceSource traceSource = (TraceSource)defaultTrace.GetProperty("TraceSource").GetValue(null); 
     traceSource.Switch.Level = SourceLevels.All; 
     traceSource.Listeners.Clear(); 
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
