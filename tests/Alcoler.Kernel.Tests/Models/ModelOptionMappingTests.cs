using System.Text.Json.Nodes;
using Alcoler.Adapters.Models;
using Xunit;

namespace Alcoler.Kernel.Tests.Models;

public class ModelOptionMappingTests
{
    [Theory]
    [InlineData("disabled", "disabled", false, "none")]
    [InlineData("none", "disabled", false, "none")]
    [InlineData("enabled", "enabled", true, "high")]
    [InlineData("medium", "enabled", true, "medium")]
    public void CanonicalCompositeReasoningMap_Evaluates(string level, string type, bool enabled, string effort)
    {
        const string expression = """
            {
              "thinking": {"type": reasoningLevel == "disabled" || reasoningLevel == "none" ? "disabled" : "enabled"},
              "enable_thinking": reasoningLevel != "disabled" && reasoningLevel != "none",
              "reasoning_effort": reasoningLevel == "disabled" ? "none" : reasoningLevel == "enabled" ? "high" : reasoningLevel,
              "reasoning": {"effort": reasoningLevel == "disabled" ? "none" : reasoningLevel == "enabled" ? "high" : reasoningLevel}
            }
            """;
        ModelOptionMapping.Validate(expression);
        var result = ModelOptionMapping.Evaluate(expression, level);
        Assert.Equal(type, result["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal(enabled, result["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(effort, result["reasoning"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void ReferenceGrammar_HandlesArithmeticArraysEscapesAndShortCircuit()
    {
        var result = ModelOptionMapping.Evaluate("""
            (maxOutputTokens > 100 && reasoningLevel != 'none') ?
              {'max_tokens': maxOutputTokens - 2 * 3, 'values': [null, true, -3, +4, 'a\u0062', 'c' + 'd'], 'ok': true || 1 / 0 == 0} : {}
            """, "high", 200);
        Assert.Equal(194d, result["max_tokens"]!.GetValue<double>());
        Assert.Null(result["values"]![0]);
        Assert.Equal("ab", result["values"]![4]!.GetValue<string>());
        Assert.True(result["ok"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("reasoningLevel.toString()")]
    [InlineData("eval('x')")]
    [InlineData("{'value': otherVariable}")]
    [InlineData("{'x':1,'x':2}")]
    [InlineData("[]")]
    [InlineData("reasoningLevel == 'high' ? {} : null")]
    [InlineData("{'model':'evil'}")]
    [InlineData("{'messages':null}")]
    [InlineData("{'tools':[]}")]
    [InlineData("{'stream':false}")]
    [InlineData("reasoningLevel == 'high' ? {} : {'input':[]}")]
    public void InvalidOrStructuralMappings_AreRejected(string expression) =>
        Assert.Throws<ArgumentException>(() => ModelOptionMapping.Validate(expression));

    [Fact]
    public void MissingValuesTypeErrorsAndUnsafeNumbers_AreActionable()
    {
        Assert.Contains("requires an effective value", Assert.Throws<ArgumentException>(() => ModelOptionMapping.Evaluate("{'x': reasoningLevel}")).Message);
        Assert.Contains("Boolean operand", Assert.Throws<ArgumentException>(() => ModelOptionMapping.Evaluate("reasoningLevel ? {} : {}", "high")).Message);
        Assert.Throws<ArgumentException>(() => ModelOptionMapping.Evaluate("{'x':1/0}"));
        Assert.Throws<ArgumentException>(() => ModelOptionMapping.Validate("{'x':9007199254740992}"));
    }

    [Fact]
    public void BoundedParser_RejectsDeepAndOversizedExpressions()
    {
        Assert.Throws<ArgumentException>(() => ModelOptionMapping.Validate(new string('(', 100) + "{}" + new string(')', 100)));
        Assert.Throws<ArgumentException>(() => ModelOptionMapping.Validate("{'x':'" + new string('a', 33_000) + "'}"));
        Assert.Throws<ArgumentException>(() => ModelOptionMapping.Validate("{'x':" + string.Join("+", Enumerable.Repeat("1", 1000)) + "}"));
    }

    [Fact]
    public void MergePatch_NullDeletesAndArraysReplace()
    {
        var body = JsonNode.Parse("""{"thinking":{"type":"adaptive","budget":100},"list":[1,2],"remove":true}""")!.AsObject();
        var patch = ModelOptionMapping.Evaluate("""{'thinking':{'budget':null},'list':[3],'remove':null}""");
        ModelOptionMapping.MergePatch(body, patch);
        Assert.Null(body["thinking"]!["budget"]);
        Assert.Equal("adaptive", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Single(body["list"]!.AsArray());
        Assert.False(body.ContainsKey("remove"));
    }
}
