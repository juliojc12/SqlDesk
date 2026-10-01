import { Component, type ErrorInfo, type ReactNode } from 'react'

interface State {
  error: Error | null
}

/**
 * Última linha de defesa: um erro de renderização não deixa mais a janela em branco. Mostra o que houve, avisa que o que está
 * no banco não foi afetado e oferece recarregar a interface (as abas e o texto são restaurados da sessão salva).
 */
export class ErrorBoundary extends Component<{ children: ReactNode }, State> {
  state: State = { error: null }

  static getDerivedStateFromError(error: Error): State {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Erro de renderização', error, info.componentStack)
  }

  render() {
    if (!this.state.error) return this.props.children
    return (
      <div role="alert" className="flex h-full flex-col items-center justify-center gap-3 bg-app p-8 text-center text-fg">
        <h1 className="text-lg font-semibold">Algo deu errado na interface</h1>
        <p className="max-w-xl text-sm text-muted">
          Ocorreu um erro inesperado ao desenhar a tela. Nada foi alterado no banco de dados por causa dele. Recarregar a interface restaura as
          abas e o texto da última sessão salva; transações abertas continuam abertas no servidor enquanto a conexão existir.
        </p>
        <pre className="max-h-40 max-w-xl select-text overflow-auto rounded-md border border-line bg-input p-3 text-left text-xs">{this.state.error.message}</pre>
        <button className="rounded-md bg-blue-600 px-4 py-2 text-sm text-white hover:bg-blue-700" onClick={() => location.reload()}>
          Recarregar a interface
        </button>
      </div>
    )
  }
}
