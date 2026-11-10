import { useEffect, useRef, useState } from 'react'
import { Collapsible } from '@base-ui/react/collapsible'
import { Toaster, toast } from 'sonner'
import clsx from 'clsx'
import { send, listen } from './bridge.js'

// Row status: 'scanning' | 'ok' | 'bad' | 'running' | 'done' | 'failed'
export default function App() {
  const [rows, setRows] = useState([])
  const [phase, setPhase] = useState('scanning') // scanning | ready | fixing | fixed | error
  const [log, setLog] = useState([])
  const logRef = useRef(null)
  const mainRef = useRef(null)

  // The window sizes itself to the content, so opening Details grows it instead of cutting it off.
  useEffect(() => {
    const ro = new ResizeObserver(() => send({ type: 'size', height: Math.ceil(mainRef.current.scrollHeight) }))
    ro.observe(mainRef.current)
    return () => ro.disconnect()
  }, [])

  useEffect(() => {
    const off = listen((m) => {
      if (m.type === 'checks') {
        setRows(m.checks.map((c) => ({ ...c, status: c.ok ? 'ok' : 'bad' })))
        setPhase('ready')
      } else if (m.type === 'step') {
        setRows((rs) => rs.map((r) => (r.id === m.id ? { ...r, status: m.state, detail: m.detail ?? r.detail } : r)))
      } else if (m.type === 'log') {
        setLog((l) => [...l, m.line])
      } else if (m.type === 'result') {
        setPhase(m.ok ? 'fixed' : 'error')
        if (m.ok) {
          toast.success('Apollo Twin is back', {
            description: m.message,
            action: { label: 'Open Console', onClick: () => send({ type: 'openConsole' }) },
          })
        } else {
          toast.error('Fix didn’t finish', { description: m.message })
        }
      }
    })
    send({ type: 'scan' })
    return off
  }, [])

  useEffect(() => {
    logRef.current?.scrollTo({ top: logRef.current.scrollHeight })
  }, [log])

  const allOk = phase === 'ready' && rows.length > 0 && rows.every((r) => r.status === 'ok')
  const deviceMissing = rows.find((r) => r.id === 'device')?.status === 'bad'

  function fix() {
    setPhase('fixing')
    setLog([])
    send({ type: 'fix' })
  }

  return (
    <main className="app" ref={mainRef}>
      <header className="header">
        <h1>Apollo Twin Fix</h1>
        <p>Gets UAD Console to detect an Apollo Twin USB again after a UAD v12 update.</p>
      </header>

      <ul className="checks" aria-live="polite">
        {phase === 'scanning' && rows.length === 0
          ? SKELETON.map((label) => <Row key={label} label={label} status="scanning" detail="Checking…" />)
          : rows.map((r) => <Row key={r.id} {...r} />)}
      </ul>

      {deviceMissing && phase === 'ready' && (
        <p className="note">Turn the Twin on and connect it with its USB 3 cable, then re-check. You can still run the fix.</p>
      )}

      <div className="actions">
        {phase === 'fixed' || allOk ? (
          <>
            <button className="btn primary" onClick={() => send({ type: 'openConsole' })}>Open UAD Console</button>
            <button className="btn ghost" onClick={() => { setPhase('scanning'); send({ type: 'scan' }) }}>Re-check</button>
          </>
        ) : (
          <>
            <button className="btn primary" onClick={fix} disabled={phase !== 'ready' && phase !== 'error'}>
              <span className={clsx('btn-label', phase === 'fixing' && 'busy')}>
                {phase === 'fixing' && <Spinner />}
                {phase === 'fixing' ? 'Fixing…' : phase === 'error' ? 'Try again' : 'Fix it'}
              </span>
            </button>
            <button className="btn ghost" disabled={phase === 'fixing' || phase === 'scanning'}
              onClick={() => { setPhase('scanning'); send({ type: 'scan' }) }}>Re-check</button>
          </>
        )}
      </div>

      <Collapsible.Root className="details">
        <Collapsible.Trigger className="details-trigger">
          <Chevron />
          Details
          {log.length > 0 && <span className="count">{log.length}</span>}
        </Collapsible.Trigger>
        <Collapsible.Panel className="details-panel">
          <pre ref={logRef} className="log">{log.length ? log.join('\n') : 'Nothing yet. Steps show up here while the fix runs.'}</pre>
        </Collapsible.Panel>
      </Collapsible.Root>

      <Toaster theme="system" position="bottom-center" richColors closeButton />
    </main>
  )
}

const SKELETON = ['Apollo Twin connected', 'UAD software installed', 'Apollo mixer engine', 'USB audio driver registered', 'Mixer engine running']

function Row({ label, status, detail }) {
  return (
    <li className={clsx('row', `is-${status}`)}>
      <span className="icon" aria-hidden>
        {/* keyed so each status change crossfades in */}
        <span key={status} className="icon-inner"><StatusIcon status={status} /></span>
      </span>
      <span className="text">
        <span className="label">{label}</span>
        <span className="detail">{detail}</span>
      </span>
      <span className="sr-only">{status}</span>
    </li>
  )
}

function StatusIcon({ status }) {
  if (status === 'scanning' || status === 'running') return <Spinner />
  if (status === 'ok' || status === 'done')
    return <svg viewBox="0 0 16 16"><path d="M4 8.5l2.5 2.5L12 5.5" /></svg>
  if (status === 'failed')
    return <svg viewBox="0 0 16 16"><path d="M5 5l6 6M11 5l-6 6" /></svg>
  return <svg viewBox="0 0 16 16"><circle cx="8" cy="8" r="2" className="dot" /></svg>
}

function Spinner() {
  return <svg className="spinner" viewBox="0 0 16 16"><circle cx="8" cy="8" r="6" /></svg>
}

function Chevron() {
  return <svg className="chevron" viewBox="0 0 16 16" aria-hidden><path d="M6 4l4 4-4 4" /></svg>
}
