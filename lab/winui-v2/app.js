'use strict';

const MOCK_ENV_KEY = 'msfs_winui_v2_mock_env';

const DEFAULT_SETTINGS = {
  bindHost: '0.0.0.0',
  port: 39000,
  wssPort: 39002,
  localDomain: 'ao.home.arpa',
  sampleMs: 200,
  pollMs: 25,
  reconnectMs: 2000,
  reconnectMaxMs: 10000,
  wssMode: 'required',
};

const DEFAULT_MOCK_ENV = {
  dotnetReady: true,
  managedDllReady: true,
  nativeDllReady: true,
  mkcertInstalled: false,
  certReady: false,
  keyReady: false,
  rootCaReady: false,
  firewallWsOpen: false,
  firewallWssOpen: false,
  wsPortFree: true,
  wssPortFree: true,
};

const DEFAULT_ELEVATION = {
  status: 'not_required',
  lastAction: '',
  message: 'No elevated action required yet.',
};

const state = {
  settings: loadSettings(),
  env: loadMockEnv(),
  checks: [],
  selectedCheckId: null,
  lastDiagnosticsAt: null,
  bridgeApi: window.bridgeApi || null,
  elevation: { ...DEFAULT_ELEVATION },
  runtime: {
    running: false,
    pid: null,
    uptimeSec: 0,
    timer: null,
  },
};

function el(id) {
  return document.getElementById(id);
}

function appendRuntimeLog(message) {
  const log = el('runtimeLog');
  if (!log) return;
  const line = `[${new Date().toLocaleTimeString('en-US', { hour12: false })}] ${message}\n`;
  log.textContent += line;
  log.scrollTop = log.scrollHeight;
}

function loadSettings() {
  return { ...DEFAULT_SETTINGS, wssMode: 'required' };
}

function loadMockEnv() {
  try {
    const raw = localStorage.getItem(MOCK_ENV_KEY);
    if (!raw) return { ...DEFAULT_MOCK_ENV };
    const parsed = JSON.parse(raw);
    return {
      ...DEFAULT_MOCK_ENV,
      ...parsed,
    };
  } catch {
    return { ...DEFAULT_MOCK_ENV };
  }
}

function saveMockEnv() {
  localStorage.setItem(MOCK_ENV_KEY, JSON.stringify(state.env));
}

function resetElevationStatus() {
  state.elevation = { ...DEFAULT_ELEVATION };
}

function setElevationStatus(status, message, lastAction) {
  state.elevation.status = status;
  if (typeof message === 'string') {
    state.elevation.message = message;
  }
  if (lastAction) {
    state.elevation.lastAction = lastAction;
  }
}

function isAdminRepairCheckId(checkId) {
  if (!checkId) return false;
  return checkId === 'network.firewall_ws'
    || checkId === 'network.firewall_wss'
    || checkId.startsWith('network.firewall_private_');
}

function getAdminCommandForCopy() {
  const lastAction = (state.elevation.lastAction || '').trim();
  if (lastAction.startsWith('./repair-elevated-v0.ps1')) {
    return lastAction;
  }

  if (state.selectedCheckId && isAdminRepairCheckId(state.selectedCheckId)) {
    return buildRepairCommand(state.selectedCheckId);
  }

  return buildRepairCommand(`network.firewall_private_${state.settings.port}`);
}

function setModePill() {
  const mode = state.bridgeApi ? 'Mode: Host bridgeApi' : 'Mode: Mock';
  el('connectionMode').textContent = mode;
}

function statusClass(status) {
  if (status === 'pass') return 'pass';
  if (status === 'warn') return 'warn';
  return 'fail';
}

function issueCount() {
  return state.checks.filter((check) => check.status !== 'pass').length;
}

function getCheckStatus(id) {
  const check = state.checks.find((item) => item.id === id);
  return check ? check.status : null;
}

function getCheckStatusByIds(ids) {
  for (const id of ids) {
    const status = getCheckStatus(id);
    if (status) return status;
  }
  return null;
}

function isCheckPassByIds(ids) {
  return getCheckStatusByIds(ids) === 'pass';
}

function getWsFirewallIds() {
  return ['network.firewall_ws', `network.firewall_private_${state.settings.port}`];
}

function getWssFirewallIds() {
  return ['network.firewall_wss', `network.firewall_private_${state.settings.wssPort}`];
}

function getWsPortIds() {
  return ['network.port_ws', `network.port_${state.settings.port}`];
}

function getWssPortIds() {
  return ['network.port_wss', `network.port_${state.settings.wssPort}`];
}

function isCertReady() {
  if (state.checks.length === 0) {
    return state.env.certReady && state.env.keyReady;
  }
  return isCheckPassByIds(['network.wss_cert']) && isCheckPassByIds(['network.wss_key']);
}

function getBootstrapUrlParts() {
  const hasHost = window.location.hostname && window.location.hostname.length > 0;
  const hostForOpen = hasHost ? window.location.hostname : '127.0.0.1';
  const hostForDisplay = hasHost ? window.location.hostname : '<windows-ip>';
  const path = '/bootstrap';

  return {
    openUrl: `http://${hostForOpen}:${state.settings.port}${path}`,
    displayUrl: `http://${hostForDisplay}:${state.settings.port}${path}`,
  };
}

function buildRepairCommand(checkId) {
  const s = state.settings;

  if (checkId.startsWith('network.port_')) {
    const parsedPort = Number(checkId.split('_').pop());
    const port = Number.isFinite(parsedPort) ? parsedPort : s.port;
    return `netstat -ano | findstr ":${port}"`;
  }

  if (checkId.startsWith('network.firewall_private_')) {
    const parsedPort = Number(checkId.split('_').pop());
    const port = Number.isFinite(parsedPort) ? parsedPort : s.port;
    const action = port === s.wssPort ? 'OpenFirewall39002' : 'OpenFirewall39000';
    return `./repair-elevated-v0.ps1 -Action ${action} -Port ${port}`;
  }

  switch (checkId) {
    case 'network.mkcert':
      return 'winget install FiloSottile.mkcert';
    case 'network.wss_cert':
    case 'network.wss_key':
    case 'network.root_ca':
      return `./setup-wss-cert-v0.ps1 -LocalDomain ${s.localDomain} -CertDir "certs"`;
    case 'network.firewall_ws':
      return `./repair-elevated-v0.ps1 -Action OpenFirewall39000 -Port ${s.port}`;
    case 'network.firewall_wss':
      return `./repair-elevated-v0.ps1 -Action OpenFirewall39002 -Port ${s.wssPort}`;
    case 'network.port_ws':
      return `netstat -ano | findstr ":${s.port}"`;
    case 'network.port_wss':
      return `netstat -ano | findstr ":${s.wssPort}"`;
    default:
      return 'No repair command available for this check.';
  }
}

function buildMockChecks() {
  const s = state.settings;
  const env = state.env;

  return [
    {
      id: 'runtime.dotnet',
      label: '.NET runtime / SDK (source layout)',
      status: env.dotnetReady ? 'pass' : 'warn',
    },
    {
      id: 'simconnect.managed_dll',
      label: 'Managed SimConnect DLL',
      status: env.managedDllReady ? 'pass' : 'fail',
    },
    {
      id: 'simconnect.native_dll',
      label: 'Native SimConnect DLL',
      status: env.nativeDllReady ? 'pass' : 'fail',
    },
    {
      id: 'network.mkcert',
      label: 'mkcert installed',
      status: env.mkcertInstalled ? 'pass' : 'warn',
    },
    {
      id: 'network.wss_cert',
      label: `Secure certificate file (${s.localDomain}.pem)`,
      status: env.certReady ? 'pass' : 'warn',
    },
    {
      id: 'network.wss_key',
      label: `Secure key file (${s.localDomain}-key.pem)`,
      status: env.keyReady ? 'pass' : 'warn',
    },
    {
      id: 'network.root_ca',
      label: 'Root CA exported',
      status: env.rootCaReady ? 'pass' : 'warn',
    },
    {
      id: `network.firewall_private_${s.port}`,
      label: `Firewall inbound TCP ${s.port}`,
      status: env.firewallWsOpen ? 'pass' : 'warn',
    },
    {
      id: `network.firewall_private_${s.wssPort}`,
      label: `Firewall inbound TCP ${s.wssPort}`,
      status: env.firewallWssOpen ? 'pass' : 'warn',
    },
    {
      id: `network.port_${s.port}`,
      label: `Port ${s.port} availability`,
      status: env.wsPortFree ? 'pass' : 'warn',
    },
    {
      id: `network.port_${s.wssPort}`,
      label: `Port ${s.wssPort} availability`,
      status: env.wssPortFree ? 'pass' : 'warn',
    },
  ];
}

function mapHostChecks(reportChecks) {
  return reportChecks.map((item) => ({
    id: String(item.id || 'host.unknown'),
    label: String(item.message || item.id || 'Unknown check'),
    status: item.status === 'pass' || item.status === 'warn' || item.status === 'fail' ? item.status : 'warn',
    repairAction: String(item.repairAction || ''),
  }));
}

async function runDiagnostics() {
  appendRuntimeLog('Running setup diagnostics...');

  if (state.bridgeApi && typeof state.bridgeApi.runDiagnostics === 'function') {
    try {
      const report = await state.bridgeApi.runDiagnostics({
        port: state.settings.port,
        wssPort: state.settings.wssPort,
        localDomain: state.settings.localDomain,
        certDir: 'certs',
      });
      state.checks = report && Array.isArray(report.checks) ? mapHostChecks(report.checks) : buildMockChecks();
      appendRuntimeLog('Diagnostics completed (host bridgeApi).');
    } catch (error) {
      appendRuntimeLog(`Diagnostics failed in host mode: ${String(error)}`);
      state.checks = buildMockChecks();
    }
  } else {
    state.checks = buildMockChecks();
    appendRuntimeLog('Diagnostics completed (mock mode).');
  }

  state.lastDiagnosticsAt = new Date();
  if (!state.selectedCheckId || !state.checks.find((check) => check.id === state.selectedCheckId)) {
    state.selectedCheckId = state.checks[0] ? state.checks[0].id : null;
  }

  renderAll();
}

function setSelectedCheck(checkId) {
  if (!state.checks.find((check) => check.id === checkId)) return;
  state.selectedCheckId = checkId;
  renderDiagnosticsTable();
  renderRepairCommand();
}

function selectCheckForRepair(checkId) {
  state.selectedCheckId = checkId;
  if (isAdminRepairCheckId(checkId)) {
    const command = buildRepairCommand(checkId);
    setElevationStatus('required', 'Administrator approval is required for firewall repair action.', command);
  }
  renderRepairCommand();
  void applySelectedRepair();
}

function applySelectedRepairMock(needsElevation, command) {
  if (!state.selectedCheckId) return;

  const id = state.selectedCheckId;
  const wsFirewallId = `network.firewall_private_${state.settings.port}`;
  const wssFirewallId = `network.firewall_private_${state.settings.wssPort}`;
  const wsPortId = `network.port_${state.settings.port}`;
  const wssPortId = `network.port_${state.settings.wssPort}`;

  if (id === 'network.mkcert') state.env.mkcertInstalled = true;
  if (id === 'network.firewall_ws' || id === wsFirewallId) state.env.firewallWsOpen = true;
  if (id === 'network.firewall_wss' || id === wssFirewallId) state.env.firewallWssOpen = true;
  if (id === 'network.port_ws' || id === wsPortId) state.env.wsPortFree = true;
  if (id === 'network.port_wss' || id === wssPortId) state.env.wssPortFree = true;

  if (id === 'network.wss_cert' || id === 'network.wss_key' || id === 'network.root_ca') {
    if (!state.env.mkcertInstalled) {
      appendRuntimeLog('Repair blocked: install mkcert first.');
      return;
    }
    state.env.certReady = true;
    state.env.keyReady = true;
    state.env.rootCaReady = true;
  }

  if (needsElevation) {
    setElevationStatus('completed', 'Elevated repair completed in mock mode.', command);
  }

  saveMockEnv();
  appendRuntimeLog(`Applied mock repair for ${id}.`);
  void runDiagnostics();
}

async function applySelectedRepair() {
  if (!state.selectedCheckId) return;

  const command = buildRepairCommand(state.selectedCheckId);
  const needsElevation = isAdminRepairCheckId(state.selectedCheckId);

  if (needsElevation) {
    setElevationStatus('required', 'Administrator approval is required for this repair action.', command);
  }

  if (state.bridgeApi && typeof state.bridgeApi.applyRepair === 'function') {
    try {
      await state.bridgeApi.applyRepair({
        checkId: state.selectedCheckId,
        command,
      });
      if (needsElevation) {
        setElevationStatus('completed', 'Elevated repair completed.', command);
      }
      appendRuntimeLog(`Repair command sent to host: ${state.selectedCheckId}`);
    } catch (error) {
      if (needsElevation) {
        setElevationStatus('blocked', 'Elevated action was denied or blocked by policy.', command);
      }
      appendRuntimeLog(`Repair failed in host mode: ${String(error)}`);
      renderAll();
      return;
    }
  } else {
    applySelectedRepairMock(needsElevation, command);
    return;
  }

  void runDiagnostics();
}

async function setupCertificate() {
  const command = buildSetupCertCommand();
  appendRuntimeLog(`Running certificate setup: ${command}`);

  if (state.bridgeApi && typeof state.bridgeApi.runSetupCertificate === 'function') {
    try {
      await state.bridgeApi.runSetupCertificate({
        localDomain: state.settings.localDomain,
        certDir: 'certs',
      });
      appendRuntimeLog('Certificate setup completed in host mode.');
      void runDiagnostics();
      return;
    } catch (error) {
      appendRuntimeLog(`Certificate setup failed in host mode: ${String(error)}`);
      return;
    }
  }

  if (!state.env.mkcertInstalled) {
    appendRuntimeLog('Certificate setup blocked: install mkcert first.');
    state.selectedCheckId = 'network.mkcert';
    renderRepairCommand();
    return;
  }

  state.env.certReady = true;
  state.env.keyReady = true;
  state.env.rootCaReady = true;
  saveMockEnv();
  appendRuntimeLog('Certificate setup completed in mock mode.');
  void runDiagnostics();
}

function getStartBlockers() {
  const blockers = [];

  if (!state.lastDiagnosticsAt) {
    blockers.push('Run setup check first.');
    return blockers;
  }

  if (!isCertReady()) {
    blockers.push('Secure mode is required, but cert/key are not ready.');
  }

  if (getCheckStatusByIds(['simconnect.managed_dll']) === 'fail') {
    blockers.push('Managed SimConnect DLL is missing.');
  }
  if (getCheckStatusByIds(['simconnect.native_dll']) === 'fail') {
    blockers.push('Native SimConnect DLL is missing.');
  }

  const wsPort = getCheckStatusByIds(getWsPortIds());
  const wssPort = getCheckStatusByIds(getWssPortIds());
  if (wsPort && wsPort !== 'pass') blockers.push(`Port ${state.settings.port} is not ready.`);
  if (wssPort && wssPort !== 'pass') blockers.push(`Port ${state.settings.wssPort} is not ready.`);

  return blockers;
}

function startUptimeTimer() {
  stopUptimeTimer();
  state.runtime.timer = setInterval(() => {
    state.runtime.uptimeSec += 1;
    renderRuntime();
  }, 1000);
}

function stopUptimeTimer() {
  if (state.runtime.timer) {
    clearInterval(state.runtime.timer);
    state.runtime.timer = null;
  }
}

async function startBridge() {
  if (state.runtime.running) {
    appendRuntimeLog('Bridge is already running.');
    return;
  }

  const blockers = getStartBlockers();
  if (blockers.length > 0) {
    appendRuntimeLog(`Start blocked: ${blockers.join(' ')}`);
    switchView('setup');
    renderAll();
    return;
  }

  const command = buildStartCommand();

  if (state.bridgeApi && typeof state.bridgeApi.startBridge === 'function') {
    try {
      const result = await state.bridgeApi.startBridge({
        settings: state.settings,
        command,
      });
      state.runtime.pid = result && result.pid ? result.pid : 'host';
      appendRuntimeLog('Bridge started in host mode.');
    } catch (error) {
      appendRuntimeLog(`Bridge start failed in host mode: ${String(error)}`);
      return;
    }
  } else {
    state.runtime.pid = Math.floor(3000 + Math.random() * 6000);
    appendRuntimeLog(`Bridge started in mock mode. PID=${state.runtime.pid}`);
  }

  state.runtime.running = true;
  state.runtime.uptimeSec = 0;
  startUptimeTimer();
  renderAll();
}

async function stopBridge() {
  if (!state.runtime.running) {
    appendRuntimeLog('Bridge is already idle.');
    return;
  }

  if (state.bridgeApi && typeof state.bridgeApi.stopBridge === 'function') {
    try {
      await state.bridgeApi.stopBridge();
      appendRuntimeLog('Bridge stopped in host mode.');
    } catch (error) {
      appendRuntimeLog(`Bridge stop failed in host mode: ${String(error)}`);
      return;
    }
  } else {
    appendRuntimeLog('Bridge stopped in mock mode.');
  }

  stopUptimeTimer();
  state.runtime.running = false;
  state.runtime.pid = null;
  state.runtime.uptimeSec = 0;
  renderAll();
}

async function restartBridge() {
  await stopBridge();
  await startBridge();
}

function formatUptime(sec) {
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = sec % 60;
  if (h > 0) return `${h}h ${m}m ${s}s`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

function buildSetupCertCommand() {
  const s = state.settings;
  return `./setup-wss-cert-v0.ps1 -LocalDomain ${s.localDomain} -CertDir "certs"`;
}

function buildStartCommand() {
  const s = state.settings;
  const args = [
    './start-msfs-sync.ps1',
    `-BindHost ${s.bindHost}`,
    `-Port ${s.port}`,
    `-WssPort ${s.wssPort}`,
    `-LocalDomain ${s.localDomain}`,
    `-SampleIntervalMs ${s.sampleMs}`,
    `-PollIntervalMs ${s.pollMs}`,
    `-ReconnectDelayMs ${s.reconnectMs}`,
    `-ReconnectMaxDelayMs ${s.reconnectMaxMs}`,
    '-RequireWss',
  ];

  return args.join(' ');
}

function setStepBadge(id, stateText, kind) {
  const badge = el(id);
  if (!badge) return;
  badge.textContent = stateText;
  badge.className = 'badge';
  if (kind) badge.classList.add(kind);
}

function renderElevationStatus() {
  const badge = el('elevStatusBadge');
  const message = el('elevStatusMessage');
  const copyButton = el('setupCopyAdminCommand');
  if (!badge || !message || !copyButton) return;

  let label = 'Not required';
  let kind = 'ok';

  if (state.elevation.status === 'required') {
    label = 'Elevation required';
    kind = 'warn';
  } else if (state.elevation.status === 'completed') {
    label = 'Elevation completed';
    kind = 'ok';
  } else if (state.elevation.status === 'blocked') {
    label = 'Policy blocked';
    kind = 'bad';
  }

  badge.textContent = label;
  badge.className = 'badge';
  badge.classList.add(kind);

  let detail = state.elevation.message || 'No elevated action required yet.';
  if (state.elevation.lastAction) {
    detail += ` Last action: ${state.elevation.lastAction}`;
  }
  if (state.elevation.status === 'blocked') {
    detail += ' Use "Copy Admin Command" and run it in Administrator PowerShell.';
  }
  message.textContent = detail;

  const showCopy = state.elevation.status === 'blocked';
  copyButton.style.display = showCopy ? 'inline-block' : 'none';
  copyButton.disabled = !showCopy;
}

function renderOverview() {
  const pass = state.checks.filter((check) => check.status === 'pass').length;
  const warn = state.checks.filter((check) => check.status === 'warn').length;
  const fail = state.checks.filter((check) => check.status === 'fail').length;

  el('summaryPass').textContent = String(pass);
  el('summaryWarn').textContent = String(warn);
  el('summaryFail').textContent = String(fail);

  el('ovBridgeStatus').textContent = state.runtime.running ? 'Running' : 'Idle';
  el('ovWssMode').textContent = 'required (locked)';

  if (!state.lastDiagnosticsAt) {
    el('ovReadiness').textContent = 'Not checked';
  } else {
    const blockers = getStartBlockers();
    el('ovReadiness').textContent = blockers.length === 0 ? 'Ready' : `Needs setup (${blockers.length})`;
  }

  const bootstrap = getBootstrapUrlParts();
  const secureUrl = isCertReady()
    ? `wss://${state.settings.localDomain}:${state.settings.wssPort}/stream`
    : 'required but cert/key not ready';

  const endpointWss = el('endpointWss');
  if (endpointWss) endpointWss.textContent = secureUrl;

  const endpointBootstrap = el('endpointBootstrap');
  if (endpointBootstrap) endpointBootstrap.textContent = bootstrap.displayUrl;
}

function renderSetupCommands() {
  const setupCmdMkcert = el('setupCmdMkcert');
  if (setupCmdMkcert) setupCmdMkcert.textContent = buildRepairCommand('network.mkcert');

  const setupCmdCert = el('setupCmdCert');
  if (setupCmdCert) setupCmdCert.textContent = buildSetupCertCommand();

  const setupCmdFirewallWss = el('setupCmdFirewallWss');
  if (setupCmdFirewallWss) {
    setupCmdFirewallWss.textContent = buildRepairCommand(`network.firewall_private_${state.settings.wssPort}`);
  }

  const setupBootstrapUrl = el('setupBootstrapUrl');
  if (setupBootstrapUrl) setupBootstrapUrl.textContent = getBootstrapUrlParts().displayUrl;
}

function renderSetup() {

  renderSetupCommands();

  const hasDiagnostics = !!state.lastDiagnosticsAt;
  const certReady = isCertReady();
  const trustReady = isCheckPassByIds(['network.root_ca']);
  const fwReady = isCheckPassByIds(getWsFirewallIds()) && isCheckPassByIds(getWssFirewallIds());
  const blockers = getStartBlockers();

  setStepBadge('setupStep1', hasDiagnostics ? 'Done' : 'Pending', hasDiagnostics ? 'ok' : 'warn');
  setStepBadge('setupStep2', certReady ? 'Done' : 'Needs action', certReady ? 'ok' : 'warn');
  setStepBadge('setupStep3', trustReady ? 'Done' : 'Guided', trustReady ? 'ok' : 'warn');

  if (state.elevation.status === 'blocked') {
    setStepBadge('setupStep4', 'Blocked', 'bad');
  } else if (fwReady) {
    setStepBadge('setupStep4', 'Done', 'ok');
  } else if (state.elevation.status === 'required') {
    setStepBadge('setupStep4', 'Awaiting UAC', 'warn');
  } else if (state.elevation.status === 'completed') {
    setStepBadge('setupStep4', 'Completed', 'ok');
  } else {
    setStepBadge('setupStep4', 'Optional/Recommended', 'warn');
  }

  setStepBadge('setupStep5', hasDiagnostics && blockers.length === 0 ? 'Ready' : 'Blocked', hasDiagnostics && blockers.length === 0 ? 'ok' : 'bad');

  renderElevationStatus();

  if (!hasDiagnostics) {
    el('setupSummary').textContent = 'Run setup check to refresh status.';
    return;
  }

  if (blockers.length === 0) {
    if (state.elevation.status === 'blocked') {
      el('setupSummary').textContent = 'Runtime can start, but elevated repair is blocked by policy. Use manual admin shell command if LAN clients fail.';
    } else {
      el('setupSummary').textContent = 'Setup ready. Bridge can start with required secure mode.';
    }
  } else {
    el('setupSummary').textContent = `Not ready: ${blockers.join(' ')}`;
  }
}

function guideTextForCheck(check) {
  const id = check && check.id ? check.id : '';
  const s = state.settings;

  if (id === 'simconnect.managed_dll') {
    return 'Place `Microsoft.FlightSimulator.SimConnect.dll` in `lib/`.';
  }
  if (id === 'simconnect.native_dll') {
    return 'Place `SimConnect.dll` in `lib/`.';
  }
  if (id === 'runtime.dotnet') {
    return 'Optional for release package, required for source build.';
  }
  if (id === 'network.mkcert') {
    return 'Needed for first-time certificate generation.';
  }
  if (id === 'network.wss_cert') {
    return '`setup-wss-cert-v0.ps1` creates certificate.';
  }
  if (id === 'network.wss_key') {
    return '`setup-wss-cert-v0.ps1` creates key.';
  }
  if (id === 'network.root_ca') {
    return 'Install root CA on listener device via bootstrap.';
  }
  if (id === 'network.firewall_ws' || id === `network.firewall_private_${s.port}`) {
    return `Open inbound rule for stream port (TCP ${s.port}).`;
  }
  if (id === 'network.firewall_wss' || id === `network.firewall_private_${s.wssPort}`) {
    return `Open inbound rule for secure stream port (TCP ${s.wssPort}).`;
  }
  if (id === 'network.port_ws' || id === `network.port_${s.port}`) {
    return `Port ${s.port} must be free for listener.`;
  }
  if (id === 'network.port_wss' || id === `network.port_${s.wssPort}`) {
    return `Port ${s.wssPort} must be free for secure listener.`;
  }
  if (id.startsWith('network.firewall_private_')) {
    const parsedPort = Number(id.split('_').pop());
    if (Number.isFinite(parsedPort)) {
      return `Open inbound firewall rule for TCP ${parsedPort}.`;
    }
  }
  if (id.startsWith('network.port_')) {
    const parsedPort = Number(id.split('_').pop());
    if (Number.isFinite(parsedPort)) {
      return `Port ${parsedPort} must be free before starting runtime.`;
    }
  }
  if (check && typeof check.repairAction === 'string' && check.repairAction.trim()) {
    return 'Use Apply Selected Repair or copy the provided repair action.';
  }
  return 'Select this row, then apply repair if needed.';
}
function renderDiagnosticsTable() {
  const tbody = el('diagnosticsTableBody');
  if (!tbody) return;
  tbody.innerHTML = '';

  state.checks.forEach((check) => {
    const row = document.createElement('tr');
    if (check.id === state.selectedCheckId) row.classList.add('selected');

    const checkCell = document.createElement('td');
    checkCell.textContent = check.label;

    const statusCell = document.createElement('td');
    statusCell.innerHTML = `<span class="status ${statusClass(check.status)}">${check.status.toUpperCase()}</span>`;

    const guideCell = document.createElement('td');
    guideCell.textContent = guideTextForCheck(check);

    row.appendChild(checkCell);
    row.appendChild(statusCell);
    row.appendChild(guideCell);
    row.addEventListener('click', () => setSelectedCheck(check.id));
    tbody.appendChild(row);
  });

  const diagnosticsLastRun = el('diagnosticsLastRun');
  if (diagnosticsLastRun) {
    diagnosticsLastRun.textContent = state.lastDiagnosticsAt ? state.lastDiagnosticsAt.toLocaleString() : 'Never';
  }
}

function renderRepairCommand() {
  if (!state.selectedCheckId) {
    el('repairCommand').textContent = 'Select a check.';
    return;
  }

  const selected = state.checks.find((check) => check.id === state.selectedCheckId);
  const explicit = selected && selected.repairAction && selected.repairAction.trim();
  el('repairCommand').textContent = explicit || buildRepairCommand(state.selectedCheckId);
}

function renderRuntime() {
  el('runtimeBridgeStatus').textContent = state.runtime.running ? 'Running' : 'Idle';
  el('runtimePid').textContent = state.runtime.pid == null ? '-' : String(state.runtime.pid);
  el('runtimeUptime').textContent = formatUptime(state.runtime.uptimeSec);
  el('runtimeWssMode').textContent = 'required (locked)';
  el('runtimeSimconnect').textContent = state.runtime.running ? 'Connected' : 'Waiting';
  el('runtimeStartCommand').textContent = buildStartCommand();

  el('btnRuntimeStart').disabled = state.runtime.running;
  el('btnRuntimeStop').disabled = !state.runtime.running;
}

function renderStatusBar() {
  el('statusBridge').textContent = `Bridge: ${state.runtime.running ? 'Running' : 'Idle'}`;
  el('statusPreflight').textContent = `Diagnostics: ${state.lastDiagnosticsAt ? state.lastDiagnosticsAt.toLocaleString() : 'Not run'}`;
  el('statusIssues').textContent = `Issues: ${issueCount()}`;
  el('statusMode').textContent = 'Secure: required (locked)';
}

function renderAll() {
  renderOverview();
  renderSetup();
  renderDiagnosticsTable();
  renderRepairCommand();
  renderRuntime();
  renderStatusBar();
}

function switchView(view) {
  document.querySelectorAll('.view').forEach((item) => {
    item.classList.toggle('active', item.id === `view-${view}`);
  });

  document.querySelectorAll('.nav-btn').forEach((item) => {
    item.classList.toggle('active', item.dataset.view === view);
  });
}

async function copyText(value) {
  try {
    await navigator.clipboard.writeText(value);
    appendRuntimeLog('Copied to clipboard.');
  } catch {
    appendRuntimeLog('Copy failed (clipboard unavailable).');
  }
}

function openBootstrapPage() {
  const bootstrap = getBootstrapUrlParts();
  const popup = window.open(bootstrap.openUrl, '_blank', 'noopener,noreferrer');
  if (!popup) {
    appendRuntimeLog(`Popup blocked. Open manually: ${bootstrap.displayUrl}`);
  } else {
    appendRuntimeLog(`Opened bootstrap page: ${bootstrap.openUrl}`);
  }
}

function bindEvents() {
  document.querySelectorAll('.nav-btn').forEach((button) => {
    button.addEventListener('click', () => switchView(button.dataset.view));
  });

  el('ovRunDiagnostics').addEventListener('click', () => {
    switchView('setup');
    void runDiagnostics();
  });

  el('ovOpenSetup').addEventListener('click', () => {
    switchView('setup');
  });

  el('ovStartBridge').addEventListener('click', () => {
    switchView('runtime');
    void startBridge();
  });

  el('setupRunDiagnostics').addEventListener('click', () => void runDiagnostics());
  el('setupInstallMkcert').addEventListener('click', () => selectCheckForRepair('network.mkcert'));
  el('setupSetupCert').addEventListener('click', () => void setupCertificate());
  el('setupOpenBootstrap').addEventListener('click', () => openBootstrapPage());
  el('setupCopyBootstrap').addEventListener('click', () => copyText(getBootstrapUrlParts().displayUrl));
  const setupOpenFirewallWss = el('setupOpenFirewallWss');
  if (setupOpenFirewallWss) {
    setupOpenFirewallWss.addEventListener('click', () => selectCheckForRepair('network.firewall_private_' + state.settings.wssPort));
  }
  el('setupCopyAdminCommand').addEventListener('click', () => {
    const command = getAdminCommandForCopy();
    void copyText(command);
  });
  el('setupSimulateElevBlock').addEventListener('click', () => {
    const command = getAdminCommandForCopy();
    setElevationStatus('blocked', 'Simulated: policy denied elevated action.', command);
    appendRuntimeLog('Simulated elevation policy block.');
    renderAll();
  });
  el('setupClearElevState').addEventListener('click', () => {
    resetElevationStatus();
    appendRuntimeLog('Elevation status reset.');
    renderAll();
  });
  el('setupStartBridge').addEventListener('click', () => {
    switchView('runtime');
    void startBridge();
  });

  el('btnRunDiagnostics').addEventListener('click', () => void runDiagnostics());
  el('btnApplyRepair').addEventListener('click', () => void applySelectedRepair());
  el('btnCopyRepair').addEventListener('click', () => copyText(el('repairCommand').textContent));

  el('btnRuntimeStart').addEventListener('click', () => void startBridge());
  el('btnRuntimeStop').addEventListener('click', () => void stopBridge());
  el('btnRuntimeRestart').addEventListener('click', () => void restartBridge());

  el('btnResetMockState').addEventListener('click', async () => {
    state.env = { ...DEFAULT_MOCK_ENV };
    resetElevationStatus();
    saveMockEnv();
    if (state.runtime.running) {
      await stopBridge();
    }
    appendRuntimeLog('Mock state reset to defaults.');
    await runDiagnostics();
  });

  window.addEventListener('beforeunload', () => {
    stopUptimeTimer();
  });
}

function initialize() {
  setModePill();
  bindEvents();
  renderAll();
  appendRuntimeLog('WinUI v2 mockup initialized.');
  void runDiagnostics();
}

initialize();

