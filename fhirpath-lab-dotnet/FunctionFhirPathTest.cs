using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Ignixa.Abstractions;
using Ignixa.FhirPath.Analysis;
using Ignixa.FhirPath.Evaluation;
using Ignixa.FhirPath.Expressions;
using Ignixa.FhirPath.Parser;
using Ignixa.FhirPath.Visitors;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using Ignixa.Specification.Generated;
using Ignixa.Specification.Extensions;
using FhirPathLab_DotNetEngine.Serialization;

namespace FhirPathLab_DotNetEngine;

public class FunctionFhirPathTest
{
    private readonly ILogger<FunctionFhirPathTest> _logger;
    private static readonly FhirPathParser _fhirPathParser = new();
    private static readonly FhirPathEvaluator _evaluator = new();
    
    // Lazy-initialized analyzers for each FHIR version
    private static readonly Lazy<FhirPathAnalyzer> _stu3Analyzer = new(() => new FhirPathAnalyzer(_stu3Schema.Value));
    private static readonly Lazy<FhirPathAnalyzer> _r4Analyzer = new(() => new FhirPathAnalyzer(_r4Schema.Value));
    private static readonly Lazy<FhirPathAnalyzer> _r4bAnalyzer = new(() => new FhirPathAnalyzer(_r4bSchema.Value));
    private static readonly Lazy<FhirPathAnalyzer> _r5Analyzer = new(() => new FhirPathAnalyzer(_r5Schema.Value));
    private static readonly Lazy<FhirPathAnalyzer> _r6Analyzer = new(() => new FhirPathAnalyzer(_r6Schema.Value));
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private static readonly string _evaluatorVersion = GetEvaluatorVersion();
    
    // Lazy-initialized schema providers for all FHIR versions
    private static readonly Lazy<IFhirSchemaProvider> _stu3Schema = new(() => FhirVersion.Stu3.GetSchemaProvider());
    private static readonly Lazy<IFhirSchemaProvider> _r4Schema = new(() => FhirVersion.R4.GetSchemaProvider());
    private static readonly Lazy<IFhirSchemaProvider> _r4bSchema = new(() => FhirVersion.R4B.GetSchemaProvider());
    private static readonly Lazy<IFhirSchemaProvider> _r5Schema = new(() => FhirVersion.R5.GetSchemaProvider());
    private static readonly Lazy<IFhirSchemaProvider> _r6Schema = new(() => FhirVersion.R6.GetSchemaProvider());

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

    private static ISchema GetSchemaForVersion(string fhirVersion) => fhirVersion.ToUpperInvariant() switch
    {
        "STU3" or "R3" => _stu3Schema.Value,
        "R4" => _r4Schema.Value,
        "R4B" => _r4bSchema.Value,
        "R5" => _r5Schema.Value,
        "R6" => _r6Schema.Value,
        _ => _r4Schema.Value
    };

    private static FhirPathAnalyzer GetAnalyzerForVersion(string fhirVersion) => fhirVersion.ToUpperInvariant() switch
    {
        "STU3" or "R3" => _stu3Analyzer.Value,
        "R4" => _r4Analyzer.Value,
        "R4B" => _r4bAnalyzer.Value,
        "R5" => _r5Analyzer.Value,
        "R6" => _r6Analyzer.Value,
        _ => _r4Analyzer.Value
    };

    [Function("FHIRPathTester-CapabilityStatement")]
    public IActionResult RunCapabilityStatement(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metadata")] HttpRequest req)
    {
        _logger.LogInformation("CapabilityStatement");

        var capabilityStatement = new JsonObject
        {
            ["resourceType"] = "CapabilityStatement",
            ["title"] = "FHIRPath Lab DotNet expression evaluator (Ignixa)",
            ["description"] = "Supports FHIR STU3, R4, R4B, R5, R6. Features: AST output, trace, validation.",
            ["status"] = "active",
            ["date"] = "2026-01-19",
            ["kind"] = "instance",
            ["fhirVersion"] = "4.0.1",
            ["format"] = new JsonArray { "application/fhir+json" },
            ["implementationGuide"] = new JsonArray { "STU3", "R4", "R4B", "R5", "R6" },
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
        return await ProcessFhirPathRequest(req, "R4");
    }

    [Function("FHIRPathTester-STU3")]
    public async Task<IActionResult> RunFhirPathTestStu3(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "$fhirpath-stu3")] HttpRequest req)
    {
        return await ProcessFhirPathRequest(req, "STU3");
    }

    [Function("FHIRPathTester-R4B")]
    public async Task<IActionResult> RunFhirPathTestR4B(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "$fhirpath-r4b")] HttpRequest req)
    {
        return await ProcessFhirPathRequest(req, "R4B");
    }

    [Function("FHIRPathTester-R5")]
    public async Task<IActionResult> RunFhirPathTestR5(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "$fhirpath-r5")] HttpRequest req)
    {
        return await ProcessFhirPathRequest(req, "R5");
    }

    [Function("FHIRPathTester-R6")]
    public async Task<IActionResult> RunFhirPathTestR6(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "$fhirpath-r6")] HttpRequest req)
    {
        return await ProcessFhirPathRequest(req, "R6");
    }

    private async Task<IActionResult> ProcessFhirPathRequest(HttpRequest req, string fhirVersion)
    {
        _logger.LogInformation("FhirPath Expression Evaluation (Ignixa) - FHIR {Version}", fhirVersion);

        ParametersJsonNode operationParameters;
        if (req.Method != "POST")
        {
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
            operationParameters = await JsonSourceNodeFactory.ParseAsync<ParametersJsonNode>(req.Body, CancellationToken.None);
        }

        var resourceParam = operationParameters.FindParameter("resource");
        var contextParam = operationParameters.FindParameter("context");
        var expressionParam = operationParameters.FindParameter("expression");
        var terminologyParam = operationParameters.FindParameter("terminologyserver");
        var variablesParam = operationParameters.FindParameter("variables");
        var debugTraceParam = operationParameters.FindParameter("debug_trace");

        ResourceJsonNode? resource = resourceParam?.Resource;
        string? resourceId = resource == null ? resourceParam?.GetValueAs<string>() : null;
        string? context = contextParam?.GetValueAs<string>();
        string? expression = expressionParam?.GetValueAs<string>();
        string? terminologyServerUrl = terminologyParam?.GetValueAs<string>();
        bool debugTrace = debugTraceParam?.GetValueAs<bool>() ?? false;

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

        var schema = GetSchemaForVersion(fhirVersion);
        var result = EvaluateFhirPathTesterExpression(resourceId, resource, context, expression, terminologyServerUrl, variablesParam, debugTrace, schema, fhirVersion);

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
        ParameterJsonNode? pcVariables,
        bool debugTrace,
        ISchema schema,
        string fhirVersion)
    {
        var result = new ParametersJsonNode { Id = "fhirpath" };
        var traceOutput = new List<Ignixa.FhirPath.Evaluation.TraceEntry>();

        // Build configuration parameters
        var configParam = new ParameterJsonNode { Name = "parameters" };
        result.Parameter.Add(configParam);

        AddPart(configParam, "evaluator", $"{_evaluatorVersion} ({fhirVersion})");
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
        Expression parsedExpression;
        try
        {
            parsedExpression = _fhirPathParser.Parse(expression);
        }
        catch (Exception ex)
        {
            return CreateOperationOutcomeResult("error", "invalid", $"Invalid expression: {ex.Message}", expression);
        }

        // Validate expression against schema if we have a resource type
        string? rootTypeName = resource?.ResourceType;
        AnalysisResult? analysisResult = null;
        
        if (!string.IsNullOrEmpty(rootTypeName))
        {
            try
            {
                var analyzer = GetAnalyzerForVersion(fhirVersion);
                
                // Determine the validation root type - if context is specified, 
                // analyze the context expression to find the resulting type
                string validationTypeName = rootTypeName;
                if (!string.IsNullOrEmpty(context))
                {
                    try
                    {
                        var contextResult = analyzer.Analyze(context, rootTypeName);
                        // Get the first inferred type from the context expression
                        var contextTypes = contextResult.InferredTypes?.Types;
                        if (contextTypes?.Count > 0)
                        {
                            var contextType = contextTypes[0];
                            if (!string.IsNullOrEmpty(contextType.TypeName))
                            {
                                validationTypeName = contextType.TypeName;
                            }
                        }
                    }
                    catch
                    {
                        // If context analysis fails, fall back to root type
                    }
                }
                
                // Analyze the expression to get inferred types for AST
                analysisResult = analyzer.Analyze(parsedExpression, validationTypeName);
                
                var validationIssues = analysisResult.Issues.ToList();
                if (validationIssues.Count > 0)
                {
                    // Create an OperationOutcome resource for validation issues
                    var outcome = new JsonObject
                    {
                        ["resourceType"] = "OperationOutcome",
                        ["issue"] = new JsonArray(validationIssues.Select(issue => 
                        {
                            var issueNode = new JsonObject
                            {
                                ["severity"] = issue.Severity.ToString().ToLowerInvariant(),
                                ["code"] = GetIssueCode(issue),
                                ["diagnostics"] = issue.Message
                            };
                            
                            if (!string.IsNullOrEmpty(issue.Location))
                            {
                                issueNode["location"] = new JsonArray(issue.Location);
                            }
                            
                            return issueNode;
                        }).ToArray())
                    };
                    
                    // Add as debugOutcome part inside parameters
                    var outcomePart = new ParameterJsonNode { Name = "debugOutcome" };
                    outcomePart.MutableNode["resource"] = JsonNode.Parse(outcome.ToJsonString());
                    configParam.Part.Add(outcomePart);
                }
            }
            catch (Exception ex)
            {
                // If validation fails, log but continue with evaluation
                var warnParam = new ParameterJsonNode { Name = "warning" };
                warnParam.SetValue("valueString", $"Validation skipped: {ex.Message}");
                result.Parameter.Add(warnParam);
            }
        }

        // Add AST output inside the parameters part (after analysis for return type info)
        var astPart = new ParameterJsonNode { Name = "parseDebugTree" };
        astPart.SetValue("valueString", ExpressionToJsonAst(parsedExpression, analysisResult));
        configParam.Part.Add(astPart);

        // Create evaluation context with variables and trace handler
        var evalContext = CreateEvaluationContext(pcVariables, resource, schema, debugTrace ? traceOutput : null);

        // Convert resource to IElement for evaluation
        IElement? inputElement = resource?.ToElement(schema);

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
                // Evaluate expression - use empty collection if no context element
                outputValues = _evaluator.Evaluate(contextElement, parsedExpression, evalContext).ToList();
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

        // Add trace output if enabled
        if (debugTrace && traceOutput.Count > 0)
        {
            foreach (var trace in traceOutput)
            {
                var traceParam = new ParameterJsonNode { Name = "trace" };
                traceParam.SetValue("valueString", trace.Name);
                result.Parameter.Add(traceParam);

                // Add trace items for each focus element
                foreach (var element in trace.Focus)
                {
                    var elementPart = new ParameterJsonNode { Name = element.InstanceType ?? string.Empty };
                    traceParam.Part.Add(elementPart);

                    if (element.Value != null)
                    {
                        SetTypedValue(elementPart, element.InstanceType!, element.Value);
                    }
                    else
                    {
                        // Complex type - serialize as JSON extension
                        var json = SerializeElementToJson(element);
                        AddExtension(elementPart, ExtensionUrlJsonValue, json);
                    }

                    // Add resource-path extension if available
                    if (!string.IsNullOrEmpty(element.Location))
                    {
                        var extensionArray = elementPart.MutableNode["extension"]?.AsArray() ?? new JsonArray();
                        extensionArray.Add(new JsonObject
                        {
                            ["url"] = "http://fhir.forms-lab.com/StructureDefinition/resource-path",
                            ["valueString"] = element.Location
                        });
                        elementPart.MutableNode["extension"] = extensionArray;
                    }
                }
            }
        }

        return result;
    }

    private static EvaluationContext CreateEvaluationContext(
        ParameterJsonNode? pcVariables, 
        ResourceJsonNode? resource, 
        ISchema schema,
        List<Ignixa.FhirPath.Evaluation.TraceEntry>? traceOutput)
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
        var childGroups = element.Children().GroupBy(c => c.Name);
        foreach (var group in childGroups)
        {
            var children = group.ToList();
            if (children.Count == 1)
            {
                var child = children[0];
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
            else
            {
                // Multiple children with same name = array
                var array = new JsonArray();
                foreach (var child in children)
                {
                    if (child.Value != null)
                    {
                        array.Add(JsonValue.Create(child.Value));
                    }
                    else
                    {
                        var childObj = new JsonObject();
                        SerializeElementChildren(child, childObj);
                        array.Add(childObj);
                    }
                }
                target[group.Key] = array;
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

    private static string GetIssueCode(ValidationIssue issue)
    {
        // Map validation issue types to OperationOutcome issue codes
        var message = issue.Message.ToLowerInvariant();
        if (message.Contains("not found") || message.Contains("unknown"))
            return "not-found";
        if (message.Contains("not supported"))
            return "not-supported";
        if (message.Contains("invalid") || message.Contains("incorrect"))
            return "invalid";
        if (message.Contains("required"))
            return "required";
        return "informational";
    }

    /// <summary>
    /// Converts a parsed FHIRPath expression to an AST string representation
    /// </summary>
    private static string ExpressionToAst(Expression expr, int indent = 0)
    {
        var sb = new StringBuilder();
        var prefix = new string(' ', indent * 2);
        
        switch (expr)
        {
            case ConstantExpression ce:
                sb.Append($"{prefix}Constant: {ce.Value} ({ce.Value?.GetType().Name ?? "null"})");
                break;
                
            case VariableRefExpression vre:
                sb.Append($"{prefix}Variable: %{vre.Name}");
                break;
                
            case IdentifierExpression ide:
                sb.Append($"{prefix}Identifier: {ide.Name}");
                break;
                
            case ScopeExpression se:
                sb.Append($"{prefix}Scope: ${se.ScopeName}");
                break;
                
            case QuantityExpression qe:
                sb.Append($"{prefix}Quantity: {qe.Value} '{qe.Unit}'");
                break;
                
            case EmptyExpression:
                sb.Append($"{prefix}Empty: {{}}");
                break;
                
            case ChildExpression che:
                sb.AppendLine($"{prefix}Child: .{che.ChildName}");
                if (che.Focus != null)
                    sb.Append(ExpressionToAst(che.Focus, indent + 1));
                break;
                
            case PropertyAccessExpression pae:
                sb.AppendLine($"{prefix}PropertyAccess: .{pae.PropertyName}");
                if (pae.Focus != null)
                    sb.Append(ExpressionToAst(pae.Focus, indent + 1));
                break;
                
            case IndexerExpression ie:
                sb.AppendLine($"{prefix}Indexer:");
                sb.AppendLine($"{prefix}  Collection:");
                sb.Append(ExpressionToAst(ie.Collection, indent + 2));
                sb.AppendLine($"{prefix}  Index:");
                sb.Append(ExpressionToAst(ie.Index, indent + 2));
                break;
                
            case UnaryExpression ue:
                sb.AppendLine($"{prefix}Unary: {ue.Operator}");
                sb.Append(ExpressionToAst(ue.Operand, indent + 1));
                break;
                
            case BinaryExpression be:
                sb.AppendLine($"{prefix}Binary: {be.Operator}");
                sb.AppendLine($"{prefix}  Left:");
                sb.Append(ExpressionToAst(be.Left, indent + 2));
                sb.AppendLine($"{prefix}  Right:");
                sb.Append(ExpressionToAst(be.Right, indent + 2));
                break;
                
            case ParenthesizedExpression pe:
                sb.AppendLine($"{prefix}Parenthesized:");
                sb.Append(ExpressionToAst(pe.InnerExpression, indent + 1));
                break;
                
            case FunctionCallExpression fce:
                sb.AppendLine($"{prefix}Function: {fce.FunctionName}({fce.Arguments.Count} args)");
                if (fce.Focus != null)
                {
                    sb.AppendLine($"{prefix}  Focus:");
                    sb.Append(ExpressionToAst(fce.Focus, indent + 2));
                }
                for (int i = 0; i < fce.Arguments.Count; i++)
                {
                    sb.AppendLine($"{prefix}  Arg[{i}]:");
                    sb.Append(ExpressionToAst(fce.Arguments[i], indent + 2));
                }
                break;
                
            default:
                sb.Append($"{prefix}{expr.GetType().Name}: {expr}");
                break;
        }
        
        if (!sb.ToString().EndsWith(Environment.NewLine))
            sb.AppendLine();
            
        return sb.ToString();
    }

    /// <summary>
    /// Converts a parsed FHIRPath expression to a JSON AST representation matching the UI's JsonNode interface
    /// </summary>
    private static string ExpressionToJsonAst(Expression expr, AnalysisResult? analysisResult = null)
    {
        var visitor = new JsonAstVisitor();
        var node = expr.AcceptVisitor(visitor, analysisResult);
        
        // Add inferred return type from analysis if available (for the root node)
        // Note: The visitor also populates types from expression.InferredType, but this handles the root result
        if (analysisResult?.InferredTypes?.Types.Count > 0)
        {
            var types = analysisResult.InferredTypes.Types
                .Select(t => t.TypeName)
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToList();
            
            string returnType;
            if (types.Count == 1)
                returnType = types[0];
            else
                returnType = string.Join(" | ", types);
                
            node["ReturnType"] = returnType;
        }
        
        return node.ToJsonString(_jsonOptions);
    }

    [Function("Warmer")]
    public void WarmUp([TimerTrigger("0 */15 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Warmer function executed at: {time}", DateTime.Now);
    }
}
