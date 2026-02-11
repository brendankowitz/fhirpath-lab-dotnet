using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ignixa.Abstractions;
using Ignixa.FhirPath.Analysis;
using Ignixa.FhirPath.Evaluation;
using Ignixa.FhirPath.Expressions;
using Ignixa.FhirPath.Visitors;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using FhirPathLab_DotNetEngine.Models;
using FhirPathLab_DotNetEngine.Serialization;

namespace FhirPathLab_DotNetEngine.Services;

/// <summary>
/// Service for formatting FHIRPath evaluation results as FHIR Parameters resources.
/// </summary>
public sealed class ResultFormatter
{
    private const string ExtensionUrlJsonValue = "http://fhir.forms-lab.com/StructureDefinition/json-value";
    private const string ExtensionUrlResourcePath = "http://fhir.forms-lab.com/StructureDefinition/resource-path";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string EvaluatorVersion = GetEvaluatorVersion();

    private static string GetEvaluatorVersion()
    {
        var assembly = typeof(FhirPathEvaluator).Assembly;
        var fullVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        // Extract just the base version (e.g., "0.0.149" from "0.0.149-dev-fhirpath-lab.7+Branch...")
        var version = fullVersion;
        var dashIndex = fullVersion.IndexOf('-');
        var plusIndex = fullVersion.IndexOf('+');
        if (dashIndex > 0)
        {
            version = fullVersion[..dashIndex];
        }
        else if (plusIndex > 0)
        {
            version = fullVersion[..plusIndex];
        }

        return $"Ignixa-{version}";
    }

    /// <summary>
    /// Formats a FHIRPath result as a FHIR Parameters resource.
    /// </summary>
    public ResourceJsonNode FormatResult(FhirPathResult result)
    {
        if (!result.IsSuccess)
        {
            return CreateOperationOutcomeResult("error", "invalid", result.Error!, result.ErrorDiagnostics);
        }

        var parameters = new ParametersJsonNode { Id = "fhirpath" };
        var configParam = BuildConfigParameters(
            parameters,
            result.Request.FhirVersion,
            result.Request.Context,
            result.Request.Expression,
            result.Request.ResourceId,
            result.Request.Resource,
            result.Request.TerminologyServerUrl);

        // Add AST with type info
        if (result.ParsedExpression != null)
        {
            AddPart(configParam, "parseDebugTree", ExpressionToJsonAst(
                result.ParsedExpression.Expression,
                result.ParsedExpression.Analysis,
                result.ParsedExpression.ExpressionScopeType));

            AddPart(configParam, "parseDebug", ExpressionToDebugText(
                result.ParsedExpression.Expression,
                result.ParsedExpression.Analysis));

            // Add expected return type from analysis
            if (result.ParsedExpression.Analysis?.InferredTypes?.Types.Count > 0)
            {
                var types = result.ParsedExpression.Analysis.InferredTypes.Types
                    .Select(t => t.ToString())
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Distinct()
                    .ToList();

                if (types.Count > 0)
                {
                    var expectedType = types.Count == 1 ? types[0] : string.Join(" | ", types);
                    AddPart(configParam, "expectedReturnType", expectedType);
                }
            }

            // Add validation issues if any
            if (result.ParsedExpression.ValidationIssues?.Count > 0)
            {
                AddValidationOutcome(configParam, result.ParsedExpression.ValidationIssues);
            }
        }

        // Add evaluation results
        foreach (var evalResult in result.Results)
        {
            if (evalResult.Error != null)
            {
                var errorParam = new ParameterJsonNode { Name = "error" };
                errorParam.SetValue("valueString", evalResult.Error);
                parameters.Parameter.Add(errorParam);
                return parameters;
            }

            AddResultParameter(parameters, evalResult.ContextPath, evalResult.OutputValues, evalResult.TraceOutput);

            // Add debug trace entries if any
            if (evalResult.DebugTraceEntries.Count > 0)
            {
                AddDebugTraceParameter(parameters, evalResult.DebugTraceEntries);
            }
        }

        return parameters;
    }

    /// <summary>
    /// Creates an OperationOutcome for error responses.
    /// </summary>
    public static ResourceJsonNode CreateOperationOutcomeResult(
        string severity,
        string code,
        string message,
        string? diagnostics = null)
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

        return outcome;
    }

    private static ParameterJsonNode BuildConfigParameters(
        ParametersJsonNode result,
        string fhirVersion,
        string? context,
        string? expression,
        string? resourceId,
        ResourceJsonNode? resource,
        string? terminologyServerUrl)
    {
        var configParam = new ParameterJsonNode { Name = "parameters" };
        result.Parameter.Add(configParam);

        AddPart(configParam, "evaluator", $"{EvaluatorVersion} ({fhirVersion})");
        if (!string.IsNullOrEmpty(context))
        {
            AddPart(configParam, "context", context);
        }
        if (!string.IsNullOrEmpty(expression))
        {
            AddPart(configParam, "expression", expression);
        }
        if (!string.IsNullOrEmpty(resourceId))
        {
            AddPart(configParam, "resource", resourceId);
        }
        else if (resource != null)
        {
            AddResourcePart(configParam, "resource", resource);
        }
        if (!string.IsNullOrEmpty(terminologyServerUrl))
        {
            AddPart(configParam, "terminologyServerUrl", terminologyServerUrl);
        }

        return configParam;
    }

    private static void AddValidationOutcome(ParameterJsonNode configParam, List<ValidationIssue> issues)
    {
        var outcome = new OperationOutcomeJsonNode();

        foreach (var issue in issues)
        {
            var issueComponent = new OperationOutcomeJsonNode.IssueComponent
            {
                Severity = MapSeverity(issue.Severity),
                Code = MapIssueCode(issue),
                Diagnostics = issue.Message
            };

            if (!string.IsNullOrEmpty(issue.Location))
                issueComponent.Expression.Add(issue.Location);

            outcome.Issue.Add(issueComponent);
        }

        var outcomePart = new ParameterJsonNode { Name = "debugOutcome" };
        outcomePart.MutableNode["resource"] = JsonNode.Parse(outcome.SerializeToString());
        configParam.Part.Add(outcomePart);
    }

    private static OperationOutcomeJsonNode.IssueSeverity MapSeverity(ValidationIssueSeverity severity) => severity switch
    {
        ValidationIssueSeverity.Error => OperationOutcomeJsonNode.IssueSeverity.Error,
        ValidationIssueSeverity.Warning => OperationOutcomeJsonNode.IssueSeverity.Warning,
        ValidationIssueSeverity.Information => OperationOutcomeJsonNode.IssueSeverity.Information,
        _ => OperationOutcomeJsonNode.IssueSeverity.Information
    };

    private static OperationOutcomeJsonNode.IssueType MapIssueCode(ValidationIssue issue)
    {
        var message = issue.Message.ToLowerInvariant();
        if (message.Contains("not found") || message.Contains("unknown"))
            return OperationOutcomeJsonNode.IssueType.NotFound;
        if (message.Contains("not supported"))
            return OperationOutcomeJsonNode.IssueType.NotSupported;
        if (message.Contains("invalid") || message.Contains("incorrect"))
            return OperationOutcomeJsonNode.IssueType.Invalid;
        if (message.Contains("required"))
            return OperationOutcomeJsonNode.IssueType.Required;
        return OperationOutcomeJsonNode.IssueType.Informational;
    }

    private static void AddResultParameter(
        ParametersJsonNode result,
        string contextPath,
        IEnumerable<IElement> outputValues,
        List<TraceEntry> traceOutput)
    {
        var resultParam = new ParameterJsonNode { Name = "result" };
        if (!string.IsNullOrEmpty(contextPath))
        {
            resultParam.SetValue("valueString", contextPath);
        }
        result.Parameter.Add(resultParam);

        foreach (var outputValue in outputValues)
        {
            var resultPart = new ParameterJsonNode { Name = outputValue.InstanceType ?? "(null)" };
            resultParam.Part.Add(resultPart);

            // Add resource path extension if location is available
            if (!string.IsNullOrEmpty(outputValue.Location))
            {
                AddPathExtension(resultPart, outputValue.Location);
            }

            if (outputValue.Value != null && outputValue.HasPrimitiveValue)
            {
                SetTypedValue(resultPart, outputValue.InstanceType!, outputValue.Value);
            }
            else
            {
                // For complex types, serialize using Children() to get proper FHIR structure
                var json = SerializeElementToJson(outputValue);
                resultPart.MutableNode[$"value{outputValue.InstanceType}"] = JsonNode.Parse(json);
            }
        }

        foreach (var trace in traceOutput)
        {
            var traceParam = new ParameterJsonNode { Name = "trace" };
            traceParam.SetValue("valueString", trace.Name);
            resultParam.Part.Add(traceParam);

            foreach (var element in trace.Focus)
            {
                var elementPart = new ParameterJsonNode { Name = element.InstanceType ?? string.Empty };
                traceParam.Part.Add(elementPart);

                // Add resource path extension if location is available
                if (!string.IsNullOrEmpty(element.Location))
                {
                    AddPathExtension(elementPart, element.Location);
                }

                if (element.Value != null && element.HasPrimitiveValue)
                {
                    SetTypedValue(elementPart, element.InstanceType!, element.Value);
                }
                else
                {
                    // For complex types, serialize using Children() to get proper FHIR structure
                    var json = SerializeElementToJson(element);
                    elementPart.MutableNode[$"value{element.InstanceType}"] = JsonNode.Parse(json);
                }
            }
        }
    }

    private static void AddDebugTraceParameter(ParametersJsonNode parameters, List<NodeEvaluationEntry> debugTraceEntries)
    {
        // Create the debug-trace wrapper parameter
        var debugTraceParam = new ParameterJsonNode { Name = "debug-trace" };
        parameters.Parameter.Add(debugTraceParam);

        // Group entries by their key
        var groupedEntries = debugTraceEntries.GroupBy(entry => entry.GetKey());

        foreach (var group in groupedEntries)
        {
            var debugParam = new ParameterJsonNode { Name = group.Key };
            debugTraceParam.Part.Add(debugParam);

            foreach (var entry in group)
            {
                // Add resource-path parts from Results
                foreach (var result in entry.Results)
                {
                    if (!string.IsNullOrEmpty(result.Location))
                    {
                        var resourcePathPart = new ParameterJsonNode { Name = "resource-path" };
                        resourcePathPart.SetValue("valueString", result.Location);
                        debugParam.Part.Add(resourcePathPart);
                    }
                }

                // Add focus-resource-path parts from FocusElements
                foreach (var focusElement in entry.FocusElements)
                {
                    if (!string.IsNullOrEmpty(focusElement.Location))
                    {
                        var focusPathPart = new ParameterJsonNode { Name = "focus-resource-path" };
                        focusPathPart.SetValue("valueString", focusElement.Location);
                        debugParam.Part.Add(focusPathPart);
                    }
                }

                // Add this-resource-path part from ThisElement
                if (entry.ThisElement != null && !string.IsNullOrEmpty(entry.ThisElement.Location))
                {
                    var thisPathPart = new ParameterJsonNode { Name = "this-resource-path" };
                    thisPathPart.SetValue("valueString", entry.ThisElement.Location);
                    debugParam.Part.Add(thisPathPart);
                }

                // Add index part
                if (entry.Index.HasValue)
                {
                    var indexPart = new ParameterJsonNode { Name = "index" };
                    indexPart.SetValue("valueInteger", JsonValue.Create(entry.Index.Value));
                    debugParam.Part.Add(indexPart);
                }
            }
        }
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
        // Ignixa returns values in the correct FHIR format, just pass through
        var valueTypeName = $"value{char.ToUpperInvariant(instanceType[0])}{instanceType[1..]}";
        param.SetValue(valueTypeName, JsonValue.Create(value));
    }

    private static void AddPathExtension(ParameterJsonNode param, string path)
    {
        AddExtensionToParam(param, ExtensionUrlResourcePath, path);
    }

    private static void AddJsonValueExtension(ParameterJsonNode param, string jsonValue)
    {
        AddExtensionToParam(param, ExtensionUrlJsonValue, jsonValue);
    }

    private static void AddExtensionToParam(ParameterJsonNode param, string url, string value)
    {
        // Get or create extension array
        if (param.MutableNode["extension"] is not JsonArray extensionArray)
        {
            extensionArray = new JsonArray();
            param.MutableNode["extension"] = extensionArray;
        }

        // Add new extension
        extensionArray.Add(new JsonObject
        {
            ["url"] = url,
            ["valueString"] = value
        });
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
            bool isCollection = children.Count > 1 || IsCollectionElement(children[0]);

            if (!isCollection)
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

    private static bool IsCollectionElement(IElement element)
    {
        if (element.Type?.IsCollection == true)
            return true;

        if (!string.IsNullOrEmpty(element.Location))
        {
            var lastSegment = element.Location.Split('.')[^1];
            if (lastSegment.Contains('[', StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string ExpressionToJsonAst(
        Expression expr,
        AnalysisResult? analysisResult = null,
        string? rootTypeName = null)
    {
        var visitor = new JsonAstVisitor { RootTypeName = rootTypeName };
        var node = expr.AcceptVisitor(visitor, analysisResult);

        // Add inferred return type from analysis if available (for the root node)
        if (analysisResult?.InferredTypes?.Types.Count > 0)
        {
            var types = analysisResult.InferredTypes.Types
                .Select(t => t.TypeName)
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToList();

            // Only set ReturnType if we have actual type names
            if (types.Count > 0)
            {
                string returnType;
                if (types.Count == 1)
                {
                    returnType = types[0];
                }
                else
                {
                    returnType = string.Join(" | ", types);
                }

                var isCollection = analysisResult.InferredTypes.Types.Any(t => t.IsCollection);
                if (isCollection && !returnType.EndsWith("[]"))
                    returnType += "[]";

                node["ReturnType"] = returnType;
            }
        }

        return node.ToJsonString(JsonOptions);
    }

    private static string ExpressionToDebugText(
        Expression expr,
        AnalysisResult? analysisResult)
    {
        // Get expression text from position info or visitor
        var exprText = ExpressionToText(expr);
        
        // Get return type
        string returnType = "";
        if (analysisResult?.InferredTypes?.Types.Count > 0)
        {
            var types = analysisResult.InferredTypes.Types
                .Select(t => t.ToString())
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToList();
            returnType = types.Count == 1 ? types[0] : string.Join(" | ", types);
        }
        
        return string.IsNullOrEmpty(returnType) 
            ? $"{exprText}\r\n" 
            : $"{exprText} : {returnType}\r\n";
    }

    private static string ExpressionToText(Expression expr)
    {
        return expr switch
        {
            // ChildExpression derives from FunctionCallExpression, match first
            ChildExpression child => child.Focus != null
                ? $"{ExpressionToText(child.Focus)}.{child.ChildName}"
                : child.ChildName,
            // BinaryExpression derives from FunctionCallExpression, match first
            BinaryExpression bin => $"{ExpressionToText(bin.Left)} {bin.Operator} {ExpressionToText(bin.Right)}",
            // IndexerExpression derives from FunctionCallExpression, match first
            IndexerExpression indexer => $"{ExpressionToText(indexer.Collection)}[{ExpressionToText(indexer.Index)}]",
            // UnaryExpression derives from FunctionCallExpression, match first
            UnaryExpression unary => $"{unary.Operator}{ExpressionToText(unary.Operand)}",
            // PropertyAccessExpression must be before FunctionCallExpression
            PropertyAccessExpression prop => prop.Focus != null
                ? $"{ExpressionToText(prop.Focus)}.{prop.PropertyName}"
                : prop.PropertyName,
            FunctionCallExpression func => FormatFunctionCall(func),
            ConstantExpression constant => FormatConstant(constant),
            IdentifierExpression id => id.Name,
            ParenthesizedExpression paren => $"({ExpressionToText(paren.InnerExpression)})",
            ScopeExpression scope => scope.ScopeName switch
            {
                "this" => "$this",
                "index" => "$index",
                "total" => "$total",
                _ => "$this"
            },
            VariableRefExpression varRef => $"%{varRef.Name}",
            _ => expr.ToString() ?? ""
        };
    }

    private static string FormatConstant(ConstantExpression constant)
    {
        if (constant.Value == null) return "{}";
        return constant.Value switch
        {
            string s when s.StartsWith('@') => s,
            string s => $"'{s}'",
            bool b => b ? "true" : "false",
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            decimal d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", constant.Value)
        };
    }

    private static string FormatFunctionCall(FunctionCallExpression func)
    {
        var focus = func.Focus != null ? ExpressionToText(func.Focus) : "";
        var args = string.Join(", ", func.Arguments.Select(ExpressionToText));
        var prefix = string.IsNullOrEmpty(focus) ? "" : $"{focus}.";
        return $"{prefix}{func.FunctionName}({args})";
    }
}
