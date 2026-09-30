import { useState } from 'react'
import { BridgeCallError, invoke } from './bridge'

export default function App() {
  const [result, setResult] = useState<string>('')

  async function ping() {
    try {
      const r = await invoke('ping', { message: 'olá' })
      setResult(`${r.message} (${r.serverTime})`)
    } catch (e) {
      setResult(e instanceof BridgeCallError ? `Erro: ${e.detail.message}` : String(e))
    }
  }

  return (
    <main className="min-h-screen bg-white text-neutral-900 dark:bg-neutral-900 dark:text-neutral-100 p-8">
      <h1 className="text-2xl font-semibold">SqlDesk</h1>
      <button
        onClick={ping}
        className="mt-4 rounded-md border border-neutral-300 dark:border-neutral-600 px-3 py-1.5 hover:bg-neutral-100 dark:hover:bg-neutral-800"
      >
        Ping
      </button>
      <p className="mt-4 font-mono" data-testid="ping-result">{result}</p>
    </main>
  )
}
