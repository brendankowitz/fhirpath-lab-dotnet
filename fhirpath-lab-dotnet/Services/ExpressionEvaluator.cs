using System.Text.Json.Nodes;
using Ignixa.Abstractions;
using Ignixa.FhirPath.Evaluation;
using Ignixa.FhirPath.Expressions;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using FhirPathLab_DotNetEngine.Models;

namespace FhirPathLab_DotNetEngine.Services;

/// <summary>
/// Service for evaluating FHIRPath expressions against FHIR resources.
/// </summary>
public sealed class ExpressionEvaluator
{
    private static readonly FhirPathEvaluator Evaluator = new();
    private readonly SchemaProviderFactory _schemaFactory;

    public ExpressionEvaluator(SchemaProviderFactory schemaFactory)
    {
        _schemaFactory = schemaFactory;
    }

    /// <summary>
    /// Gets the evaluation contexts based on the context expression.
    /// </summary>
    /// <param name="inputElement">The input element to evaluate against.</param>
    /// <param name="contextExpression">Optional context expression to select sub-elements.</param>
    /// <param name="variables">Optional variables for the evaluation context.</param>
    /// <param name="resource">The resource for variable binding.</param>
    /// <param name="schema">The FHIR schema to use.</param>
    /// <returns>Dictionary mapping context paths to elements.</returns>
    public Dictionary<string, IElement?> GetEvaluationContexts(
        IElement? inputElement,
        Expression? contextExpression,
        ParameterJsonNode? variables,
        ResourceJsonNode? resource,
        ISchema schema)
    {
        var contexts = new Dictionary<string, IElement?>();

        if (contextExpression != null && inputElement != null)
        {
            var evalContext = CreateEvaluationContext(variables, resource, schema, new List<TraceEntry>());
            foreach (var ctx in Evaluator.Evaluate(inputElement, contextExpression, evalContext))
            {
                contexts[ctx.Location] = ctx;
            }
        }
        else
        {
            contexts[""] = inputElement;
        }

        return contexts;
    }

    /// <summary>
    /// Evaluates a FHIRPath expression against all context elements.
    /// </summary>
    /// <param name="parsedExpression">The parsed expression to evaluate.</param>
    /// <param name="contextExpression">Optional context expression.</param>
    /// <param name="resource">The FHIR resource.</param>
    /// <param name="variables">Optional variables.</param>
    /// <param name="fhirVersion">The FHIR version.</param>
    /// <returns>List of evaluation results for each context.</returns>
    public List<EvaluationResult> Evaluate(
        ParsedExpression parsedExpression,
        Expression? contextExpression,
        ResourceJsonNode? resource,
        ParameterJsonNode? variables,
        string fhirVersion)
    {
        var schema = _schemaFactory.GetSchema(fhirVersion);
        IElement? inputElement = resource?.ToElement(schema);
        var contexts = GetEvaluationContexts(inputElement, contextExpression, variables, resource, schema);
        var results = new List<EvaluationResult>();

        foreach (var (contextPath, contextElement) in contexts)
        {
            var traceOutput = new List<TraceEntry>();
            var evalContext = CreateEvaluationContext(variables, resource, schema, traceOutput);

            List<IElement> outputValues;
            string? error = null;

            try
            {
                if (contextElement == null)
                {
                    outputValues = [];
                }
                else
                {
                    outputValues = Evaluator.Evaluate(contextElement, parsedExpression.Expression, evalContext).ToList();
                }
            }
            catch (Exception ex)
            {
                outputValues = [];
                error = $"Expression evaluation error: {ex.Message}";
            }

            results.Add(new EvaluationResult(contextPath, outputValues, traceOutput, error));
        }

        return results;
    }

    /// <summary>
    /// Creates an evaluation context with the provided variables and resource.
    /// </summary>
    public EvaluationContext CreateEvaluationContext(
        ParameterJsonNode? pcVariables,
        ResourceJsonNode? resource,
        ISchema schema,
        List<TraceEntry>? traceOutput)
    {
        EvaluationContext evalContext = new FhirEvaluationContext();

        // Set up trace handler if trace output collection is provided
        if (traceOutput != null)
        {
            evalContext = evalContext.WithTraceHandler(entry => traceOutput.Add(entry));
        }

        // Set %resource variable if a resource is provided
        if (resource != null)
        {
            var resourceElement = resource.ToElement(schema);
            evalContext = ((FhirEvaluationContext)evalContext)
                .WithResource(resourceElement)
                .WithRootResource(resourceElement);
        }

        if (pcVariables?.Part == null)
        {
            return evalContext;
        }

        foreach (var varParam in pcVariables.Part)
        {
            var varValue = varParam.GetValue();
            if (varValue == null)
            {
                continue;
            }

            // Parse variable value as FHIR element by wrapping it in a temp structure
            var wrapperJson = new JsonObject
            {
                ["resourceType"] = "Basic",
                ["extension"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["url"] = "value",
                        ["value"] = varValue.DeepClone()
                    }
                }
            };
            var wrapper = JsonSourceNodeFactory.Parse<ResourceJsonNode>(wrapperJson.ToJsonString());
            var wrapperElement = wrapper.ToElement(schema);

            // Extract the value from the extension
            var valueElements = wrapperElement.Children("extension")
                .SelectMany(ext => ext.Children("value"))
                .ToList();

            if (valueElements.Count > 0)
            {
                evalContext = evalContext.WithEnvironmentVariable(varParam.Name, valueElements);
            }
        }

        return evalContext;
    }
}
