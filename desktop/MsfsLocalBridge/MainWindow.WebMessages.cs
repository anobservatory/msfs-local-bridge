using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using MsfsLocalBridge.Models;

namespace MsfsLocalBridge;

public partial class MainWindow
{
    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var envelope = JsonSerializer.Deserialize<WebMessageEnvelope>(e.WebMessageAsJson, _jsonOptions);
        if (envelope is null)
        {
            return;
        }

        if (string.Equals(envelope.Type, "ready", StringComparison.OrdinalIgnoreCase))
        {
            await PublishStateAsync();
            await PostWindowStateAsync();
            return;
        }

        if (string.Equals(envelope.Type, "resize", StringComparison.OrdinalIgnoreCase))
        {
            ApplyContentHeight(envelope.ContentHeight);
            return;
        }

        if (!string.Equals(envelope.Type, "action", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(envelope.Type, "refresh", StringComparison.OrdinalIgnoreCase) && !_actionInProgress)
                await RefreshStateAsync();
            return;
        }

        if (_actionInProgress)
        {
            await PostNotificationAsync("Please wait for the current action to finish.");
            return;
        }

        _actionInProgress = true;
        await PostActionStateAsync(true);
        try
        {
            await HandleActionAsync(envelope.Action);
        }
        catch (Exception ex)
        {
            await PostNotificationAsync($"Action failed: {ex.Message}");
            await RefreshStateAsync();
        }
        finally
        {
            _actionInProgress = false;
            if (envelope.Action != "close-window") await PostActionStateAsync(false);
        }
    }

    private async Task HandleActionAsync(string? action)
    {
        switch (action)
        {
            case "minimize-window":
                WindowState = WindowState.Minimized;
                return;
            case "toggle-maximize-window":
                return;
            case "close-window":
                Close();
                return;
            case "start-bridge":
                await _sessionService.StartAsync();
                break;
            case "stop-bridge":
                await _sessionService.StopAsync();
                break;
            case "restart-bridge":
                await _sessionService.RestartAsync();
                break;
            case "copy-link":
                if (!_currentState.CanShareConnection) break;
                Clipboard.SetText(_currentState.ConnectUrl);
                await PostNotificationAsync("Copied AO connect URL.");
                break;
            case "copy-bootstrap-url":
                Clipboard.SetText(_currentState.LocalBridgeUrl);
                await PostNotificationAsync("Copied local bridge URL.");
                break;
            case "copy-diagnostics":
                Clipboard.SetText(_lastDiagnosticsJson);
                await PostNotificationAsync("Copied diagnostics JSON.");
                break;
            case "copy-log":
                Clipboard.SetText(_sessionService.RuntimeLog);
                await PostNotificationAsync("Copied runtime log.");
                break;
            case "clear-log":
                _sessionService.ClearLog();
                break;
            case "copy-mac-setup":
                Clipboard.SetText($"curl -fsSL {_currentState.BootstrapUrl}/listener/mac.sh | bash");
                await PostNotificationAsync("Copied Mac bootstrap command.");
                break;
            case "copy-windows-setup":
                Clipboard.SetText($"powershell -ExecutionPolicy Bypass -Command \"iwr '{_currentState.BootstrapUrl}/listener/windows.ps1' -UseBasicParsing | iex\"");
                await PostNotificationAsync("Copied Windows bootstrap command.");
                break;
            case "open-bootstrap-page":
                OpenExternal(_currentState.OpenAoUrl);
                break;
            case "open-mobile-guide":
                OpenExternal(_currentState.BootstrapUrl);
                break;
            case "install-dotnet":
                await PostNotificationAsync(await _prerequisiteInstaller.InstallDotNetDesktopRuntimeAsync());
                break;
            case "install-vcredist":
                await PostNotificationAsync(await _prerequisiteInstaller.InstallVcRedistAsync());
                break;
            case "open-firewall-rules":
            case "enable-device-access":
                await SetDeviceAccessAsync(true);
                break;
            case "disable-device-access":
                await SetDeviceAccessAsync(false);
                break;
        }

        if (action is "start-bridge" or "stop-bridge" or "restart-bridge" or
            "install-dotnet" or "install-vcredist" or "open-firewall-rules" or
            "enable-device-access" or "disable-device-access")
        {
            await RefreshStateAsync();
        }
        else
        {
            await PublishStateAsync();
        }
    }

    private async Task SetDeviceAccessAsync(bool enabled)
    {
        DiagnosticsResult? accessDiagnostics = null;
        if (enabled)
        {
            var (networkDiagnostics, _) = await _diagnosticsService.GetAsync(forceRefresh: true);
            accessDiagnostics = networkDiagnostics;
            if (!networkDiagnostics.Checks.Any(check => check.Id == "network.sharing_profile" && check.Status == "pass"))
            {
                await PostNotificationAsync("Sharing requires a Private network profile. In Windows network settings, choose Private only for a trusted home network, then enable access again. Local sync is still available.");
                return;
            }
        }
        if (enabled)
        {
            // Always use the fresh report, even if the displayed rule was ready.
            var ready = accessDiagnostics!.Checks.Any(check => check.Id == "network.firewall_private_39000" && check.Status == "pass");
            if (!ready)
            {
                try
                {
                    var exitCode = await _powerShellRunner.RunElevatedAsync(_workspace.BridgeRepoRoot,
                        $"-ExecutionPolicy Bypass -File \"{_workspace.RepairScriptPath}\" -Action OpenFirewall39000 -Port 39000 -Force");
                    if (exitCode != 0) throw new InvalidOperationException("Firewall setup failed. Other device access was not enabled.");
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    await PostNotificationAsync("Administrator permission was cancelled. Other device access was not enabled; local access is still available.");
                    return;
                }
            }
        }

        var wasRunning = _sessionService.IsRunning;
        await _sessionService.StopAsync();
        _connectionSettings.Save(enabled);
        if (wasRunning) await _sessionService.StartAsync();
        await PostNotificationAsync(enabled ? "Other device access enabled on your private network." : "Other device access disabled. Bridge accepts local connections only.");
    }
}
