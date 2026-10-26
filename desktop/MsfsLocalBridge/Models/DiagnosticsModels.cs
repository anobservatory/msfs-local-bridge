using System.Text.Json.Serialization;

namespace MsfsLocalBridge.Models;

internal sealed class DiagnosticsResult
{
    [JsonPropertyName("overallStatus")]
    public string OverallStatus { get; set; } = "warn";

    [JsonPropertyName("checks")]
    public List<DiagnosticsCheck> Checks { get; set; } = [];

    [JsonPropertyName("summary")]
    public DiagnosticsSummary Summary { get; set; } = new();
}

internal sealed class DiagnosticsSummary
{
    [JsonPropertyName("pass")]
    public int Pass { get; set; }

    [JsonPropertyName("warn")]
    public int Warn { get; set; }

    [JsonPropertyName("fail")]
    public int Fail { get; set; }
}

internal sealed class DiagnosticsCheck
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "warn";

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("repairAction")]
    public string RepairAction { get; set; } = string.Empty;
}
