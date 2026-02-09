using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;

namespace FhirPathLab_DotNetEngine.Models;

/// <summary>
/// Represents the input parameters for a FHIRPath evaluation request.
/// </summary>
public sealed class FhirPathRequest
{
    /// <summary>
    /// The FHIR resource to evaluate the expression against.
    /// </summary>
    public ResourceJsonNode? Resource { get; init; }

    /// <summary>
    /// URL to fetch the resource from if Resource is not directly provided.
    /// </summary>
    public string? ResourceId { get; init; }

    /// <summary>
    /// Optional FHIRPath expression to select context nodes within the resource.
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// The FHIRPath expression to evaluate.
    /// </summary>
    public required string Expression { get; init; }

    /// <summary>
    /// Optional terminology server URL for vocabulary operations.
    /// </summary>
    public string? TerminologyServerUrl { get; init; }

    /// <summary>
    /// Optional variables to make available during expression evaluation.
    /// </summary>
    public ParameterJsonNode? Variables { get; init; }

    /// <summary>
    /// Whether to include trace output in the response.
    /// </summary>
    public bool DebugTrace { get; init; }

    /// <summary>
    /// The FHIR version to use for evaluation (e.g., "R4", "R5").
    /// </summary>
    public required string FhirVersion { get; init; }
}
