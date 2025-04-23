// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.DataFormats;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.Pipeline;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular.DataFormats;

/// <summary>
/// Decoder for CSV files that preserves tabular structure, modeled after TabularExcelDecoder.
/// </summary>
internal sealed class TabularCsvDecoder : IContentDecoder
{
    private readonly TabularExcelDecoderConfig _config;
    private readonly ILogger<TabularCsvDecoder> _log;
    private readonly AzureCosmosDbTabularMemory? _memory;
    private string _datasetName = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="TabularCsvDecoder"/> class.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="memory">Optional memory instance for schema extraction.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public TabularCsvDecoder(
        TabularExcelDecoderConfig? config = null,
        AzureCosmosDbTabularMemory? memory = null,
        ILoggerFactory? loggerFactory = null)
    {
        this._config = config ?? new TabularExcelDecoderConfig();
        this._memory = memory;
        this._log = (loggerFactory ?? DefaultLogger.Factory).CreateLogger<TabularCsvDecoder>();
    }

    public TabularCsvDecoder WithDatasetName(string datasetName)
    {
        this._datasetName = datasetName;
        return this;
    }

    public TabularCsvDecoder WithMemory(AzureCosmosDbTabularMemory memory)
    {
        var newDecoder = new TabularCsvDecoder(
            this._config,
            memory,
            null);

        if (!string.IsNullOrEmpty(this._datasetName))
        {
            newDecoder._datasetName = this._datasetName;
        }

        this._log.LogInformation("Created new TabularCsvDecoder instance with memory set");
        return newDecoder;
    }

    public static IContentDecoder CreateWithDatasetName(
        TabularExcelDecoderConfig config,
        string datasetName,
        object? memory = null,
        ILoggerFactory? loggerFactory = null)
    {
        var decoder = new TabularCsvDecoder(
            config,
            memory as AzureCosmosDbTabularMemory,
            loggerFactory);

        if (!string.IsNullOrEmpty(datasetName))
        {
            decoder._datasetName = datasetName;
        }

        return decoder;
    }

    public static TabularCsvDecoder WithDatasetName(TabularCsvDecoder decoder, string datasetName)
    {
        if (!string.IsNullOrEmpty(datasetName))
        {
            decoder._datasetName = datasetName;
        }
        return decoder;
    }

    public bool SupportsMimeType(string mimeType)
    {
        return mimeType != null && (
            mimeType.Equals("text/csv", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Equals("application/csv", StringComparison.OrdinalIgnoreCase) ||
            //mimeType.Equals("text/plain", StringComparison.OrdinalIgnoreCase) || // Sometimes CSVs are uploaded as plain text
            (MimeTypes.CSVData != null && mimeType.StartsWith(MimeTypes.CSVData, StringComparison.OrdinalIgnoreCase))
        );
    }

    public async Task<FileContent> DecodeAsync(string filename, CancellationToken cancellationToken = default)
    {
        this._log.LogDebug("Decoding CSV file: {Filename}", filename);
        using var stream = File.OpenRead(filename);
        return await this.DecodeStreamInternalAsync(stream, filename, cancellationToken);
    }

    public async Task<FileContent> DecodeAsync(BinaryData data, CancellationToken cancellationToken = default)
    {
        using var stream = data.ToStream();
        return await this.DecodeStreamInternalAsync(stream, originalFilename: null, cancellationToken: cancellationToken);
    }

    public async Task<FileContent> DecodeAsync(Stream data, CancellationToken cancellationToken = default)
    {
        return await this.DecodeStreamInternalAsync(data, originalFilename: null, cancellationToken: cancellationToken);
    }

    // Internal method to handle the actual stream decoding, accepting an optional original filename
    private async Task<FileContent> DecodeStreamInternalAsync(Stream data, string? originalFilename = null, CancellationToken cancellationToken = default)
    {
        this._log.LogDebug("Internal: Extracting tabular data from CSV file stream. Original filename: {OriginalFilename}", originalFilename ?? "Unknown");
        var result = new FileContent(MimeTypes.PlainText);

        string csvFileName = originalFilename != null ? Path.GetFileName(originalFilename) : "csv_import";
        List<string> headers = new();
        List<Dictionary<string, object>> rows = new();

        // Prepare importBatchId before row processing
        string importBatchId = string.Empty;

        // Read the CSV into memory (assume UTF-8)
        int totalLines = 0;
        int totalRowsAdded = 0;
        using (var reader = new StreamReader(data, Encoding.UTF8, true, 1024, leaveOpen: true))
        {
            int rowIndex = 0;
            while (!reader.EndOfStream)
            {
                string? line = await reader.ReadLineAsync();
                totalLines++;
                if (line == null) break;

                List<string> fields = ParseCsvLine(line);

                if (rowIndex == this._config.HeaderRowIndex)
                {
                    // Header row
                    foreach (var field in fields)
                    {
                        string headerText = field ?? string.Empty;
                        if (this._config.NormalizeHeaderNames)
                        {
                            headerText = NormalizeHeaderName(headerText);
                        }
                        if (string.IsNullOrWhiteSpace(headerText))
                        {
                            headerText = $"{this._config.DefaultColumnPrefix}{headers.Count + 1}";
                        }
                        headers.Add(headerText);
                    }
                    rowIndex++;
                    continue;
                }

                if (rowIndex < this._config.HeaderRowIndex)
                {
                    // Skip rows before header
                    rowIndex++;
                    continue;
                }

                // Data row
                try
                {
                    var rowData = new Dictionary<string, object>();
                    if (this._config.IncludeRowNumbers)
                    {
                        rowData["_rowNumber"] = rowIndex + 1;
                    }
                    if (this._config.IncludeWorksheetNames)
                    {
                        rowData["_csv_file"] = csvFileName;
                    }

                    for (int i = 0; i < fields.Count; i++)
                    {
                        string columnName = (i < headers.Count) ? headers[i] : $"{this._config.DefaultColumnPrefix}{i + 1}";
                        object value = string.IsNullOrEmpty(fields[i]) ? this._config.BlankCellValue : fields[i];
                        rowData[columnName] = value;
                    }
                    // Restore: Add import_batch_id to rowData for compatibility with RecordOps
                    if (!string.IsNullOrEmpty(importBatchId))
                    {
                        rowData["import_batch_id"] = importBatchId;
                        Console.WriteLine($"TabularCsvDecoder: Adding import batch ID to row data: {importBatchId}");
                    }

                    // Optionally skip empty rows if configured
                    if (this._config.SkipEmptyRows && rowData.All(kvp => string.IsNullOrEmpty(kvp.Value?.ToString())))
                    {
                        this._log.LogWarning("TabularCsvDecoder: Skipping empty row at CSV line {LineNumber} (row index {RowIndex})", totalLines, rowIndex);
                    }
                    else
                    {
                        rows.Add(rowData);
                        totalRowsAdded++;
                    }
                }
                catch (Exception ex)
                {
                    this._log.LogError(ex, "TabularCsvDecoder: Failed to process row at CSV line {LineNumber} (row index {RowIndex}). Row will be skipped.", totalLines, rowIndex);
                    Console.WriteLine($"ERROR: Failed to process CSV row {rowIndex + 1} in file {csvFileName}: {ex.GetType().Name} - {ex.Message}");
                }
                rowIndex++;
            }
        }

        this._log.LogInformation($"TabularCsvDecoder: Finished reading CSV. Total lines: {totalLines}, Rows added: {totalRowsAdded}, Headers: {headers.Count}");
        // Schema extraction
        string schemaId = string.Empty;
        // importBatchId is already declared above
        if (this._memory == null)
        {
            this._log.LogWarning("TabularCsvDecoder: _memory is null, schema will not be created.");
        }
        if (string.IsNullOrEmpty(this._datasetName))
        {
            this._log.LogWarning("TabularCsvDecoder: _datasetName is null or empty, schema will not be created.");
        }
        if (this._memory != null && !string.IsNullOrEmpty(this._datasetName))
        {
            try
            {
                this._log.LogInformation($"TabularCsvDecoder: [PRE-SCHEMA] About to extract and store schema for dataset '{this._datasetName}' using memory instance '{this._memory.GetType().FullName}'.");
                var schema = ExtractSchemaFromCsv(headers, rows, this._datasetName, csvFileName);
                this._log.LogInformation($"TabularCsvDecoder: [POST-SCHEMA] Schema object created: ID={schema?.Id ?? "null"}, File={schema?.File ?? "null"}, DatasetName={schema?.DatasetName ?? "null"}");
                if (schema != null)
                {
                    string? indexName = null;
                    if (cancellationToken.GetType().GetProperty("IndexName")?.GetValue(cancellationToken) is string ctxIndexName)
                    {
                        indexName = ctxIndexName;
                        this._log.LogDebug("Using index name '{IndexName}' from context for schema storage", indexName);
                    }
                    // If indexName is still null or empty, default to _datasetName
                    if (string.IsNullOrEmpty(indexName) && !string.IsNullOrEmpty(this._datasetName))
                    {
                        indexName = this._datasetName;
                        this._log.LogDebug("Index name not found in context; defaulting to dataset name '{DatasetName}' for schema storage", this._datasetName);
                    }
                    Console.WriteLine($"TabularCsvDecoder: Storing schema with ID={schema.Id} to database index: {indexName ?? "default"}, " +
                                     $"Dataset: {this._datasetName}, File: {schema.File}");
                    var storedSchemaId = await this._memory.StoreSchemaAsync(schema, indexName, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(storedSchemaId))
                    {
                        schemaId = storedSchemaId;
                        importBatchId = schema.ImportBatchId;
                        Console.WriteLine($"TabularCsvDecoder: Schema storage SUCCESS! ID={schemaId}, ImportBatchId={importBatchId}");
                        this._log.LogInformation("Stored schema with ID {SchemaId} and import batch ID {ImportBatchId} for dataset {DatasetName}",
                            schemaId, importBatchId, this._datasetName);
                    }
                    else
                    {
                        Console.WriteLine($"TabularCsvDecoder: Schema storage FAILED! StoreSchemaAsync returned null or empty schemaId.");
                        this._log.LogWarning("TabularCsvDecoder: StoreSchemaAsync returned null or empty schemaId.");
                    }
                }
                else
                {
                    this._log.LogWarning("TabularCsvDecoder: ExtractSchemaFromCsv returned null.");
                }
            }
            catch (Exception ex)
            {
                var errorMsg = $"CSV_SCHEMA_ERROR: Failed to create/store schema for dataset '{this._datasetName}', file '{csvFileName}'. " +
                               $"_memory is {(this._memory == null ? "null" : "set")}, _datasetName='{this._datasetName}', headers=[{string.Join(",", headers)}]. " +
                               $"Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
                Console.WriteLine(errorMsg);
                this._log.LogError(ex, errorMsg);
            }
        }

        // Build chunks
        int chunkNumber = 0;
        foreach (var rowData in rows)
        {
            chunkNumber++;
            var metadata = new Dictionary<string, string>
            {
                ["csv_file"] = csvFileName,
                ["rowNumber"] = rowData.ContainsKey("_rowNumber") ? rowData["_rowNumber"].ToString() ?? "" : chunkNumber.ToString(),
                ["_csv_file"] = csvFileName,
                ["_rowNumber"] = rowData.ContainsKey("_rowNumber") ? rowData["_rowNumber"].ToString() ?? "" : chunkNumber.ToString(),
            };
            if (!string.IsNullOrEmpty(this._datasetName))
            {
                metadata["dataset_name"] = this._datasetName;
            }
            if (!string.IsNullOrEmpty(schemaId))
            {
                metadata["schema_id"] = schemaId;
            }
            // Do NOT add import_batch_id to metadata; use only top-level ImportBatchId property

            // Build sentence format
            var textBuilder = new StringBuilder();
            // Use the same prefix as Excel for parser compatibility
            textBuilder.Append($"Record from worksheet {csvFileName}, row {metadata["rowNumber"]}: ");
            if (!string.IsNullOrEmpty(schemaId))
            {
                textBuilder.Append($"schema_id is {schemaId}. ");
            }
            if (!string.IsNullOrEmpty(importBatchId))
            {
                textBuilder.Append($"import_batch_id is {importBatchId}. ");
            }
            foreach (var kvp in rowData)
            {
                if (kvp.Key == "schema_id" || kvp.Key == "import_batch_id") continue;
                if (kvp.Key.StartsWith("_")) continue;
                string valueStr = kvp.Value?.ToString() ?? "NULL";
                textBuilder.Append($"{kvp.Key} is {valueStr}. ");
            }
            var chunkText = textBuilder.ToString().TrimEnd();
            var chunk = new Chunk(chunkText, chunkNumber, metadata);
            result.Sections.Add(chunk);
        }

        // Create a standardized import summary
        Console.WriteLine($"========== IMPORT SUMMARY ==========");
        Console.WriteLine($"File: {originalFilename ?? csvFileName}");
        Console.WriteLine($"Total source lines: {totalLines}");
        Console.WriteLine($"Total records imported: {rows.Count}");
        Console.WriteLine($"Import batch ID: {importBatchId}");
        Console.WriteLine($"To validate in Cosmos DB, run:");
        Console.WriteLine($"SELECT COUNT(1) FROM c WHERE c.importBatchId = '{importBatchId}'");
        Console.WriteLine($"====================================");
        
        // Additional details
        Console.WriteLine($"Header count: {headers.Count}");
        Console.WriteLine($"Rows processed/added: {totalRowsAdded} of {totalLines} lines");
        
        // Log import batch ID for database validation
        if (!string.IsNullOrEmpty(importBatchId))
        {
            this._log.LogInformation("CSV IMPORT SUMMARY - File: {FileName}, Source lines: {SourceLines}, Imported records: {ImportedRecords}, Batch ID: {BatchId}",
                originalFilename ?? csvFileName, totalLines, rows.Count, importBatchId);
        }

        return result;
    }

    // Custom CSV line parser (handles quoted fields, commas, newlines)
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        if (string.IsNullOrEmpty(line)) return fields;
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    // Schema extraction for CSV
    private TabularDataSchema ExtractSchemaFromCsv(List<string> headers, List<Dictionary<string, object>> rows, string datasetName, string csvFileName)
    {
        // Generate a timestamp-based unique ID similar to Excel decoder
        string timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        string sourceFileName = Path.GetFileNameWithoutExtension(csvFileName);
        string uniqueId = $"schema_{sourceFileName}_{timestamp}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        
        Console.WriteLine($"Creating schema for CSV dataset {datasetName} with source file {csvFileName}, ID: {uniqueId}");

        var schema = new TabularDataSchema
        {
            Id = uniqueId,
            DatasetName = datasetName,
            ImportDate = DateTime.UtcNow,
            SourceFile = csvFileName,
            File = csvFileName, // Set File property to source file name for proper partition key
            Columns = new List<SchemaColumn>(),
            Metadata = new Dictionary<string, string>(),
            ImportBatchId = Guid.NewGuid().ToString()
        };

        // Add schema columns from headers
        Console.WriteLine($"Creating {headers.Count} schema columns for CSV file {csvFileName}");
        foreach (var header in headers)
        {
            string normalizedName = header;
            if (this._config.NormalizeHeaderNames)
            {
                normalizedName = NormalizeHeaderName(header);
            }
            // For CSV, always use string type
            string dataType = "string";
            var commonValues = SampleColumnValues(rows, header);

            var column = new SchemaColumn
            {
                Name = header,
                NormalizedName = normalizedName,
                DataType = dataType,
                IsRequired = false,
                CommonValues = commonValues
            };
            schema.Columns.Add(column);
            Console.WriteLine($"Added column: {header}, type: {dataType}");
        }

        // Ensure document_type is set for schema recognition
        schema.Metadata["document_type"] = "schema";
        
        Console.WriteLine($"CSV Schema creation complete. ID={schema.Id}, File='{schema.File}', DatasetName='{schema.DatasetName}', " +
                         $"Columns={schema.Columns.Count}, ImportBatchId={schema.ImportBatchId}");
        
        this._log.LogInformation($"TabularCsvDecoder: Created schema with ID={schema.Id}, File='{schema.File}', " +
                               $"DatasetName='{schema.DatasetName}', Columns={schema.Columns.Count}, " +
                               $"Metadata keys: {string.Join(",", schema.Metadata.Keys)}");

        return schema;
    }

    // Infer data type for a column by sampling values
    private string InferColumnDataType(List<Dictionary<string, object>> rows, string columnName)
    {
        var dataTypes = new Dictionary<string, int>
        {
            { "string", 0 },
            { "number", 0 },
            { "boolean", 0 },
            { "date", 0 }
        };
        int sampleCount = 0;
        foreach (var row in rows.Take(100))
        {
            if (!row.TryGetValue(columnName, out var value) || value == null) continue;
            sampleCount++;
            if (bool.TryParse(value.ToString(), out _))
            {
                dataTypes["boolean"]++;
            }
            else if (DateTime.TryParse(value.ToString(), out _))
            {
                dataTypes["date"]++;
            }
            else if (double.TryParse(value.ToString(), out _))
            {
                dataTypes["number"]++;
            }
            else
            {
                dataTypes["string"]++;
            }
        }
        if (sampleCount == 0) return "string";
        return dataTypes.OrderByDescending(kvp => kvp.Value).First().Key;
    }

    // Sample common values for a column
    private List<string> SampleColumnValues(List<Dictionary<string, object>> rows, string columnName)
    {
        var values = new HashSet<string>();
        foreach (var row in rows.Take(100))
        {
            if (!row.TryGetValue(columnName, out var value) || value == null) continue;
            string val = value.ToString() ?? "";
            if (!string.IsNullOrEmpty(val) && values.Count < 10)
            {
                values.Add(val);
            }
        }
        return values.ToList();
    }

    // Normalize header name (same as Excel decoder)
    private static string NormalizeHeaderName(string headerName)
    {
        if (string.IsNullOrEmpty(headerName))
        {
            return headerName;
        }
        var s_invalidCharsRegex = new Regex(@"[^\w\d]", RegexOptions.Compiled);
        var normalized = s_invalidCharsRegex.Replace(headerName, "_");
        while (normalized.Contains("__"))
        {
            normalized = normalized.Replace("__", "_");
        }
        normalized = normalized.Trim('_');
        return normalized;
    }
}
