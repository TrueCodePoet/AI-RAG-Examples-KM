// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.DataFormats;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.Pipeline;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular.DataFormats;

/// <summary>
/// Decoder for Excel files that preserves tabular structure.
/// </summary>
[Experimental("KMEXP00")]
public sealed class TabularExcelDecoder : IContentDecoder
{
    private readonly TabularExcelDecoderConfig _config;
    private readonly ILogger<TabularExcelDecoder> _log;
    private readonly AzureCosmosDbTabularMemory? _memory;
    private string _datasetName = string.Empty;
    private static readonly Regex s_invalidCharsRegex = new(@"[^\w\d]", RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the <see cref="TabularExcelDecoder"/> class.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="memory">Optional memory instance for schema extraction.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    internal TabularExcelDecoder(
        TabularExcelDecoderConfig? config = null, 
        AzureCosmosDbTabularMemory? memory = null,
        ILoggerFactory? loggerFactory = null)
    {
        this._config = config ?? new TabularExcelDecoderConfig();
        this._memory = memory;
        this._log = (loggerFactory ?? DefaultLogger.Factory).CreateLogger<TabularExcelDecoder>();
    }

    /// <summary>
    /// Sets the dataset name for schema extraction.
    /// </summary>
    /// <param name="datasetName">The dataset name.</param>
    /// <returns>The decoder instance for method chaining.</returns>
    public TabularExcelDecoder WithDatasetName(string datasetName)
    {
        this._datasetName = datasetName;
        return this;
    }
    
    /// <summary>
    /// Creates a new instance with the memory instance set.
    /// </summary>
    /// <param name="memory">The memory instance.</param>
    /// <returns>A new decoder instance with the memory set.</returns>
    internal TabularExcelDecoder WithMemory(AzureCosmosDbTabularMemory memory)
    {
        // Create a new instance with the memory parameter
        var newDecoder = new TabularExcelDecoder(
            this._config,
            memory,
            null); // Use default logger factory
        
        // Copy over the dataset name
        if (!string.IsNullOrEmpty(this._datasetName))
        {
            newDecoder._datasetName = this._datasetName;
        }
        
        this._log.LogInformation("Created new TabularExcelDecoder instance with memory set");
        return newDecoder;
    }

    /// <summary>
    /// Factory method to create a TabularExcelDecoder with dataset name.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="datasetName">The dataset name.</param>
    /// <param name="memory">Optional memory instance for schema extraction.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <returns>A new TabularExcelDecoder instance.</returns>
    public static IContentDecoder CreateWithDatasetName(
        TabularExcelDecoderConfig config, 
        string datasetName,
        object? memory = null,
        ILoggerFactory? loggerFactory = null)
    {
        // Create a new instance with the memory parameter
        var decoder = new TabularExcelDecoder(
            config, 
            memory as AzureCosmosDbTabularMemory, 
            loggerFactory);
        
        // Set the dataset name
        if (!string.IsNullOrEmpty(datasetName))
        {
            decoder._datasetName = datasetName;
        }
        
        return decoder;
    }

    /// <summary>
    /// Sets the dataset name for schema extraction and metadata.
    /// </summary>
    /// <param name="datasetName">The dataset name.</param>
    /// <returns>The decoder instance for method chaining.</returns>
    public static TabularExcelDecoder WithDatasetName(TabularExcelDecoder decoder, string datasetName)
    {
        if (!string.IsNullOrEmpty(datasetName))
        {
            decoder._datasetName = datasetName;
        }
        return decoder;
    }

    /// <inheritdoc />
    public bool SupportsMimeType(string mimeType)
    {
        return mimeType != null && mimeType.StartsWith(MimeTypes.MsExcelX, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<FileContent> DecodeAsync(string filename, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(filename);
        return await this.DecodeAsync(stream, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<FileContent> DecodeAsync(BinaryData data, CancellationToken cancellationToken = default)
    {
        using var stream = data.ToStream();
        return await this.DecodeAsync(stream, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<FileContent> DecodeAsync(Stream data, CancellationToken cancellationToken = default)
    {
        this._log.LogDebug("Extracting tabular data from MS Excel file");

        var result = new FileContent(MimeTypes.PlainText);
        XLWorkbook? workbook = null;

        try
        {
            // Create a memory stream to make a copy of the data
            // This allows us to rewind and try different approaches if needed
            using var memoryStream = new MemoryStream();
            data.CopyTo(memoryStream);
            memoryStream.Position = 0;

            try
            {
                // First attempt: Try to load with default options
                workbook = new XLWorkbook(memoryStream);
            }
            catch (Exception ex)
            {
                // Enhanced detection for PivotTable and XML structure issues
                bool isPivotTableIssue = 
                    // Check stack trace for PivotTable references
                    ex.StackTrace?.Contains("PivotTableCacheDefinitionPartReader", StringComparison.Ordinal) == true ||
                    ex.InnerException?.StackTrace?.Contains("PivotTableCacheDefinitionPartReader", StringComparison.Ordinal) == true ||
                    // Check message for XML structure issues
                    ex.Message.Contains("element structure in XML", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("PartStructureException", StringComparison.OrdinalIgnoreCase) ||
                    // Check exception type
                    ex.GetType().Name.Contains("PartStructureException") ||
                    ex.InnerException?.GetType().Name.Contains("PartStructureException") == true;

                if (isPivotTableIssue && this._config.SkipPivotTables)
                {
                    // Log warning but don't throw exception
                    this._log.LogWarning("Failed to load Excel file, likely due to a PivotTable structure issue. Skipping this file. Error: {ErrorMessage}", ex.Message);
                    
                    // Add a note to the result to indicate the file was skipped
                    result.Sections.Add(new Chunk("This Excel file could not be processed due to PivotTable or XML structure issues.", 1, 
                        new Dictionary<string, string> { ["processing_error"] = "PivotTable structure issue" }));
                    
                    return result; // Return result with error note
                }
                else
                {
                    // Log other potential loading errors and rethrow
                    this._log.LogError(ex, "Failed to load Excel file: {ErrorMessage}", ex.Message);
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            // Catch any other exceptions that might occur during stream handling
            this._log.LogError(ex, "Error processing Excel file stream: {ErrorMessage}", ex.Message);
            throw;
        }

        using (workbook) // Ensure disposal if workbook was loaded successfully
        {
                    // Variables to store schema ID and import batch ID
                    string schemaId = string.Empty;
                    string importBatchId = string.Empty;
                    
                    // Extract schema if memory is provided and dataset name is set
                    if (this._memory != null && !string.IsNullOrEmpty(this._datasetName))
                    {
                        try
                        {
                            var schema = ExtractSchemaFromWorkbook(workbook, this._datasetName);
                            if (schema != null)
                            {
                                // Get the index name from the current operation context if available
                                string? indexName = null;
                                
                                // Try to extract index name from the pipeline context if available
                                // This ensures schema is stored in the same container as the data
                                if (cancellationToken.GetType().GetProperty("IndexName")?.GetValue(cancellationToken) is string ctxIndexName)
                                {
                                    indexName = ctxIndexName;
                                    this._log.LogDebug("Using index name '{IndexName}' from context for schema storage", indexName);
                                }
                                
                                // Store schema and get its ID
                                try
                                {
                                    var storedSchemaId = await this._memory.StoreSchemaAsync(schema, indexName, cancellationToken).ConfigureAwait(false);
                                    if (!string.IsNullOrEmpty(storedSchemaId))
                                    {
                                        schemaId = storedSchemaId;
                                        importBatchId = schema.ImportBatchId;
                                        this._log.LogInformation("Stored schema with ID {SchemaId} and import batch ID {ImportBatchId} for dataset {DatasetName}", 
                                            schemaId, importBatchId, this._datasetName);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    this._log.LogError(ex, "Error storing schema for dataset {DatasetName}", this._datasetName);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            this._log.LogError(ex, "Error extracting schema from Excel file for dataset {DatasetName}", this._datasetName);
                        }
                    }

            var chunkNumber = 0;
            foreach (var worksheet in workbook.Worksheets)
            {

                // Skip worksheet if not in the list of worksheets to process
                if (!this._config.ProcessAllWorksheets &&
                    !this._config.WorksheetsToProcess.Contains(worksheet.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var worksheetName = worksheet.Name;
                this._log.LogDebug("Processing worksheet: {WorksheetName}", worksheetName);

                var rangeUsed = worksheet.RangeUsed();
                if (rangeUsed == null)
                {
                    this._log.LogDebug("Worksheet {WorksheetName} is empty", worksheetName);
                    continue;
                }

                // Get headers from the specified row
                var headers = new List<string>();
                var headerRow = rangeUsed.Row(this._config.HeaderRowIndex + 1);

                if (this._config.UseFirstRowAsHeader && headerRow != null)
                {
                    foreach (var cell in headerRow.CellsUsed())
                    {
                        var headerText = cell.Value.ToString() ?? string.Empty;

                        // Normalize header name if configured
                        if (this._config.NormalizeHeaderNames)
                        {
                            headerText = NormalizeHeaderName(headerText);
                        }

                        // If header is empty, use default column prefix with column number
                        if (string.IsNullOrWhiteSpace(headerText))
                        {
                            headerText = $"{this._config.DefaultColumnPrefix}{cell.Address.ColumnNumber}";
                        }

                        headers.Add(headerText);
                    }
                }

                // Process data rows
                var rowsUsed = rangeUsed.RowsUsed();
                if (rowsUsed == null)
                {
                    continue;
                }

                // Skip the header row if using first row as header
                var startRow = this._config.UseFirstRowAsHeader ? this._config.HeaderRowIndex + 1 : 0;

                foreach (var row in rowsUsed.Skip(startRow))
                {
                    // Skip row if configured to skip empty or hidden rows
                    if ((this._config.SkipEmptyRows && !row.CellsUsed().Any()) ||
                        (this._config.SkipHiddenRows && worksheet.Row(row.RowNumber()).IsHidden))
                    {
                        continue;
                    }

                    var rowNumber = row.RowNumber();
                    var cells = row.Cells().ToList();

                    // Create a dictionary to hold the row data
                    var rowData = new Dictionary<string, object>();

                    // Add metadata if configured
                    if (this._config.IncludeWorksheetNames)
                    {
                        rowData["_worksheet"] = worksheetName;
                    }

                    if (this._config.IncludeRowNumbers)
                    {
                        rowData["_rowNumber"] = rowNumber;
                    }

                    // Process each cell in the row
                    for (var i = 0; i < cells.Count; i++)
                    {
                        var cell = cells[i];

                        // Skip hidden columns if configured
                        if (this._config.SkipHiddenColumns && worksheet.Column(cell.Address.ColumnNumber).IsHidden)
                        {
                            continue;
                        }

                        // Get the column name (from headers or generate one)
                        string columnName;
                        if (this._config.UseFirstRowAsHeader && i < headers.Count)
                        {
                            columnName = headers[i];
                        }
                        else
                        {
                            columnName = $"{this._config.DefaultColumnPrefix}{cell.Address.ColumnNumber}";
                        }

                        // Extract the cell value based on its type
                        object cellValue = this.ExtractCellValue(cell);

                        // Add to row data
                        rowData[columnName] = cellValue;
                    }

                    // Add schema ID and import batch ID if available - ensure they're in the payload
                    if (!string.IsNullOrEmpty(schemaId))
                    {
                        // Add to the row data to ensure it gets into the payload
                        rowData["schema_id"] = schemaId;
                        Console.WriteLine($"TabularExcelDecoder: Adding schema ID to row data: {schemaId}");
                    }
                    
                    if (!string.IsNullOrEmpty(importBatchId))
                    {
                        // Add to the row data to ensure it gets into the payload
                        rowData["import_batch_id"] = importBatchId;
                        Console.WriteLine($"TabularExcelDecoder: Adding import batch ID to row data: {importBatchId}");
                    }
                    
                    // Log the row data for debugging
                    Console.WriteLine($"TabularExcelDecoder: Row data contains {rowData.Count} fields, including schema_id: {rowData.ContainsKey("schema_id")}, import_batch_id: {rowData.ContainsKey("import_batch_id")}");

                    // Create a chunk for this row
                    chunkNumber++;
                    var metadata = new Dictionary<string, string>
                    {
                        ["worksheetName"] = worksheetName,
                        ["rowNumber"] = rowNumber.ToString(),
                        ["_worksheet"] = worksheetName,  // Add with underscore prefix for source dictionary
                        ["_rowNumber"] = rowNumber.ToString(),  // Add with underscore prefix for source dictionary
                    };

                    // Serialize the row data including schema ID and import batch ID
                    // This ensures these values are preserved in the payload
                    metadata["tabular_data"] = JsonSerializer.Serialize(rowData, AzureCosmosDbTabularConfig.DefaultJsonSerializerOptions);

                    // Add dataset name if provided
                    if (!string.IsNullOrEmpty(this._datasetName))
                    {
                        metadata["dataset_name"] = this._datasetName;
                    }
                    
                    // Add schema ID and import batch ID to metadata as well
                    // This is critical for ensuring these values are passed to the memory record
                    if (!string.IsNullOrEmpty(schemaId))
                    {
                        metadata["schema_id"] = schemaId;
                        Console.WriteLine($"TabularExcelDecoder: Adding schema ID to chunk metadata: {schemaId}");
                    }
                    
                    if (!string.IsNullOrEmpty(importBatchId))
                    {
                        metadata["import_batch_id"] = importBatchId;
                        Console.WriteLine($"TabularExcelDecoder: Adding import batch ID to chunk metadata: {importBatchId}");
                    }

                    // Create a more descriptive text representation for the chunk content
                    var sb = new StringBuilder();
                    sb.Append($"Record from worksheet {worksheetName}, row {rowNumber}:");
                    
                    // Add schema ID and import batch ID to the text representation as well
                    // This ensures they're included in the text field which is parsed by FromMemoryRecord
                    if (!string.IsNullOrEmpty(schemaId))
                    {
                        sb.Append($" schema_id is {schemaId}.");
                    }
                    
                    if (!string.IsNullOrEmpty(importBatchId))
                    {
                        sb.Append($" import_batch_id is {importBatchId}.");
                    }
                    
                    foreach (var kvp in rowData)
                    {
                        // Skip internal metadata fields and the schema/import batch fields we already added
                        if (kvp.Key.StartsWith("_") || 
                            kvp.Key == "schema_id" || 
                            kvp.Key == "import_batch_id") continue;

                        // Append as " Key is Value." - adjust phrasing as needed
                        sb.Append($" {kvp.Key} is {kvp.Value?.ToString() ?? "NULL"}.");
                    }

                    var chunkText = sb.ToString().Trim();
                    Console.WriteLine($"TabularExcelDecoder: Created chunk with text: {chunkText.Substring(0, Math.Min(100, chunkText.Length))}...");
                    result.Sections.Add(new Chunk(chunkText, chunkNumber, metadata)); // Trim potential trailing space
                }
            }
        } // End using workbook

        return result;
    }

    /// <summary>
    /// Extracts the value from a cell based on its type.
    /// </summary>
    /// <param name="cell">The cell to extract the value from.</param>
    /// <returns>The extracted value.</returns>
    private object ExtractCellValue(IXLCell cell)
    {
        if (cell == null || cell.Value.IsBlank)
        {
            return this._config.BlankCellValue;
        }

        if (this._config.PreserveDataTypes)
        {
            if (cell.Value.IsBoolean)
            {
                return cell.Value.GetBoolean();
            }
            else if (cell.Value.IsDateTime)
            {
                var dateTime = cell.Value.GetDateTime();
                return dateTime.ToString(this._config.DateFormat, this._config.DateFormatProvider);
            }
            else if (cell.Value.IsTimeSpan)
            {
                var timeSpan = cell.Value.GetTimeSpan();
                return timeSpan.ToString(this._config.TimeFormat, this._config.TimeFormatProvider);
            }
            else if (cell.Value.IsNumber)
            {
                return cell.Value.GetNumber();
            }
            else if (cell.Value.IsText)
            {
                return cell.Value.GetText();
            }
            else if (cell.Value.IsError)
            {
                return cell.Value.GetError().ToString();
            }
            else
            {
                return cell.Value.ToString() ?? string.Empty;
            }
        }
        else
        {
            // Convert everything to string
            return cell.Value.ToString() ?? string.Empty;
        }
    }

    /// <summary>
    /// Extracts schema information from a workbook.
    /// </summary>
    /// <param name="workbook">The workbook to extract schema from.</param>
    /// <param name="datasetName">The dataset name.</param>
    /// <returns>The extracted schema.</returns>
    private TabularDataSchema ExtractSchemaFromWorkbook(XLWorkbook workbook, string datasetName)
    {
        // Use a default source file name
        string sourceFileName = "excel_import";
        
        // Try to get a more specific name if available
        try
        {
            // Different versions of ClosedXML might have different property structures
            // This is a safer approach that won't throw exceptions if properties aren't available
            var workbookName = workbook.Properties.Title;
            if (!string.IsNullOrEmpty(workbookName))
            {
                sourceFileName = Path.GetFileName(workbookName);
                Console.WriteLine($"Using source file name from workbook title: {sourceFileName}");
            }
        }
        catch (Exception ex)
        {
            // Just log and continue with the default name
            Console.WriteLine($"Could not get workbook name: {ex.Message}");
        }
        
        var schema = new TabularDataSchema
        {
            DatasetName = datasetName,
            ImportDate = DateTime.UtcNow,
            SourceFile = sourceFileName,
            File = sourceFileName, // Set File property to source file name for proper partition key
            Columns = new List<SchemaColumn>()
        };
        
        Console.WriteLine($"Created schema for dataset {datasetName} with source file {sourceFileName}");

        // Process the first worksheet to extract schema
        var worksheet = workbook.Worksheets.FirstOrDefault();
        if (worksheet == null)
        {
            return schema;
        }

        var rangeUsed = worksheet.RangeUsed();
        if (rangeUsed == null)
        {
            return schema;
        }

        // Get headers from the specified row
        var headerRow = rangeUsed.Row(this._config.HeaderRowIndex + 1);
        if (this._config.UseFirstRowAsHeader && headerRow != null)
        {
            foreach (var cell in headerRow.CellsUsed())
            {
                var headerText = cell.Value.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(headerText))
                {
                    headerText = $"{this._config.DefaultColumnPrefix}{cell.Address.ColumnNumber}";
                }

                // Normalize header name if configured
                string normalizedName = headerText;
                if (this._config.NormalizeHeaderNames)
                {
                    normalizedName = NormalizeHeaderName(headerText);
                }

                // Infer data type by sampling values in the column
                string dataType = InferColumnDataType(worksheet, cell.Address.ColumnNumber, this._config.HeaderRowIndex + 1);

                // Sample common values
                var commonValues = SampleColumnValues(worksheet, cell.Address.ColumnNumber, this._config.HeaderRowIndex + 1);

                // Create schema column
                var column = new SchemaColumn
                {
                    Name = headerText,
                    NormalizedName = normalizedName,
                    DataType = dataType,
                    IsRequired = false, // Default to false
                    CommonValues = commonValues
                };

                schema.Columns.Add(column);
            }
        }

        return schema;
    }

    /// <summary>
    /// Infers the data type of a column by sampling values.
    /// </summary>
    /// <param name="worksheet">The worksheet.</param>
    /// <param name="columnNumber">The column number.</param>
    /// <param name="startRow">The starting row.</param>
    /// <returns>The inferred data type.</returns>
    private string InferColumnDataType(IXLWorksheet worksheet, int columnNumber, int startRow)
    {
        var dataTypes = new Dictionary<string, int>
        {
            { "string", 0 },
            { "number", 0 },
            { "boolean", 0 },
            { "date", 0 }
        };

        // Sample up to 100 rows to determine the most common data type
        var maxRows = Math.Min(worksheet.LastRowUsed().RowNumber(), startRow + 100);
        var sampleCount = 0;

        for (var rowNumber = startRow + 1; rowNumber <= maxRows; rowNumber++)
        {
            var cell = worksheet.Cell(rowNumber, columnNumber);
            if (cell == null || cell.Value.IsBlank)
            {
                continue;
            }

            sampleCount++;

            if (cell.Value.IsBoolean)
            {
                dataTypes["boolean"]++;
            }
            else if (cell.Value.IsDateTime)
            {
                dataTypes["date"]++;
            }
            else if (cell.Value.IsNumber)
            {
                dataTypes["number"]++;
            }
            else
            {
                dataTypes["string"]++;
            }
        }

        // If no samples, default to string
        if (sampleCount == 0)
        {
            return "string";
        }

        // Return the most common data type
        return dataTypes.OrderByDescending(kvp => kvp.Value).First().Key;
    }

    /// <summary>
    /// Samples common values from a column.
    /// </summary>
    /// <param name="worksheet">The worksheet.</param>
    /// <param name="columnNumber">The column number.</param>
    /// <param name="startRow">The starting row.</param>
    /// <returns>A list of common values.</returns>
    private List<string> SampleColumnValues(IXLWorksheet worksheet, int columnNumber, int startRow)
    {
        var values = new HashSet<string>();
        var maxRows = Math.Min(worksheet.LastRowUsed().RowNumber(), startRow + 100);

        for (var rowNumber = startRow + 1; rowNumber <= maxRows; rowNumber++)
        {
            var cell = worksheet.Cell(rowNumber, columnNumber);
            if (cell == null || cell.Value.IsBlank)
            {
                continue;
            }

            var value = cell.Value.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(value) && values.Count < 10)
            {
                values.Add(value);
            }
        }

        return values.ToList();
    }

    /// <summary>
    /// Normalizes a header name by replacing invalid characters with underscores.
    /// </summary>
    /// <param name="headerName">The header name to normalize.</param>
    /// <returns>The normalized header name.</returns>
    private static string NormalizeHeaderName(string headerName)
    {
        if (string.IsNullOrEmpty(headerName))
        {
            return headerName;
        }

        // Replace invalid characters with underscores
        var normalized = s_invalidCharsRegex.Replace(headerName, "_");

        // Remove consecutive underscores
        while (normalized.Contains("__"))
        {
            normalized = normalized.Replace("__", "_");
        }

        // Trim underscores from start and end
        normalized = normalized.Trim('_');

        return normalized;
    }
}
