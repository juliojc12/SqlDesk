import { useEffect, useRef, useState } from 'react'
import type { Cell, GuardChange, GuardInfo } from '../contracts'
import { changedCells, describeChange, isSampleTruncated } from '../guardDiff'
import { btnBase, btnDanger, Modal } from './Modal'

const amberBtn = 'rounded-md bg-amber-500 px-3 py-1.5 text-sm font-medium text-black hover:bg-amber-400'

/**
 * Primeira confirmação: nada foi executado. Cancelar é o padrão (recebe o foco), para que um Enter distraído não confirme.
 * Com `irreversible` (MySQL/MariaDB com DDL, comando que confirma sozinho ou alvo em tabela sem transação, como MyISAM), o texto roda direto, sem transação e sem a
 * segunda confirmação: o diálogo avisa que não haverá volta.
 */
export function DangerDialog({ blocked, irreversible = false, onCancel, onContinue }: {
  blocked: { line: number; description: string }[]
  irreversible?: boolean
  onCancel: () => void
  onContinue: () => void
}) {
  const cancelRef = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    cancelRef.current?.focus()
  }, [])
  return (
    <Modal title="Comando destrutivo" onCancel={onCancel} width={620} role="alertdialog">
      <div className="p-5">
        <h2 className="text-base font-semibold text-danger">Este texto contém comandos destrutivos</h2>
        <p className="mt-2 text-sm text-muted">Nada foi executado. Os comandos abaixo precisam da sua confirmação:</p>
        <ul className="thin-scroll mt-3 max-h-64 select-text space-y-2 overflow-auto text-sm">
          {blocked.map((b, i) => (
            <li key={i} className="flex gap-3 rounded-md border border-line px-3 py-2">
              <span className="shrink-0 font-mono text-muted">linha {b.line}</span>
              <span>{b.description}</span>
            </li>
          ))}
        </ul>
        {irreversible ? (
          <p role="note" data-testid="irreversible-warning" className="mt-3 rounded-md border border-red-600 bg-red-600/15 px-3 py-2 text-sm">
            Este comando muda a estrutura do banco ou grava de forma definitiva e <strong>não pode ser desfeito</strong>: o MySQL/MariaDB
            confirma sozinho mudanças de estrutura, e tabelas sem transação (como MyISAM) não voltam com Rollback. Não haverá
            Commit/Rollback depois.
          </p>
        ) : (
          <p className="mt-3 text-sm text-muted">
            Se continuar, o texto roda dentro de uma transação e você verá o resultado antes de decidir entre Commit e Rollback.
          </p>
        )}
        <div className="mt-5 flex justify-end gap-2">
          <button ref={cancelRef} onClick={onCancel} className={btnBase}>Cancelar</button>
          <button onClick={onContinue} className={btnDanger}>{irreversible ? 'Executar mesmo assim' : 'Continuar'}</button>
        </div>
      </div>
    </Modal>
  )
}

function Value({ v }: { v: Cell }) {
  if (v === null) return <span className="rounded bg-hover px-1 text-[11px] italic text-muted">NULL</span>
  return <>{typeof v === 'boolean' ? (v ? 'true' : 'false') : String(v)}</>
}

function SampleTable({ title, columns, rows, changed }: { title: string; columns: string[]; rows: Cell[][]; changed: boolean[][] }) {
  return (
    <table className="border-collapse text-[13px]" aria-label={title}>
      <caption className="sticky left-0 bg-surface px-2 py-1 text-left text-xs font-semibold uppercase tracking-wide text-muted">{title}</caption>
      <thead>
        <tr>
          {columns.map((c, i) => (
            <th key={i} className="whitespace-nowrap border-b border-line px-2 py-1 text-left font-semibold">{c}</th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rows.map((r, i) => (
          <tr key={i}>
            {r.map((v, c) => (
              <td
                key={c}
                data-changed={changed[i]?.[c] ? 'true' : undefined}
                className={`max-w-56 truncate whitespace-nowrap border-b border-line/60 px-2 py-1 ${changed[i]?.[c] ? 'bg-amber-500/25 font-medium' : ''}`}
              >
                <Value v={v} />
              </td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function ChangeBlock({ change }: { change: GuardChange }) {
  const hasAfter = change.kind === 'updateWithoutWhere' && change.after.length > 0
  const changed = changedCells(change.before, change.after)
  return (
    <section className="rounded-lg border border-line p-3" aria-label={describeChange(change)}>
      <h3 className="text-sm font-semibold">{describeChange(change)}</h3>
      <p className="text-xs text-muted">linha {change.line}</p>
      {change.hasPreview && change.before.length > 0 && (
        <>
          <div className="thin-scroll mt-2 max-h-60 select-text overflow-auto rounded-md border border-line">
            <div className="flex min-w-max gap-6">
              <SampleTable title={hasAfter ? 'Antes' : 'Linhas apagadas'} columns={change.columns} rows={change.before} changed={hasAfter ? changed : []} />
              {hasAfter && <SampleTable title="Depois" columns={change.columns} rows={change.after} changed={changed} />}
            </div>
          </div>
          {isSampleTruncated(change) && (
            <p className="mt-1 text-xs text-muted">Amostra das primeiras {change.before.length} linhas.</p>
          )}
        </>
      )}
    </section>
  )
}

/** Segunda confirmação: o texto já rodou dentro de uma transação. Rollback é o padrão (foco, Enter e Esc). */
export function GuardDialog({ guard, busy, onCommit, onRollback }: {
  guard: GuardInfo
  busy: boolean
  onCommit: () => void
  onRollback: () => void
}) {
  const rollbackRef = useRef<HTMLButtonElement>(null)
  const [left, setLeft] = useState(guard.timeoutSeconds)
  useEffect(() => {
    rollbackRef.current?.focus()
  }, [])
  useEffect(() => {
    const h = window.setInterval(() => setLeft((s) => Math.max(0, s - 1)), 1000)
    return () => window.clearInterval(h)
  }, [])

  return (
    <Modal title="Confirmar alterações" onCancel={onRollback} width={920} role="alertdialog">
      <div className="flex max-h-[90vh] flex-col p-5">
        <h2 className="text-base font-semibold">Confirmar as alterações?</h2>
        <p className="mt-1 text-sm text-muted">
          O texto já foi executado dentro de uma transação e ainda não foi gravado.
          {guard.usesSavepoint
            ? ' Esta aba já tinha uma transação aberta: Rollback desfaz só este trecho; Commit mantém as alterações nela (você ainda precisará dar commit).'
            : ' Commit grava; Rollback desfaz tudo.'}
        </p>

        <div className="thin-scroll mt-3 min-h-0 flex-1 space-y-3 overflow-auto">
          {guard.previewUnavailable && (
            <p role="note" className="rounded-md bg-amber-500/20 px-3 py-2 text-sm">
              Uma das tabelas tem trigger e não permite mostrar o antes e o depois. O comando foi executado sem a amostra
              {guard.totalAffected > 0 && `; ${guard.totalAffected.toLocaleString('pt-BR')} linhas afetadas no total`}.
            </p>
          )}
          {guard.approximate && (
            <p role="note" className="rounded-md bg-amber-500/20 px-3 py-2 text-sm">
              Não foi possível ligar cada resultado ao seu comando (o texto tem IF, loop ou consultas no meio), então não há amostra do
              antes e depois.{guard.totalAffected > 0 && ` Linhas afetadas por comandos sem resultado: ${guard.totalAffected.toLocaleString('pt-BR')}.`}
            </p>
          )}
          {guard.changes.map((c, i) => <ChangeBlock key={i} change={c} />)}
        </div>

        <div className="mt-4 flex items-center justify-end gap-3">
          <span className="mr-auto text-sm text-muted" role="timer" aria-live="off">
            Rollback automático em {left} s{left === 0 && ' (aguardando o servidor)'}
          </span>
          <button ref={rollbackRef} disabled={busy} onClick={onRollback} className={btnBase}>Rollback</button>
          <button disabled={busy} onClick={onCommit} className={btnDanger}>Commit</button>
        </div>
      </div>
    </Modal>
  )
}

/** Fechar uma aba com transação aberta: Cancelar é o padrão. */
export function TranCloseDialog({ title, busy, onCancel, onCommit, onRollback }: {
  title: string
  busy: boolean
  onCancel: () => void
  onCommit: () => void
  onRollback: () => void
}) {
  const cancelRef = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    cancelRef.current?.focus()
  }, [])
  return (
    <Modal title="Transação aberta" onCancel={onCancel} role="alertdialog">
      <div className="p-5">
        <h2 className="text-base font-semibold">Transação aberta</h2>
        <p className="mt-2 text-sm text-muted">
          A aba <strong className="text-fg">{title}</strong> tem uma transação aberta. Fechá-la sem decidir descartaria as alterações não confirmadas.
        </p>
        <div className="mt-5 flex justify-end gap-2">
          <button ref={cancelRef} onClick={onCancel} className={btnBase}>Cancelar</button>
          <button disabled={busy} onClick={onRollback} className={btnBase}>Rollback</button>
          <button disabled={busy} onClick={onCommit} className={amberBtn}>Commit</button>
        </div>
      </div>
    </Modal>
  )
}

/** Fechar o app com transações abertas: cada aba precisa de uma decisão. */
export function AppCloseDialog({ tabs, onDecide, onCancel, onDone }: {
  tabs: { tabId: string; title: string; count: number }[]
  onDecide: (tabId: string, commit: boolean) => Promise<void>
  onCancel: () => void
  onDone: () => void
}) {
  const [done, setDone] = useState<Record<string, 'commit' | 'rollback'>>({})
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)
  const cancelRef = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    cancelRef.current?.focus()
  }, [])

  const decide = async (tabId: string, commit: boolean) => {
    setBusy(true)
    try {
      await onDecide(tabId, commit)
      setDone((d) => ({ ...d, [tabId]: commit ? 'commit' : 'rollback' }))
      setErrors((e) => ({ ...e, [tabId]: '' }))
    } catch (e) {
      setErrors((x) => ({ ...x, [tabId]: String(e instanceof Error ? e.message : e) }))
    } finally {
      setBusy(false)
    }
  }

  const allDone = tabs.every((t) => done[t.tabId])
  const fired = useRef(false)
  useEffect(() => {
    if (allDone && !fired.current) {
      fired.current = true
      onDone()
    }
  }, [allDone, onDone])

  const rollbackAll = async () => {
    for (const t of tabs.filter((x) => !done[x.tabId])) await decide(t.tabId, false)
  }

  return (
    <Modal title="Transações abertas" onCancel={onCancel} width={560} role="alertdialog">
      <div className="p-5">
        <h2 className="text-base font-semibold">Há transações abertas</h2>
        <p className="mt-2 text-sm text-muted">Decida o que fazer com cada uma antes de fechar o aplicativo.</p>
        <ul className="mt-3 space-y-2">
          {tabs.map((t) => (
            <li key={t.tabId} className="rounded-md border border-line px-3 py-2 text-sm">
              <div className="flex items-center gap-2">
                <span className="min-w-0 flex-1 truncate font-medium">{t.title}</span>
                {done[t.tabId] ? (
                  <span className="text-muted">{done[t.tabId] === 'commit' ? 'Commit feito' : 'Rollback feito'}</span>
                ) : (
                  <>
                    <button disabled={busy} onClick={() => void decide(t.tabId, false)} className={btnBase}>Rollback</button>
                    <button disabled={busy} onClick={() => void decide(t.tabId, true)} className={amberBtn}>Commit</button>
                  </>
                )}
              </div>
              {errors[t.tabId] && <p role="alert" className="mt-1 text-xs text-danger">{errors[t.tabId]}</p>}
            </li>
          ))}
        </ul>
        <div className="mt-5 flex justify-end gap-2">
          <button ref={cancelRef} onClick={onCancel} className={btnBase}>Cancelar</button>
          <button disabled={busy || allDone} onClick={() => void rollbackAll()} className={btnDanger}>Rollback em todas e fechar</button>
        </div>
      </div>
    </Modal>
  )
}

/** Barra não bloqueante acima do editor: o texto altera dados e a aba não tem transação aberta. */
export function TranAdviceBar({ onWrap, onRunAnyway, onNeverAsk }: { onWrap: () => void; onRunAnyway: () => void; onNeverAsk: () => void }) {
  const ghost = 'rounded-md border border-current/30 px-3 py-1 text-sm font-medium hover:bg-black/10'
  return (
    <div role="status" className="flex shrink-0 flex-wrap items-center gap-3 border-b border-line bg-[var(--warn-bg)] px-4 py-2 text-sm text-[var(--warn-fg)]">
      <span className="min-w-0 flex-1">Este comando altera dados e a aba não tem transação aberta. Envolver em transação permite desfazer.</span>
      <button className={ghost} onClick={onWrap}>Envolver em transação</button>
      <button className={ghost} onClick={onRunAnyway}>Executar assim mesmo</button>
      <button className={ghost} onClick={onNeverAsk}>Não perguntar nesta aba</button>
    </div>
  )
}
