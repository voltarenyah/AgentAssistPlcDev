using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class TextDifferTests
{
    [Fact]
    public void IdenticalInputsProduceOnlySameLines()
    {
        var diff = TextDiffer.Diff("a\nb\nc", "a\nb\nc");

        Assert.Equal(
            new[] { ("same", "a"), ("same", "b"), ("same", "c") },
            diff.Select(line => (line.Kind, line.Text)));
    }

    [Fact]
    public void AddedLineInTheMiddleIsMarkedAdded()
    {
        var diff = TextDiffer.Diff("a\nc", "a\nb\nc");

        Assert.Equal(
            new[] { ("same", "a"), ("added", "b"), ("same", "c") },
            diff.Select(line => (line.Kind, line.Text)));
    }

    [Fact]
    public void RemovedLineInTheMiddleIsMarkedRemoved()
    {
        var diff = TextDiffer.Diff("a\nb\nc", "a\nc");

        Assert.Equal(
            new[] { ("same", "a"), ("removed", "b"), ("same", "c") },
            diff.Select(line => (line.Kind, line.Text)));
    }

    [Fact]
    public void ChangedLineIsRemovedThenAdded()
    {
        var diff = TextDiffer.Diff("a\nold\nc", "a\nnew\nc");

        Assert.Equal(
            new[] { ("same", "a"), ("removed", "old"), ("added", "new"), ("same", "c") },
            diff.Select(line => (line.Kind, line.Text)));
    }

    [Fact]
    public void CarriageReturnsAreIgnored()
    {
        var diff = TextDiffer.Diff("a\r\nb\r\n", "a\nb\n");

        Assert.All(diff, line => Assert.Equal(DiffLine.Same, line.Kind));
    }

    [Fact]
    public void CompletelyDifferentInputsAreAllRemovedThenAllAdded()
    {
        var diff = TextDiffer.Diff("a\nb", "x\ny");

        Assert.Equal(
            new[] { ("removed", "a"), ("removed", "b"), ("added", "x"), ("added", "y") },
            diff.Select(line => (line.Kind, line.Text)));
    }

    [Fact]
    public void CommonSuffixSurvivesAMiddleChange()
    {
        var oldText = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line{i}"));
        var newText = string.Join("\n", Enumerable.Range(1, 100).Select(i => i == 50 ? "changed" : $"line{i}"));

        var diff = TextDiffer.Diff(oldText, newText);

        Assert.Equal(101, diff.Count);
        Assert.Equal(("removed", "line50"), (diff[49].Kind, diff[49].Text));
        Assert.Equal(("added", "changed"), (diff[50].Kind, diff[50].Text));
        Assert.Equal(DiffLine.Same, diff[0].Kind);
        Assert.Equal(DiffLine.Same, diff[^1].Kind);
    }
}
