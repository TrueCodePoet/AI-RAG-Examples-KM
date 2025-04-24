using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryStorage; // For TagCollection
using Azure.Storage.Blobs;
using System.Text.RegularExpressions;

namespace AI_RAG_Examples_KM
{
    public class BlobStorageProcessor
    {
        private readonly IKernelMemory _memory;
        private readonly BlobStorageSettings _blobSettings;
        private readonly string _indexName;
        private readonly string _localDownloadPath;
        private readonly IMemoryDb? _memoryDb; // Optional for DI
        private readonly CustomTabularIngestion? _customTabularIngestion;

        public BlobStorageProcessor(
            IKernelMemory memory, 
            BlobStorageSettings blobSettings, 
            string indexName,
            string localDownloadPath,
            IMemoryDb? memoryDb = null,
            CustomTabularIngestion? customTabularIngestion = null)
        {
            _memory = memory;
            _blobSettings = blobSettings;
            _indexName = indexName;
            _localDownloadPath = localDownloadPath;
            _memoryDb = memoryDb;
            _customTabularIngestion = customTabularIngestion;
        }

        // Method 1: Process blobs from Azure Blob Storage
        public async Task ProcessBlobsFromStorageAsync()
        {
            // Use settings from the configuration object
            BlobContainerClient containerClient = new BlobContainerClient(
                        blobContainerUri: new Uri(_blobSettings.BlobContainerUrl));
            
            if (!Directory.Exists(_localDownloadPath))
            {
                try
                {
                    Directory.CreateDirectory(_localDownloadPath);
                    Console.WriteLine($"Created local directory: {_localDownloadPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to create local directory '{_localDownloadPath}': {ex.Message}");
                    return;
                }
            }
            
            await foreach (Azure.Storage.Blobs.Models.BlobItem blob in containerClient.GetBlobsAsync())
            {
                string blobName = blob.Name;
                Console.WriteLine($"Processing blob: {blobName}");
         
                BlobClient blobClient = containerClient.GetBlobClient(blobName);
         
                // Download blob content  
                Azure.Storage.Blobs.Models.BlobDownloadResult downloadResult;
                try
                {
                    downloadResult = await blobClient.DownloadContentAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to download blob {blobName}: {ex.Message}");
                    continue; // Skip to the next blob  
                }
         
                string content = downloadResult.Content.ToString(); // Assuming the blob content is text  
         
                // Determine blob type (text or binary) based on Content-Type or extension  
                string contentType = downloadResult.Details.ContentType;
                bool isText = contentType != null && contentType.StartsWith("text", StringComparison.OrdinalIgnoreCase);
         
                // Sanitize blob name for local file system  
                string sanitizedBlobName = SanitizeFileName(blobName);
         
                // Define the local file path  
                string localFilePath = Path.Combine(_localDownloadPath, sanitizedBlobName);
         
                // Ensure the local directory exists for nested blobs  
                string localDir = Path.GetDirectoryName(localFilePath);
                if (!string.IsNullOrEmpty(localDir) && !Directory.Exists(localDir))
                {
                    try
                    {
                        Directory.CreateDirectory(localDir);
                        Console.WriteLine($"Created local sub-directory: {localDir}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to create directory '{localDir}': {ex.Message}");
                        continue; // Skip to the next blob  
                    }
                }
         
                // Save the blob to the local file system  
                try
                {
                    if (isText)
                    {
                        // Save as text  
                        string value = downloadResult.Content.ToString(); // Assuming UTF-8 encoding  
                        await File.WriteAllTextAsync(localFilePath, value);
                        Console.WriteLine($"Saved text blob to '{localFilePath}'");
                    }
                    else
                    {
                        // Save as binary  
                        byte[] data = downloadResult.Content.ToArray();
                        await File.WriteAllBytesAsync(localFilePath, data);
                        Console.WriteLine($"Saved binary blob to '{localFilePath}'");
                    }
         
                    Console.WriteLine($"Uploading memory file : {localFilePath}");
                    var docId = await _memory.ImportDocumentAsync(localFilePath,
                    index: _indexName,
                    steps: ["extract_text", "split_text_in_partitions", "generate_embeddings", "save_records", "summarize", "split_text_in_partitions", "generate_embeddings", "save_records"]);
                    //s_toDelete.Add(docId);
                    Console.WriteLine($"memory added- Document Id: {docId} : {localFilePath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to save blob '{blobName}' to disk: {ex.Message}");
                    continue; // Skip to the next blob  
                }
            }
         
            Console.WriteLine("All blobs have been processed.");
        }

        // Method 2: Process files from local directory
        public async Task ProcessFilesFromLocalDirectoryAsync(string fileExtensionPattern = "*")
        {
            if (!Directory.Exists(_localDownloadPath))
            {
                Console.WriteLine($"Local directory not found: {_localDownloadPath}");
                return;
            }
         
            Console.WriteLine($"Processing files from local directory: {_localDownloadPath}");
         
            // Get all files in the directory and subdirectories
            string[] files = Directory.GetFiles(_localDownloadPath, fileExtensionPattern, SearchOption.AllDirectories);
            Console.WriteLine($"========== STARTING LOCAL FILE IMPORT SUMMARY ==========");
            Console.WriteLine($"Found {files.Length} '{fileExtensionPattern}' files to process");
            
            // Counters for detailed tracking
            int filesAttempted = 0;
            int filesSucceeded = 0;
            int filesFailed = 0;
            Dictionary<string, List<string>> filesByExtension = new();
            
            foreach (string localFilePath in files)
            {
                string relativePath = Path.GetRelativePath(_localDownloadPath, localFilePath);
                string extension = Path.GetExtension(localFilePath).ToLowerInvariant();
                
                // Track files by extension
                if (!filesByExtension.ContainsKey(extension))
                {
                    filesByExtension[extension] = new List<string>();
                }
                filesByExtension[extension].Add(relativePath);
                
                filesAttempted++;
                Console.WriteLine($"[{filesAttempted}/{files.Length}] Processing: {relativePath}");

                try
                {
                    // Use custom ingestion if provided and file is tabular
                    if (_customTabularIngestion != null &&
                        (extension == ".xlsx" || extension == ".csv"))
                    {
                        Console.WriteLine($"[CustomIngestion] Using custom ingestion for: {localFilePath}");
                        await _customTabularIngestion.ImportTabularDocumentCustomAsync(localFilePath, _indexName);
                        filesSucceeded++;
                        // Note: We might want to decide if standard ingestion should also run
                        // For now, we skip standard if custom runs
                        continue; 
                    }

                    // Standard ingestion path
                    Console.WriteLine($"Uploading memory file: {localFilePath}");
                    
                    // Create a stopwatch to track import time
                    var stopwatch = new System.Diagnostics.Stopwatch();
                    stopwatch.Start();
                    
                    // Create a CancellationToken with IndexName property set
                    var cts = new System.Threading.CancellationTokenSource();
                    var token = cts.Token;
                    // Use reflection to set the IndexName property if supported
                    var indexNameProp = token.GetType().GetProperty("IndexName");
                    if (indexNameProp != null && indexNameProp.CanWrite)
                    {
                        indexNameProp.SetValue(token, _indexName);
                    }

                    var docId = await _memory.ImportDocumentAsync(localFilePath,
                        index: _indexName,
                        tags: new TagCollection() { { "FilePath", localFilePath } },
                        cancellationToken: token
                        //,steps: ["extract_text", "split_text_in_partitions", "generate_embeddings", "save_current_records",
                        //        "summarize_data", "split_text_in_partitions", "generate_embeddings", "save_current_records"]
                        );
                    
                    stopwatch.Stop();
                    filesSucceeded++;
                    Console.WriteLine($"SUCCESS: Memory added - Document Id: {docId} : {localFilePath} (Took {stopwatch.ElapsedMilliseconds}ms)");
                }
                catch (Exception ex)
                {
                    filesFailed++;
                    Console.WriteLine($"FAILED: Could not process '{localFilePath}': {ex.Message}");
                    continue; // Skip to the next file
                }
            }
            
            // Print summary
            Console.WriteLine($"========== LOCAL FILE IMPORT SUMMARY ==========");
            Console.WriteLine($"Files found: {files.Length}");
            Console.WriteLine($"Files attempted: {filesAttempted}");
            Console.WriteLine($"Files succeeded: {filesSucceeded}");
            Console.WriteLine($"Files failed: {filesFailed}");
            
            // Print breakdown by extension
            Console.WriteLine($"Breakdown by extension:");
            foreach (var kvp in filesByExtension)
            {
                Console.WriteLine($"  {kvp.Key}: {kvp.Value.Count} files");
                // Optionally list the files with this extension
                // if (kvp.Value.Count < 10) { // Only list if not too many
                //     foreach (var file in kvp.Value) {
                //         Console.WriteLine($"    - {file}");
                //     }
                // }
            }
            
            Console.WriteLine("All local files have been processed.");
        }

        // Helper method used by ProcessBlobsFromStorageAsync
        private static string SanitizeFileName(string fileName)
        {
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string invalidReStr = $"[{invalidChars}]+";
            return Regex.Replace(fileName, invalidReStr, "_");
        }
    }
}
