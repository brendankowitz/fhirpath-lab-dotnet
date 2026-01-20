using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using FhirPathLab_DotNetEngine.Models;
using FhirPathLab_DotNetEngine.Services;

namespace FhirPathLab_DotNetEngine;

public class FunctionFhirPathTest
{
    private readonly ILogger<FunctionFhirPathTest> _logger;
    private readonly FhirPathService _fhirPathService;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public FunctionFhirPathTest(ILogger<FunctionFhirPathTest> logger, FhirPathService fhirPathService)
    {
        _logger = logger;
        _fhirPathService = fhirPathService;
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
            Content = capabilityStatement.ToJsonString(JsonOptions),
            ContentType = "application/fhir+json",
            StatusCode = (int)HttpStatusCode.OK
        };
    }

    [Function("FHIRPathTester-R4")]
    public async Task<IActionResult> RunFhirPathTestR4(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "$fhirpath-r4")] HttpRequest req)
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

    [Function("Warmer")]
    public void WarmUp([TimerTrigger("0 */15 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Warmer function executed at: {time}", DateTime.Now);
    }

    private async Task<IActionResult> ProcessFhirPathRequest(HttpRequest req, string fhirVersion)
    {
        _logger.LogInformation("FhirPath Expression Evaluation (Ignixa) - FHIR {Version}", fhirVersion);

        var operationParameters = await ParseOperationParameters(req);
        var request = await BuildFhirPathRequest(operationParameters, fhirVersion);

        if (request.Error != null)
        {
            return CreateErrorResponse(request.Error, request.ErrorDiagnostics);
        }

        var result = _fhirPathService.ProcessRequest(request.Request!);

        return new ContentResult
        {
            Content = result.SerializeToString(pretty: true),
            ContentType = "application/fhir+json",
            StatusCode = (int)HttpStatusCode.OK
        };
    }

    private static async Task<ParametersJsonNode> ParseOperationParameters(HttpRequest req)
    {
        if (req.Method != "POST")
        {
            var parameters = new ParametersJsonNode();
            foreach (var key in req.Query.Keys)
            {
                var param = new ParameterJsonNode { Name = key };
                param.SetValue("valueString", req.Query[key].ToString());
                parameters.Parameter.Add(param);
            }
            return parameters;
        }

        return await JsonSourceNodeFactory.ParseAsync<ParametersJsonNode>(req.Body, CancellationToken.None);
    }

    private static async Task<(FhirPathRequest? Request, string? Error, string? ErrorDiagnostics)> BuildFhirPathRequest(
        ParametersJsonNode operationParameters,
        string fhirVersion)
    {
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
        if (resource == null && !string.IsNullOrEmpty(resourceId) &&
            resourceId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var (loadedResource, error) = await FhirPathService.LoadResourceFromUrl(resourceId);
            if (error != null)
            {
                return (null, error, resourceId);
            }
            resource = loadedResource;
        }

        if (string.IsNullOrEmpty(expression))
        {
            return (null, "Expression parameter is required", null);
        }

        var request = new FhirPathRequest
        {
            Resource = resource,
            ResourceId = resourceId,
            Context = context,
            Expression = expression,
            TerminologyServerUrl = terminologyServerUrl,
            Variables = variablesParam,
            DebugTrace = debugTrace,
            FhirVersion = fhirVersion
        };

        return (request, null, null);
    }

    private static IActionResult CreateErrorResponse(string message, string? diagnostics = null)
    {
        var outcome = ResultFormatter.CreateOperationOutcomeResult("error", "not-found", message, diagnostics);

        return new ContentResult
        {
            Content = outcome.SerializeToString(pretty: true),
            ContentType = "application/fhir+json",
            StatusCode = (int)HttpStatusCode.BadRequest
        };
    }
}
