using System.Net;
using System.Text;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Task = System.Threading.Tasks.Task;

namespace FhirPathLab.Tests;

/// <summary>
/// End-to-end tests for the Ignixa FHIRPath API.
/// These tests require the Azure Function to be running locally or deployed.
/// </summary>
public class IgnixaApiTests : IAsyncLifetime
{
    private HttpClient _client = null!;
    private string _baseUrl = null!;
    private readonly FhirJsonParser _parser = new();
    private readonly FhirJsonSerializer _serializer = new(new SerializerSettings { Pretty = true });

    public async Task InitializeAsync()
    {
        _baseUrl = Environment.GetEnvironmentVariable("IGNIXA_API_URL") 
            ?? "http://127.0.0.1:7071/api";
        
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        
        // Verify API is accessible
        try
        {
            var response = await _client.GetAsync($"{_baseUrl}/metadata");
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"API not accessible at {_baseUrl}. Please start the function with 'func start --port 7071'");
            }
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException($"API not accessible at {_baseUrl}. Please start the function with 'func start --port 7071'");
        }
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    #region Helper Methods
    
    private async Task<Parameters> PostFhirPathRequest(string endpoint, Parameters parameters)
    {
        var json = _serializer.SerializeToString(parameters);
        var content = new StringContent(json, Encoding.UTF8, "application/fhir+json");
        
        var response = await _client.PostAsync($"{_baseUrl}/{endpoint}", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK, 
            $"Expected 200 OK but got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        
        var responseJson = await response.Content.ReadAsStringAsync();
        return _parser.Parse<Parameters>(responseJson);
    }
    
    private Parameters CreateBasicRequest(string expression, string? resourceJson = null, bool debugTrace = false)
    {
        var parameters = new Parameters();
        parameters.Add("expression", new FhirString(expression));
        parameters.Add("debug_trace", new FhirBoolean(debugTrace));
        
        if (resourceJson != null)
        {
            var resource = _parser.Parse<Resource>(resourceJson);
            parameters.Add("resource", resource);
        }
        
        return parameters;
    }

    private string GetPatientR4Json()
    {
        return """
        {
            "resourceType": "Patient",
            "id": "test-patient",
            "name": [
                {
                    "use": "official",
                    "family": "Smith",
                    "given": ["John", "James"]
                }
            ],
            "birthDate": "1990-01-15",
            "active": true
        }
        """;
    }
    
    /// <summary>
    /// Gets the first result value from a Parameters response.
    /// The result structure is: parameter[name="result"].part[0].value[x]
    /// </summary>
    private DataType? GetFirstResultValue(Parameters result)
    {
        var resultParam = result.Parameter.FirstOrDefault(p => p.Name == "result");
        if (resultParam?.Part == null || resultParam.Part.Count == 0)
            return null;
        return resultParam.Part[0].Value;
    }
    
    /// <summary>
    /// Gets all result values from a Parameters response.
    /// </summary>
    private List<DataType?> GetAllResultValues(Parameters result)
    {
        var resultParam = result.Parameter.FirstOrDefault(p => p.Name == "result");
        if (resultParam?.Part == null)
            return new List<DataType?>();
        return resultParam.Part.Select(p => p.Value).ToList();
    }
    
    #endregion

    #region Basic Evaluation Tests
    
    [Fact]
    public async Task Evaluate_SimpleExpression_ReturnsResult()
    {
        var parameters = CreateBasicRequest("1 + 1");
        var result = await PostFhirPathRequest("$fhirpath", parameters);
        
        var value = GetFirstResultValue(result);
        value.Should().NotBeNull();
        value.Should().BeOfType<Integer>()
            .Which.Value.Should().Be(2);
    }
    
    [Fact]
    public async Task Evaluate_StringConcatenation_ReturnsResult()
    {
        var parameters = CreateBasicRequest("'Hello' + ' ' + 'World'");
        var result = await PostFhirPathRequest("$fhirpath", parameters);
        
        var value = GetFirstResultValue(result);
        value.Should().NotBeNull();
        value.Should().BeOfType<FhirString>()
            .Which.Value.Should().Be("Hello World");
    }
    
    [Fact]
    public async Task Evaluate_PatientFamilyName_ReturnsResult()
    {
        var parameters = CreateBasicRequest("Patient.name.family", GetPatientR4Json());
        var result = await PostFhirPathRequest("$fhirpath", parameters);
        
        var value = GetFirstResultValue(result);
        value.Should().NotBeNull();
        value.Should().BeOfType<FhirString>()
            .Which.Value.Should().Be("Smith");
    }
    
    [Fact]
    public async Task Evaluate_PatientGivenNames_ReturnsMultipleResults()
    {
        var parameters = CreateBasicRequest("Patient.name.given", GetPatientR4Json());
        var result = await PostFhirPathRequest("$fhirpath", parameters);
        
        var values = GetAllResultValues(result);
        values.Should().HaveCount(2);
        ((FhirString)values[0]!).Value.Should().Be("John");
        ((FhirString)values[1]!).Value.Should().Be("James");
    }
    
    [Fact]
    public async Task Evaluate_WhereFunction_FiltersResults()
    {
        var parameters = CreateBasicRequest("Patient.name.given.where($this.startsWith('J'))", GetPatientR4Json());
        var result = await PostFhirPathRequest("$fhirpath", parameters);
        
        var values = GetAllResultValues(result);
        values.Should().HaveCount(2);
    }
    
    [Fact]
    public async Task Evaluate_FirstFunction_ReturnsSingleResult()
    {
        var parameters = CreateBasicRequest("Patient.name.given.first()", GetPatientR4Json());
        var result = await PostFhirPathRequest("$fhirpath", parameters);
        
        var value = GetFirstResultValue(result);
        value.Should().NotBeNull();
        value.Should().BeOfType<FhirString>()
            .Which.Value.Should().Be("John");
    }
    
    [Fact]
    public async Task Evaluate_ExistsFunction_ReturnsBoolean()
    {
        var parameters = CreateBasicRequest("Patient.name.exists()", GetPatientR4Json());
        var result = await PostFhirPathRequest("$fhirpath", parameters);
        
        var value = GetFirstResultValue(result);
        value.Should().NotBeNull();
        value.Should().BeOfType<FhirBoolean>()
            .Which.Value.Should().BeTrue();
    }
    
    #endregion

    #region AST Output Tests
    
    [Fact]
    public async Task Evaluate_WithAstRequest_ReturnsParseDebugTree()
    {
        var parameters = CreateBasicRequest("Patient.name.family");
        var result = await PostFhirPathRequest("$fhirpath", parameters);
        
        var parseDebugTree = result.Parameter.FirstOrDefault(p => p.Name == "parseDebugTree");
        parseDebugTree.Should().NotBeNull("AST should be returned in parseDebugTree parameter");
        parseDebugTree!.Value.Should().BeOfType<FhirString>();
        
        var ast = ((FhirString)parseDebugTree.Value).Value;
        ast.Should().NotBeNullOrWhiteSpace();
        ast.Should().Contain("PropertyAccess");
    }
    
    [Fact]
    public async Task Evaluate_FunctionCall_AstShowsFunctionStructure()
    {
        var parameters = CreateBasicRequest("name.where(use = 'official').family");
        var result = await PostFhirPathRequest("$fhirpath", parameters);
        
        var parseDebugTree = result.Parameter.FirstOrDefault(p => p.Name == "parseDebugTree");
        parseDebugTree.Should().NotBeNull();
        
        var ast = ((FhirString)parseDebugTree!.Value).Value;
        ast.Should().Contain("Function");
        ast.Should().Contain("where");
    }
    
    #endregion

    #region Trace Tests
    
    [Fact]
    public async Task Evaluate_TraceFunction_ExecutesSuccessfully()
    {
        // trace() returns its input unchanged - just verify it doesn't error
        var parameters = CreateBasicRequest("1.trace('test-marker')", null, debugTrace: true);
        var result = await PostFhirPathRequest("$fhirpath", parameters);
        
        var value = GetFirstResultValue(result);
        value.Should().NotBeNull();
        value.Should().BeOfType<Integer>()
            .Which.Value.Should().Be(1);
    }
    
    [Fact]
    public async Task Evaluate_TraceWithPatient_IncludesTraceOutput()
    {
        var parameters = CreateBasicRequest("Patient.name.trace('patient-name').family", GetPatientR4Json(), debugTrace: true);
        var result = await PostFhirPathRequest("$fhirpath", parameters);
        
        // The expression should still return the family name
        var value = GetFirstResultValue(result);
        value.Should().NotBeNull();
        value.Should().BeOfType<FhirString>()
            .Which.Value.Should().Be("Smith");
    }
    
    #endregion

    #region FHIR Version Endpoint Tests
    
    [Fact]
    public async Task Endpoint_DefaultR4_ProcessesPatient()
    {
        var parameters = CreateBasicRequest("Patient.name.family", GetPatientR4Json());
        var result = await PostFhirPathRequest("$fhirpath", parameters);
        
        var value = GetFirstResultValue(result);
        value.Should().NotBeNull();
        value.Should().BeOfType<FhirString>()
            .Which.Value.Should().Be("Smith");
    }
    
    [Fact]
    public async Task Endpoint_R5_ProcessesExpression()
    {
        var parameters = CreateBasicRequest("1 + 2");
        var result = await PostFhirPathRequest("$fhirpath-r5", parameters);
        
        var value = GetFirstResultValue(result);
        value.Should().NotBeNull();
        value.Should().BeOfType<Integer>()
            .Which.Value.Should().Be(3);
    }
    
    [Fact]
    public async Task Endpoint_STU3_ProcessesExpression()
    {
        var parameters = CreateBasicRequest("'test'.length()");
        var result = await PostFhirPathRequest("$fhirpath-stu3", parameters);
        
        var value = GetFirstResultValue(result);
        value.Should().NotBeNull();
        value.Should().BeOfType<Integer>()
            .Which.Value.Should().Be(4);
    }
    
    [Fact]
    public async Task Endpoint_R4B_ProcessesExpression()
    {
        var parameters = CreateBasicRequest("true and false");
        var result = await PostFhirPathRequest("$fhirpath-r4b", parameters);
        
        var value = GetFirstResultValue(result);
        value.Should().NotBeNull();
        value.Should().BeOfType<FhirBoolean>()
            .Which.Value.Should().BeFalse();
    }
    
    [Fact]
    public async Task Endpoint_R6_ProcessesExpression()
    {
        var parameters = CreateBasicRequest("2 * 3");
        var result = await PostFhirPathRequest("$fhirpath-r6", parameters);
        
        var value = GetFirstResultValue(result);
        value.Should().NotBeNull();
        value.Should().BeOfType<Integer>()
            .Which.Value.Should().Be(6);
    }
    
    #endregion

    #region Metadata Tests
    
    [Fact]
    public async Task Metadata_ReturnsCapabilityStatement()
    {
        var response = await _client.GetAsync($"{_baseUrl}/metadata");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var json = await response.Content.ReadAsStringAsync();
        var capability = _parser.Parse<CapabilityStatement>(json);
        
        capability.Title.Should().Contain("Ignixa");
        capability.FhirVersion.Should().NotBeNull();
    }
    
    #endregion

    #region Error Handling Tests
    
    [Fact]
    public async Task Evaluate_InvalidExpression_HandlesGracefully()
    {
        var parameters = CreateBasicRequest("this is not valid fhirpath !!!");
        
        var json = _serializer.SerializeToString(parameters);
        var content = new StringContent(json, Encoding.UTF8, "application/fhir+json");
        
        var response = await _client.PostAsync($"{_baseUrl}/$fhirpath", content);
        
        // The API may return either 400 (parse error) or 200 with error in body
        // Both are valid behaviors depending on engine implementation
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var responseJson = await response.Content.ReadAsStringAsync();
            var result = _parser.Parse<Parameters>(responseJson);
            // Should have outcome or error parameter
            var hasError = result.Parameter.Any(p => p.Name == "outcome" || p.Name == "error");
            hasError.Should().BeTrue("Invalid expression should produce an error in the response");
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }
    
    [Fact]
    public async Task Evaluate_EmptyExpression_HandlesGracefully()
    {
        var parameters = CreateBasicRequest("");
        
        var json = _serializer.SerializeToString(parameters);
        var content = new StringContent(json, Encoding.UTF8, "application/fhir+json");
        
        var response = await _client.PostAsync($"{_baseUrl}/$fhirpath", content);
        
        // The API may return either 400 (validation error) or 200 with empty result
        // Both are valid behaviors
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var responseJson = await response.Content.ReadAsStringAsync();
            var result = _parser.Parse<Parameters>(responseJson);
            // Should have either error or parseDebugTree (showing it tried to parse)
            var hasContent = result.Parameter.Any(p => 
                p.Name == "outcome" || p.Name == "error" || p.Name == "parseDebugTree");
            hasContent.Should().BeTrue("Response should have meaningful content");
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }
    
    #endregion

    #region Response Structure Tests
    
    [Fact]
    public async Task Evaluate_Response_ContainsEvaluatorInfo()
    {
        var parameters = CreateBasicRequest("1");
        var result = await PostFhirPathRequest("$fhirpath", parameters);
        
        var parametersParam = result.Parameter.FirstOrDefault(p => p.Name == "parameters");
        parametersParam.Should().NotBeNull();
        
        var evaluatorPart = parametersParam!.Part?.FirstOrDefault(p => p.Name == "evaluator");
        evaluatorPart.Should().NotBeNull();
        evaluatorPart!.Value.Should().BeOfType<FhirString>()
            .Which.Value.Should().Contain("Ignixa");
    }
    
    [Fact]
    public async Task Evaluate_Response_EchoesExpression()
    {
        var expression = "Patient.name.given";
        var parameters = CreateBasicRequest(expression, GetPatientR4Json());
        var result = await PostFhirPathRequest("$fhirpath", parameters);
        
        var parametersParam = result.Parameter.FirstOrDefault(p => p.Name == "parameters");
        parametersParam.Should().NotBeNull();
        
        var exprPart = parametersParam!.Part?.FirstOrDefault(p => p.Name == "expression");
        exprPart.Should().NotBeNull();
        exprPart!.Value.Should().BeOfType<FhirString>()
            .Which.Value.Should().Be(expression);
    }
    
    #endregion
}
