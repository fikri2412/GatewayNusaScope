import { useState } from 'react'
import { SANDBOX_CSP, SANDBOX_FLAGS, buildSandboxDoc } from '../canvas'

/**
 * Pratinjau HTML dari artifact. Isolasi berlapis: iframe `sandbox="allow-scripts"` TANPA `allow-same-origin`
 * (origin buram: tidak bisa membaca DOM, storage, atau token milik aplikasi; tanpa popup/form/navigasi top),
 * CSP tanpa jaringan lewat meta dan atribut `csp`, `srcdoc` (tidak pernah di-origin aplikasi), referrer dimatikan.
 * Token JWT tidak pernah diteruskan ke frame ini.
 */
export function SandboxPreview({ source }: { source: string }) {
  const [run, setRun] = useState(0)
  // `csp` (Chromium) belum ada di tipe React; browser lain mengabaikannya dan tetap dilindungi meta CSP.
  const extra = { csp: SANDBOX_CSP }
  return (
    <div className="canvas-preview">
      <div className="canvas-diagram-bar">
        <span className="muted">Sandbox: tanpa jaringan dan tanpa akses ke aplikasi.</span>
        <button type="button" className="ghost-button" onClick={() => setRun((n) => n + 1)}>Muat ulang</button>
      </div>
      <iframe
        key={run}
        className="canvas-preview-frame"
        title="Pratinjau HTML (sandbox)"
        sandbox={SANDBOX_FLAGS}
        referrerPolicy="no-referrer"
        srcDoc={buildSandboxDoc(source)}
        {...extra}
      />
    </div>
  )
}
