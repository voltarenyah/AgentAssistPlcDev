using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Chat;

public sealed record DeepSeekBalanceInfo(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("totalBalance")] string TotalBalance,
    [property: JsonPropertyName("grantedBalance")] string GrantedBalance,
    [property: JsonPropertyName("toppedUpBalance")] string ToppedUpBalance);

public sealed record DeepSeekBalanceDto(
    [property: JsonPropertyName("isAvailable")] bool IsAvailable,
    [property: JsonPropertyName("balances")] IReadOnlyList<DeepSeekBalanceInfo> Balances,
    [property: JsonPropertyName("fetchedAt")] string FetchedAt);

public sealed record DeepSeekBalanceFetchResult(
    HttpStatusCode StatusCode,
    DeepSeekBalanceDto? Balance,
    string? Error)
{
    public bool IsSuccess => Balance is not null && Error is null;
}

/// <summary>Injectable client for the DeepSeek account balance endpoint.</summary>
public sealed class DeepSeekBalanceClient(HttpClient http)
{
    public async Task<DeepSeekBalanceFetchResult> FetchAsync(
        string apiKey,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("DeepSeek API key must not be empty.", nameof(apiKey));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            baseUrl.TrimEnd('/') + "/user/balance");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new DeepSeekBalanceFetchResult(
                response.StatusCode,
                null,
                SanitizeError(payload, apiKey));
        }

        try
        {
            return new DeepSeekBalanceFetchResult(
                response.StatusCode,
                ParseBalance(payload),
                null);
        }
        catch (JsonException)
        {
            return new DeepSeekBalanceFetchResult(
                HttpStatusCode.BadGateway,
                null,
                "DeepSeek returned an invalid balance response.");
        }
    }

    private static DeepSeekBalanceDto ParseBalance(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var isAvailable = ReadBoolean(root, "is_available")
            ?? ReadBoolean(root, "isAvailable")
            ?? false;
        var balances = new List<DeepSeekBalanceInfo>();
        var balanceProperty = root.TryGetProperty("balance_infos", out var upstreamBalances)
            ? upstreamBalances
            : root.TryGetProperty("balances", out var safeBalances) ? safeBalances : default;

        if (balanceProperty.ValueKind == JsonValueKind.Array)
        {
            foreach (var balance in balanceProperty.EnumerateArray())
            {
                balances.Add(new DeepSeekBalanceInfo(
                    ReadString(balance, "currency"),
                    ReadString(balance, "total_balance", "totalBalance"),
                    ReadString(balance, "granted_balance", "grantedBalance"),
                    ReadString(balance, "topped_up_balance", "toppedUpBalance")));
            }
        }

        return new DeepSeekBalanceDto(
            isAvailable,
            balances,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    private static string SanitizeError(string payload, string apiKey)
    {
        var message = TryReadErrorMessage(payload) ?? "DeepSeek balance request failed.";
        message = message.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        return Truncate(message, 300);
    }

    private static string? TryReadErrorMessage(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var nested))
                    return nested.GetString();
            }

            return root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool? ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";
}
