using Ignixa.Abstractions;
using Ignixa.FhirPath.Analysis;
using Ignixa.Specification.Generated;
using Ignixa.Specification.Extensions;

namespace FhirPathLab_DotNetEngine.Services;

/// <summary>
/// Factory for creating and caching FHIR schema providers and analyzers.
/// Uses lazy initialization to defer expensive schema loading until first use.
/// </summary>
public sealed class SchemaProviderFactory
{
    // Lazy-initialized schema providers for all FHIR versions
    private static readonly Lazy<IFhirSchemaProvider> Stu3Schema = new(() => FhirVersion.Stu3.GetSchemaProvider());
    private static readonly Lazy<IFhirSchemaProvider> R4Schema = new(() => FhirVersion.R4.GetSchemaProvider());
    private static readonly Lazy<IFhirSchemaProvider> R4BSchema = new(() => FhirVersion.R4B.GetSchemaProvider());
    private static readonly Lazy<IFhirSchemaProvider> R5Schema = new(() => FhirVersion.R5.GetSchemaProvider());
    private static readonly Lazy<IFhirSchemaProvider> R6Schema = new(() => FhirVersion.R6.GetSchemaProvider());

    // Lazy-initialized analyzers for each FHIR version (stateless, safe to reuse)
    private static readonly Lazy<FhirPathAnalyzer> Stu3Analyzer = new(() => new FhirPathAnalyzer(Stu3Schema.Value));
    private static readonly Lazy<FhirPathAnalyzer> R4Analyzer = new(() => new FhirPathAnalyzer(R4Schema.Value));
    private static readonly Lazy<FhirPathAnalyzer> R4BAnalyzer = new(() => new FhirPathAnalyzer(R4BSchema.Value));
    private static readonly Lazy<FhirPathAnalyzer> R5Analyzer = new(() => new FhirPathAnalyzer(R5Schema.Value));
    private static readonly Lazy<FhirPathAnalyzer> R6Analyzer = new(() => new FhirPathAnalyzer(R6Schema.Value));

    /// <summary>
    /// Gets the FHIR schema provider for the specified FHIR version.
    /// </summary>
    /// <param name="fhirVersion">The FHIR version (e.g., "R4", "R5", "STU3").</param>
    /// <returns>The schema provider for the specified version, defaults to R4 if unknown.</returns>
    public ISchema GetSchema(string fhirVersion) => fhirVersion.ToUpperInvariant() switch
    {
        "STU3" or "R3" => Stu3Schema.Value,
        "R4" => R4Schema.Value,
        "R4B" => R4BSchema.Value,
        "R5" => R5Schema.Value,
        "R6" => R6Schema.Value,
        _ => R4Schema.Value
    };

    /// <summary>
    /// Gets the FHIRPath analyzer for the specified FHIR version.
    /// </summary>
    /// <param name="fhirVersion">The FHIR version (e.g., "R4", "R5", "STU3").</param>
    /// <returns>The analyzer for the specified version, defaults to R4 if unknown.</returns>
    public FhirPathAnalyzer GetAnalyzer(string fhirVersion) => fhirVersion.ToUpperInvariant() switch
    {
        "STU3" or "R3" => Stu3Analyzer.Value,
        "R4" => R4Analyzer.Value,
        "R4B" => R4BAnalyzer.Value,
        "R5" => R5Analyzer.Value,
        "R6" => R6Analyzer.Value,
        _ => R4Analyzer.Value
    };
}
