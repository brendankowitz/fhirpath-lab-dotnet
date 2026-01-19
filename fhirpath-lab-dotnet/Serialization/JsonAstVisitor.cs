using System.Text.Json.Nodes;
using Ignixa.FhirPath.Analysis;
using Ignixa.FhirPath.Expressions;
using Ignixa.FhirPath.Visitors;

namespace FhirPathLab_DotNetEngine.Serialization;

/// <summary>
/// Visitor that converts a FhirPath expression tree into a JSON representation
/// matching the fhirpath-lab UI expectations.
///
/// The UI expects specific expression type names:
/// - AxisExpression with Name "builtin.that" for the expression scope (root context)
/// - AxisExpression with Name "builtin.this" for $this
/// - ChildExpression (not Child) with name without dot prefix
/// - FunctionCallExpression (not FunctionCall)
/// - VariableRefExpression (not Variable) with name without % prefix
/// - ConstantExpression (not Constant)
/// </summary>
public class JsonAstVisitor : IFhirPathExpressionVisitor<AnalysisResult?, JsonObject>
{
    /// <summary>
    /// The root type name for the expression scope (e.g., "Patient").
    /// This is used to set the ReturnType of the AxisExpression "builtin.that" node.
    /// </summary>
    public string? RootTypeName { get; set; }
    public JsonObject VisitBinary(BinaryExpression expression, AnalysisResult? context)
    {
        var node = CreateNode(expression, "Binary", expression.Operator, context);
        node["Arguments"] = new JsonArray(
            expression.Left.AcceptVisitor(this, context),
            expression.Right.AcceptVisitor(this, context)
        );
        return node;
    }

    public JsonObject VisitChild(ChildExpression expression, AnalysisResult? context)
    {
        // UI expects "ChildExpression" and name without dot prefix (UI adds the dot)
        var node = CreateNode(expression, "ChildExpression", expression.ChildName, context);

        if (expression.Focus != null)
        {
            node["Arguments"] = new JsonArray(expression.Focus.AcceptVisitor(this, context));
        }
        else
        {
            // No focus means this is the root - inject expression scope node
            node["Arguments"] = new JsonArray(CreateExpressionScopeNode());
        }
        return node;
    }

    public JsonObject VisitConstant(ConstantExpression expression, AnalysisResult? context)
    {
        var valueStr = expression.Value?.ToString() ?? "null";
        // UI expects "ConstantExpression"
        var node = CreateNode(expression, "ConstantExpression", valueStr, context);
        // Set return type based on the actual value type
        var typeName = expression.Value?.GetType().Name?.ToLowerInvariant() switch
        {
            "string" => "string",
            "int32" or "int64" => "integer",
            "single" or "double" or "decimal" => "decimal",
            "boolean" => "boolean",
            "datetime" or "datetimeoffset" => "dateTime",
            null => "null",
            _ => expression.Value?.GetType().Name ?? "unknown"
        };
        node["ReturnType"] = typeName;
        return node;
    }

    public JsonObject VisitEmpty(EmptyExpression expression, AnalysisResult? context)
    {
        return CreateNode(expression, "Empty", "{}", context);
    }

    public JsonObject VisitFunctionCall(FunctionCallExpression expression, AnalysisResult? context)
    {
        // UI expects "FunctionCallExpression"
        var node = CreateNode(expression, "FunctionCallExpression", expression.FunctionName, context);

        var args = new JsonArray();
        if (expression.Focus != null)
        {
            args.Add(expression.Focus.AcceptVisitor(this, context));
        }
        else
        {
            // No focus means this is the root - inject expression scope node
            args.Add(CreateExpressionScopeNode());
        }

        foreach (var arg in expression.Arguments)
        {
            args.Add(arg.AcceptVisitor(this, context));
        }

        node["Arguments"] = args;

        return node;
    }

    public JsonObject VisitIdentifier(IdentifierExpression expression, AnalysisResult? context)
    {
        // An identifier at the root level represents a child access on the context
        var node = CreateNode(expression, "ChildExpression", expression.Name, context);
        // Inject expression scope as the focus
        node["Arguments"] = new JsonArray(CreateExpressionScopeNode());
        return node;
    }

    public JsonObject VisitIndexer(IndexerExpression expression, AnalysisResult? context)
    {
        var node = CreateNode(expression, "Indexer", "[]", context);
        node["Arguments"] = new JsonArray(
            expression.Collection.AcceptVisitor(this, context),
            expression.Index.AcceptVisitor(this, context)
        );
        return node;
    }

    public JsonObject VisitParenthesized(ParenthesizedExpression expression, AnalysisResult? context)
    {
        var node = CreateNode(expression, "Parenthesized", "()", context);
        node["Arguments"] = new JsonArray(expression.InnerExpression.AcceptVisitor(this, context));
        return node;
    }

    public JsonObject VisitPropertyAccess(PropertyAccessExpression expression, AnalysisResult? context)
    {
        // UI expects "ChildExpression" for property access, name without dot prefix
        var node = CreateNode(expression, "ChildExpression", expression.PropertyName, context);

        if (expression.Focus != null)
        {
            node["Arguments"] = new JsonArray(expression.Focus.AcceptVisitor(this, context));
        }
        else
        {
            // No focus means this is the root - inject expression scope node
            node["Arguments"] = new JsonArray(CreateExpressionScopeNode());
        }
        return node;
    }

    public JsonObject VisitQuantity(QuantityExpression expression, AnalysisResult? context)
    {
        // UI expects "ConstantExpression" for quantities
        var node = CreateNode(expression, "ConstantExpression", $"{expression.Value} '{expression.Unit}'", context);
        node["ReturnType"] = "Quantity";
        return node;
    }

    public JsonObject VisitScope(ScopeExpression expression, AnalysisResult? context)
    {
        // UI expects "AxisExpression" with "builtin.this" for $this, "builtin.index" for $index, etc.
        // Note: Ignixa parser uses "that" for the implicit context ($context or expression scope)
        var scopeName = expression.ScopeName ?? "";
        var isExpressionScope = scopeName is "that" or "context" or "";
        var name = scopeName switch
        {
            "this" => "builtin.this",
            "index" => "builtin.index",
            "total" => "builtin.total",
            "that" or "context" or "" => "builtin.that",
            _ => $"builtin.{scopeName}"
        };
        var node = CreateNode(expression, "AxisExpression", name, context);

        // For "builtin.that" (expression scope), always set the RootTypeName as ReturnType
        // This ensures the expression scope has the correct return type even when
        // the parser creates an explicit scope expression. We overwrite any existing
        // ReturnType since the RootTypeName is the authoritative source for expression scope.
        if (isExpressionScope && !string.IsNullOrEmpty(RootTypeName))
        {
            node["ReturnType"] = RootTypeName;
        }

        return node;
    }

    public JsonObject VisitUnary(UnaryExpression expression, AnalysisResult? context)
    {
        var node = CreateNode(expression, "Unary", expression.Operator, context);
        node["Arguments"] = new JsonArray(expression.Operand.AcceptVisitor(this, context));
        return node;
    }

    public JsonObject VisitVariable(VariableRefExpression expression, AnalysisResult? context)
    {
        // UI expects "VariableRefExpression" with name without % prefix
        return CreateNode(expression, "VariableRefExpression", expression.Name, context);
    }

    /// <summary>
    /// Creates an expression scope node representing "builtin.that" - the implicit context
    /// that the expression operates on.
    /// </summary>
    private JsonObject CreateExpressionScopeNode()
    {
        return new JsonObject
        {
            ["ExpressionType"] = "AxisExpression",
            ["Name"] = "builtin.that",
            ["ReturnType"] = RootTypeName ?? ""
        };
    }

    private static JsonObject CreateNode(Expression expression, string type, string name, AnalysisResult? context)
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

        // Add inferred type from NodeTypes map if available
        if (context?.NodeTypes?.TryGetValue(expression, out var typeSet) == true && typeSet.Types.Count > 0)
        {
            var typeNames = typeSet.Types.Select(t => t.TypeName).Where(t => !string.IsNullOrEmpty(t)).Distinct();
            var isCollection = typeSet.Types.Any(t => t.IsCollection);
            var typeName = string.Join(", ", typeNames);
            if (isCollection && !typeName.EndsWith("[]"))
                typeName += "[]";
            node["ReturnType"] = typeName;
        }

        return node;
    }
}
