// Talks to the C# host through WebView2. Outside the exe (vite dev) a mock
// host plays back a fake scan + fix so the UI can be previewed in a browser.
const webview = window.chrome?.webview

export function send(msg) {
  if (webview) webview.postMessage(msg)
  else mock(msg)
}

export function listen(fn) {
  if (webview) {
    const h = (e) => fn(e.data)
    webview.addEventListener('message', h)
    return () => webview.removeEventListener('message', h)
  }
  listeners.add(fn)
  return () => listeners.delete(fn)
}

// ponytail: dev-only mock, never used inside the exe.
const listeners = new Set()
const emit = (m) => listeners.forEach((fn) => fn(m))
const wait = (ms) => new Promise((r) => setTimeout(r, ms))

async function mock(msg) {
  if (msg.type === 'size') return
  if (msg.type === 'scan') {
    await wait(600)
    emit({ type: 'checks', checks: [
      { id: 'device', label: 'Apollo Twin connected', ok: true, detail: 'Twin USB found on USB' },
      { id: 'software', label: 'UAD software installed', ok: true, detail: 'Version 12.0.0.602' },
      { id: 'engine', label: 'Apollo mixer engine', ok: false, detail: 'Missing: installer skipped it' },
      { id: 'asio', label: 'USB audio driver registered', ok: false, detail: 'Not registered' },
      { id: 'running', label: 'Mixer engine running', ok: false, detail: 'Not running' },
    ] })
  }
  if (msg.type === 'fix') {
    for (const id of ['engine', 'asio', 'running']) {
      emit({ type: 'step', id, state: 'running', detail: 'Working…' })
      emit({ type: 'log', line: `Starting ${id}` })
      await wait(900)
      emit({ type: 'step', id, state: 'done', detail: 'Done' })
    }
    emit({ type: 'result', ok: true, message: 'UAD Console can see your Apollo Twin again.' })
  }
}
