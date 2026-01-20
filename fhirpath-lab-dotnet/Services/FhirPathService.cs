using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using FhirPathLab_DotNetEngine.Models;

namespace FhirPathLab_DotNetEngine.Services;

/// <summary>
/// Main orchestrator service for FHIRPath evaluation.
/// Coordinates parsing, analysis, evaluation, and result formatting.
/// </summary>
public sealed class FhirPathService
{
    private readonly ExpressionAnalyzer _analyzer;
    private readonly ExpressionEvaluator _evaluator;
    private readonly ResultFormatter _formatter;

    public FhirPathService(
        ExpressionAnalyzer analyzer,
        ExpressionEvaluator evaluator,
        ResultFormatter formatter)
    {
        _analyzer = analyzer;
        _evaluator = evaluator;
        _formatter = formatter;
    }

    /// <summary>
    /// Processes a FHIRPath evaluation request and returns the formatted result.
    /// </summary>
    /// <param name="request">The FHIRPath request containing expression and resource.</param>
    /// <returns>A FHIR Parameters resource containing the evaluation results.</returns>
    public ResourceJsonNode ProcessRequest(FhirPathRequest request)
    {
        var result = Evaluate(request);
        return _formatter.FormatResult(result);
    }

    /// <summary>
    /// Evaluates a FHIRPath request and returns the structured result.
    /// </summary>
    /// <param name="request">The FHIRPath request.</param>
    /// <returns>The evaluation result.</returns>
    public FhirPathResult Evaluate(FhirPathRequest request)
    {
        // Validate required input
        if (string.IsNullOrEmpty(request.Expression))
        {
            return new FhirPathResult
            {
                Request = request,
                Error = "Expression parameter is required"
            };
        }

        // Parse and analyze expressions
        var rootTypeName = request.Resource?.ResourceType;
        var (parsed, contextExpr, parseError) = _analyzer.ParseAndAnalyze(
            request.Expression,
            request.Context,
            rootTypeName,
            request.FhirVersion);

        if (parseError != null)
        {
            return new FhirPathResult
            {
                Request = request,
                Error = parseError,
                ErrorDiagnostics = request.Expression
            };
        }

        // Evaluate the expression
        var evaluationResults = _evaluator.Evaluate(
            parsed!,
            contextExpr,
            request.Resource,
            request.Variables,
            request.FhirVersion);

        return new FhirPathResult
        {
            Request = request,
            ParsedExpression = parsed,
            ContextExpression = contextExpr,
            Results = evaluationResults
        };
    }

    /// <summary>
    /// Loads a resource from a remote URL.
    /// </summary>
    /// <param name="url">The URL to fetch the resource from.</param>
    /// <returns>The parsed resource or null if fetch failed.</returns>
    public static async Task<(ResourceJsonNode? Resource, string? Error)> LoadResourceFromUrl(string url)
    {
        try
        {
            using var httpClient = new HttpClient();
            var response = await httpClient.GetStringAsync(url);
            var resource = JsonSourceNodeFactory.Parse<ResourceJsonNode>(response);
            return (resource, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Unable to retrieve resource {url}: {ex.Message}");
        }
    }
}
