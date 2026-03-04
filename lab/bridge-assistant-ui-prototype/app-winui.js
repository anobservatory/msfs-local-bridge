'use strict';

const SETTINGS_KEY = 'msfs_bridge_winui_settings_v1';

const DEFAULT_SETTINGS = {
  bindHost: '0.0.0.0',
  wsPort: 39000,
  wssPort: 39002,
  domain: 'ao.home.arpa',
  sampleMs: 200,
  pollMs: 25,
  reconnectMs: 2000,
  reconnectMaxMs: 10000,
  wssMode: 'auto', // auto | disabled | required
};

const state = {
  settings: loadSettings(),
  checks: [],
  selectedCheckId: 'network.wss_cert',
  diagnosticsRan: false,
  bridgeRunning: false,
  heartbeatTimer: null,
  uptimeTimer: null,
  uptimeSeconds: 0,
  navExpanded: true,
};

const bridgeApi = window.bridgeApi || null;

const navPane = document.getElementById('navPane');
const navToggleBtn = document.getElementById('navToggle');
const navWarnBadge = document.getElementById('nav-warn-badge');

const checkTable = document.getElementById('check-table');
const repairCmd = document.getElementById('repair-cmd');
const certCmd = document.getElementById('cert-cmd');

const logConsole = document.getElementById('log-console');
const dashLog = document.getElementById('dash-log');

const bridgeDot = document.getElementById('bridge-dot');
const bridgeLabel = document.getElementById('bridge-label');
const sbDot = document.getElementById('sb-dot');
const sbBridge = document.getElementById('sb-bridge');
const sbSimconnect = document.getElementById('sb-simconnect');
const sbIssues = document.getElementById('sb-issues');
const sbMode = document.getElementById('sb-mode');

const runtimeConn = document.getElementById('runtime-conn');
const runtimeUptime = document.getElementById('runtime-uptime');
const runtimeMode = document.getElementById('runtime-mode');

const dashBridge = document.getElementById('dash-bridge');
const dashPass = document.getElementById('dash-pass');
const dashWarn = document.getElementById('dash-warn');
const dashFail = document.getElementById('dash-fail');
const infobarCert = document.getElementById('infobar-cert');

const certDomainInput = document.getElementById('cert-domain');
const certPathCert = document.getElementById('cert-path-cert');
const certPathKey = document.getElementById('cert-path-key');
const certStatusCert = document.getElementById('cert-status-cert');
const certStatusKey = document.getElementById('cert-status-key');
const certStatusRoot = document.getElementById('cert-status-root');
const mkcertStatus = document.getElementById('mkcert-status');
const dashStartNote = document.getElementById('dash-start-note');
const runtimeStartNote = document.getElementById('runtime-start-note');
const dashStartBtn = document.getElementById('dash-start');
const wizLaunchBtn = document.getElementById('wiz-launch');
const runtimeStartBtn = document.getElementById('btn-bridge-start');

function timestamp() {
  return new Date().toLocaleTimeString('en-US', { hour12: false });
}

function appendLog(target, message) {
  if (!target) return;
  const line = `[${timestamp()}] ${message}\n`;
  const wasAtBottom = target.scrollTop + target.clientHeight >= target.scrollHeight - 8;
  target.textContent += line;
  if (wasAtBottom) {
    target.scrollTop = target.scrollHeight;
  }
}

function addLog(message) {
  appendLog(logConsole, message);
  appendLog(dashLog, message);
}

function loadSettings() {
  try {
    const raw = window.localStorage.getItem(SETTINGS_KEY);
    if (!raw) return { ...DEFAULT_SETTINGS };
    const parsed = JSON.parse(raw);
    return {
      ...DEFAULT_SETTINGS,
      ...parsed,
      wsPort: Number(parsed.wsPort) || DEFAULT_SETTINGS.wsPort,
      wssPort: Number(parsed.wssPort) || DEFAULT_SETTINGS.wssPort,
      sampleMs: Number(parsed.sampleMs) || DEFAULT_SETTINGS.sampleMs,
      pollMs: Number(parsed.pollMs) || DEFAULT_SETTINGS.pollMs,
      reconnectMs: Number(parsed.reconnectMs) || DEFAULT_SETTINGS.reconnectMs,
      reconnectMaxMs: Number(parsed.reconnectMaxMs) || DEFAULT_SETTINGS.reconnectMaxMs,
    };
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
}

function saveSettings() {
  window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(state.settings));
}

function buildDefaultChecks() {
  const ws = state.settings.wsPort;
  const wss = state.settings.wssPort;
  const domain = state.settings.domain;

  return [
    {
      id: 'runtime.dotnet',
      label: '.NET 8 runtime availability',
      status: 'pass',
      repair: 'Install .NET 8 runtime (winget install Microsoft.DotNet.Runtime.8)',
    },
    {
      id: 'dependency.vc_redist_x64',
      label: 'Visual C++ Redistributable (x64)',
      status: 'pass',
      repair: 'Install Microsoft Visual C++ 2015-2022 Redistributable (x64).',
    },
    {
      id: 'runtime.standard_user',
      label: 'PowerShell standard-user mode',
      status: 'warn',
      repair: 'Use normal PowerShell for bridge run. Use Administrator shell only for repair script.',
    },
    {
      id: 'simconnect.managed_dll',
      label: 'Managed SimConnect DLL in lib/',
      status: 'pass',
      repair: 'Copy Microsoft.FlightSimulator.SimConnect.dll into lib/.',
    },
    {
      id: 'simconnect.native_dll',
      label: 'Native SimConnect DLL in lib/',
      status: 'pass',
      repair: 'Copy SimConnect.dll into lib/.',
    },
    {
      id: 'network.firewall_private_ws',
      label: `Firewall rule for inbound TCP ${ws}`,
      status: 'warn',
      repair: `Run as Administrator: .\\repair-elevated-v0.ps1 -Action OpenFirewall39000 -Port ${ws}`,
    },
    {
      id: 'network.firewall_private_wss',
      label: `Firewall rule for inbound TCP ${wss}`,
      status: 'warn',
      repair: `Run as Administrator: .\\repair-elevated-v0.ps1 -Action OpenFirewall39002 -Port ${wss}`,
    },
    {
      id: 'network.mkcert',
      label: 'mkcert executable on PATH',
      status: 'warn',
      repair: 'Install mkcert and rerun certificate setup script.',
    },
    {
      id: 'network.wss_cert',
      label: 'WSS certificate file',
      status: 'warn',
      repair: `Run: .\\setup-wss-cert-v0.ps1 -LocalDomain ${domain} -CertDir "certs"`,
    },
    {
      id: 'network.wss_key',
      label: 'WSS private key file',
      status: 'warn',
      repair: `Run: .\\setup-wss-cert-v0.ps1 -LocalDomain ${domain} -CertDir "certs"`,
    },
    {
      id: 'network.root_ca',
      label: 'Root CA export file',
      status: 'warn',
      repair: `Run: .\\setup-wss-cert-v0.ps1 -LocalDomain ${domain} -CertDir "certs"`,
    },
    {
      id: 'network.port_ws',
      label: `TCP ${ws} availability`,
      status: 'pass',
      repair: `Stop conflicting process or choose another port than ${ws}.`,
    },
    {
      id: 'network.port_wss',
      label: `TCP ${wss} availability`,
      status: 'pass',
      repair: `Stop conflicting process or choose another port than ${wss}.`,
    },
  ];
}

function findCheck(id) {
  return state.checks.find((check) => check.id === id);
}

function countByStatus(status) {
  return state.checks.filter((check) => check.status === status).length;
}

function totalIssues() {
  return countByStatus('warn') + countByStatus('fail');
}

function normalizeDiagnosticsId(rawId) {
  const ws = String(state.settings.wsPort);
  const wss = String(state.settings.wssPort);

  if (rawId === `network.firewall_private_${ws}`) return 'network.firewall_private_ws';
  if (rawId === `network.firewall_private_${wss}`) return 'network.firewall_private_wss';
  if (rawId === `network.port_${ws}`) return 'network.port_ws';
  if (rawId === `network.port_${wss}`) return 'network.port_wss';
  return rawId;
}

function applyDiagnosticsReport(report) {
  const baseChecks = buildDefaultChecks();
  const checkMap = new Map(baseChecks.map((check) => [check.id, check]));

  if (!report || !Array.isArray(report.checks)) {
    state.checks = baseChecks;
    return;
  }

  report.checks.forEach((incoming) => {
    const mappedId = normalizeDiagnosticsId(incoming.id);
    const target = checkMap.get(mappedId);
    if (!target) return;

    if (incoming.status === 'pass' || incoming.status === 'warn' || incoming.status === 'fail') {
      target.status = incoming.status;
    }

    if (typeof incoming.repairAction === 'string' && incoming.repairAction.trim()) {
      target.repair = incoming.repairAction.trim();
    }
  });

  state.checks = baseChecks;
}

function setCheckSelection(checkId) {
  const check = findCheck(checkId);
  if (!check) return;
  state.selectedCheckId = checkId;
  repairCmd.textContent = check.repair;
  renderChecks();
}

function iconForStatus(status) {
  if (status === 'pass') return 'check_circle';
  if (status === 'warn') return 'warning';
  return 'error';
}

function renderChecks() {
  checkTable.innerHTML = '';

  state.checks.forEach((check) => {
    const row = document.createElement('div');
    row.className = 'check-row';
    if (check.id === state.selectedCheckId) {
      row.classList.add('selected');
    }

    row.innerHTML = `
      <span class="msr check-row-icon ${check.status}">${iconForStatus(check.status)}</span>
      <span class="check-label">${check.label}</span>
      <span class="badge ${check.status}">${check.status}</span>
    `;

    row.addEventListener('click', () => {
      setCheckSelection(check.id);
    });

    checkTable.appendChild(row);
  });

  if (!findCheck(state.selectedCheckId) && state.checks[0]) {
    state.selectedCheckId = state.checks[0].id;
  }

  const selected = findCheck(state.selectedCheckId);
  if (selected) {
    repairCmd.textContent = selected.repair;
  }
}

function isWssCertificateReady() {
  return findCheck('network.wss_cert')?.status === 'pass' && findCheck('network.wss_key')?.status === 'pass';
}

function isMkcertReady() {
  return findCheck('network.mkcert')?.status === 'pass';
}

function getStartBlockingReason() {
  if (state.bridgeRunning) {
    return 'Bridge is already running. Stop first before starting again.';
  }
  if (state.settings.wssMode === 'required' && !isWssCertificateReady()) {
    return 'WSS mode is Required. Complete certificate setup and re-run diagnostics first.';
  }
  return '';
}

function syncStartButtons() {
  const blockReason = getStartBlockingReason();
  const disabled = blockReason.length > 0;
  const title = disabled ? blockReason : 'Start bridge';

  [dashStartBtn, wizLaunchBtn, runtimeStartBtn].forEach((button) => {
    if (!button) return;
    button.disabled = disabled;
    button.title = title;
  });

  const note = disabled ? blockReason : 'Start is available with current settings.';
  if (runtimeStartNote) {
    runtimeStartNote.textContent = note;
    runtimeStartNote.classList.toggle('warn', disabled);
  }
  if (dashStartNote) {
    dashStartNote.textContent = disabled
      ? blockReason
      : 'If WSS mode is Required, Start stays blocked until certificate and key checks pass.';
    dashStartNote.classList.toggle('warn', disabled);
  }
}

function updateHealthCounts() {
  const pass = countByStatus('pass');
  const warn = countByStatus('warn');
  const fail = countByStatus('fail');
  const issues = warn + fail;

  dashPass.textContent = String(pass);
  dashWarn.textContent = String(warn);
  dashFail.textContent = String(fail);

  navWarnBadge.textContent = String(issues);
  navWarnBadge.style.display = issues > 0 ? '' : 'none';

  sbIssues.textContent = `Issues: ${issues}`;
}

function setBridgeState(running, hasError = false) {
  state.bridgeRunning = running;

  const dotClass = hasError ? 'error' : running ? 'running' : 'idle';
  const label = hasError ? 'Error' : running ? 'Running' : 'Idle';

  bridgeDot.className = `bridge-dot ${dotClass}`;
  bridgeLabel.textContent = label;

  sbDot.className = `status-dot ${dotClass}`;
  sbBridge.textContent = `Bridge: ${label}`;
  dashBridge.textContent = label;

  const dashCard = document.getElementById('dash-bridge-card');
  if (dashCard) {
    dashCard.className = `health-card ${running ? 'pass' : hasError ? 'fail' : 'neutral'}`;
  }
  syncStartButtons();
}

function formatUptime(seconds) {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

function updateCertCards() {
  const cert = findCheck('network.wss_cert')?.status || 'warn';
  const key = findCheck('network.wss_key')?.status || 'warn';
  const root = findCheck('network.root_ca')?.status || 'warn';

  certStatusCert.className = `badge ${cert} cert-badge`;
  certStatusKey.className = `badge ${key} cert-badge`;
  certStatusRoot.className = `badge ${root} cert-badge`;

  certStatusCert.textContent = cert === 'pass' ? 'Installed' : cert === 'fail' ? 'Missing' : 'Warning';
  certStatusKey.textContent = key === 'pass' ? 'Installed' : key === 'fail' ? 'Missing' : 'Warning';
  certStatusRoot.textContent = root === 'pass' ? 'Trusted' : root === 'fail' ? 'Failed' : 'Not verified';

  if (mkcertStatus) {
    const mkcert = findCheck('network.mkcert')?.status || 'warn';
    mkcertStatus.className = `badge ${mkcert}`;
    mkcertStatus.textContent = mkcert === 'pass' ? 'Ready' : mkcert === 'fail' ? 'Missing' : 'Needs check';
  }
}

function updateWizardBadges() {
  const step1 = document.getElementById('wiz-badge-1');
  const step2 = document.getElementById('wiz-badge-2');
  const step3 = document.getElementById('wiz-badge-3');
  const step4 = document.getElementById('wiz-badge-4');

  if (state.diagnosticsRan) {
    step1.className = 'badge pass';
    step1.textContent = 'Complete';
  } else {
    step1.className = 'badge';
    step1.textContent = 'Pending';
  }

  const firewallReady =
    findCheck('network.firewall_private_ws')?.status === 'pass' &&
    findCheck('network.firewall_private_wss')?.status === 'pass';

  if (firewallReady) {
    step2.className = 'badge pass';
    step2.textContent = 'Complete';
  } else {
    step2.className = 'badge warn';
    step2.textContent = 'Action needed';
  }

  if (isMkcertReady() && isWssCertificateReady()) {
    step3.className = 'badge pass';
    step3.textContent = 'Complete';
  } else {
    step3.className = 'badge warn';
    step3.textContent = isMkcertReady() ? 'Generate cert' : 'Install mkcert';
  }

  if (state.bridgeRunning) {
    step4.className = 'badge pass';
    step4.textContent = 'Running';
  } else {
    step4.className = 'badge';
    step4.textContent = 'Pending';
  }
}

function buildCertCommand() {
  return `.\\setup-wss-cert-v0.ps1 -LocalDomain ${state.settings.domain} -CertDir "certs"`;
}

function buildStartCommand() {
  const s = state.settings;
  const parts = [
    '.\\start-msfs-sync.ps1',
    `-BindHost ${s.bindHost}`,
    `-Port ${s.wsPort}`,
    `-WssPort ${s.wssPort}`,
    `-LocalDomain ${s.domain}`,
    `-SampleIntervalMs ${s.sampleMs}`,
    `-PollIntervalMs ${s.pollMs}`,
    `-ReconnectDelayMs ${s.reconnectMs}`,
    `-ReconnectMaxDelayMs ${s.reconnectMaxMs}`,
  ];

  if (s.wssMode === 'disabled') {
    parts.push('-DisableWss');
  } else if (s.wssMode === 'required') {
    parts.push('-RequireWss');
  }

  return parts.join(' ');
}

function updateEndpoints() {
  const s = state.settings;
  const wsUrl = `ws://${s.bindHost}:${s.wsPort}/stream`;
  const bootstrapUrl = `http://<windows-ip>:${s.wsPort}/bootstrap`;

  let wssUrl;
  if (s.wssMode === 'disabled') {
    wssUrl = 'disabled by -DisableWss';
  } else if (isWssCertificateReady()) {
    wssUrl = `wss://${s.domain}:${s.wssPort}/stream`;
  } else if (s.wssMode === 'required') {
    wssUrl = `required but not ready (missing cert)`;
  } else {
    wssUrl = `wss://${s.domain}:${s.wssPort}/stream (fallback to WS)`;
  }

  document.getElementById('ep-ws').textContent = wsUrl;
  document.getElementById('ep-wss').textContent = wssUrl;
  document.getElementById('ep-btr').textContent = bootstrapUrl;

  document.getElementById('runtime-ws').textContent = wsUrl;
  document.getElementById('runtime-wss').textContent = wssUrl;

  certPathCert.textContent = `certs/${s.domain}.pem`;
  certPathKey.textContent = `certs/${s.domain}-key.pem`;

  certCmd.textContent = buildCertCommand();

  let modeText = 'Auto (fallback allowed)';
  if (s.wssMode === 'disabled') modeText = 'Disabled (WS only)';
  if (s.wssMode === 'required') modeText = 'Required';

  runtimeMode.textContent = modeText;
  sbMode.textContent = `WSS mode: ${s.wssMode}`;
}

function syncInfobar() {
  const certReady = isWssCertificateReady();
  infobarCert.style.display = certReady ? 'none' : '';
}

function refreshUi() {
  renderChecks();
  updateHealthCounts();
  updateCertCards();
  updateEndpoints();
  updateWizardBadges();
  syncInfobar();
  syncStartButtons();
}

function setInputsFromSettings() {
  const s = state.settings;
  document.getElementById('set-bind').value = s.bindHost;
  document.getElementById('set-ws-port').value = String(s.wsPort);
  document.getElementById('set-wss-port').value = String(s.wssPort);
  document.getElementById('set-domain').value = s.domain;
  document.getElementById('set-sample-ms').value = String(s.sampleMs);
  document.getElementById('set-poll-ms').value = String(s.pollMs);
  document.getElementById('set-reconnect-ms').value = String(s.reconnectMs);
  document.getElementById('set-reconnect-max-ms').value = String(s.reconnectMaxMs);
  certDomainInput.value = s.domain;

  document.querySelectorAll('#wss-toggle .toggle-btn').forEach((button) => {
    button.classList.toggle('active', button.dataset.val === s.wssMode);
  });
}

function readSettingsFromInputs() {
  const prev = state.settings;

  const wsPort = Number(document.getElementById('set-ws-port').value);
  const wssPort = Number(document.getElementById('set-wss-port').value);
  const sampleMs = Number(document.getElementById('set-sample-ms').value);
  const pollMs = Number(document.getElementById('set-poll-ms').value);
  const reconnectMs = Number(document.getElementById('set-reconnect-ms').value);
  const reconnectMaxMs = Number(document.getElementById('set-reconnect-max-ms').value);

  state.settings = {
    ...prev,
    bindHost: (document.getElementById('set-bind').value || DEFAULT_SETTINGS.bindHost).trim(),
    wsPort: Number.isFinite(wsPort) && wsPort > 0 ? wsPort : DEFAULT_SETTINGS.wsPort,
    wssPort: Number.isFinite(wssPort) && wssPort > 0 ? wssPort : DEFAULT_SETTINGS.wssPort,
    domain: (document.getElementById('set-domain').value || DEFAULT_SETTINGS.domain).trim(),
    sampleMs: Number.isFinite(sampleMs) && sampleMs > 0 ? sampleMs : DEFAULT_SETTINGS.sampleMs,
    pollMs: Number.isFinite(pollMs) && pollMs > 0 ? pollMs : DEFAULT_SETTINGS.pollMs,
    reconnectMs: Number.isFinite(reconnectMs) && reconnectMs > 0 ? reconnectMs : DEFAULT_SETTINGS.reconnectMs,
    reconnectMaxMs:
      Number.isFinite(reconnectMaxMs) && reconnectMaxMs > 0
        ? reconnectMaxMs
        : DEFAULT_SETTINGS.reconnectMaxMs,
  };

  certDomainInput.value = state.settings.domain;
}

function navigateTo(viewId) {
  if (!viewId) return;

  document.querySelectorAll('.view').forEach((view) => {
    view.classList.toggle('is-active', view.id === `view-${viewId}`);
  });

  document.querySelectorAll('.nav-item').forEach((item) => {
    item.classList.toggle('is-selected', item.dataset.view === viewId);
  });
}

async function runDiagnostics() {
  addLog('Running diagnostics-v0.ps1 -Format Json ...');

  if (bridgeApi && typeof bridgeApi.runDiagnostics === 'function') {
    try {
      const report = await bridgeApi.runDiagnostics({
        port: state.settings.wsPort,
        wssPort: state.settings.wssPort,
        localDomain: state.settings.domain,
        certDir: 'certs',
      });
      applyDiagnosticsReport(report);
      state.diagnosticsRan = true;
      addLog('Diagnostics completed (bridge API).');
      refreshUi();
      return;
    } catch (error) {
      addLog(`Diagnostics failed via bridge API: ${String(error)}`);
    }
  }

  if (!state.diagnosticsRan) {
    state.checks = buildDefaultChecks();
  }
  state.diagnosticsRan = true;
  addLog('Diagnostics completed (mock mode). Connect a bridge API to run real scripts.');
  refreshUi();
}

async function generateCertificate() {
  const command = buildCertCommand();
  addLog(`Preparing certificate setup: ${command}`);

  const mkcertReady = isMkcertReady();
  if (bridgeApi && typeof bridgeApi.runSetupCertificate === 'function' && !mkcertReady) {
    addLog('Certificate setup blocked: mkcert is not ready. Install mkcert, then run diagnostics and retry.');
    selectMkcertCheck(true);
    return;
  }

  if (!bridgeApi && !mkcertReady) {
    addLog('mkcert status is unknown in mock mode. Proceeding for UI demonstration only.');
  }

  if (bridgeApi && typeof bridgeApi.runSetupCertificate === 'function') {
    try {
      await bridgeApi.runSetupCertificate({
        localDomain: state.settings.domain,
        certDir: 'certs',
      });
      addLog('Certificate setup script executed via bridge API.');
      await runDiagnostics();
      return;
    } catch (error) {
      addLog(`Certificate setup failed: ${String(error)}`);
      return;
    }
  }

  const certCheck = findCheck('network.wss_cert');
  const keyCheck = findCheck('network.wss_key');
  if (certCheck) certCheck.status = 'pass';
  if (keyCheck) keyCheck.status = 'pass';
  addLog('Certificate generated (mock mode). Run diagnostics to verify trust chain.');
  refreshUi();
}

async function verifyTrustViaDiagnostics() {
  addLog('Verifying trust via diagnostics...');

  if (bridgeApi && typeof bridgeApi.verifyTrust === 'function') {
    try {
      await bridgeApi.verifyTrust({
        localDomain: state.settings.domain,
        certDir: 'certs',
      });
      addLog('Trust verification command executed via bridge API.');
    } catch (error) {
      addLog(`Trust verification command failed: ${String(error)}`);
    }
  } else {
    const rootCheck = findCheck('network.root_ca');
    if (rootCheck) rootCheck.status = 'pass';
    addLog('Trust marked as verified (mock mode).');
  }

  await runDiagnostics();
}

function beginHeartbeat() {
  stopHeartbeat();

  state.uptimeSeconds = 0;
  runtimeUptime.textContent = '0s';
  runtimeConn.textContent = '1';

  state.uptimeTimer = setInterval(() => {
    state.uptimeSeconds += 1;
    runtimeUptime.textContent = formatUptime(state.uptimeSeconds);
  }, 1000);

  state.heartbeatTimer = setInterval(() => {
    addLog('Heartbeat OK | ownship stream | 5 Hz');
  }, 2000);
}

function stopHeartbeat() {
  if (state.heartbeatTimer) {
    clearInterval(state.heartbeatTimer);
    state.heartbeatTimer = null;
  }
  if (state.uptimeTimer) {
    clearInterval(state.uptimeTimer);
    state.uptimeTimer = null;
  }
}

async function startBridge() {
  if (state.bridgeRunning) {
    addLog('Bridge already running.');
    return;
  }

  const blockedReason = getStartBlockingReason();
  if (blockedReason) {
    addLog(`Start blocked: ${blockedReason}`);
    return;
  }

  const command = buildStartCommand();
  addLog(`Starting bridge with command: ${command}`);

  if (bridgeApi && typeof bridgeApi.startBridge === 'function') {
    try {
      await bridgeApi.startBridge({ settings: state.settings, command });
      addLog('Bridge start command dispatched via bridge API.');
    } catch (error) {
      setBridgeState(false, true);
      addLog(`Bridge failed to start: ${String(error)}`);
      return;
    }
  }

  setBridgeState(true, false);
  sbSimconnect.textContent = 'SimConnect: Connected';
  beginHeartbeat();
  updateWizardBadges();
}

async function stopBridge() {
  if (!state.bridgeRunning) {
    addLog('Bridge is already idle.');
    return;
  }

  if (bridgeApi && typeof bridgeApi.stopBridge === 'function') {
    try {
      await bridgeApi.stopBridge();
      addLog('Bridge stop command dispatched via bridge API.');
    } catch (error) {
      addLog(`Bridge stop failed: ${String(error)}`);
    }
  }

  stopHeartbeat();
  setBridgeState(false, false);
  sbSimconnect.textContent = 'SimConnect: Waiting';
  runtimeConn.textContent = '0';
  runtimeUptime.textContent = '—';
  addLog('Bridge stopped.');
  updateWizardBadges();
}

function selectFirewallCheck() {
  const wsCheck = findCheck('network.firewall_private_ws');
  const wssCheck = findCheck('network.firewall_private_wss');

  if (wsCheck && wsCheck.status !== 'pass') {
    setCheckSelection(wsCheck.id);
    return;
  }

  if (wssCheck) {
    setCheckSelection(wssCheck.id);
  }
}

function selectMkcertCheck(navigate = false) {
  const mkcertCheck = findCheck('network.mkcert');
  if (!mkcertCheck) return;
  if (navigate) navigateTo('preflight');
  setCheckSelection(mkcertCheck.id);
}

function applyDomainFromCertInput() {
  const value = certDomainInput.value.trim();
  if (!value) return;

  state.settings.domain = value;
  document.getElementById('set-domain').value = value;

  state.checks = buildDefaultChecks();
  state.selectedCheckId = 'network.wss_cert';
  refreshUi();

  addLog(`Domain updated to ${value}. Run certificate setup again if needed.`);
}

function updateWssModeButton(mode) {
  document.querySelectorAll('#wss-toggle .toggle-btn').forEach((button) => {
    button.classList.toggle('active', button.dataset.val === mode);
  });
}

navToggleBtn.addEventListener('click', () => {
  state.navExpanded = !state.navExpanded;
  navPane.classList.toggle('compact', !state.navExpanded);
});

document.querySelectorAll('.nav-item[data-view]').forEach((button) => {
  button.addEventListener('click', () => {
    navigateTo(button.dataset.view);
  });
});

document.addEventListener('click', (event) => {
  const navBtn = event.target.closest('[data-nav]');
  if (navBtn) {
    navigateTo(navBtn.dataset.nav);
  }

  const copyBtn = event.target.closest('[data-copy]');
  if (copyBtn) {
    const source = document.getElementById(copyBtn.dataset.copy);
    if (source) {
      navigator.clipboard?.writeText(source.textContent).catch(() => {});
      const icon = copyBtn.querySelector('.msr');
      if (icon) {
        icon.textContent = 'check';
        setTimeout(() => {
          icon.textContent = 'content_copy';
        }, 1000);
      }
    }
  }
});

document.querySelectorAll('.infobar-dismiss').forEach((button) => {
  button.addEventListener('click', () => {
    const bar = button.closest('.infobar');
    if (bar) {
      bar.style.display = 'none';
    }
  });
});

document.getElementById('dash-start').addEventListener('click', async () => {
  navigateTo('runtime');
  await startBridge();
});

document.getElementById('dash-diagnostics').addEventListener('click', async () => {
  navigateTo('preflight');
  await runDiagnostics();
});

document.getElementById('dash-certs').addEventListener('click', () => {
  navigateTo('certs');
});

document.getElementById('dash-firewall').addEventListener('click', () => {
  navigateTo('preflight');
  selectFirewallCheck();
});

document.getElementById('wiz-run-diagnostics').addEventListener('click', async () => {
  await runDiagnostics();
});

document.getElementById('wiz-show-firewall').addEventListener('click', () => {
  navigateTo('preflight');
  selectFirewallCheck();
  addLog('Firewall repair command selected. Run it in Administrator PowerShell.');
});

document.getElementById('wiz-rerun-after-firewall').addEventListener('click', async () => {
  await runDiagnostics();
});

document.getElementById('wiz-generate-cert').addEventListener('click', async () => {
  navigateTo('certs');
  await generateCertificate();
});

document.getElementById('wiz-rerun-after-cert').addEventListener('click', async () => {
  await runDiagnostics();
});

document.getElementById('wiz-launch').addEventListener('click', async () => {
  navigateTo('runtime');
  await startBridge();
});

document.getElementById('btn-run-all').addEventListener('click', async () => {
  await runDiagnostics();
});

document.getElementById('btn-select-firewall').addEventListener('click', () => {
  selectFirewallCheck();
});

document.getElementById('btn-rerun-after-repair').addEventListener('click', async () => {
  await runDiagnostics();
});

document.getElementById('btn-copy-cmd').addEventListener('click', () => {
  navigator.clipboard?.writeText(repairCmd.textContent).catch(() => {});
});

document.getElementById('btn-apply-domain').addEventListener('click', () => {
  applyDomainFromCertInput();
});

certDomainInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') {
    applyDomainFromCertInput();
  }
});

document.getElementById('btn-gen-cert').addEventListener('click', async () => {
  await generateCertificate();
});

document.getElementById('btn-verify-trust').addEventListener('click', async () => {
  await verifyTrustViaDiagnostics();
});

document.getElementById('btn-rerun-cert-diagnostics').addEventListener('click', async () => {
  await runDiagnostics();
});

document.getElementById('btn-check-prereq').addEventListener('click', async () => {
  await runDiagnostics();
});

document.getElementById('btn-select-mkcert').addEventListener('click', () => {
  selectMkcertCheck(true);
});

document.getElementById('btn-copy-cert-cmd').addEventListener('click', () => {
  navigator.clipboard?.writeText(certCmd.textContent).catch(() => {});
});

document.getElementById('btn-bridge-start').addEventListener('click', async () => {
  await startBridge();
});

document.getElementById('btn-bridge-stop').addEventListener('click', async () => {
  await stopBridge();
});

document.getElementById('btn-bridge-restart').addEventListener('click', async () => {
  await stopBridge();
  await startBridge();
});

document.getElementById('btn-clear-log').addEventListener('click', () => {
  logConsole.textContent = '';
});

document.querySelectorAll('#wss-toggle .toggle-btn').forEach((button) => {
  button.addEventListener('click', () => {
    updateWssModeButton(button.dataset.val);
  });
});

document.getElementById('btn-save-settings').addEventListener('click', () => {
  readSettingsFromInputs();

  const activeMode =
    document.querySelector('#wss-toggle .toggle-btn.active')?.dataset.val || DEFAULT_SETTINGS.wssMode;
  state.settings.wssMode = activeMode;

  saveSettings();

  state.checks = buildDefaultChecks();
  state.selectedCheckId = 'network.wss_cert';

  refreshUi();
  addLog('Settings saved. Run diagnostics to refresh status under new settings.');
});

document.getElementById('btn-reset-defaults').addEventListener('click', () => {
  state.settings = { ...DEFAULT_SETTINGS };
  setInputsFromSettings();
  saveSettings();

  state.checks = buildDefaultChecks();
  state.selectedCheckId = 'network.wss_cert';

  refreshUi();
  addLog('Settings reset to defaults.');
});

window.addEventListener('beforeunload', () => {
  stopHeartbeat();
});

state.checks = buildDefaultChecks();
setInputsFromSettings();
setBridgeState(false, false);
refreshUi();
setCheckSelection(state.selectedCheckId);
addLog('Implementable WinUI variant ready. Run diagnostics first.');
