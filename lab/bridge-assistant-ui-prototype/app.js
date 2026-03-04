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

let intervalId = null;

function renderChecks() {
  checksContainer.innerHTML = "";

  checks.forEach((check) => {
    const row = document.createElement("div");
    row.className = "check-row";

    const label = document.createElement("label");
    label.textContent = check.label;
    row.appendChild(label);

    const status = document.createElement("span");
    status.className = `status ${check.status}`;
    status.textContent = check.status;
    row.appendChild(status);

    row.addEventListener("click", () => {
      repairCommand.textContent = check.repair;
    });

    checksContainer.appendChild(row);
  });

  const passCount = checks.filter((item) => item.status === "pass").length;
  const warnCount = checks.filter((item) => item.status === "warn").length;
  const failCount = checks.filter((item) => item.status === "fail").length;

  summaryPass.textContent = String(passCount);
  summaryWarn.textContent = String(warnCount);
  summaryFail.textContent = String(failCount);
}

function setCheckStatus(checkId, nextStatus) {
  const check = checks.find((item) => item.id === checkId);
  if (!check) {
    return;
  }
  check.status = nextStatus;
}

function addLog(message) {
  const timestamp = new Date().toLocaleTimeString();
  logConsole.textContent += `[${timestamp}] ${message}\n`;
  logConsole.scrollTop = logConsole.scrollHeight;
}

document.querySelectorAll(".step").forEach((button) => {
  button.addEventListener("click", () => {
    document.querySelectorAll(".step").forEach((item) => {
      item.classList.remove("is-active");
    });
    button.classList.add("is-active");

    const target = button.dataset.target;
    document.querySelectorAll(".section").forEach((section) => {
      section.classList.remove("is-visible");
    });
    document.getElementById(target).classList.add("is-visible");
  });
});

document.getElementById("btn-run-scan").addEventListener("click", () => {
  setCheckStatus("runtime.standard_user", "warn");
  setCheckStatus("network.firewall_private_39000", "warn");
  setCheckStatus("network.wss_cert", "fail");
  setCheckStatus("network.wss_key", "fail");
  setCheckStatus("network.root_ca", "warn");
  setCheckStatus("network.mkcert", "warn");
  renderChecks();
  addLog("Preflight scan completed. Blocking certificate issues detected.");
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
  setCheckStatus("network.root_ca", "pass");
  renderChecks();
  addLog("Auto-fix executed. Re-run verify step to confirm system trust.");
});

document.getElementById("btn-generate-cert").addEventListener("click", () => {
  setCheckStatus("network.wss_cert", "pass");
  setCheckStatus("network.wss_key", "pass");
  setCheckStatus("network.root_ca", "warn");

  const certCard = document.getElementById("card-cert").querySelector(".status");
  certCard.className = "status pass";
  certCard.textContent = "Installed";

  const keyCard = document.getElementById("card-key").querySelector(".status");
  keyCard.className = "status pass";
  keyCard.textContent = "Installed";

  renderChecks();
  addLog("Certificate files generated with mkcert.");
});

document.getElementById("btn-verify-cert").addEventListener("click", () => {
  setCheckStatus("network.root_ca", "pass");
  const rootCard = document.getElementById("card-root").querySelector(".status");
  rootCard.className = "status pass";
  rootCard.textContent = "Trusted";
  renderChecks();
  addLog("Trust chain verified in LocalMachine\\Root.");
});

document.getElementById("btn-start").addEventListener("click", () => {
  if (intervalId !== null) {
    addLog("Bridge already running.");
    return;
  }

  runtimeState.textContent = "Running";
  runtimeState.classList.add("live");
  addLog("Launching run-bridge.ps1 with WSS enabled...");
  addLog("WS endpoint: ws://192.168.0.24:39000");
  addLog("WSS endpoint: wss://bridge.local:39002");

  intervalId = window.setInterval(() => {
    addLog("Heartbeat OK | sim stream active | 24Hz");
  }, 1800);
});

document.getElementById("btn-stop").addEventListener("click", () => {
  if (intervalId === null) {
    addLog("Bridge is already idle.");
    return;
  }

  window.clearInterval(intervalId);
  intervalId = null;
  runtimeState.textContent = "Idle";
  runtimeState.classList.remove("live");
  addLog("Bridge stopped.");
});

renderChecks();
addLog("Prototype ready. Try each workflow section from the left menu.");
