import { useEffect, useRef } from 'react'
import { formatDuration } from '../exporter'
import { btnBase, btnPrimary, Modal } from './Modal'

/** Resultado truncado: exportar só o que está carregado ou reexecutar o statement e gravar tudo direto no arquivo. */
export function ExportChoiceDialog({ loaded, hasSort, canRerun, onLoaded, onRerun, onCancel }: {
  loaded: number
  hasSort: boolean
  canRerun: boolean
  onLoaded: () => void
  onRerun: () => void
  onCancel: () => void
}) {
  const ref = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    ref.current?.focus()
  }, [])
  const n = loaded.toLocaleString('pt-BR')
  return (
    <Modal title="Resultado truncado" onCancel={onCancel} width={520} role="alertdialog">
      <div className="p-5">
        <h2 className="text-base font-semibold">O resultado está truncado</h2>
        <p className="mt-2 text-sm text-muted">
          A grade mostra só as primeiras {n} linhas. Você pode exportar só essas linhas ou reexecutar o texto e gravar todas as linhas direto no arquivo, em streaming.
        </p>
        {hasSort && (
          <p className="mt-2 text-sm text-muted">
            A reexecução exporta na ordem original da consulta (a ordenação feita na grade só vale para as linhas carregadas); a ordem das colunas é mantida.
          </p>
        )}
        {!canRerun && <p className="mt-2 text-sm text-danger">Para reexecutar, a aba precisa estar conectada e livre.</p>}
        <div className="mt-5 flex flex-wrap justify-end gap-2">
          <button ref={ref} onClick={onCancel} className={btnBase}>Cancelar</button>
          <button onClick={onLoaded} className={btnBase}>Só as {n} linhas carregadas</button>
          <button disabled={!canRerun} onClick={onRerun} className={`${btnPrimary} disabled:opacity-40`}>Reexecutar e exportar tudo</button>
        </div>
      </div>
    </Modal>
  )
}

export interface ExportJob {
  id: string
  format: 'csv' | 'xlsx'
  /** Nome do arquivo, para mostrar. */
  name: string
  rows: number
  status: 'running' | 'done' | 'error'
  message?: string
  path?: string
  elapsedMs?: number
}

/** Notificação da exportação: progresso com Cancelar e, ao terminar, Abrir arquivo e Abrir pasta. */
export function ExportToast({ job, onCancel, onOpenFile, onShowFolder, onDismiss }: {
  job: ExportJob
  onCancel: () => void
  onOpenFile: () => void
  onShowFolder: () => void
  onDismiss: () => void
}) {
  const ghost = 'rounded-md border border-line px-2.5 py-1 text-sm hover:bg-hover'
  return (
    <div role="status" aria-live="polite" className="fixed bottom-14 right-4 z-40 w-[420px] rounded-xl border border-line bg-surface p-4 text-sm shadow-2xl">
      {job.status === 'running' && (
        <>
          <p className="font-medium">Exportando para {job.format === 'xlsx' ? 'XLSX' : 'CSV'}…</p>
          <p className="mt-1 truncate text-muted" title={job.name}>{job.name}</p>
          <p className="mt-1 text-muted">{job.rows > 0 ? `${job.rows.toLocaleString('pt-BR')} linhas gravadas` : 'Preparando…'}</p>
          <div className="mt-3 flex justify-end"><button className={ghost} onClick={onCancel}>Cancelar</button></div>
        </>
      )}
      {job.status === 'done' && (
        <>
          <p className="font-medium">Exportação concluída</p>
          <p className="mt-1 truncate text-muted" title={job.path}>{job.name}</p>
          <p className="mt-1 text-muted">{job.rows.toLocaleString('pt-BR')} {job.rows === 1 ? 'linha' : 'linhas'} em {formatDuration(job.elapsedMs ?? 0)}</p>
          <div className="mt-3 flex justify-end gap-2">
            <button className={ghost} onClick={onShowFolder}>Abrir pasta</button>
            <button className={ghost} onClick={onOpenFile}>Abrir arquivo</button>
            <button className={ghost} aria-label="Fechar" onClick={onDismiss}>Fechar</button>
          </div>
        </>
      )}
      {job.status === 'error' && (
        <>
          <p role="alert" className="font-medium text-danger">Não foi possível exportar</p>
          <p className="mt-1 select-text text-muted">{job.message}</p>
          <div className="mt-3 flex justify-end"><button className={ghost} onClick={onDismiss}>Fechar</button></div>
        </>
      )}
    </div>
  )
}
