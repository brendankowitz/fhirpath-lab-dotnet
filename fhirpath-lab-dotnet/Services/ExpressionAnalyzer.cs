using Ignixa.FhirPath.Analysis;
using Ignixa.FhirPath.Expressions;
using Ignixa.FhirPath.Parser;
using Ignixa.FhirPath.Visitors;
using FhirPathLab_DotNetEngine.Models;

namespace FhirPathLab_DotNetEngine.Services;

/// <summary>
/// Service for parsing and analyzing FHIRPath expressions.
/// </summary>
public sealed class ExpressionAnalyzer
{
    private static readonly FhirPathParser Parser = new();
    private readonly SchemaProviderFactory _schemaFactory;

    public ExpressionAnalyzer(SchemaProviderFactory schemaFactory)
    {
        _schemaFactory = schemaFactory;
    }

    /// <summary>
    /// Parses a FHIRPath expression string.
    /// </summary>
    /// <param name="expression">The FHIRPath expression to parse.</param>
    /// <returns>The parsed expression.</returns>
    /// <exception cref="FormatException">Thrown when the expression is invalid.</exception>
    public Expression Parse(string expression) => Parser.Parse(expression);

    /// <summary>
    /// Parses and analyzes a FHIRPath expression.
    /// </summary>
    /// <param name="expression">The FHIRPath expression string.</param>
    /// <param name="contextExpression">Optional context expression string.</param>
    /// <param name="rootTypeName">The root type name for analysis (e.g., "Patient").</param>
    /// <param name="fhirVersion">The FHIR version to use.</param>
    /// <returns>A tuple with the parsed expression and analysis results, or error information.</returns>
    public (ParsedExpression? Parsed, Expression? ContextExpr, string? Error) ParseAndAnalyze(
        string expression,
        string? contextExpression,
        string? rootTypeName,
        string fhirVersion)
    {
        // Parse main expression
        Expression parsedExpression;
        try
        {
            parsedExpression = Parse(expression);
        }
        catch (Exception ex)
        {
            return (null, null, $"Invalid expression: {ex.Message}");
        }

        // Parse context expression if provided
        Expression? contextExpr = null;
        if (!string.IsNullOrEmpty(contextExpression))
        {
            try
            {
                contextExpr = Parse(contextExpression);
            }
            catch (Exception ex)
            {
                return (null, null, $"Invalid context expression: {ex.Message}");
            }
        }

        // Analyze expressions
        string? expressionScopeType = rootTypeName;
        AnalysisResult? analysisResult = null;
        List<ValidationIssue>? validationIssues = null;

        if (!string.IsNullOrEmpty(rootTypeName))
        {
            try
            {
                var analyzer = _schemaFactory.GetAnalyzer(fhirVersion);

                // If context expression provided, analyze it to determine the scope type
                if (contextExpr != null)
                {
                    var contextAnalysis = analyzer.Analyze(contextExpr, rootTypeName);
                    var contextTypes = contextAnalysis.InferredTypes?.Types;
                    if (contextTypes?.Count > 0 && !string.IsNullOrEmpty(contextTypes[0].TypeName))
                    {
                        expressionScopeType = contextTypes[0].TypeName;
                    }
                }

                // Analyze main expression (expressionScopeType is non-null here since we're inside the rootTypeName check)
                analysisResult = analyzer.Analyze(parsedExpression, expressionScopeType!);
                validationIssues = analysisResult.Issues.ToList();
            }
            catch
            {
                // Analysis is optional - continue without it
            }
        }

        var parsed = new ParsedExpression(
            parsedExpression,
            analysisResult,
            validationIssues,
            expressionScopeType);

        return (parsed, contextExpr, null);
    }
}
