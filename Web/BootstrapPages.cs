using System.Net;
using System.Text;

internal static class BootstrapPages
{
  internal static string BuildBootstrapHtml(string bootstrapBaseUrl, string wssClientUrl, string aoConnectUrl, bool wssEnabled)
  {
    var html = new StringBuilder();
    html.AppendLine("<!doctype html>");
    html.AppendLine("<html lang=\"en\">");
    html.AppendLine("<head>");
    html.AppendLine("  <meta charset=\"utf-8\" />");
    html.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
    html.AppendLine("  <title>AO MSFS Listener Bootstrap</title>");
    html.AppendLine("  <style>");
    html.AppendLine("    body { font-family: -apple-system, Segoe UI, Roboto, sans-serif; margin: 24px; line-height: 1.5; }");
    html.AppendLine("    code { background: #f3f3f3; padding: 2px 6px; border-radius: 4px; }");
    html.AppendLine("    pre { background: #111; color: #e9e9e9; padding: 12px; border-radius: 8px; overflow-x: auto; }");
    html.AppendLine("    .warn { color: #a15800; font-weight: 600; }");
    html.AppendLine("  </style>");
    html.AppendLine("</head>");
    html.AppendLine("<body>");
    html.AppendLine("  <h1>AO MSFS Listener Bootstrap</h1>");
    html.AppendLine($"  <p>Bootstrap base: <code>{WebUtility.HtmlEncode(bootstrapBaseUrl)}</code></p>");
    if (!wssEnabled)
    {
      html.AppendLine("  <p class=\"warn\">WSS is currently disabled on host bridge. Enable cert + WSS first.</p>");
    }
    else
    {
      html.AppendLine($"  <p>WSS bridge URL: <code>{WebUtility.HtmlEncode(wssClientUrl)}</code></p>");
      html.AppendLine($"  <p>AO connect URL: <code>{WebUtility.HtmlEncode(aoConnectUrl)}</code></p>");
    }

    html.AppendLine("  <h2>Mac setup (one-time)</h2>");
    html.AppendLine($"  <pre>curl -fsSL {WebUtility.HtmlEncode(bootstrapBaseUrl)}/listener/mac.sh | bash</pre>");

    html.AppendLine("  <h2>Windows setup (one-time)</h2>");
    html.AppendLine($"  <pre>powershell -ExecutionPolicy Bypass -Command \"iwr '{WebUtility.HtmlEncode(bootstrapBaseUrl)}/listener/windows.ps1' -UseBasicParsing | iex\"</pre>");

    html.AppendLine("  <h2>Files</h2>");
    html.AppendLine("  <ul>");
    html.AppendLine($"    <li><a href=\"{WebUtility.HtmlEncode(bootstrapBaseUrl)}/manifest.json\">manifest.json</a></li>");
    html.AppendLine($"    <li><a href=\"{WebUtility.HtmlEncode(bootstrapBaseUrl)}/ca/rootCA.pem\">rootCA.pem</a></li>");
    html.AppendLine($"    <li><a href=\"{WebUtility.HtmlEncode(bootstrapBaseUrl)}/listener/mac.sh\">listener/mac.sh</a></li>");
    html.AppendLine($"    <li><a href=\"{WebUtility.HtmlEncode(bootstrapBaseUrl)}/listener/windows.ps1\">listener/windows.ps1</a></li>");
    html.AppendLine("  </ul>");

    if (wssEnabled)
    {
      html.AppendLine("  <h2>Open AO</h2>");
      html.AppendLine($"  <p><a href=\"{WebUtility.HtmlEncode(aoConnectUrl)}\" target=\"_blank\" rel=\"noreferrer\">Open anobservatory.com with this bridge</a></p>");
    }

    html.AppendLine("</body>");
    html.AppendLine("</html>");
    return html.ToString();
  }

  internal static string BuildBootstrapFallbackText(string bootstrapBaseUrl, string wssClientUrl, string aoConnectUrl, bool wssEnabled)
  {
    var sb = new StringBuilder();
    sb.AppendLine("AO MSFS Listener Bootstrap");
    sb.AppendLine();
    sb.AppendLine($"Bootstrap base: {bootstrapBaseUrl}");
    sb.AppendLine($"Manifest: {bootstrapBaseUrl}/manifest.json");
    sb.AppendLine($"Root CA: {bootstrapBaseUrl}/ca/rootCA.pem");
    sb.AppendLine($"Mac script: {bootstrapBaseUrl}/listener/mac.sh");
    sb.AppendLine($"Windows script: {bootstrapBaseUrl}/listener/windows.ps1");
    sb.AppendLine();
    if (wssEnabled)
    {
      sb.AppendLine($"WSS bridge URL: {wssClientUrl}");
      sb.AppendLine($"AO connect URL: {aoConnectUrl}");
    }
    else
    {
      sb.AppendLine("WSS is disabled. Configure cert + WSS first.");
    }

    return sb.ToString();
  }

  internal static string BuildMacListenerScript(string bootstrapBaseUrl, string wssPublicHost, string bootstrapHostIp)
  {
    var caUrl = $"{bootstrapBaseUrl}/ca/rootCA.pem";
    var needHostsMapping = !IPAddress.TryParse(wssPublicHost, out _);
    var domainLine = needHostsMapping && !string.IsNullOrWhiteSpace(bootstrapHostIp)
      ? $"echo \"{bootstrapHostIp} {wssPublicHost}\" | sudo tee -a /etc/hosts >/dev/null"
      : "echo \"[info] hosts mapping skipped (IP-based WSS host or missing host IP)\"";

    return
  $@"#!/usr/bin/env bash
set -euo pipefail

TMP_CA=""$(mktemp /tmp/ao-rootca.XXXXXX.pem)""
cleanup() {{
  rm -f ""$TMP_CA""
}}
trap cleanup EXIT

echo ""[1/3] Downloading root CA...""
curl -fsSL ""{caUrl}"" -o ""$TMP_CA""

echo ""[2/3] Trusting root CA in system keychain (sudo prompt expected)...""
sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain ""$TMP_CA""

echo ""[3/3] Applying host mapping when needed...""
{domainLine}
sudo dscacheutil -flushcache || true
sudo killall -HUP mDNSResponder || true

echo ""[PASS] Listener bootstrap complete.""
";
  }

  internal static string BuildWindowsListenerScript(string bootstrapBaseUrl, string wssPublicHost, string bootstrapHostIp)
  {
    var caUrl = $"{bootstrapBaseUrl}/ca/rootCA.pem";
    var needHostsMapping = !IPAddress.TryParse(wssPublicHost, out _);
    var domainBlock = needHostsMapping && !string.IsNullOrWhiteSpace(bootstrapHostIp)
      ? $@"
$hostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$mapping = '{bootstrapHostIp} {wssPublicHost}'
$existing = Get-Content -Path $hostsPath -ErrorAction SilentlyContinue
if (-not ($existing -match '\s{wssPublicHost.Replace(".", "\\.")}(\s|$)')) {{
  Add-Content -Path $hostsPath -Value $mapping
}}
"
      : @"
Write-Host '[info] hosts mapping skipped (IP-based WSS host or missing host IP)' -ForegroundColor Yellow
";

    return
  $@"$ErrorActionPreference = 'Stop'
$tempCa = Join-Path $env:TEMP 'ao-rootCA.pem'

Write-Host '[1/3] Downloading root CA...'
Invoke-WebRequest -Uri '{caUrl}' -OutFile $tempCa -UseBasicParsing

Write-Host '[2/3] Trusting root CA in CurrentUser Root store...'
Import-Certificate -FilePath $tempCa -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null

Write-Host '[3/3] Applying host mapping when needed...'
{domainBlock}

Write-Host '[PASS] Listener bootstrap complete.' -ForegroundColor Green
";
  }
}
