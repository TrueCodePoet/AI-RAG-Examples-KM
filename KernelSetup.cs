using Microsoft.Extensions.Configuration;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.AI.AzureOpenAI;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDb; // Added for WithAzureCosmosDbMemory extension method
using Microsoft.KernelMemory.MemoryStorage; // For IMemoryDb
using Microsoft.SemanticKernel;
using Azure.Search.Documents; // Added for SearchClientConfig
using Microsoft.Azure.Cosmos; // For CosmosClient

namespace AI_RAG_Examples_KM // Assuming this is the namespace based on project name
{
    // Configuration class for Cosmos DB settings (used for both standard and tabular)
    public class CosmosDbSettings
    {
        public string Endpoint { get; set; } = string.Empty;
        public string APIKey { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = "memory"; // Default value

        // FuzzyMatch config for tabular memory
        public FuzzyMatchSettings FuzzyMatch { get; set; } = new FuzzyMatchSettings();
    }

    public class FuzzyMatchSettings
    {
        public bool Enabled { get; set; } = false;
        public bool CaseInsensitive { get; set; } = true;
        public int MinimumLength { get; set; } = 2;
        public string Operator { get; set; } = "CONTAINS";
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
        CosmosDbSettings CosmosDbTabularSettings,
        CosmosDbSettings CosmosDbStandardSettings,
        BlobStorageSettings BlobStorageSettings
    );

    public static class KernelInitializer
    {
        public static AppConfiguration LoadConfiguration()
        {
            var azureOpenAITextConfig = new AzureOpenAIConfig();
            var azureOpenAIEmbeddingConfig = new AzureOpenAIConfig();
            var azureAISearchConfig = new AzureAISearchConfig();
            var cosmosDbTabularSettings = new CosmosDbSettings();
            var cosmosDbStandardSettings = new CosmosDbSettings();
            var blobStorageSettings = new BlobStorageSettings();

            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .Build();

            configuration.Bind("KernelMemory:Services:AzureOpenAIText", azureOpenAITextConfig);
            configuration.Bind("KernelMemory:Services:AzureOpenAIEmbedding", azureOpenAIEmbeddingConfig);
            configuration.Bind("KernelMemory:Services:AzureAISearch", azureAISearchConfig);
            configuration.Bind("KernelMemory:Services:AzureCosmosDbTabular", cosmosDbTabularSettings);
            configuration.Bind("KernelMemory:Services:AzureCosmosDb", cosmosDbStandardSettings);
            configuration.Bind("AzureBlobStorage", blobStorageSettings);

            // It's important to set Auth type after binding
            azureAISearchConfig.Auth = AzureAISearchConfig.AuthTypes.APIKey;
            azureOpenAIEmbeddingConfig.Auth = AzureOpenAIConfig.AuthTypes.APIKey;
            azureOpenAITextConfig.Auth = AzureOpenAIConfig.AuthTypes.APIKey;


            return new AppConfiguration(
                azureOpenAITextConfig,
                azureOpenAIEmbeddingConfig,
                azureAISearchConfig, // Pass this even if not used directly in memory init below
                cosmosDbTabularSettings,
                cosmosDbStandardSettings,
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

        /// <summary>
        /// Initializes a Kernel Memory instance with specific pipeline configuration.
        /// </summary>
        /// <param name="textConfig">Azure OpenAI text configuration</param>
        /// <param name="embeddingConfig">Azure OpenAI embedding configuration</param>
        /// <param name="cosmosTabularConfig">Cosmos DB Tabular settings</param>
        /// <param name="cosmosStandardConfig">Cosmos DB Standard settings</param>
        /// <param name="useTabularPipeline">Whether to use tabular-specific pipeline configuration</param>
        /// <param name="indexName">The index name to use</param>
        /// <returns>Configured IKernelMemory instance</returns>

        public static (IKernelMemory Memory, IMemoryDb? MemoryDb) InitializeMemory(
            AzureOpenAIConfig textConfig,
            AzureOpenAIConfig embeddingConfig,
            CosmosDbSettings cosmosTabularConfig,
            CosmosDbSettings cosmosStandardConfig,
            bool useTabularPipeline,
            string indexName)
        {
            Console.WriteLine($"Initializing {(useTabularPipeline ? "tabular" : "standard")} memory pipeline for index: {indexName}");

            var builder = new KernelMemoryBuilder()
                .WithAzureOpenAITextGeneration(textConfig)
                .WithAzureOpenAITextEmbeddingGeneration(embeddingConfig)
                .WithSearchClientConfig(new SearchClientConfig { MaxMatchesCount = 5, Temperature = 0.4, TopP = .95 });

            // Use AzureCosmosDbTabular for both pipelines, but with different configurations
            builder = builder
                .WithAzureCosmosDbTabularMemory(
                    endpoint: cosmosTabularConfig.Endpoint,
                    apiKey: cosmosTabularConfig.APIKey);

            if (useTabularPipeline)
            {
                // Tabular pipeline - specialized for structured data
                builder = builder
                    .WithTabularDecoderAndDataset(
                        datasetName: indexName,
                        configure: config =>
                        {
                            // Configure Excel parsing options for tabular data
                            config.UseFirstRowAsHeader = true;
                            config.PreserveDataTypes = true;
                            config.ProcessAllWorksheets = true;
                        });
            }
            else
            {
                // Standard pipeline - use custom partitioning for text data
                builder = builder
                    .WithCustomTextPartitioningOptions(
                        new Microsoft.KernelMemory.Configuration.TextPartitioningOptions
                        {
                            MaxTokensPerParagraph = 1000,
                            OverlappingTokens = 100
                        });
            }

            var memory = builder.Build<MemoryServerless>(
                new KernelMemoryBuilderBuildOptions { AllowMixingVolatileAndPersistentData = true });

            Console.WriteLine($"* Registering pipeline handlers for {(useTabularPipeline ? "tabular" : "standard")} pipeline...");

            // Common handlers for both pipelines
            memory.Orchestrator.AddHandler<Microsoft.KernelMemory.Handlers.TextExtractionHandler>("extract_text");
            memory.Orchestrator.AddHandler<Microsoft.KernelMemory.Handlers.GenerateEmbeddingsHandler>("generate_embeddings");
            memory.Orchestrator.AddHandler<Microsoft.KernelMemory.Handlers.SummarizationHandler>("summarize_data");
            memory.Orchestrator.AddHandler<Microsoft.KernelMemory.Handlers.SaveRecordsHandler>("save_current_records");

            // Only add TextPartitioningHandler for standard pipeline
            if (!useTabularPipeline)
            {
                memory.Orchestrator.AddHandler<Microsoft.KernelMemory.Handlers.TextPartitioningHandler>("split_text_in_partitions");
                Console.WriteLine("* Added TextPartitioningHandler to standard pipeline");
            }
            else
            {
                Console.WriteLine("* Skipping TextPartitioningHandler for tabular pipeline");
            }

            // Extract the IMemoryDb instance using reflection (temporary, until DI is fully supported)
            IMemoryDb? memoryDb = null;
            try
            {
                var memoryType = memory.GetType();
                var memoryDbField = memoryType.GetField("_memoryDb", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (memoryDbField != null)
                {
                    var dbValue = memoryDbField.GetValue(memory);
                    if (dbValue is IMemoryDb dbInstance)
                    {
                        memoryDb = dbInstance;
                        Console.WriteLine($"Successfully extracted IMemoryDb of type: {memoryDb.GetType().FullName}");
                    }
                    else
                    {
                        Console.WriteLine("WARNING: _memoryDb field exists but is not IMemoryDb");
                    }
                }
                else
                {
                    Console.WriteLine("WARNING: Could not find _memoryDb field in MemoryServerless instance");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR extracting IMemoryDb: {ex.Message}");
            }

            return (memory, memoryDb);
        }
    }
}
