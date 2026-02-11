using Ignixa.Abstractions;
using Ignixa.FhirPath.Analysis;
using Ignixa.FhirPath.Evaluation;
using Ignixa.FhirPath.Expressions;
using Ignixa.FhirPath.Visitors;

namespace FhirPathLab_DotNetEngine.Models;

/// <summary>
/// Result of parsing and analyzing a FHIRPath expression.
/// </summary>
public sealed record ParsedExpression(
    Expression Expression,
    AnalysisResult? Analysis,
    List<ValidationIssue>? ValidationIssues,
    string? ExpressionScopeType);

/// <summary>
/// Result of evaluating a FHIRPath expression against a single context.
/// </summary>
public sealed record EvaluationResult(
    string ContextPath,
    List<IElement> OutputValues,
    List<TraceEntry> TraceOutput,
    List<NodeEvaluationEntry> DebugTraceEntries,
    string? Error);

/// <summary>
/// Complete result of a FHIRPath evaluation request.
/// </summary>
public sealed class FhirPathResult
{
    /// <summary>
    /// The original request that produced this result.
    /// </summary>
    public required FhirPathRequest Request { get; init; }

    /// <summary>
    /// The parsed and analyzed main expression.
    /// </summary>
    public ParsedExpression? ParsedExpression { get; init; }

    /// <summary>
    /// The parsed context expression (if provided).
    /// </summary>
    public Expression? ContextExpression { get; init; }

    /// <summary>
    /// Evaluation results for each context node.
    /// </summary>
    public List<EvaluationResult> Results { get; init; } = [];

    /// <summary>
    /// Error message if the request failed before evaluation.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Error details/diagnostics if the request failed.
    /// </summary>
    public string? ErrorDiagnostics { get; init; }

    /// <summary>
    /// Whether the request was successful.
    /// </summary>
    public bool IsSuccess => Error == null;
}
