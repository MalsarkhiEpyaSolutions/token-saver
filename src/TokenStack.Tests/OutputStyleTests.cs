using System.Text.Json.Nodes;
using TokenStack.Core.Claude;
using TokenStack.Core.Components;
using Xunit;

namespace TokenStack.Tests;

public class OutputStyleTests
{
    // ---------- the tri-state ask/remember decision ----------

    [Fact]
    public void Resolve_NeverAsked_NonInteractive_StaysOff()
        => Assert.False(OutputStyleComponent.Resolve(null, ask: null));

    [Fact]
    public void Resolve_NeverAsked_Interactive_TakesTheAnswer()
    {
        Assert.True(OutputStyleComponent.Resolve(null, () => true));
        Assert.False(OutputStyleComponent.Resolve(null, () => false));
    }

    [Fact]
    public void Resolve_AlreadyAnswered_NeverAsksAgain()
    {
        var asked = false;
        Assert.False(OutputStyleComponent.Resolve(false, () => { asked = true; return true; }));
        Assert.True(OutputStyleComponent.Resolve(true, () => { asked = true; return false; }));
        Assert.False(asked); // a declined install must not nag on every re-run
    }

    // ---------- the embedded style ----------

    [Fact]
    public void ReadEmbedded_CarriesTheStyleNameAndTheCapRule()
    {
        var md = OutputStyleComponent.ReadEmbedded();
        Assert.Contains($"name: {OutputStyleComponent.StyleName}", md);
        Assert.Contains("Cap suggestion lists at five", md);
        // The cap must never swallow exhaustive results — that exception is the whole safety net.
        Assert.Contains("NEVER applies to exhaustive results", md);
    }

    // ---------- settings.json surgery ----------

    [Fact]
    public void SetOutputStyle_SetsThenReportsNoFurtherChange()
    {
        var root = JsonNode.Parse("{}")!;
        Assert.True(ClaudeSurgeon.SetOutputStyle(root, "Concise Plus"));
        Assert.Equal("Concise Plus", root["outputStyle"]!.GetValue<string>());
        Assert.False(ClaudeSurgeon.SetOutputStyle(root, "Concise Plus")); // idempotent
    }

    [Fact]
    public void RemoveOutputStyle_ClearsOurs()
    {
        var root = JsonNode.Parse("""{"outputStyle":"Concise Plus","env":{"X":"1"}}""")!;
        Assert.True(ClaudeSurgeon.RemoveOutputStyle(root, "Concise Plus"));
        Assert.Null(root["outputStyle"]);
        Assert.Equal("1", root["env"]!["X"]!.GetValue<string>()); // siblings survive
    }

    [Fact]
    public void RemoveOutputStyle_LeavesAStyleTheUserPicked()
    {
        var root = JsonNode.Parse("""{"outputStyle":"Explanatory"}""")!;
        Assert.False(ClaudeSurgeon.RemoveOutputStyle(root, "Concise Plus"));
        Assert.Equal("Explanatory", root["outputStyle"]!.GetValue<string>());
    }

    [Fact]
    public void RemoveOutputStyle_NoKey_IsNoChange()
        => Assert.False(ClaudeSurgeon.RemoveOutputStyle(JsonNode.Parse("{}")!, "Concise Plus"));
}
