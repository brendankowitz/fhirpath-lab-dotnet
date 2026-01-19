using System.Text.Json.Nodes;
using Ignixa.FhirPath.Analysis;
using Ignixa.FhirPath.Expressions;
using Ignixa.FhirPath.Visitors;

namespace FhirPathLab_DotNetEngine.Serialization;

/// <summary>
/// Visitor that converts a FhirPath expression tree into a JSON representation 
/// matching the fhirpath-lab UI expectations.
/// </summary>
public class JsonAstVisitor : IFhirPathExpressionVisitor<AnalysisResult?, JsonObject>
{
    public JsonObject VisitBinary(BinaryExpression expression, AnalysisResult? context)
    {
        var node = CreateNode(expression, "Binary", expression.Operator);
        node["Arguments"] = new JsonArray(
            expression.Left.AcceptVisitor(this, context),
            expression.Right.AcceptVisitor(this, context)
        );
        return node;
    }

    public JsonObject VisitChild(ChildExpression expression, AnalysisResult? context)
    {
        var node = CreateNode(expression, "Child", $".{expression.ChildName}");
        if (expression.Focus != null)
        {
            node["Arguments"] = new JsonArray(expression.Focus.AcceptVisitor(this, context));
        }
        return node;
    }

    public JsonObject VisitConstant(ConstantExpression expression, AnalysisResult? context)
    {
        var valueStr = expression.Value?.ToString() ?? "null";
        // Special case: strings are quoted in ToString(), but UI might expect raw value or specific format
        // The previous implementation used .Value?.ToString()
        var node = CreateNode(expression, "Constant", valueStr);
        node["ReturnType"] = expression.Value?.GetType().Name;
        return node;
    }

    public JsonObject VisitEmpty(EmptyExpression expression, AnalysisResult? context)
    {
        return CreateNode(expression, "Empty", "{}");
    }

    public JsonObject VisitFunctionCall(FunctionCallExpression expression, AnalysisResult? context)
    {
        var node = CreateNode(expression, "FunctionCall", expression.FunctionName);
        
        var args = new JsonArray();
        if (expression.Focus != null)
        {
            args.Add(expression.Focus.AcceptVisitor(this, context));
        }
        
        foreach (var arg in expression.Arguments)
        {
            args.Add(arg.AcceptVisitor(this, context));
        }
        
        if (args.Count > 0)
        {
            node["Arguments"] = args;
        }
        
        return node;
    }

    public JsonObject VisitIdentifier(IdentifierExpression expression, AnalysisResult? context)
    {
        return CreateNode(expression, "Identifier", expression.Name);
    }

    public JsonObject VisitIndexer(IndexerExpression expression, AnalysisResult? context)
    {
        var node = CreateNode(expression, "Indexer", "[]");
        node["Arguments"] = new JsonArray(
            expression.Collection.AcceptVisitor(this, context),
            expression.Index.AcceptVisitor(this, context)
        );
        return node;
    }

    public JsonObject VisitParenthesized(ParenthesizedExpression expression, AnalysisResult? context)
    {
        var node = CreateNode(expression, "Parenthesized", "()");
        node["Arguments"] = new JsonArray(expression.InnerExpression.AcceptVisitor(this, context));
        return node;
    }

    public JsonObject VisitPropertyAccess(PropertyAccessExpression expression, AnalysisResult? context)
    {
        var node = CreateNode(expression, "PropertyAccess", $".{expression.PropertyName}");
        if (expression.Focus != null)
        {
            node["Arguments"] = new JsonArray(expression.Focus.AcceptVisitor(this, context));
        }
        return node;
    }

    public JsonObject VisitQuantity(QuantityExpression expression, AnalysisResult? context)
    {
        return CreateNode(expression, "Quantity", $"{expression.Value} '{expression.Unit}'");
    }

    public JsonObject VisitScope(ScopeExpression expression, AnalysisResult? context)
    {
        return CreateNode(expression, "Scope", $"${expression.ScopeName}");
    }

    public JsonObject VisitUnary(UnaryExpression expression, AnalysisResult? context)
    {
        var node = CreateNode(expression, "Unary", expression.Operator);
        node["Arguments"] = new JsonArray(expression.Operand.AcceptVisitor(this, context));
        return node;
    }

    public JsonObject VisitVariable(VariableRefExpression expression, AnalysisResult? context)
    {
        return CreateNode(expression, "Variable", $"%{expression.Name}");
    }

    private JsonObject CreateNode(Expression expression, string type, string name)
    {
        var node = new JsonObject
        {
            ["ExpressionType"] = type,
            ["Name"] = name
        };

        // Add position information if available
        if (expression.Location != null)
        {
            node["Position"] = expression.Location.RawPosition;
            node["Length"] = expression.Location.Length;
            node["Line"] = expression.Location.LineNumber;
            node["Column"] = expression.Location.LinePosition;
        }

        // Add inferred type from analysis if populated on the expression
        if (!string.IsNullOrEmpty(expression.InferredType))
        {
            node["ReturnType"] = expression.InferredType;
        }

        return node;
    }
}
