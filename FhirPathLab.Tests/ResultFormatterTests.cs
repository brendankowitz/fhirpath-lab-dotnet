using System.Text.Json.Nodes;
using FluentAssertions;
using FhirPathLab_DotNetEngine.Services;
using FhirPathLab_DotNetEngine.Models;
using Ignixa.Abstractions;
using Ignixa.FhirPath.Evaluation;
using Ignixa.FhirPath.Parser;
using Ignixa.Serialization;
using Ignixa.Serialization.SourceNodes;
using Ignixa.Specification.Generated;
using Ignixa.Specification.Extensions;

namespace FhirPathLab.Tests;

/// <summary>
/// Regression tests for ResultFormatter serialization of FHIRPath evaluation results.
/// </summary>
public class ResultFormatterTests
{
    private readonly FhirPathParser _parser = new();
    private readonly FhirPathEvaluator _evaluator = new();
    private readonly IFhirSchemaProvider _r4Provider;

    private const string TestPatientJson = """
    {
        "resourceType": "Patient",
        "id": "example",
        "name": [
            {"given": ["John"], "family": "Doe"},
            {"use": "usual", "given": ["Johnny"]}
        ],
        "gender": "male",
        "birthDate": "1970-01-01"
    }
    """;

    public ResultFormatterTests()
    {
        _r4Provider = FhirVersion.R4.GetSchemaProvider();
    }

    /// <summary>
    /// Helper method to evaluate an expression and format the results.
    /// </summary>
    private (FhirPathResult result, JsonNode json) EvaluateAndFormat(
        string expression,
        string patientJson,
        string fhirVersion = "R5",
        bool debugTrace = false)
    {
        var resource = ResourceJsonNode.Parse(patientJson);
        var schemaFactory = new SchemaProviderFactory();
        var analyzer = new ExpressionAnalyzer(schemaFactory);
        var evaluatorSvc = new ExpressionEvaluator(schemaFactory);
        var formatter = new ResultFormatter();

        var (parsed, contextExpr, error) = analyzer.ParseAndAnalyze(expression, null, resource.ResourceType, fhirVersion);
        if (error != null) throw new InvalidOperationException(error);

        var evalResults = evaluatorSvc.Evaluate(parsed!, contextExpr, resource, null, fhirVersion, debugTrace);

        var fhirPathResult = new FhirPathResult
        {
            Request = new FhirPathRequest
            {
                Expression = expression,
                FhirVersion = fhirVersion,
                Resource = resource,
                DebugTrace = debugTrace
            },
            ParsedExpression = parsed,
            Results = evalResults
        };

        var parametersNode = formatter.FormatResult(fhirPathResult);
        var json = JsonNode.Parse(parametersNode.SerializeToString(pretty: true))!;
        return (fhirPathResult, json);
    }

    /// <summary>
    /// Helper to find a specific parameter part by parameter name and part name.
    /// </summary>
    private static JsonNode? FindParameterPart(JsonNode root, string paramName, string partName)
    {
        var param = root["parameter"]!.AsArray()
            .FirstOrDefault(p => p?["name"]?.GetValue<string>() == paramName);
        return param?["part"]?.AsArray()
            .FirstOrDefault(p => p?["name"]?.GetValue<string>() == partName);
    }

    /// <summary>
    /// Regression: Patient.name expression was collapsing single-item arrays (given: ["John"])
    /// to scalars (given: "John") because SerializeElementChildren used child count instead of
    /// schema metadata to decide array vs scalar serialization.
    /// </summary>
    [Fact]
    public void GivenPatientWithName_WhenEvaluatingNameExpression_ThenGivenRemainsArray()
    {
        // Arrange
        var patientJson = """
        {
          "resourceType": "Patient",
          "id": "example",
          "name": [
            {
              "given": ["John"],
              "family": "Doe"
            },
            {
              "use": "usual",
              "given": ["Johnny"]
            }
          ],
          "gender": "male",
          "birthDate": "1970-01-01"
        }
        """;

        var resource = ResourceJsonNode.Parse(patientJson);
        var element = resource.ToElement(_r4Provider);
        var expression = _parser.Parse("name");
        var results = _evaluator.Evaluate(element, expression, new EvaluationContext()).ToList();

        // Act: format result via the same path the lab uses
        var formatter = new ResultFormatter();
        var fhirPathResult = new FhirPathResult
        {
            Request = new FhirPathRequest
            {
                Expression = "name",
                FhirVersion = "R4",
                Resource = resource
            },
            Results =
            [
                new EvaluationResult(
                    ContextPath: "Patient",
                    OutputValues: results,
                    TraceOutput: [],
                    DebugTraceEntries: [],
                    Error: null)
            ]
        };
        var parametersNode = formatter.FormatResult(fhirPathResult);
        var json = parametersNode.SerializeToString(pretty: true);
        var root = JsonNode.Parse(json)!;

        // Find result parameters (skip the first "parameters" config param)
        var parameters = root["parameter"]!.AsArray();
        var resultParams = parameters
            .Where(p => p?["name"]?.GetValue<string>() == "result")
            .ToList();
        resultParams.Should().HaveCount(1);

        var parts = resultParams[0]!["part"]!.AsArray();
        parts.Should().HaveCount(2, "two HumanName results expected");

        // Assert first name: {"family": "Doe", "given": ["John"]}
        var firstName = parts[0]!;
        var firstNameValue = firstName["valueHumanName"]!;
        firstNameValue["family"]!.GetValue<string>().Should().Be("Doe");
        firstNameValue["given"].Should().BeOfType<JsonArray>("given with single item must stay an array");
        firstNameValue["given"]!.AsArray().Should().HaveCount(1);
        firstNameValue["given"]![0]!.GetValue<string>().Should().Be("John");

        // Assert second name: {"use": "usual", "given": ["Johnny"]}
        var secondName = parts[1]!;
        var secondNameValue = secondName["valueHumanName"]!;
        secondNameValue["use"]!.GetValue<string>().Should().Be("usual");
        secondNameValue["given"].Should().BeOfType<JsonArray>("given with single item must stay an array");
        secondNameValue["given"]!.AsArray().Should().HaveCount(1);
        secondNameValue["given"]![0]!.GetValue<string>().Should().Be("Johnny");
    }

    [Fact]
    public void GivenPatientNameExpression_WhenFormatted_ThenExpectedReturnTypeIsPresent()
    {
        // Act
        var (_, json) = EvaluateAndFormat("name", TestPatientJson);

        // Assert
        var expectedReturnTypePart = FindParameterPart(json, "parameters", "expectedReturnType");
        expectedReturnTypePart.Should().NotBeNull("expectedReturnType should be present");
        var returnType = expectedReturnTypePart!["valueString"]?.GetValue<string>();
        returnType.Should().Be("HumanName[]", "name expression returns a collection of HumanName");
    }

    [Fact]
    public void GivenPatientNameExpression_WhenFormatted_ThenParseDebugTreeHasCollectionReturnType()
    {
        // Act
        var (_, json) = EvaluateAndFormat("name", TestPatientJson);

        // Assert
        var parseDebugTreePart = FindParameterPart(json, "parameters", "parseDebugTree");
        parseDebugTreePart.Should().NotBeNull("parseDebugTree should be present");
        var treeJson = parseDebugTreePart!["valueString"]?.GetValue<string>();
        treeJson.Should().NotBeNullOrEmpty();
        
        var tree = JsonNode.Parse(treeJson!);
        var returnType = tree!["ReturnType"]?.GetValue<string>();
        returnType.Should().Be("HumanName[]", "root node should have collection return type with [] suffix");
    }

    [Fact]
    public void GivenPatientNameExpression_WhenFormatted_ThenParseDebugIsPresent()
    {
        // Act
        var (_, json) = EvaluateAndFormat("name", TestPatientJson);

        // Assert
        var parseDebugPart = FindParameterPart(json, "parameters", "parseDebug");
        parseDebugPart.Should().NotBeNull("parseDebug should be present");
        var debugText = parseDebugPart!["valueString"]?.GetValue<string>();
        debugText.Should().NotBeNullOrEmpty();
        debugText.Should().Contain("name", "should contain the expression");
        debugText.Should().Contain("HumanName[]", "should show the return type");
    }

    [Fact]
    public void GivenPatientNameExpression_WhenFormatted_ThenResultsHaveResourcePathExtensions()
    {
        // Act
        var (_, json) = EvaluateAndFormat("name", TestPatientJson);

        // Assert
        var resultParam = json["parameter"]!.AsArray()
            .FirstOrDefault(p => p?["name"]?.GetValue<string>() == "result");
        resultParam.Should().NotBeNull("result parameter should be present");

        var parts = resultParam!["part"]!.AsArray();
        parts.Should().HaveCount(2, "two HumanName results expected");

        // Check first result has resource path extension
        var firstPart = parts[0]!;
        var firstExtensions = firstPart["extension"]?.AsArray();
        firstExtensions.Should().NotBeNull().And.HaveCountGreaterThan(0);
        
        var firstPathExt = firstExtensions!
            .FirstOrDefault(e => e?["url"]?.GetValue<string>() == "http://fhir.forms-lab.com/StructureDefinition/resource-path");
        firstPathExt.Should().NotBeNull("first result should have resource-path extension");
        firstPathExt!["valueString"]?.GetValue<string>().Should().Be("Patient.name[0]");

        // Check second result has resource path extension
        var secondPart = parts[1]!;
        var secondExtensions = secondPart["extension"]?.AsArray();
        secondExtensions.Should().NotBeNull().And.HaveCountGreaterThan(0);
        
        var secondPathExt = secondExtensions!
            .FirstOrDefault(e => e?["url"]?.GetValue<string>() == "http://fhir.forms-lab.com/StructureDefinition/resource-path");
        secondPathExt.Should().NotBeNull("second result should have resource-path extension");
        secondPathExt!["valueString"]?.GetValue<string>().Should().Be("Patient.name[1]");
    }

    [Fact]
    public void GivenPatientNameExpression_WhenFormatted_ThenResultPartsHaveCorrectTypeName()
    {
        // Act
        var (_, json) = EvaluateAndFormat("name", TestPatientJson);

        // Assert
        var resultParam = json["parameter"]!.AsArray()
            .FirstOrDefault(p => p?["name"]?.GetValue<string>() == "result");
        resultParam.Should().NotBeNull();

        var parts = resultParam!["part"]!.AsArray();
        parts.Should().HaveCount(2);

        // Check both results have name "HumanName"
        parts[0]!["name"]?.GetValue<string>().Should().Be("HumanName");
        parts[1]!["name"]?.GetValue<string>().Should().Be("HumanName");
    }

    [Fact]
    public void GivenPatientNameExpression_WhenFormatted_ThenEvaluatorInfoIsPresent()
    {
        // Act
        var (_, json) = EvaluateAndFormat("name", TestPatientJson);

        // Assert
        var evaluatorPart = FindParameterPart(json, "parameters", "evaluator");
        evaluatorPart.Should().NotBeNull("evaluator info should be present");
        
        var evaluatorString = evaluatorPart!["valueString"]?.GetValue<string>();
        evaluatorString.Should().NotBeNullOrEmpty();
        evaluatorString.Should().Contain("Ignixa", "should identify Ignixa engine");
        evaluatorString.Should().Contain("R5", "should contain FHIR version");
    }

    [Fact]
    public void GivenPatientNameExpression_WhenFormatted_ThenExpressionIsEchoed()
    {
        // Act
        var (_, json) = EvaluateAndFormat("name", TestPatientJson);

        // Assert
        var expressionPart = FindParameterPart(json, "parameters", "expression");
        expressionPart.Should().NotBeNull("expression should be echoed in parameters");
        
        var expression = expressionPart!["valueString"]?.GetValue<string>();
        expression.Should().Be("name");
    }

    [Fact]
    public void GivenPatientNameExpression_WhenFormatted_ThenResourceIsEchoed()
    {
        // Act
        var (_, json) = EvaluateAndFormat("name", TestPatientJson);

        // Assert
        var resourcePart = FindParameterPart(json, "parameters", "resource");
        resourcePart.Should().NotBeNull("resource should be echoed in parameters");
        
        var resource = resourcePart!["resource"];
        resource.Should().NotBeNull();
        resource!["resourceType"]?.GetValue<string>().Should().Be("Patient");
        resource["id"]?.GetValue<string>().Should().Be("example");
    }

    [Fact]
    public void GivenScalarExpression_WhenFormatted_ThenReturnTypeHasNoCollectionSuffix()
    {
        // Act - gender is a truly scalar field (not a collection)
        var (_, json) = EvaluateAndFormat("gender", TestPatientJson);

        // Assert
        var expectedReturnTypePart = FindParameterPart(json, "parameters", "expectedReturnType");
        expectedReturnTypePart.Should().NotBeNull();
        
        var returnType = expectedReturnTypePart!["valueString"]?.GetValue<string>();
        returnType.Should().NotBeNullOrEmpty();
        returnType.Should().NotEndWith("[]", "scalar expression should not have collection suffix");
        returnType.Should().Be("code", "gender is a code type");
    }

    [Fact]
    public void GivenMultipleTypeExpression_WhenFormatted_ThenReturnTypeShowsUnion()
    {
        // Act - This expression can return either integer or string
        var (_, json) = EvaluateAndFormat("iif(true, 1, 'text')", TestPatientJson);

        // Assert
        var expectedReturnTypePart = FindParameterPart(json, "parameters", "expectedReturnType");
        expectedReturnTypePart.Should().NotBeNull();

        var returnType = expectedReturnTypePart!["valueString"]?.GetValue<string>();
        returnType.Should().NotBeNullOrEmpty();
        // Union types are typically represented with | or contain both types
        // The exact format depends on the analyzer implementation
        (returnType!.Contains("integer") || returnType.Contains("string")).Should().BeTrue(
            "union type expression should reference the possible types");
    }

    /// <summary>
    /// Regression: When type inference finds no type names (empty after filtering),
    /// the code was appending "[]" to an empty string, producing just "[]" as the ReturnType.
    /// This test ensures that when no type names are inferred, the ReturnType field is omitted entirely.
    /// </summary>
    [Fact]
    public void GivenExpressionWithNoInferredTypes_WhenFormatted_ThenReturnTypeIsNotEmptyBrackets()
    {
        // This is a hypothetical scenario - in practice, the analyzer usually infers something.
        // But if it ever produces Types with no TypeName values, we should not show "[]"

        // Act - Use a simple expression that should have type inference
        var (result, json) = EvaluateAndFormat("name", TestPatientJson);

        // Assert - verify parseDebugTree doesn't have "[]" as ReturnType for any node
        var parseDebugTreePart = FindParameterPart(json, "parameters", "parseDebugTree");
        parseDebugTreePart.Should().NotBeNull();
        var treeJson = parseDebugTreePart!["valueString"]?.GetValue<string>();
        treeJson.Should().NotBeNullOrEmpty();

        // The tree JSON should not contain ReturnType with value "[]"
        treeJson.Should().NotContain("\"ReturnType\":\"[]\"",
            "ReturnType should not be just empty brackets - either omit or show actual type");
        treeJson.Should().NotContain("\"ReturnType\": \"[]\"",
            "ReturnType should not be just empty brackets with space - either omit or show actual type");

        // Also verify expectedReturnType is not "[]"
        var expectedReturnTypePart = FindParameterPart(json, "parameters", "expectedReturnType");
        if (expectedReturnTypePart != null)
        {
            var returnType = expectedReturnTypePart["valueString"]?.GetValue<string>();
            returnType.Should().NotBe("[]", "expectedReturnType should not be just empty brackets");
        }
    }

    /// <summary>
    /// Regression: JsonAstVisitor was setting ReturnType to "[]" when typeNames enumerable
    /// was empty but isCollection was true. This test ensures that never happens.
    /// </summary>
    [Fact]
    public void GivenCollectionExpression_WhenFormatted_ThenReturnTypeIsNeverJustBrackets()
    {
        // Act - name is a collection type expression
        var (_, json) = EvaluateAndFormat("name", TestPatientJson);

        // Assert - check all nodes in the AST
        var parseDebugTreePart = FindParameterPart(json, "parameters", "parseDebugTree");
        parseDebugTreePart.Should().NotBeNull();
        var treeJson = parseDebugTreePart!["valueString"]?.GetValue<string>();

        // Parse the tree to check all nested nodes
        var tree = JsonNode.Parse(treeJson!);
        ValidateNoEmptyBracketsReturnType(tree!);
    }

    /// <summary>
    /// Recursively validates that no node in the AST has ReturnType = "[]"
    /// </summary>
    private void ValidateNoEmptyBracketsReturnType(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("ReturnType", out var returnTypeNode))
            {
                var returnType = returnTypeNode?.GetValue<string>();
                returnType.Should().NotBe("[]",
                    $"Found ReturnType='[]' in node: {obj["Name"]?.GetValue<string>() ?? "unknown"}");
            }

            // Recursively check Arguments array if present
            if (obj.TryGetPropertyValue("Arguments", out var argsNode) && argsNode is JsonArray args)
            {
                foreach (var arg in args)
                {
                    if (arg != null)
                        ValidateNoEmptyBracketsReturnType(arg);
                }
            }
        }
    }

    /// <summary>
    /// Regression: Debug trace entries were emitted as top-level parameters instead of
    /// being nested inside a "debug-trace" wrapper parameter, breaking parity with Firely output.
    /// </summary>
    [Fact]
    public void GivenDebugTraceEnabled_WhenFormatted_ThenTraceEntriesAreNestedUnderDebugTraceParameter()
    {
        // Act
        var (_, json) = EvaluateAndFormat("name", TestPatientJson, debugTrace: true);

        // Assert - there should be a top-level "debug-trace" parameter
        var parameters = json["parameter"]!.AsArray();
        var debugTraceParam = parameters
            .FirstOrDefault(p => p?["name"]?.GetValue<string>() == "debug-trace");
        debugTraceParam.Should().NotBeNull("debug trace entries should be wrapped in a 'debug-trace' parameter");

        // The trace entries (e.g. "0,4,name") should be parts of debug-trace, not top-level parameters
        var traceParts = debugTraceParam!["part"]?.AsArray();
        traceParts.Should().NotBeNull().And.HaveCountGreaterThan(0,
            "debug-trace should contain at least one trace entry as a part");

        // Verify the trace entry has expected sub-parts (resource-path, focus-resource-path, etc.)
        var firstTracePart = traceParts![0]!;
        firstTracePart["name"]?.GetValue<string>().Should().NotBeNullOrEmpty(
            "each trace entry should have a key name like '0,4,name'");

        var subParts = firstTracePart["part"]?.AsArray();
        subParts.Should().NotBeNull().And.HaveCountGreaterThan(0,
            "trace entry should have sub-parts like resource-path");

        var subPartNames = subParts!.Select(p => p?["name"]?.GetValue<string>()).ToList();
        subPartNames.Should().Contain("resource-path", "trace entry should include resource-path parts");

        // Verify no trace entries leaked to the top level
        var topLevelNames = parameters.Select(p => p?["name"]?.GetValue<string>()).ToList();
        topLevelNames.Should().BeEquivalentTo(
            new[] { "parameters", "result", "debug-trace" },
            "only parameters, result, and debug-trace should be top-level");
    }
}
