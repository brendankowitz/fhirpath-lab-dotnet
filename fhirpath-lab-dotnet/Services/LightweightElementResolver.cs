using System.Text.RegularExpressions;
using Ignixa.Abstractions;
using Ignixa.Serialization.SourceNodes;

namespace FhirPathLab_DotNetEngine.Services;

/// <summary>
/// Lightweight reference resolver for FHIRPath resolve() function.
/// Creates minimal FHIR resources from reference strings to enable type checking
/// in expressions like: Encounter.participant.individual.where(resolve() is Practitioner)
/// Also resolves contained resources from the root resource context.
/// </summary>
public sealed class LightweightElementResolver
{
    private readonly IFhirSchemaProvider _schemaProvider;
    private IElement? _rootResource;

    // Regex to parse FHIR references like "Patient/123", "http://server/fhir/Patient/123"
    private static readonly Regex ReferencePattern = new(
        @"^(?:(?<base>https?://[^/]+/[^/]+/)?)?(?<type>[A-Z][a-zA-Z]+)/(?<id>[A-Za-z0-9\-\.]+)$",
        RegexOptions.Compiled);

    public LightweightElementResolver(IFhirSchemaProvider schemaProvider)
    {
        _schemaProvider = schemaProvider ?? throw new ArgumentNullException(nameof(schemaProvider));
    }

    /// <summary>
    /// Sets the root resource for resolving contained references.
    /// </summary>
    public void SetRootResource(IElement? rootResource)
    {
        _rootResource = rootResource;
    }

    /// <summary>
    /// Resolves a reference string to a FHIR resource element.
    /// Handles contained references (#id) if root resource is available.
    /// Creates minimal resources for external references to enable type checking.
    /// </summary>
    /// <param name="reference">The FHIR reference string (e.g., "Patient/123" or "#contained1")</param>
    /// <returns>An IElement representing the resolved resource, or null if parsing fails</returns>
    public IElement? Resolve(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        // Handle contained references like "#contained1"
        if (reference.StartsWith('#'))
        {
            return ResolveContained(reference[1..]); // Remove the '#' prefix
        }

        var parsed = ParseReference(reference);
        if (parsed == null)
        {
            return null;
        }

        // Create a minimal FHIR resource with just resourceType and id
        string json = $"{{\"resourceType\":\"{parsed.Value.ResourceType}\",\"id\":\"{parsed.Value.ResourceId}\"}}";

        // ToElement returns SchemaAwareElement which implements IElement
        var typedElement = ResourceJsonNode.Parse(json).ToElement(_schemaProvider);

        return (IElement)typedElement;
    }

    /// <summary>
    /// Resolves a contained resource by ID from the root resource.
    /// </summary>
    private IElement? ResolveContained(string containedId)
    {
        if (_rootResource == null)
        {
            return null;
        }

        var containedResources = _rootResource.Children("contained");
        foreach (var contained in containedResources)
        {
            var idChildren = contained.Children("id");
            if (idChildren.Count > 0)
            {
                var id = idChildren[0].Value?.ToString();
                if (id == containedId)
                {
                    return contained;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Parses a FHIR reference string into its components.
    /// </summary>
    private static (string ResourceType, string ResourceId)? ParseReference(string reference)
    {
        // Handle fragment references
        var fragmentIndex = reference.IndexOf('#');
        if (fragmentIndex > 0)
        {
            reference = reference[..fragmentIndex];
        }

        // Handle query parameters
        var queryIndex = reference.IndexOf('?');
        if (queryIndex > 0)
        {
            reference = reference[..queryIndex];
        }

        var match = ReferencePattern.Match(reference);
        if (!match.Success)
        {
            // Try simple "Type/id" format
            var parts = reference.Split('/');
            if (parts.Length >= 2)
            {
                // Find the last two parts that look like Type/id
                for (int i = parts.Length - 2; i >= 0; i--)
                {
                    if (parts[i].Length > 0 && char.IsUpper(parts[i][0]))
                    {
                        return (parts[i], parts[i + 1]);
                    }
                }
            }
            return null;
        }

        return (match.Groups["type"].Value, match.Groups["id"].Value);
    }
}
