// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Reflection;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryStorage;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Helper class for memory-related operations.
/// </summary>
internal static class MemoryHelper
{
    /// <summary>
    /// Helper method to get the IMemoryDb instance from the memory object.
    /// </summary>
    internal static IMemoryDb? GetMemoryDbFromKernelMemory(IKernelMemory memory)
    {
        try
        {
            Console.WriteLine("=== DIAGNOSTIC: Getting IMemoryDb from IKernelMemory ===");
            Console.WriteLine($"IKernelMemory implementation type: {memory.GetType().FullName}");
            
            // Use reflection to access the internal _memoryDb field
            var memoryType = memory.GetType();
            
            // List all fields to see what's available
            var allFields = memoryType.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Console.WriteLine($"Found {allFields.Length} private instance fields in {memoryType.Name}:");
            foreach (var field in allFields)
            {
                Console.WriteLine($"  - {field.Name} ({field.FieldType.Name})");
            }
            
            var memoryDbField = memoryType.GetField("_memoryDb", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (memoryDbField != null)
            {
                Console.WriteLine($"Found _memoryDb field of type {memoryDbField.FieldType.FullName}");
                var memoryDb = memoryDbField.GetValue(memory);
                
                if (memoryDb == null)
                {
                    Console.WriteLine("WARNING: _memoryDb field exists but its value is null");
                    return null;
                }
                
                Console.WriteLine($"_memoryDb instance is of type: {memoryDb.GetType().FullName}");
                
                // Check if it's an IMemoryDb
                if (memoryDb is IMemoryDb memoryDbInstance)
                {
                    Console.WriteLine("Successfully cast to IMemoryDb");
                    
                    // Check if it's the specific AzureCosmosDbTabularMemory type we need
                    if (memoryDbInstance is AzureCosmosDbTabularMemory)
                    {
                        Console.WriteLine("SUCCESS: _memoryDb is an AzureCosmosDbTabularMemory instance");
                    }
                    else
                    {
                        Console.WriteLine($"WARNING: _memoryDb is IMemoryDb but not AzureCosmosDbTabularMemory, it's {memoryDbInstance.GetType().FullName}");
                    }
                    
                    return memoryDbInstance;
                }
                else
                {
                    Console.WriteLine($"ERROR: Memory DB is not IMemoryDb, it's {memoryDb.GetType().FullName}");
                }
            }
            else
            {
                Console.WriteLine("ERROR: Could not find _memoryDb field in memory object");
                
                // Try to look for alternative field names that might contain the memory DB
                var potentialFields = allFields.Where(f => 
                    f.Name.Contains("memory", StringComparison.OrdinalIgnoreCase) || 
                    f.Name.Contains("db", StringComparison.OrdinalIgnoreCase) ||
                    f.FieldType.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
                    f.FieldType.Name.Contains("Db", StringComparison.OrdinalIgnoreCase)).ToList();
                
                if (potentialFields.Any())
                {
                    Console.WriteLine("Potential alternative fields found:");
                    foreach (var field in potentialFields)
                    {
                        var value = field.GetValue(memory);
                        Console.WriteLine($"  - {field.Name} ({field.FieldType.Name}): {(value == null ? "null" : value.GetType().FullName)}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR getting memory DB: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
        
        Console.WriteLine("=== END DIAGNOSTIC ===");
        return null;
    }

    /// <summary>
    /// Helper method to find and update TabularExcelDecoder instances in the pipeline
    /// </summary>
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
    
    /// <summary>
    /// Helper method to find and update TabularExcelDecoder instances in the pipeline
    /// </summary>
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
