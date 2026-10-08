import type { Bootstrap } from './types'

type Response = { type: 'response'; id: string; ok: boolean; result?: unknown; error?: string }
type EventMessage = { type: 'event'; name: string; data: unknown }
type WebView = { postMessage: (data: unknown) => void; addEventListener: (name: 'message', listener: (e: { data: Response | EventMessage }) => void) => void }
declare global { interface Window { chrome?: { webview?: WebView } } }
const webview = window.chrome?.webview
export const isNative = !!webview
const pending = new Map<string, { resolve: (value: unknown) => void; reject: (error: Error) => void }>()
const listeners = new Map<string, Set<(data: unknown) => void>>()
webview?.addEventListener('message', ({ data }) => {
  if (data.type === 'event') { listeners.get(data.name)?.forEach(listener => listener(data.data)); return }
  const request = pending.get(data.id)
  if (!request) return
  pending.delete(data.id)
  if (data.ok) request.resolve(data.result)
  else request.reject(new Error(data.error || '操作失败，请重试。'))
})

export function onEvent<T>(name: string, callback: (data: T) => void) {
  const listener = callback as (data: unknown) => void
  if (!listeners.has(name)) listeners.set(name, new Set())
  listeners.get(name)!.add(listener)
  return () => { listeners.get(name)?.delete(listener) }
}

export function request<T = void>(method: string, params: unknown = {}, signal?: AbortSignal): Promise<T> {
  if (signal?.aborted) return Promise.reject(new DOMException('操作已取消。', 'AbortError'))
  if (!webview) return previewRequest(method, params) as Promise<T>
  const id = crypto.randomUUID()
  return new Promise<T>((resolve, reject) => {
    const timeout = window.setTimeout(() => cancel(new Error('操作超时，请重试。')), 90_000)
    const cleanup = () => { window.clearTimeout(timeout); signal?.removeEventListener('abort', abort) }
    const cancel = (error: Error) => {
      cleanup(); pending.delete(id); reject(error)
      webview.postMessage({ version: 1, id: crypto.randomUUID(), method: 'request.cancel', params: { requestId: id } })
    }
    const abort = () => cancel(new DOMException('操作已取消。', 'AbortError'))
    pending.set(id, { resolve: value => { cleanup(); resolve(value as T) }, reject: error => { cleanup(); reject(error) } })
    signal?.addEventListener('abort', abort, { once: true })
    webview.postMessage({ version: 1, id, method, params })
  })
}

// Browser preview is intentionally empty: it never pretends to capture or translate.
const preview: Bootstrap = {
  version: 'Browser preview', protocol: 1, oneOcrReady: false, hasApiKey: false,
  hotkeys: [44446, 44445, 44447, 44448, 44449, 44450].map((id, i) => ({ id, modifiers: 1, key: [81,87,65,79,50,51][i], text: ['Alt + Q','Alt + W','Alt + A','Alt + O','Ctrl + 2','Ctrl + 3'][i], registered: false, error: false })),
  settings: { screenshotFolder: '图片 / Starshot', extraFolders: [], subfolders: false, subfolderPattern: '{year}/{month}', filenamePattern: '{title}_{timestamp}', regionFilenamePattern: '{title}_region_{timestamp}', autoCopy: true, autoCopyOcr: false, ultraHdr: true, capacityManual: false, capacity: 8, captureMode: 2, videoCodec: 0, sdrFormat: 0, hdrFormat: 0, quality: 2, colorManagement: false, deleteSdrHdr: false, sdrWhite: 0, monitorSource: 0, muteFullscreen: false, ocrEngine: 0, endpoint: 'https://api.openai.com/v1/chat/completions', model: 'gpt-4.1-mini', targetLanguage: '简体中文', theme: 1, language: '', autoUpdate: true, previewUpdates: false, startupHidden: true, highPriority: false },
}
async function previewRequest(method: string, params: unknown) {
  const p = params as Record<string, unknown>
  switch (method) {
    case 'app.bootstrap': return structuredClone(preview)
    case 'app.ready': case 'window.hide': return null
    case 'settings.get': return structuredClone(preview.settings)
    case 'settings.set': Object.assign(preview.settings, { [p.key as string]: p.value }); return structuredClone(preview.settings)
    case 'library.list': return { items: [], total: 0, offset: 0 }
    case 'clipboard.read': return { text: '', image: null, items: [], historyEnabled: false }
    default: throw new Error('这是浏览器设计预览。请在 Starshot 桌面版中使用原生功能。')
  }
}
