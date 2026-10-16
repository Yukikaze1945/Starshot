import { lazy, Suspense, useCallback, useEffect, useState } from 'react'
import { onEvent, request } from './bridge'
import type { Bootstrap, OcrResult } from './types'
import { OcrLab } from './OcrLab'
import './OcrPopup.css'
import { CompactTitleBar, useWindowChrome } from './WindowChrome'
const Dialog = lazy(() => import('./App').then(module => ({ default: module.Dialog })))
const SettingsPanel = lazy(() => import('./SettingsPanel').then(module => ({ default: module.SettingsPanel })))
export function OcrPopupApp() {
  const [boot, setBoot] = useState<Bootstrap | null>(null), [result, setResult] = useState<OcrResult | null>(null), [settings, setSettings] = useState(false), [message, setMessage] = useState<{ text: string; error: boolean } | null>(null), [error, setError] = useState('')
  const notify = useCallback((text: string, error = false) => setMessage({ text, error }), [])
  useWindowChrome(boot)
  const configure = useCallback(() => setSettings(true), [])
  useEffect(() => onEvent<OcrResult>('ocr.result', setResult), [])
  useEffect(() => onEvent('settings.changed', () => { void request<Bootstrap>('app.bootstrap').then(setBoot).catch(error => notify(error.message, true)) }), [notify])
  useEffect(() => { const close = (event: KeyboardEvent) => { if (event.key === 'Escape' && !settings && !document.querySelector('.selection-format')) { event.preventDefault(); void request('window.close') } }; document.addEventListener('keydown', close); return () => document.removeEventListener('keydown', close) }, [settings])
  useEffect(() => { let live = true; void request<Bootstrap>('app.bootstrap').then(value => { if (live) { setBoot(value); return request('app.ready') } }).catch(error => live && setError(error.message)); return () => { live = false } }, [])
  useEffect(() => { if (boot) document.documentElement.dataset.theme = boot.settings.theme === 2 ? 'dark' : boot.settings.theme === 1 ? 'light' : matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light' }, [boot])
  useEffect(() => { if (!message) return; const timer = window.setTimeout(() => setMessage(null), 5000); return () => window.clearTimeout(timer) }, [message])
  if (error) return <><CompactTitleBar/><div className="fatal"><p>{error}</p><button onClick={() => location.reload()}>重新加载</button></div></>
  if (!boot) return <><CompactTitleBar/><div className="loading">正在准备文字窗口…</div></>
  return <><CompactTitleBar/><main className="ocr-popup ocr-with-chrome" inert={settings}><OcrLab boot={boot} result={result} compact configure={configure} notify={notify} update={setBoot}/></main>{settings && <Suspense fallback={<div className="loading">正在打开设置…</div>}><Dialog title="OCR 与翻译设置" className="settings-dialog popup-settings" onClose={() => setSettings(false)}><SettingsPanel initialTab="translation" boot={boot} update={setBoot} notify={notify}/></Dialog></Suspense>}{message && <div role="status" className={`toast ${message.error ? 'error' : ''}`}>{message.text}</div>}</>
}
