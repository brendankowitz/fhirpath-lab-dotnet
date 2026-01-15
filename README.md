# FhirPathLab-DotNetEngine (Ignixa)

## Overview
The fhirpath-lab is a dedicated tool for testing out fhirpath expressions against the various
fhirpath execution engines available - dotnet (Ignixa), java (HAPI) and javascript (nlm).

This project implements the dotnet engine using the **Ignixa** FhirPath library, a high-performance
FHIR tooling library that provides:
- FhirPath expression parsing and evaluation
- High-performance JSON serialization
- FHIR resource handling

## Prerequisites
- .NET 9.0 SDK
- Azure Functions Core Tools v4
- Ignixa source code (referenced as project dependency from `../../fhir-server-contrib/src/Core/`)

## Running Locally
```bash
cd fhirpath-lab-dotnet
func start
```

## API Endpoints

### GET /api/metadata
Returns the CapabilityStatement for the FhirPath tester.

### GET/POST /api/$fhirpath
Evaluates FhirPath expressions against FHIR resources.

**Parameters:**
- `expression` (required): The FhirPath expression to evaluate
- `resource`: A FHIR resource (inline JSON or URL)
- `context`: Optional context expression to scope evaluation
- `terminologyserver`: Optional terminology server URL

## Architecture
- **Azure Functions v4** with .NET 9 isolated worker model
- **Ignixa.FhirPath**: Expression parsing and evaluation
- **Ignixa.Serialization**: JSON parsing and serialization

Issues will be managed through the fhirpath-lab project, and not this specific repo's issue list.
