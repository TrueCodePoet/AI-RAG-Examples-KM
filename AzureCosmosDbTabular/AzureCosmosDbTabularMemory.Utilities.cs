// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Text.RegularExpressions;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Azure Cosmos DB implementation of <see cref="IMemoryDb"/> for tabular data.
/// </summary>
internal sealed partial class AzureCosmosDbTabularMemory
{
    /// <summary>
    /// Normalizes field names from camelCase to snake_case.
    /// </summary>
    /// <param name="fieldName">The field name to normalize.</param>
    /// <returns>The normalized field name.</returns>
    private string NormalizeFieldName(string fieldName)
    {
        if (!fieldName.StartsWith("data.")) return fieldName;

        // Extract the part after "data."
        string field = fieldName.Substring(5);

        // Convert camelCase or PascalCase to snake_case
        // e.g., "serverPurpose" → "server_purpose"
        string snakeCase = System.Text.RegularExpressions.Regex.Replace(
            field,
            "(?<=[a-z])(?=[A-Z])",
            "_"
        ).ToLowerInvariant();

        return "data." + snakeCase;
    }

    /// <summary>
    /// Infers the data type of a value.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The inferred data type.</returns>
    private string InferDataType(object? value)
    {
        if (value == null)
        {
            return "string";
        }

        if (value is bool)
        {
            return "boolean";
        }

        if (value is int or long or float or double or decimal)
        {
            return "number";
        }

        if (value is DateTime)
        {
            return "date";
        }

        string valueStr = value.ToString() ?? string.Empty;

        if (bool.TryParse(valueStr, out _))
        {
            return "boolean";
        }

        if (int.TryParse(valueStr, out _) || double.TryParse(valueStr, out _))
        {
            return "number";
        }

        if (DateTime.TryParse(valueStr, out _))
        {
            return "date";
        }

        return "string";
    }

    /// <summary>
    /// Validates a value against a data type.
    /// </summary>
    /// <param name="value">The value to validate.</param>
    /// <param name="dataType">The data type.</param>
    /// <returns>True if the value is valid for the data type, false otherwise.</returns>
    private bool ValidateValue(object value, string dataType)
    {
        if (value == null)
        {
            return true; // Null is valid for any type
        }

        string valueStr = value.ToString() ?? string.Empty;

        switch (dataType.ToLowerInvariant())
        {
            case "string":
                return true; // Any value is valid as a string

            case "number":
            case "integer":
                return double.TryParse(valueStr, out _);

            case "boolean":
                return bool.TryParse(valueStr, out _);

            case "date":
            case "datetime":
                return DateTime.TryParse(valueStr, out _);

            default:
                return true; // Unknown type, assume valid
        }
    }

    /// <summary>
    /// Normalizes a column name to snake_case.
    /// </summary>
    /// <param name="columnName">The column name to normalize.</param>
    /// <returns>The normalized column name.</returns>
    private static string NormalizeColumnName(string columnName)
    {
        // Convert camelCase or PascalCase to snake_case
        string snakeCase = Regex.Replace(
            columnName,
            "(?<=[a-z])(?=[A-Z])",
            "_"
        ).ToLowerInvariant();

        // Replace spaces and other non-alphanumeric characters with underscores
        snakeCase = Regex.Replace(snakeCase, "[^a-z0-9]", "_");

        // Remove consecutive underscores
        while (snakeCase.Contains("__"))
        {
            snakeCase = snakeCase.Replace("__", "_");
        }

        // Trim underscores from start and end
        return snakeCase.Trim('_');
    }
}
