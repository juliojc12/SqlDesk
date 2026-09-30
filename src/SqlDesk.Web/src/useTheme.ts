import { useEffect, useState } from 'react'

export type Theme = 'dark' | 'light'

/** Tema efetivo: `data-theme` no <html> (configuração futura) ou o do sistema. */
export function useTheme(): Theme {
  const read = (): Theme => {
    const forced = document.documentElement.dataset.theme
    if (forced === 'dark' || forced === 'light') return forced
    return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
  }
  const [theme, setTheme] = useState<Theme>(read)
  useEffect(() => {
    const mq = window.matchMedia('(prefers-color-scheme: dark)')
    const update = () => setTheme(read())
    mq.addEventListener('change', update)
    return () => mq.removeEventListener('change', update)
  }, [])
  return theme
}
