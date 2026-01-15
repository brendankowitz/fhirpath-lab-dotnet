using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Ignixa.Abstractions;
using Ignixa.FhirPath.Evaluation;
using Ignixa.FhirPath.Parser;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;

namespace FhirPathLab_DotNetEngine;

public class FunctionFhirPathTest
{
    private readonly ILogger<FunctionFhirPathTest> _logger;
    private static readonly FhirPathParser _fhirPathParser = new();
    private static readonly FhirPathEvaluator _evaluator = new();
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private static readonly string _evaluatorVersion = GetEvaluatorVersion();

    public FunctionFhirPathTest(ILogger<FunctionFhirPathTest> logger)
    {
        _logger = logger;
    }

    private static string GetEvaluatorVersion()
    {
        var assembly = typeof(FhirPathEvaluator).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        return $"Ignixa-{version}";
    }

    [Function("FHIRPathTester-CapabilityStatement")]
    public IActionResult RunCapabilityStatement(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metadata")] HttpRequest req)
    {
        _logger.LogInformation("CapabilityStatement");

        var capabilityStatement = new JsonObject
        {
            ["resourceType"] = "CapabilityStatement",
            ["title"] = "FHIRPath Lab DotNet expression evaluator (Ignixa)",
            ["status"] = "active",
            ["date"] = "2026-01-15",
            ["kind"] = "instance",
            ["fhirVersion"] = "4.0.1",
            ["format"] = new JsonArray { "application/fhir+json" },
            ["rest"] = new JsonArray
            {
                new JsonObject
                {
                    ["mode"] = "server",
                    ["security"] = new JsonObject { ["cors"] = true },
                    ["operation"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "fhirpath",
                            ["definition"] = "http://fhirpath-lab.org/OperationDefinition/fhirpath"
                        }
                    }
                }
            }
        };

        return new ContentResult
        {
            Content = capabilityStatement.ToJsonString(_jsonOptions),
            ContentType = "application/fhir+json",
            StatusCode = (int)HttpStatusCode.OK
        };
    }

    [Function("FHIRPathTester")]
    public async Task<IActionResult> RunFhirPathTest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "$fhirpath")] HttpRequest req)
    {
        _logger.LogInformation("FhirPath Expression dotnet Evaluation (Ignixa)");

        ParametersJsonNode operationParameters;
        if (req.Method != "POST")
        {
            // Read the parameters from the request query string
            operationParameters = new ParametersJsonNode();
            foreach (var key in req.Query.Keys)
            {
                var param = new ParameterJsonNode { Name = key };
                param.SetValue("valueString", req.Query[key].ToString());
                operationParameters.Parameter.Add(param);
            }
        }
        else
        {
            // Read the FHIR parameters resource from the request body
            operationParameters = await JsonSourceNodeFactory.ParseAsync<ParametersJsonNode>(req.Body, CancellationToken.None);
        }

        var resourceParam = operationParameters.FindParameter("resource");
        var contextParam = operationParameters.FindParameter("context");
        var expressionParam = operationParameters.FindParameter("expression");
        var terminologyParam = operationParameters.FindParameter("terminologyserver");
        var variablesParam = operationParameters.FindParameter("variables");

        ResourceJsonNode? resource = resourceParam?.Resource;
        string? resourceId = resource == null ? resourceParam?.GetValueAs<string>() : null;
        string? context = contextParam?.GetValueAs<string>();
        string? expression = expressionParam?.GetValueAs<string>();
        string? terminologyServerUrl = terminologyParam?.GetValueAs<string>();

        // Load resource from remote server if URL provided
        if (resource == null && !string.IsNullOrEmpty(resourceId) && resourceId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var httpClient = new HttpClient();
                var response = await httpClient.GetStringAsync(resourceId);
                resource = JsonSourceNodeFactory.Parse<ResourceJsonNode>(response);
            }
            catch (HttpRequestException ex)
            {
                return CreateErrorResponse($"Unable to retrieve resource {resourceId}: {ex.Message}", resourceId);
            }
        }

        var result = EvaluateFhirPathTesterExpression(resourceId, resource, context, expression, terminologyServerUrl, variablesParam);

        return new ContentResult
        {
            Content = result.SerializeToString(pretty: true),
            ContentType = "application/fhir+json",
            StatusCode = (int)HttpStatusCode.OK
        };
    }

    private const string ExtensionUrlJsonValue = "http://fhir.forms-lab.com/StructureDefinition/json-value";

    public static ParametersJsonNode EvaluateFhirPathTesterExpression(
        string? resourceId,
        ResourceJsonNode? resource,
        string? context,
        string? expression,
        string? terminologyServerUrl,
        ParameterJsonNode? pcVariables)
    {
        var result = new ParametersJsonNode { Id = "fhirpath" };

        // Build configuration parameters
        var configParam = new ParameterJsonNode { Name = "parameters" };
        result.Parameter.Add(configParam);

        AddPart(configParam, "evaluator", _evaluatorVersion);
        if (!string.IsNullOrEmpty(context))
            AddPart(configParam, "context", context);
        if (!string.IsNullOrEmpty(expression))
            AddPart(configParam, "expression", expression);
        if (!string.IsNullOrEmpty(resourceId))
            AddPart(configParam, "resource", resourceId);
        else if (resource != null)
            AddResourcePart(configParam, "resource", resource);
        if (!string.IsNullOrEmpty(terminologyServerUrl))
            AddPart(configParam, "terminologyServerUrl", terminologyServerUrl);

        // Validate expression
        if (string.IsNullOrEmpty(expression))
        {
            return CreateOperationOutcomeResult("error", "required", "Expression parameter is required");
        }

        // Parse the expression
        Ignixa.FhirPath.Expressions.Expression parsedExpression;
        try
        {
            parsedExpression = _fhirPathParser.Parse(expression);
        }
        catch (Exception ex)
        {
            return CreateOperationOutcomeResult("error", "invalid", $"Invalid expression: {ex.Message}", expression);
        }

        // Create evaluation context with variables
        var evalContext = CreateEvaluationContext(pcVariables, resource);

        // Convert resource to IElement for evaluation
        IElement? inputElement = resource?.ToElement(null!);

        // Determine evaluation contexts
        var contextList = new Dictionary<string, IElement?>();
        if (!string.IsNullOrEmpty(context) && inputElement != null)
        {
            try
            {
                var contextExpr = _fhirPathParser.Parse(context);
                foreach (var ctxResult in _evaluator.Evaluate(inputElement, contextExpr, evalContext))
                {
                    contextList[ctxResult.Location] = ctxResult;
                }
            }
            catch (Exception ex)
            {
                return CreateOperationOutcomeResult("error", "invalid", $"Invalid context expression: {ex.Message}", context);
            }
        }
        else
        {
            contextList[""] = inputElement;
        }

        // Execute expression for each context
        foreach (var (contextKey, contextElement) in contextList)
        {
            IEnumerable<IElement> outputValues;
            try
            {
                outputValues = contextElement != null
                    ? _evaluator.Evaluate(contextElement, parsedExpression, evalContext).ToList()
                    : [];
            }
            catch (Exception ex)
            {
                var errorParam = new ParameterJsonNode { Name = "error" };
                errorParam.SetValue("valueString", $"Expression evaluation error: {ex.Message}");
                result.Parameter.Add(errorParam);
                return result;
            }

            var resultParam = new ParameterJsonNode { Name = "result" };
            if (!string.IsNullOrEmpty(contextKey))
                resultParam.SetValue("valueString", contextKey);
            result.Parameter.Add(resultParam);

            foreach (var outputValue in outputValues)
            {
                var resultPart = new ParameterJsonNode { Name = outputValue.InstanceType ?? "(null)" };
                resultParam.Part.Add(resultPart);

                if (outputValue.Value != null)
                {
                    // Primitive value - set appropriate value[x]
                    SetTypedValue(resultPart, outputValue.InstanceType!, outputValue.Value);
                }
                else
                {
                    // Complex type - serialize as JSON extension
                    var json = SerializeElementToJson(outputValue);
                    AddExtension(resultPart, ExtensionUrlJsonValue, json);
                }
            }
        }

        return result;
    }

    private static EvaluationContext CreateEvaluationContext(ParameterJsonNode? pcVariables, ResourceJsonNode? resource)
    {
        EvaluationContext evalContext = new FhirEvaluationContext();

        // Set %resource variable if a resource is provided
        if (resource != null)
        {
            var resourceElement = resource.ToElement(null!);
            evalContext = ((FhirEvaluationContext)evalContext).WithResource(resourceElement).WithRootResource(resourceElement);
        }

        if (pcVariables?.Part == null)
            return evalContext;

        foreach (var varParam in pcVariables.Part)
        {
            var varValue = varParam.GetValue();
            if (varValue == null)
                continue;

            // Parse variable value as FHIR element by wrapping it in a temp structure
            var wrapperJson = new JsonObject { ["resourceType"] = "Basic", ["extension"] = new JsonArray { new JsonObject { ["url"] = "value", ["value"] = varValue.DeepClone() } } };
            var wrapper = JsonSourceNodeFactory.Parse<ResourceJsonNode>(wrapperJson.ToJsonString());
            var wrapperElement = wrapper.ToElement(null!);

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

    private static void AddPart(ParameterJsonNode parent, string name, string value)
    {
        var part = new ParameterJsonNode { Name = name };
        part.SetValue("valueString", value);
        parent.Part.Add(part);
    }

    private static void AddResourcePart(ParameterJsonNode parent, string name, ResourceJsonNode resource)
    {
        var part = new ParameterJsonNode { Name = name };
        part.MutableNode["resource"] = JsonNode.Parse(resource.SerializeToString());
        parent.Part.Add(part);
    }

    private static void SetTypedValue(ParameterJsonNode param, string instanceType, object value)
    {
        var valueTypeName = $"value{char.ToUpperInvariant(instanceType[0])}{instanceType.Substring(1)}";
        param.SetValue(valueTypeName, JsonValue.Create(value));
    }

    private static void AddExtension(ParameterJsonNode param, string url, string value)
    {
        param.MutableNode["extension"] = new JsonArray
        {
            new JsonObject
            {
                ["url"] = url,
                ["valueString"] = value
            }
        };
    }

    private static string SerializeElementToJson(IElement element)
    {
        var obj = new JsonObject();
        SerializeElementChildren(element, obj);
        return obj.ToJsonString();
    }

    private static void SerializeElementChildren(IElement element, JsonObject target)
    {
        foreach (var child in element.Children())
        {
            if (child.Value != null)
            {
                target[child.Name] = JsonValue.Create(child.Value);
            }
            else
            {
                var childObj = new JsonObject();
                SerializeElementChildren(child, childObj);
                target[child.Name] = childObj;
            }
        }
    }

    private static ParametersJsonNode CreateOperationOutcomeResult(string severity, string code, string message, string? diagnostics = null)
    {
        var outcome = new OperationOutcomeJsonNode();
        var issue = new OperationOutcomeJsonNode.IssueComponent
        {
            Severity = severity switch
            {
                "error" => OperationOutcomeJsonNode.IssueSeverity.Error,
                "warning" => OperationOutcomeJsonNode.IssueSeverity.Warning,
                "information" => OperationOutcomeJsonNode.IssueSeverity.Information,
                _ => OperationOutcomeJsonNode.IssueSeverity.Error
            },
            Code = code switch
            {
                "required" => OperationOutcomeJsonNode.IssueType.Required,
                "invalid" => OperationOutcomeJsonNode.IssueType.Invalid,
                "not-found" => OperationOutcomeJsonNode.IssueType.NotFound,
                _ => OperationOutcomeJsonNode.IssueType.Exception
            },
            Details = new CodeableConceptJsonNode { Text = message },
            Diagnostics = diagnostics!
        };
        outcome.Issue.Add(issue);

        // Return as Parameters wrapping the OperationOutcome for consistent API
        var result = new ParametersJsonNode { Id = "fhirpath" };
        var errorParam = new ParameterJsonNode { Name = "outcome" };
        errorParam.MutableNode["resource"] = JsonNode.Parse(outcome.SerializeToString());
        result.Parameter.Add(errorParam);
        return result;
    }

    private static IActionResult CreateErrorResponse(string message, string? diagnostics = null)
    {
        var outcome = new OperationOutcomeJsonNode();
        outcome.Issue.Add(new OperationOutcomeJsonNode.IssueComponent
        {
            Severity = OperationOutcomeJsonNode.IssueSeverity.Error,
            Code = OperationOutcomeJsonNode.IssueType.NotFound,
            Details = new CodeableConceptJsonNode { Text = message },
            Diagnostics = diagnostics!
        });

        return new ContentResult
        {
            Content = outcome.SerializeToString(pretty: true),
            ContentType = "application/fhir+json",
            StatusCode = (int)HttpStatusCode.BadRequest
        };
    }

    [Function("Warmer")]
    public void WarmUp([TimerTrigger("0 */15 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Warmer function executed at: {time}", DateTime.Now);
    }
}
