using System;
using System.IO;

using Cursorial.CLI.Wire;

using Xunit;

namespace Cursorial.Tests.CLI.Wire;

public class EmitTests
{
    private static StringWriter Writer() => new() { NewLine = "\n" };

    private static string Lines(Variable variable)
    {
        var writer = Writer();
        Emit.WriteLines(writer, variable);
        return writer.ToString();
    }

    private static string Env(VariableBag bag)
    {
        var writer = Writer();
        Emit.WriteEnv(writer, bag);
        return writer.ToString();
    }

    private static string Json(VariableBag bag)
    {
        var writer = Writer();
        Emit.WriteJson(writer, bag);
        return writer.ToString();
    }

    // --- lines ---

    [Fact]
    public void Lines_Text_OneLine()
    {
        var v = new Variable("name", VariableKind.Text, new[] { "release-v2" }, Array.Empty<int>());

        Assert.Equal("release-v2\n", Lines(v));
    }

    [Fact]
    public void Lines_MultiSelection_OnePerLabel_NoIndices()
    {
        var v = new Variable("picks", VariableKind.Selection, new[] { "alpha", "gamma" }, new[] { 0, 2 });

        Assert.Equal("alpha\ngamma\n", Lines(v));
    }

    [Fact]
    public void Lines_Bool()
    {
        var v = new Variable("sure", VariableKind.Bool, new[] { "false" }, Array.Empty<int>());

        Assert.Equal("false\n", Lines(v));
    }

    // --- env ---

    [Fact]
    public void Env_Text()
    {
        var bag = new VariableBag();
        bag.BindText("name", "release-v2");

        Assert.Equal("NAME='release-v2'\n", Env(bag));
    }

    [Fact]
    public void Env_EmbeddedSingleQuote_Escaped()
    {
        var bag = new VariableBag();
        bag.BindText("msg", "it's done");

        Assert.Equal("MSG='it'\\''s done'\n", Env(bag));
    }

    [Fact]
    public void Env_NameTransform_UppercasesAndFoldsNonAlnum()
    {
        var bag = new VariableBag();
        bag.BindText("my-var.2", "x");

        Assert.Equal("MY_VAR_2='x'\n", Env(bag));
    }

    [Fact]
    public void Env_SingleSelection_EmitsIndex()
    {
        var bag = new VariableBag();
        bag.BindSelection("pick", new[] { "gamma" }, new[] { 2 });

        Assert.Equal("PICK='gamma'\nPICK_INDEX='2'\n", Env(bag));
    }

    [Fact]
    public void Env_MultiSelection_SpaceJoins()
    {
        var bag = new VariableBag();
        bag.BindSelection("picks", new[] { "alpha", "gamma" }, new[] { 0, 2 });

        Assert.Equal("PICKS='alpha gamma'\nPICKS_INDEX='0 2'\n", Env(bag));
    }

    [Fact]
    public void Env_Bool()
    {
        var bag = new VariableBag();
        bag.BindBool("sure", true);

        Assert.Equal("SURE='true'\n", Env(bag));
    }

    [Fact]
    public void Env_EmptyBag_EmitsNothing()
        => Assert.Equal("", Env(new VariableBag()));

    // --- json ---

    [Fact]
    public void Json_TextAndBool()
    {
        var bag = new VariableBag();
        bag.BindText("name", "x");
        bag.BindBool("sure", true);

        Assert.Equal("{\n  \"name\": \"x\",\n  \"sure\": true\n}\n", Json(bag));
    }

    [Fact]
    public void Json_EscapesQuotesBackslashesAndControlChars()
    {
        var bag = new VariableBag();
        bag.BindText("msg", "a\"b\\c\nd\te" + '\u0001' + "f");

        Assert.Equal("{\n  \"msg\": \"a\\\"b\\\\c\\nd\\te\\u0001f\"\n}\n", Json(bag));
    }

    [Fact]
    public void Json_SingleSelection_ScalarShapes()
    {
        var bag = new VariableBag();
        bag.BindSelection("pick", new[] { "gamma" }, new[] { 2 });

        Assert.Equal("{\n  \"pick\": \"gamma\",\n  \"pick.index\": 2\n}\n", Json(bag));
    }

    [Fact]
    public void Json_MultiSelection_ArrayShapes()
    {
        var bag = new VariableBag();
        bag.BindSelection("picks", new[] { "alpha", "gamma" }, new[] { 0, 2 });

        Assert.Equal("{\n  \"picks\": [\"alpha\", \"gamma\"],\n  \"picks.index\": [0, 2]\n}\n", Json(bag));
    }

    [Fact]
    public void Json_EmptyBag_EmitsEmptyObject()
        => Assert.Equal("{}\n", Json(new VariableBag()));

    [Fact]
    public void Json_KeysNeedingEscape_AreEscaped()
    {
        var bag = new VariableBag();
        bag.BindText("we\"ird", "v");

        Assert.Equal("{\n  \"we\\\"ird\": \"v\"\n}\n", Json(bag));
    }
}
