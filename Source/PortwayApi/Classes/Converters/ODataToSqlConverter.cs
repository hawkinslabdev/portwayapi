using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PortwayApi.Interfaces;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using SqlKata;
using SqlKata.Compilers;
using Serilog;

namespace PortwayApi.Classes;

/// <summary>
/// Implements the IODataToSqlConverter interface to convert OData queries to SQL
/// </summary>
public class ODataToSqlConverter : IODataToSqlConverter
{
    private readonly IEdmModelBuilder _edmModelBuilder;
    private readonly Compiler _sqlCompiler;
    
    public ODataToSqlConverter(IEdmModelBuilder edmModelBuilder, Compiler sqlCompiler)
    {
        _edmModelBuilder = edmModelBuilder;
        _sqlCompiler = sqlCompiler;
    }
    
    public (string SqlQuery, Dictionary<string, object> Parameters) ConvertToSQL(
        string entityName, 
        Dictionary<string, string> odataParams)
    {
        Log.Debug("🔄 Converting OData to SQL for entity: {EntityName}", entityName);
        
        // Build the EDM model for this entity
        var model = _edmModelBuilder.GetEdmModel(entityName);
        
        // Get schema and table name
        string schema = "dbo";
        string tableName = entityName;
        
        var parts = entityName.Split('.');
        if (parts.Length > 1)
        {
            schema = parts[0].Replace("[", "").Replace("]", "");
            tableName = parts[1].Replace("[", "").Replace("]", "");
        }
        else
        {
            tableName = tableName.Replace("[", "").Replace("]", "");
        }
        
        // Start building the SQL Kata query
        var query = new Query($"{schema}.{tableName}");
        
        // Dictionary to hold parameters for parameterized queries
        var parameters = new Dictionary<string, object>();
        
        // Track parameter count to ensure unique names
        int parameterCount = 0;
        
        try
        {
            // Apply $select
            if (odataParams.TryGetValue("select", out var select) && !string.IsNullOrWhiteSpace(select))
            {
                var columns = select.Split(',').Select(c => c.Trim()).ToArray();
                query.Select(columns);
                Log.Debug("🔍 Applied $select: {Columns}", string.Join(", ", columns));
            }
            
            // Apply $filter
            if (odataParams.TryGetValue("filter", out var filter) && !string.IsNullOrWhiteSpace(filter))
            {
                ApplyFilter(query, filter, ref parameterCount, parameters);
                Log.Debug("🔍 Applied $filter: {Filter}", filter);
            }
            
            // Apply $orderby
            if (odataParams.TryGetValue("orderby", out var orderby) && !string.IsNullOrWhiteSpace(orderby))
            {
                ApplyOrderBy(query, orderby);
                Log.Debug("🔍 Applied $orderby: {OrderBy}", orderby);
            }
            
            // Apply $top and $skip
            if (odataParams.TryGetValue("top", out var topStr) && int.TryParse(topStr, out var top))
            {
                query.Limit(top);
                Log.Debug("🔍 Applied $top: {Top}", top);
            }
            
            if (odataParams.TryGetValue("skip", out var skipStr) && int.TryParse(skipStr, out var skip))
            {
                query.Offset(skip);
                Log.Debug("🔍 Applied $skip: {Skip}", skip);
            }
            
            // Compile the query to SQL
            var compiled = _sqlCompiler.Compile(query);
            
            Log.Debug("✅ Successfully converted OData to SQL");
            Log.Debug("SQL Query: {SqlQuery}", compiled.Sql);
            Log.Debug("Parameters: {Parameters}", string.Join(", ", compiled.NamedBindings.Select(p => $"{p.Key}={p.Value}")));
            
            // Transfer the bindings from SqlKata to our parameters dictionary
            foreach (var binding in compiled.NamedBindings)
            {
                parameters[binding.Key] = binding.Value;
            }
            
            return (compiled.Sql, parameters);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error converting OData to SQL: {Message}", ex.Message);
            throw new InvalidOperationException($"Failed to convert OData to SQL: {ex.Message}", ex);
        }
    }
    
    private void ApplyFilter(Query query, string filter, ref int parameterCount, Dictionary<string, object> parameters)
    {
        // Basic filter parsing - this is a simplified version
        // In a production implementation, you would use a proper OData filter parser
        
        // Handle some common OData filter expressions
        
        // equality: Field eq 'value'
        var eqMatch = Regex.Match(filter, @"(\w+)\s+eq\s+'([^']*)'");
        if (eqMatch.Success)
        {
            var field = eqMatch.Groups[1].Value;
            var value = eqMatch.Groups[2].ToString();
            var paramName = $"p{parameterCount++}";
            
            query.Where(field, "=", value);
            parameters[paramName] = value;
            return;
        }
        
        // contains: contains(Field, 'value')
        var containsMatch = Regex.Match(filter, @"contains\((\w+),\s*'([^']*)'\)");
        if (containsMatch.Success)
        {
            var field = containsMatch.Groups[1].Value;
            var value = containsMatch.Groups[2].Value;
            var paramName = $"p{parameterCount++}";
            
            query.WhereRaw($"{field} LIKE '%' + @{paramName} + '%'");
            parameters[paramName] = value;
            return;
        }
        
        // greater than: Field gt value
        var gtMatch = Regex.Match(filter, @"(\w+)\s+gt\s+(\d+)");
        if (gtMatch.Success)
        {
            var field = gtMatch.Groups[1].Value;
            var valueStr = gtMatch.Groups[2].Value;
            var paramName = $"p{parameterCount++}";
            
            if (int.TryParse(valueStr, out var intValue))
            {
                query.Where(field, ">", intValue);
                parameters[paramName] = intValue;
                return;
            }
        }
        
        // If not a recognized pattern, try a raw where clause with warning
        Log.Warning("⚠️ Using unsupported or complex filter expression as raw SQL: {Filter}", filter);
        query.WhereRaw(filter);
    }
    
    private void ApplyOrderBy(Query query, string orderby)
    {
        var orderParts = orderby.Split(',');
        
        foreach (var part in orderParts)
        {
            var trimmedPart = part.Trim();
            var descending = trimmedPart.EndsWith(" desc", StringComparison.OrdinalIgnoreCase);
            
            var fieldName = descending
                ? trimmedPart.Substring(0, trimmedPart.Length - 5).Trim()
                : trimmedPart.EndsWith(" asc", StringComparison.OrdinalIgnoreCase)
                    ? trimmedPart.Substring(0, trimmedPart.Length - 4).Trim()
                    : trimmedPart;
            
            if (descending)
            {
                query.OrderByDesc(fieldName);
            }
            else
            {
                query.OrderBy(fieldName);
            }
        }
    }
}