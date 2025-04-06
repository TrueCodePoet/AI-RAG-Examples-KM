using Microsoft.Extensions.Configuration;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.AI.AzureOpenAI;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;
using Microsoft.SemanticKernel;
using Azure.Search.Documents; // Added for SearchClientConfig

namespace AI_RAG_Examples_KM // Assuming this is the namespace based on project name
{
    // Configuration class for Cosmos DB Tabular settings
    public class CosmosDbSettings
    {
        public string Endpoint { get; set; } = string.Empty;
        public string APIKey { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = "memory"; // Default value
    }

    // Configuration class for Blob Storage
    public class BlobStorageSettings
    {
        public string BlobContainerUrl { get; set; } = string.Empty;
        public string SasToken { get; set; } = string.Empty;
    }

    // Helper record to hold all loaded configurations
    public record AppConfiguration(
        AzureOpenAIConfig AzureOpenAITextConfig,
        AzureOpenAIConfig AzureOpenAIEmbeddingConfig,
        AzureAISearchConfig AzureAISearchConfig,
        CosmosDbSettings CosmosDbSettings,
        BlobStorageSettings BlobStorageSettings
    );

    public static class KernelInitializer
    {
        public static AppConfiguration LoadConfiguration()
        {
            var azureOpenAITextConfig = new AzureOpenAIConfig();
            var azureOpenAIEmbeddingConfig = new AzureOpenAIConfig();
            var azureAISearchConfig = new AzureAISearchConfig();
            var cosmosDbSettings = new CosmosDbSettings();
            var blobStorageSettings = new BlobStorageSettings();

            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .Build();

            configuration.Bind("KernelMemory:Services:AzureOpenAIText", azureOpenAITextConfig);
            configuration.Bind("KernelMemory:Services:AzureOpenAIEmbedding", azureOpenAIEmbeddingConfig);
            configuration.Bind("KernelMemory:Services:AzureAISearch", azureAISearchConfig);
            configuration.Bind("KernelMemory:Services:AzureCosmosDbTabular", cosmosDbSettings);
            configuration.Bind("AzureBlobStorage", blobStorageSettings);

            // It's important to set Auth type after binding
            azureAISearchConfig.Auth = AzureAISearchConfig.AuthTypes.APIKey;
            azureOpenAIEmbeddingConfig.Auth = AzureOpenAIConfig.AuthTypes.APIKey;
            azureOpenAITextConfig.Auth = AzureOpenAIConfig.AuthTypes.APIKey;


            return new AppConfiguration(
                azureOpenAITextConfig,
                azureOpenAIEmbeddingConfig,
                azureAISearchConfig, // Pass this even if not used directly in memory init below
                cosmosDbSettings,
                blobStorageSettings
            );
        }

        public static Kernel InitializeKernel(AzureOpenAIConfig textConfig)
        {
            var kernel = Kernel.CreateBuilder()
                .AddAzureOpenAIChatCompletion(textConfig.Deployment, textConfig.Endpoint, textConfig.APIKey)
                .Build();
            return kernel;
        }

        public static IKernelMemory InitializeMemory(AzureOpenAIConfig textConfig, AzureOpenAIConfig embeddingConfig, CosmosDbSettings cosmosConfig)
        {
             var memory = new KernelMemoryBuilder()
                .WithAzureOpenAITextGeneration(textConfig)
                .WithAzureOpenAITextEmbeddingGeneration(embeddingConfig)
                //.WithAzureAISearchMemoryDb(azureAISearchConfig) // Keep commented out as per original Program.cs
                //.WithAzureCosmosDbMemory( // Keep commented out as per original Program.cs
                .WithAzureCosmosDbTabularMemory(
                    endpoint: cosmosConfig.Endpoint,
                    apiKey: cosmosConfig.APIKey)
                .WithSearchClientConfig(new SearchClientConfig { MaxMatchesCount = 5, Temperature = 0.4, TopP = .95, })
                //.WithCustomTextPartitioningOptions(new Microsoft.KernelMemory.Configuration.TextPartitioningOptions { // Keep commented out
                //    MaxTokensPerParagraph = 1000,
                //    OverlappingTokens = 100
                //})
                .WithTabularExcelDecoder(config =>
                {
                    // Configure Excel parsing options
                    config.UseFirstRowAsHeader = true;
                    config.PreserveDataTypes = true;
                    config.ProcessAllWorksheets = true;
                    // Optionally specify which worksheets to process
                    // config.WorksheetsToProcess = new List<string> { "Sheet1", "Data" };
                })
                //.With(new MsExcelDecoderConfig {BlankCellValue = "NO-VALUE"}) // Keep commented out
                //.With(new MsPowerPointDecoderConfig {SkipHiddenSlides = true, WithSlideNumber = true}) // Keep commented out
                .Build<MemoryServerless>(new KernelMemoryBuilderBuildOptions { AllowMixingVolatileAndPersistentData = true });

            Console.WriteLine("* Registering pipeline handlers..."); // Keep console output here for now

            memory.Orchestrator.AddHandler<Microsoft.KernelMemory.Handlers.TextExtractionHandler>("extract_text");
            memory.Orchestrator.AddHandler<Microsoft.KernelMemory.Handlers.TextPartitioningHandler>("split_text_in_partitions");
            memory.Orchestrator.AddHandler<Microsoft.KernelMemory.Handlers.GenerateEmbeddingsHandler>("generate_embeddings");
            memory.Orchestrator.AddHandler<Microsoft.KernelMemory.Handlers.SummarizationHandler>("summarize_data");
            memory.Orchestrator.AddHandler<Microsoft.KernelMemory.Handlers.SaveRecordsHandler>("save_current_records");

            return memory;
        }
    }
}
