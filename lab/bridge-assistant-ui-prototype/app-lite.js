const checkList = document.getElementById("check-list");
const statusValue = document.getElementById("status-value");
const statusNext = document.getElementById("status-next");
const repairCommand = document.getElementById("repair-command");
const logConsole = document.getElementById("log-console");
const advancedPanel = document.getElementById("advanced-panel");
const root = document.body;

let isReady = false;
let isRunning = false;
let heartbeatTimer = null;

const checks = [
  {
    label: "Firewall rule for TCP 39000",
    status: "warn",
    repair: ".\\repair-elevated-v0.ps1 -Action OpenFirewall39000 -Port 39000",
  },
  {
    label: "WSS certificate and key",
    status: "fail",
    repair: ".\\setup-wss-cert-v0.ps1 -LocalDomain bridge.local",
  },
  {
    label: "Root CA trust",
    status: "warn",
    repair: ".\\setup-wss-cert-v0.ps1 -LocalDomain bridge.local",
  },
  {
    label: "SimConnect library placement",
    status: "pass",
    repair: "Copy SimConnect.dll to bridge root",
  },
];

function addLog(message) {
  const timestamp = new Date().toLocaleTimeString();
  logConsole.textContent += `[${timestamp}] ${message}\n`;
  logConsole.scrollTop = logConsole.scrollHeight;
}

function summarizeStatus() {
  const warnings = checks.filter((check) => check.status === "warn").length;
  const failures = checks.filter((check) => check.status === "fail").length;
  const remaining = warnings + failures;

  if (remaining === 0) {
    isReady = true;
    root.classList.add("ready");
    statusValue.textContent = "Ready";
    statusNext.textContent = "Next: Start bridge.";
    return;
  }

  isReady = false;
  root.classList.remove("ready");
  statusValue.textContent = `Setup needed: ${remaining} item${
    remaining > 1 ? "s" : ""
  }`;
  statusNext.textContent =
    "Next: Run automatic setup to configure certificate and firewall.";
}

function renderChecks() {
  checkList.innerHTML = "";

  checks.forEach((check) => {
    const li = document.createElement("li");
    li.textContent = `${check.label}: ${check.status.toUpperCase()}`;
    li.addEventListener("click", () => {
      repairCommand.textContent = check.repair;
    });
    checkList.appendChild(li);
  });

  summarizeStatus();
}

function markAllReady() {
  checks.forEach((check) => {
    check.status = "pass";
  });
  renderChecks();
}

document.getElementById("btn-fix").addEventListener("click", () => {
  addLog("Running auto setup...");
  addLog("Applying firewall rule...");
  addLog("Generating local certificate...");
  addLog("Verifying root trust...");
  markAllReady();
  addLog("Auto setup completed.");
});

document.getElementById("btn-start").addEventListener("click", () => {
  if (!isReady) {
    addLog("Start blocked: setup is not complete. Run automatic setup first.");
    if (!advancedPanel.open) {
      advancedPanel.open = true;
    }
    return;
  }

  if (isRunning) {
    addLog("Bridge already running.");
    return;
  }

  isRunning = true;
  addLog("Starting bridge...");
  addLog("WS  : ws://192.168.0.24:39000");
  addLog("WSS : wss://bridge.local:39002");

  heartbeatTimer = window.setInterval(() => {
    addLog("Heartbeat OK | stream active | 24Hz");
  }, 2000);
});

window.addEventListener("beforeunload", () => {
  if (heartbeatTimer !== null) {
    window.clearInterval(heartbeatTimer);
  }
});

renderChecks();
addLog("Lite prototype ready.");
