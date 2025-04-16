// Copyright (c) Microsoft. All rights reserved.

using System;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.DataFormats;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular.DataFormats;
using Microsoft.KernelMemory.MemoryStorage;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Extension methods for adding Azure Cosmos DB Tabular memory connector to Kernel Memory.
/// </summary>
public static class DependencyInjection
{
    // Embedding dimension for vector search (e.g., OpenAI Ada v2 = 1536)
    public const int EmbeddingDimension = 1536;
    /// <summary>
    /// Add Azure Cosmos DB Tabular memory connector to Kernel Memory.
    /// </summary>
    /// <param name="builder">The Kernel Memory builder.</param>
    /// <param name="endpoint">The Azure Cosmos DB endpoint.</param>
    /// <param name="apiKey">The Azure Cosmos DB API key.</param>
    /// <returns>The Kernel Memory builder.</returns>
    public static IKernelMemoryBuilder WithAzureCosmosDbTabularMemory(
        this IKernelMemoryBuilder builder,
        string endpoint,
        string apiKey)
    {
        var config = new AzureCosmosDbTabularConfig
        {
            Endpoint = endpoint,
            APIKey = apiKey
        };

        return builder.WithAzureCosmosDbTabularMemory(config);
    }

    /// <summary>
    /// Add Azure Cosmos DB Tabular memory connector to Kernel Memory.
    /// </summary>
    /// <param name="builder">The Kernel Memory builder.</param>
    /// <param name="endpoint">The Azure Cosmos DB endpoint.</param>
    /// <param name="apiKey">The Azure Cosmos DB API key.</param>
    /// <param name="databaseName">The name of the database to use.</param>
    /// <returns>The Kernel Memory builder.</returns>
    public static IKernelMemoryBuilder WithAzureCosmosDbTabularMemory(
        this IKernelMemoryBuilder builder,
        string endpoint,
        string apiKey,
        string databaseName)
    {
        var config = new AzureCosmosDbTabularConfig
        {
            Endpoint = endpoint,
            APIKey = apiKey,
            DatabaseName = databaseName
        };

        return builder.WithAzureCosmosDbTabularMemory(config);
    }

    /// <summary>
    /// Add Azure Cosmos DB Tabular memory connector to Kernel Memory.
    /// </summary>
    /// <param name="builder">The Kernel Memory builder.</param>
    /// <param name="config">The Azure Cosmos DB Tabular configuration.</param>
    /// <returns>The Kernel Memory builder.</returns>
    public static IKernelMemoryBuilder WithAzureCosmosDbTabularMemory(
        this IKernelMemoryBuilder builder,
        AzureCosmosDbTabularConfig config)
    {
        // Create the Cosmos DB client
        var cosmosClient = new CosmosClient(
            config.Endpoint,
            config.APIKey,
            new CosmosClientOptions
            {
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                }
            });

        // Register the memory DB implementation
        builder.Services.AddSingleton(cosmosClient);
        builder.Services.AddSingleton(config);
        
        // Register as both the interface and the concrete type
        builder.Services.AddSingleton<AzureCosmosDbTabularMemory>();
        builder.Services.AddSingleton<IMemoryDb>(sp => sp.GetRequiredService<AzureCosmosDbTabularMemory>());
        
        // Register the TabularFilterHelper
        builder.Services.AddSingleton<TabularFilterHelper>();

        return builder;
    }

    /// <summary>
    /// Add TabularExcelDecoder to Kernel Memory.
    /// </summary>
    /// <param name="builder">The Kernel Memory builder.</param>
    /// <param name="configure">Optional action to configure the decoder.</param>
    /// <returns>The Kernel Memory builder.</returns>
    public static IKernelMemoryBuilder WithTabularExcelDecoder(
        this IKernelMemoryBuilder builder,
        Action<TabularExcelDecoderConfig>? configure = null)
    {
        var config = new TabularExcelDecoderConfig();
        configure?.Invoke(config);

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<IContentDecoder, TabularExcelDecoder>();
        builder.Services.AddSingleton<IContentDecoder, TabularCsvDecoder>();

        return builder;
    }

    /// <summary>
    /// Add TabularExcelDecoder with dataset name to Kernel Memory.
    /// </summary>
    /// <param name="builder">The Kernel Memory builder.</param>
    /// <param name="datasetName">The dataset name to use for schema extraction.</param>
    /// <param name="configure">Optional action to configure the decoder.</param>
    /// <returns>The Kernel Memory builder.</returns>
    public static IKernelMemoryBuilder WithTabularDecoderAndDataset(
        this IKernelMemoryBuilder builder,
        string datasetName,
        Action<TabularExcelDecoderConfig>? configure = null)
    {
        var config = new TabularExcelDecoderConfig();
        configure?.Invoke(config);

        // Register the Excel decoder factory
        builder.Services.AddSingleton<IContentDecoder>(serviceProvider =>
        {
            // Get logger for diagnostic information
            var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("TabularExcelDecoderRegistration");
            
            // First try to get the concrete type directly
            var memory = serviceProvider.GetService<AzureCosmosDbTabularMemory>();
            
            // If not found, try to get via interface and cast
            if (memory == null)
            {
                var memoryDb = serviceProvider.GetService<IMemoryDb>();
                memory = memoryDb as AzureCosmosDbTabularMemory;
                
                if (memory == null)
                {
                    logger?.LogWarning(
                        "Failed to get AzureCosmosDbTabularMemory instance. Schema extraction will be disabled. " +
                        "IMemoryDb implementation: {MemoryDbType}", 
                        memoryDb?.GetType().FullName ?? "null");
                }
                else
                {
                    logger?.LogInformation(
                        "Successfully obtained AzureCosmosDbTabularMemory instance via IMemoryDb");
                }
            }
            else
            {
                logger?.LogInformation("Successfully obtained AzureCosmosDbTabularMemory instance directly");
            }
            
            // Create the Excel decoder with the memory instance (may be null)
            return TabularExcelDecoder.CreateWithDatasetName(config, datasetName, memory, loggerFactory);
        });

        // Register the CSV decoder factory
        builder.Services.AddSingleton<IContentDecoder>(serviceProvider =>
        {
            // Get logger for diagnostic information
            var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("TabularCsvDecoderRegistration");
            
            // First try to get the concrete type directly
            var memory = serviceProvider.GetService<AzureCosmosDbTabularMemory>();
            
            // If not found, try to get via interface and cast
            if (memory == null)
            {
                var memoryDb = serviceProvider.GetService<IMemoryDb>();
                memory = memoryDb as AzureCosmosDbTabularMemory;
                
                if (memory == null)
                {
                    logger?.LogWarning(
                        "Failed to get AzureCosmosDbTabularMemory instance for CSV decoder. Schema extraction will be disabled. " +
                        "IMemoryDb implementation: {MemoryDbType}", 
                        memoryDb?.GetType().FullName ?? "null");
                    Console.WriteLine("CSV decoder: Memory instance is null! Schema extraction will be disabled.");
                }
                else
                {
                    logger?.LogInformation(
                        "Successfully obtained AzureCosmosDbTabularMemory instance for CSV decoder via IMemoryDb");
                    Console.WriteLine($"CSV decoder: Successfully obtained memory instance of type {memory.GetType().FullName} via IMemoryDb");
                }
            }
            else
            {
                logger?.LogInformation("Successfully obtained AzureCosmosDbTabularMemory instance for CSV decoder directly");
                Console.WriteLine($"CSV decoder: Successfully obtained memory instance of type {memory.GetType().FullName} directly");
            }
            
            Console.WriteLine($"CSV decoder: Creating with dataset name: {datasetName}");
            
            // Create the CSV decoder with the memory instance
            return TabularCsvDecoder.CreateWithDatasetName(config, datasetName, memory, loggerFactory);
        });

        return builder;
    }
}
