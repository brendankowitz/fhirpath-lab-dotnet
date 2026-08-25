using FluentAssertions;
using FhirPathLab_DotNetEngine.Models;
using FhirPathLab_DotNetEngine.Services;
using Ignixa.Abstractions;
using Ignixa.Serialization.SourceNodes;
using Ignixa.Specification.Generated;
using Ignixa.Specification.Extensions;

namespace FhirPathLab.Tests;

/// <summary>
/// Regression tests for FHIRPath instance selector evaluation.
/// </summary>
public class InstanceSelectorTests
{
    [Fact]
    public void GivenStandaloneCodingSelector_WhenEvaluated_ThenCreatesCodingWithAssignedChildren()
    {
        var result = Evaluate("Coding { system: 'http://loinc.org', code: '8480-6' }");

        result.Error.Should().BeNull();
        result.OutputValues.Should().ContainSingle();

        var coding = result.OutputValues[0];
        coding.InstanceType.Should().Be("Coding");
        coding.Children("system").Should().ContainSingle()
            .Which.Value.Should().Be("http://loinc.org");
        coding.Children("code").Should().ContainSingle()
            .Which.Value.Should().Be("8480-6");
    }

    [Fact]
    public void GivenPatientNames_WhenSelectedIntoHumanNames_ThenCreatesReshapedNames()
    {
        var patient = ResourceJsonNode.Parse("""
            {
              "resourceType": "Patient",
              "name": [
                { "family": "Smith", "given": ["John", "James"] }
              ]
            }
            """);

        var result = Evaluate(
            "name.select(HumanName { family: family, given: given.first() })",
            patient);

        result.Error.Should().BeNull();
        result.OutputValues.Should().ContainSingle();

        var name = result.OutputValues[0];
        name.InstanceType.Should().Be("HumanName");
        name.Children("family").Should().ContainSingle().Which.Value.Should().Be("Smith");
        name.Children("given").Should().ContainSingle().Which.Value.Should().Be("John");
    }

    [Fact]
    public void GivenNestedSelector_WhenEvaluated_ThenCreatesNestedQuantity()
    {
        var result = Evaluate("Observation { value: Quantity { value: 70 } }");

        result.Error.Should().BeNull();
        result.OutputValues.Should().ContainSingle();

        var observation = result.OutputValues[0];
        observation.InstanceType.Should().Be("Observation");
        var quantity = observation.Children("value").Should().ContainSingle().Which;
        quantity.InstanceType.Should().Be("Quantity");
        quantity.Children("value").Should().ContainSingle().Which.Value.Should().Be(70);
    }

    [Fact]
    public void GivenEmptySelectorValue_WhenEvaluated_ThenOmitsTheChild()
    {
        var result = Evaluate("Coding { system: 'http://loinc.org', code: ('missing').where(false) }");

        result.Error.Should().BeNull();
        result.OutputValues.Should().ContainSingle();
        result.OutputValues[0].Children("system").Should().ContainSingle();
        result.OutputValues[0].Children("code").Should().BeEmpty();
    }

    [Fact]
    public void GivenInstanceSelector_WhenEvaluated_ThenConstructsCodingWithAssignedCode()
    {
        var result = Evaluate("Coding { code: '8480-6' }");

        result.Error.Should().BeNull();
        result.OutputValues.Should().ContainSingle();

        var coding = result.OutputValues[0];
        coding.InstanceType.Should().Be("Coding");
        coding.Children("code").Should().ContainSingle()
            .Which.Value.Should().Be("8480-6");
    }

    [Fact]
    public void GivenSchemaProvider_WhenGettingInstanceFactory_ThenReturnsCachedFactory()
    {
        var schemaFactory = new SchemaProviderFactory();
        var schemaProvider = FhirVersion.R4.GetSchemaProvider();

        var first = schemaFactory.GetInstanceFactory(schemaProvider);
        var second = schemaFactory.GetInstanceFactory(schemaProvider);

        second.Should().BeSameAs(first);
    }

    private static EvaluationResult Evaluate(string expression, ResourceJsonNode? resource = null)
    {
        var schemaFactory = new SchemaProviderFactory();
        var analyzer = new ExpressionAnalyzer(schemaFactory);
        var evaluator = new ExpressionEvaluator(schemaFactory);
        var (parsed, contextExpression, error) = analyzer.ParseAndAnalyze(
            expression,
            contextExpression: null,
            rootTypeName: resource?.ResourceType,
            fhirVersion: "R4");

        error.Should().BeNull();
        parsed.Should().NotBeNull();

        return evaluator.Evaluate(parsed!, contextExpression, resource, null, "R4").Should().ContainSingle().Which;
    }
}
