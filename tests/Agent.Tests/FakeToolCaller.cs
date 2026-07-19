using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Agent.Mcp;

namespace Agent.Tests;

/// <summary>Scripted <see cref="IMcpToolCaller"/>: queued responses per tool, every call recorded.</summary>
internal sealed class FakeToolCaller : IMcpToolCaller
{
    private readonly Dictionary<string, Queue<Func<object>>> scripts = new(StringComparer.Ordinal);

    public List<string> Calls { get; } = new();

    public Dictionary<string, List<object>> CallArgs { get; } = new();

    public FakeToolCaller Respond<T>(string tool, T response) where T : notnull
    {
        Enqueue(tool, () => response);
        return this;
    }

    public FakeToolCaller Fail(string tool, string code, string message, string? remediation = null)
    {
        Enqueue(tool, () => throw new ToolCallException(code, message, remediation));
        return this;
    }

    public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
    {
        Calls.Add(tool);
        if (!CallArgs.TryGetValue(tool, out var list))
        {
            list = new List<object>();
            CallArgs[tool] = list;
        }

        list.Add(args is System.Text.Json.JsonElement element ? element.Clone() : args);
        cancellationToken.ThrowIfCancellationRequested();
        if (!scripts.TryGetValue(tool, out var queue) || queue.Count == 0)
        {
            throw new InvalidOperationException($"FakeToolCaller: no scripted response for '{tool}'.");
        }

        return Task.FromResult((T)queue.Dequeue()());
    }

    private void Enqueue(string tool, Func<object> response)
    {
        if (!scripts.TryGetValue(tool, out var queue))
        {
            queue = new Queue<Func<object>>();
            scripts[tool] = queue;
        }

        queue.Enqueue(response);
    }
}
