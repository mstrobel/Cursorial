using System;

using Cursorial.CLI.Wire;

using Xunit;

namespace Cursorial.Tests.CLI.Wire;

public class VariableBagTests
{
    [Fact]
    public void Text_Resolves()
    {
        var bag = new VariableBag();
        bag.BindText("name", "release-v2");

        Assert.True(bag.TryResolve("name", out var value));
        Assert.Equal("release-v2", value);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void Bool_ResolvesLowercaseLiteral(bool bound, string expected)
    {
        var bag = new VariableBag();
        bag.BindBool("sure", bound);

        Assert.True(bag.TryResolve("sure", out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Selection_LabelsSpaceJoined()
    {
        var bag = new VariableBag();
        bag.BindSelection("picks", new[] { "alpha", "gamma" }, new[] { 0, 2 });

        Assert.True(bag.TryResolve("picks", out var value));
        Assert.Equal("alpha gamma", value);
    }

    [Fact]
    public void Selection_IndexAccessor_SpaceJoined()
    {
        var bag = new VariableBag();
        bag.BindSelection("picks", new[] { "alpha", "gamma" }, new[] { 0, 2 });

        Assert.True(bag.TryResolve("picks.index", out var value));
        Assert.Equal("0 2", value);
    }

    [Fact]
    public void SingleSelection_Resolution()
    {
        var bag = new VariableBag();
        bag.BindSelection("pick", new[] { "gamma" }, new[] { 2 });

        Assert.True(bag.TryResolve("pick", out var label));
        Assert.Equal("gamma", label);
        Assert.True(bag.TryResolve("pick.index", out var index));
        Assert.Equal("2", index);
    }

    [Fact]
    public void IndexAccessor_OnNonSelection_DoesNotResolve()
    {
        var bag = new VariableBag();
        bag.BindText("name", "x");
        bag.BindBool("sure", true);

        Assert.False(bag.TryResolve("name.index", out var value));
        Assert.Equal("", value);
        Assert.False(bag.TryResolve("sure.index", out _));
    }

    [Fact]
    public void Unbound_ResolvesFalse_WithEmptyValue()
    {
        var bag = new VariableBag();

        Assert.False(bag.TryResolve("ghost", out var value));
        Assert.Equal("", value);
    }

    [Fact]
    public void ExactNameBinding_WinsOverIndexReading()
    {
        // A text variable literally named "picks.index" shadows the selection's index accessor.
        var bag = new VariableBag();
        bag.BindSelection("picks", new[] { "a" }, new[] { 0 });
        bag.BindText("picks.index", "shadow");

        Assert.True(bag.TryResolve("picks.index", out var value));
        Assert.Equal("shadow", value);
    }

    [Fact]
    public void Variables_PreserveBindOrder_AndCarryShape()
    {
        var bag = new VariableBag();
        bag.BindText("name", "x");
        bag.BindSelection("picks", new[] { "a", "b" }, new[] { 0, 1 });
        bag.BindBool("sure", false);

        Assert.Equal(3, bag.Variables.Count);

        Assert.Equal("name", bag.Variables[0].Name);
        Assert.Equal(VariableKind.Text, bag.Variables[0].Kind);
        Assert.Equal(new[] { "x" }, bag.Variables[0].Values);
        Assert.Empty(bag.Variables[0].Indices);

        Assert.Equal("picks", bag.Variables[1].Name);
        Assert.Equal(VariableKind.Selection, bag.Variables[1].Kind);
        Assert.Equal(new[] { "a", "b" }, bag.Variables[1].Values);
        Assert.Equal(new[] { 0, 1 }, bag.Variables[1].Indices);

        Assert.Equal("sure", bag.Variables[2].Name);
        Assert.Equal(VariableKind.Bool, bag.Variables[2].Kind);
        Assert.Equal(new[] { "false" }, bag.Variables[2].Values);
    }

    [Fact]
    public void Rebind_ReplacesValue_KeepsPosition()
    {
        var bag = new VariableBag();
        bag.BindText("first", "1");
        bag.BindText("second", "2");
        bag.BindBool("first", true); // later step rebinds, possibly with a different kind

        Assert.Equal(2, bag.Variables.Count);
        Assert.Equal("first", bag.Variables[0].Name);
        Assert.Equal(VariableKind.Bool, bag.Variables[0].Kind);
        Assert.True(bag.TryResolve("first", out var value));
        Assert.Equal("true", value);
    }

    [Fact]
    public void Selection_MismatchedLabelAndIndexCounts_Throws()
    {
        var bag = new VariableBag();

        Assert.Throws<ArgumentException>(() => bag.BindSelection("picks", new[] { "a", "b" }, new[] { 0 }));
    }

    [Fact]
    public void Selection_IsDefensivelyCopied()
    {
        var labels = new[] { "a" };
        var indices = new[] { 0 };
        var bag = new VariableBag();
        bag.BindSelection("pick", labels, indices);

        labels[0] = "mutated";
        indices[0] = 9;

        Assert.True(bag.TryResolve("pick", out var label));
        Assert.Equal("a", label);
        Assert.True(bag.TryResolve("pick.index", out var index));
        Assert.Equal("0", index);
    }
}
