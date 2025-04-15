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
            mimeType.Equals("application/csv", StringComparison.OrdinalIgnoreCase)
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

        // Read the CSV into memory (assume UTF-8)
        using (var reader = new StreamReader(data, Encoding.UTF8, true, 1024, leaveOpen: true))
        {
            int rowIndex = 0;
            while (!reader.EndOfStream)
            {
                string? line = await reader.ReadLineAsync();
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
                rows.Add(rowData);
                rowIndex++;
            }
        }

        // Schema extraction
        string schemaId = string.Empty;
        string importBatchId = string.Empty;
        if (this._memory != null && !string.IsNullOrEmpty(this._datasetName))
        {
            try
            {
                var schema = ExtractSchemaFromCsv(headers, rows, this._datasetName, csvFileName);
                if (schema != null)
                {
                    string? indexName = null;
                    if (cancellationToken.GetType().GetProperty("IndexName")?.GetValue(cancellationToken) is string ctxIndexName)
                    {
                        indexName = ctxIndexName;
                        this._log.LogDebug("Using index name '{IndexName}' from context for schema storage", indexName);
                    }
                    var storedSchemaId = await this._memory.StoreSchemaAsync(schema, indexName, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(storedSchemaId))
                    {
                        schemaId = storedSchemaId;
                        importBatchId = schema.ImportBatchId;
                        this._log.LogInformation("Stored schema with ID {SchemaId} and import batch ID {ImportBatchId} for dataset {DatasetName}",
                            schemaId, importBatchId, this._datasetName);
                    }
                }
            }
            catch (Exception ex)
            {
                this._log.LogError(ex, "Error extracting/storing schema from CSV file for dataset {DatasetName}", this._datasetName);
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
            if (!string.IsNullOrEmpty(importBatchId))
            {
                metadata["import_batch_id"] = importBatchId;
            }

            // Build sentence format
            var textBuilder = new StringBuilder();
            textBuilder.Append($"Record from csv_file {csvFileName}, row {metadata["rowNumber"]}: ");
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
        var schema = new TabularDataSchema
        {
            DatasetName = datasetName,
            ImportDate = DateTime.UtcNow,
            SourceFile = csvFileName,
            File = csvFileName,
            Columns = new List<SchemaColumn>()
        };

        foreach (var header in headers)
        {
            string normalizedName = header;
            if (this._config.NormalizeHeaderNames)
            {
                normalizedName = NormalizeHeaderName(header);
            }
            string dataType = InferColumnDataType(rows, header);
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
        }
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
