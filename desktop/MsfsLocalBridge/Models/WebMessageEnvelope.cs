namespace MsfsLocalBridge.Models;

internal sealed class WebMessageEnvelope
{
    public string Type { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public double ContentHeight { get; set; }
}
