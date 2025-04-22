using Microsoft.KernelMemory;
using Microsoft.SemanticKernel;
using Microsoft.KernelMemory.AI.AzureOpenAI; // For AzureOpenAIConfig
using Microsoft.KernelMemory.MemoryDb; // For IMemoryDb
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular; // For TabularDataSchema
using Microsoft.SemanticKernel.Connectors.OpenAI; // For MemoryFilter
using Microsoft.Extensions.DependencyInjection;
using Microsoft.KernelMemory.MemoryStorage;

namespace AI_RAG_Examples_KM
{
    public partial class KernelMemoryQueryProcessor
    {
        private readonly IKernelMemory _memory;
        private readonly Kernel _kernel;
        private readonly string _indexName;
        private readonly AzureOpenAIConfig _azureOpenAITextConfig;
        private readonly IMemoryDb? _tabularMemoryDb;
        private readonly bool _skipDatasetIdentification;
        private readonly string _fuzzyMatchOperator;

        public KernelMemoryQueryProcessor(
            IKernelMemory memory,
            Kernel kernel,
            string indexName,
            AzureOpenAIConfig azureOpenAITextConfig,
            IMemoryDb? tabularMemoryDb = null,
            string fuzzyMatchOperator = "CONTAINS")
        {
            _memory = memory;
            _kernel = kernel;
            _indexName = indexName;
            _azureOpenAITextConfig = azureOpenAITextConfig;
            _tabularMemoryDb = tabularMemoryDb;
            _fuzzyMatchOperator = fuzzyMatchOperator;

            // If we don't have a valid tabularMemoryDb instance, we'll skip the dataset identification step
            _skipDatasetIdentification = _tabularMemoryDb == null;
            if (_skipDatasetIdentification)
            {
                Console.WriteLine("WARNING: No valid tabularMemoryDb instance provided - dataset identification will be skipped");
                Console.WriteLine("This is expected when reflection cannot find the database instance in MemoryServerless implementation");
                Console.WriteLine("Will proceed without schema-based validation. Use direct parameter passing when possible.");
            }
        }
    }
}
