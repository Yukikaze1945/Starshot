import { useEffect, useRef, useState } from 'react'
import { Check, Download, LoaderCircle, Package, ShieldCheck, Trash2, X } from 'lucide-react'
import { request } from './bridge'
import type { OcrModelsStatus, Settings } from './types'

export function modelBusy(status: OcrModelsStatus | null) {
  return !!status && ['downloading', 'validating', 'deleting'].includes(status.state)
}
export function modelProgress(status: OcrModelsStatus | null) {
  return status ? Math.max(0, Math.min(100, status.downloadedBytes / Math.max(1, status.totalBytes) * 100)) : 0
}
const labels: Record<OcrModelsStatus['state'], string> = {
  notInstalled: '尚未安装', downloading: '下载中', validating: '校验与安装中', installed: '已校验 · 可使用',
  corrupt: '文件校验失败', canceled: '已取消', error: '下载失败', deleting: '等待 OCR 结束并删除',
}
type Props = { selected: Settings['ocrModel']; onSelected: (model: Settings['ocrModel']) => void;
  notify: (text: string, error?: boolean) => void; management?: boolean; onManage?: () => void }

export function OcrModels({ selected, onSelected, notify, management = false, onManage }: Props) {
  const [status, setStatus] = useState<OcrModelsStatus | null>(null)
  const [acting, setActing] = useState(false)
  const [error, setError] = useState('')
  const current = useRef({ onSelected, notify, selected }); current.current = { onSelected, notify, selected }
  const mounted = useRef(false)
  const polling = useRef(false)
  const revision = useRef(0)
  const accept = (next: OcrModelsStatus) => {
    if (!mounted.current) return
    setStatus(next); setError('')
    if (next.selected !== current.current.selected) current.current.onSelected(next.selected)
  }
  useEffect(() => {
    mounted.current = true
    const poll = async () => {
      if (polling.current) return
      polling.current = true
      const version = revision.current
      try { const next = await request<OcrModelsStatus>('ocr.models.status'); if (version === revision.current) accept(next) }
      catch (e) { if (mounted.current) setError((e as Error).message) }
      finally { polling.current = false }
    }
    void poll(); const timer = setInterval(() => void poll(), 1000)
    return () => { mounted.current = false; clearInterval(timer) }
  }, [])
  const action = async (method: string, params = {}) => {
    revision.current++
    setActing(true)
    try {
      const next = await request<OcrModelsStatus>(`ocr.models.${method}`, params)
      accept(next)
      if (method === 'select') notify(`已选择 PP-OCRv6 ${next.selected === 'small' ? 'Small' : 'Tiny'}，下次识别时加载。`)
      if (method === 'delete') notify('Small 已删除，继续使用内置 Tiny。')
      if (method === 'status') notify(next.installed ? 'Small 安装文件 SHA-256 校验通过。' : next.error || 'Small 尚未安装。', !next.installed)
    } catch (e) {
      current.current.notify((e as Error).message, true)
      try { accept(await request<OcrModelsStatus>('ocr.models.status')) } catch { /* next poll retries */ }
    } finally { if (mounted.current) setActing(false) }
  }
  const busy = modelBusy(status)
  if (!management) return <div className="ocr-model-picker">
    <select aria-label="OCR 模型" value={selected} disabled={acting || !status || busy}
      onChange={e => void action('select', { model: e.target.value })}>
      <option value="tiny">PP-OCRv6 Tiny · 内置默认</option>
      <option value="small" disabled={!status?.installed}>PP-OCRv6 Small{!status?.installed ? ' · 需要下载' : ''}</option>
    </select>
    <button className="text-button" onClick={onManage}><Package size={14}/>管理模型</button>
    {error && <small className="error-text" role="status">{error}</small>}
  </div>
  return <div className="ocr-models">
    <article className="ocr-model-card">
      <div className="ocr-model-heading"><span className="eyebrow">BUILT IN / DEFAULT</span><span className="ocr-model-badge">内置</span></div>
      <h3>PP-OCRv6 Tiny</h3><p>轻巧的默认识别模型。无需下载，首次识别时才加载。</p>
      <div className="ocr-model-actions"><button className="button" disabled={acting || busy || selected === 'tiny'} onClick={() => void action('select', { model: 'tiny' })}><Check size={15}/>{selected === 'tiny' ? '正在使用' : '使用 Tiny'}</button></div>
    </article>
    <article className="ocr-model-card">
      <div className="ocr-model-heading"><span className="eyebrow">OPTIONAL / MORE DETAIL</span><span className="ocr-model-badge">DLC · 29.67 MiB</span></div>
      <h3>PP-OCRv6 Small <small>1.0.0</small></h3>
      <p>为复杂文字提供另一种选择。仅下载官方 DET、REC 与字典，复用内置方向分类器；切换后在下一次识别时加载。</p>
      <div className="ocr-model-state" aria-live="polite">{busy ? <LoaderCircle size={15} className="spin"/> : <ShieldCheck size={15}/>}{status ? labels[status.state] : '检查安装状态…'}{selected === 'small' && status?.installed && <b>正在使用</b>}</div>
      {busy && <div className="ocr-download"><progress aria-label="Small 模型下载进度" max={100} value={modelProgress(status)}/><span>{((status?.downloadedBytes || 0)/1048576).toFixed(2)} / 29.67 MiB · {modelProgress(status).toFixed(0)}%</span></div>}
      {(error || status?.error) && <p className="error-text" role="status">{error || status?.error}</p>}
      <div className="ocr-model-actions">
        {busy ? <button className="button" disabled={acting || status?.state === 'deleting'} onClick={() => void action('cancel')}><X size={15}/>取消下载</button> : <>
          {!status?.installed && <button className="button dark-button" disabled={acting || !status} onClick={() => void action('download')}><Download size={15}/>{status?.state === 'error' || status?.state === 'canceled' ? '重新下载' : '下载 Small'}</button>}
          {status?.installed && <><button className="button dark-button" disabled={acting || selected === 'small'} onClick={() => void action('select', { model: 'small' })}><Check size={15}/>{selected === 'small' ? '正在使用' : '使用 Small'}</button><button className="button" disabled={acting} onClick={() => void action('status', { verify: true })}><ShieldCheck size={15}/>校验</button></>}
          {(status?.installed || status?.state === 'corrupt') && <button className="button" disabled={acting} onClick={() => void action('delete')}><Trash2 size={15}/>删除</button>}
        </>}
      </div>
      <footer><button className="text-button" onClick={() => void request('ocr.models.source').catch(e => notify((e as Error).message, true))}>官方来源 ↗</button><span>Apache-2.0 · SHA-256 校验</span></footer>
      {status?.directory && <details><summary>模型存储位置</summary><code>{status.directory}</code></details>}
    </article>
    <div className="settings-note">只保留当前使用的一套 OCR 引擎。下载失败、资源损坏或删除模型时回到 Tiny；截图与 HDR 处理流程保持原样。</div>
  </div>
}
