/* Paste this in browser console (e.g., https://anobservatory.com). */
/* eslint-disable no-console */
async function runAoLnaProbe(wsUrl, timeoutMs = 10000) {
  const startedAt = Date.now();
  const result = {
    wsUrl,
    pageOrigin: window.location.origin,
    secureContext: window.isSecureContext,
    userAgent: navigator.userAgent,
    startedAt,
    stage: 'init',
    ok: false,
    firstMessageAt: null,
    closeCode: null,
    closeReason: null,
    errorName: null,
    errorMessage: null
  };

  return await new Promise((resolve) => {
    let ws = null;
    let done = false;
    const finish = (patch) => {
      if (done) return;
      done = true;
      Object.assign(result, patch, { endedAt: Date.now(), durationMs: Date.now() - startedAt });
      try {
        if (ws && ws.readyState === WebSocket.OPEN) {
          ws.close(1000, 'probe-finished');
        }
      } catch {
        // ignore
      }
      console.table(result);
      resolve(result);
    };

    try {
      ws = new WebSocket(wsUrl);
      result.stage = 'socket-created';
    } catch (e) {
      finish({
        stage: 'constructor-error',
        errorName: e?.name ?? 'Error',
        errorMessage: e?.message ?? String(e)
      });
      return;
    }

    const timer = window.setTimeout(() => {
      finish({ stage: 'timeout' });
    }, timeoutMs);

    ws.onopen = () => {
      result.stage = 'open';
    };
    ws.onmessage = () => {
      if (!result.firstMessageAt) {
        result.firstMessageAt = Date.now();
      }
      window.clearTimeout(timer);
      finish({ stage: 'message', ok: true });
    };
    ws.onerror = (event) => {
      result.stage = 'error-event';
      result.errorMessage = event?.message ?? 'WebSocket error event';
    };
    ws.onclose = (event) => {
      finish({
        stage: result.stage === 'error-event' ? 'closed-after-error' : 'closed',
        closeCode: event.code,
        closeReason: event.reason || ''
      });
    };
  });
}

console.log('Ready: runAoLnaProbe("ws://<LAN_IP>:39000/stream")');
console.log('Ready: runAoLnaProbe("wss://localhost:39002/stream")');
