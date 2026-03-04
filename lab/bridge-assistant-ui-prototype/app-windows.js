const checks = [
  {
    id: "runtime.dotnet",
    label: ".NET runtime availability",
    status: "pass",
    repair: ".\\publish-v0.ps1",
  },
  {
    id: "runtime.standard_user",
    label: "PowerShell standard-user mode",
    status: "warn",
    repair: "Run bridge from non-admin shell",
  },
  {
    id: "network.firewall_private_39000",
    label: "Firewall rule for TCP 39000",
    status: "warn",
    repair: ".\\repair-elevated-v0.ps1 -Action OpenFirewall39000 -Port 39000",
  },
  {
    id: "network.wss_cert",
    label: "WSS certificate presence",
    status: "fail",
    repair: ".\\setup-wss-cert-v0.ps1 -LocalDomain bridge.local",
  },
  {
    id: "network.wss_key",
    label: "WSS key presence",
    status: "fail",
    repair: ".\\setup-wss-cert-v0.ps1 -LocalDomain bridge.local",
  },
  {
    id: "network.root_ca",
    label: "Root CA export",
    status: "warn",
    repair: ".\\setup-wss-cert-v0.ps1 -LocalDomain bridge.local",
  },
  {
    id: "network.mkcert",
    label: "mkcert executable",
    status: "warn",
    repair: "winget install FiloSottile.mkcert",
  },
  {
    id: "simconnect.dll",
    label: "SimConnect library placement",
    status: "pass",
    repair: "Copy SimConnect.dll to bridge root",
  },
];

const checksContainer = document.getElementById("checks");
const repairCommand = document.getElementById("repair-command");
const summaryPass = document.getElementById("summary-pass");
const summaryWarn = document.getElementById("summary-warn");
const summaryFail = document.getElementById("summary-fail");
const runtimeState = document.getElementById("runtime-state");
const logConsole = document.getElementById("log-console");

let heartbeatTimer = null;

function renderChecks() {
  checksContainer.innerHTML = "";

  checks.forEach((check) => {
    const row = document.createElement("div");
    row.className = "check-row";
    row.innerHTML = `
      <label>${check.label}</label>
      <span class="status ${check.status}">${check.status}</span>
    `;
    row.addEventListener("click", () => {
      repairCommand.textContent = check.repair;
    });
    checksContainer.appendChild(row);
  });

  summaryPass.textContent = String(
    checks.filter((check) => check.status === "pass").length
  );
  summaryWarn.textContent = String(
    checks.filter((check) => check.status === "warn").length
  );
  summaryFail.textContent = String(
    checks.filter((check) => check.status === "fail").length
  );
}

function setStatus(checkId, status) {
  const target = checks.find((check) => check.id === checkId);
  if (!target) {
    return;
  }
  target.status = status;
}

function addLog(message) {
  const timestamp = new Date().toLocaleTimeString();
  logConsole.textContent += `[${timestamp}] ${message}\n`;
  logConsole.scrollTop = logConsole.scrollHeight;
}

function showView(targetId) {
  document.querySelectorAll(".section").forEach((section) => {
    section.classList.remove("is-visible");
  });
  const target = document.getElementById(targetId);
  if (target) {
    target.classList.add("is-visible");
  }
}

document.querySelectorAll(".nav-item").forEach((button) => {
  button.addEventListener("click", () => {
    document.querySelectorAll(".nav-item").forEach((navItem) => {
      navItem.classList.remove("is-active");
    });
    button.classList.add("is-active");
    showView(button.dataset.target);
  });
});

document.getElementById("btn-run-scan").addEventListener("click", () => {
  setStatus("runtime.standard_user", "warn");
  setStatus("network.firewall_private_39000", "warn");
  setStatus("network.wss_cert", "fail");
  setStatus("network.wss_key", "fail");
  setStatus("network.root_ca", "warn");
  setStatus("network.mkcert", "warn");
  renderChecks();
  addLog("Diagnostics completed. Certificate and policy actions required.");
});

document.getElementById("btn-auto-fix").addEventListener("click", () => {
  checks.forEach((check) => {
    if (check.status === "warn") {
      check.status = "pass";
    }
    if (check.status === "fail" && check.id.startsWith("network.wss_")) {
      check.status = "pass";
    }
  });
  setStatus("network.root_ca", "pass");
  renderChecks();
  addLog("Automated fixes applied. Validate trust and restart session.");
});

document.getElementById("btn-generate-cert").addEventListener("click", () => {
  setStatus("network.wss_cert", "pass");
  setStatus("network.wss_key", "pass");
  setStatus("network.root_ca", "warn");

  const certCard = document.getElementById("card-cert").querySelector(".status");
  certCard.className = "status pass";
  certCard.textContent = "Installed";

  const keyCard = document.getElementById("card-key").querySelector(".status");
  keyCard.className = "status pass";
  keyCard.textContent = "Installed";

  renderChecks();
  addLog("Server certificate and private key generated.");
});

document.getElementById("btn-verify-cert").addEventListener("click", () => {
  setStatus("network.root_ca", "pass");
  const rootCard = document.getElementById("card-root").querySelector(".status");
  rootCard.className = "status pass";
  rootCard.textContent = "Trusted";
  renderChecks();
  addLog("Root trust verified in LocalMachine\\Root certificate store.");
});

document.getElementById("btn-start").addEventListener("click", () => {
  if (heartbeatTimer !== null) {
    addLog("Bridge already running.");
    return;
  }

  runtimeState.textContent = "Running";
  runtimeState.classList.add("live");

  addLog("Starting run-bridge.ps1");
  addLog("WS  : ws://192.168.0.24:39000");
  addLog("WSS : wss://bridge.local:39002");

  heartbeatTimer = window.setInterval(() => {
    addLog("Heartbeat OK | stream active | 24Hz");
  }, 2000);
});

document.getElementById("btn-stop").addEventListener("click", () => {
  if (heartbeatTimer === null) {
    addLog("Bridge already stopped.");
    return;
  }

  window.clearInterval(heartbeatTimer);
  heartbeatTimer = null;
  runtimeState.textContent = "Idle";
  runtimeState.classList.remove("live");
  addLog("Bridge stopped.");
});

renderChecks();
addLog("Windows native variant ready.");
