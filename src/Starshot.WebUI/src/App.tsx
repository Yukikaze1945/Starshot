import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { Aperture, ArrowDownToLine, ArrowRight, ArrowUpRight, Check, ChevronDown, Clipboard, Copy, Crop, Expand, FileText, FolderOpen, Image, Keyboard, LoaderCircle, Maximize, Pin, Plus, ScanText, Search, Settings2, SlidersHorizontal, Sparkles, Video, X, Zap } from 'lucide-react'
import { isNative, onEvent, request } from './bridge'
import type { Bootstrap, CaptureMode, ClipboardState, Library, Page, Shot, Thumbnail } from './types'
import { detail } from './math'
import { SettingsPanel } from './SettingsPanel'
import { OcrLab } from './OcrLab'
import { restoreWorkspace, snapshotWorkspace } from './workspace'

export const modes: { id: CaptureMode; label: string; caption: string; icon: typeof Crop; hotkey?: number }[] = [
  { id: 'region', label: '区域截图', caption: '框选，标注，然后定格。', icon: Crop, hotkey: 44446 },
  { id: 'screen', label: '整个屏幕', caption: '完整留下眼前的一切。', icon: Maximize, hotkey: 44445 },
  { id: 'long', label: '滚动长图', caption: '框选后按 L，滚动拼接。', icon: Expand },
  { id: 'gif', label: '录制 GIF', caption: '框选后按 G，记录动态。', icon: Video },
  { id: 'ocr', label: '提取文字', caption: '从像素到可以编辑的文字。', icon: ScanText, hotkey: 44448 },
]
const pageNames = { studio: '创作台', gallery: '截图库', ocr: '文字实验室', clipboard: '剪贴板' }
const thumbCache = new Map<string, Thumbnail>()
export const sizeText = (bytes: number) => bytes < 1024 ** 2 ? `${Math.round(bytes / 1024)} KB` : `${(bytes / 1024 ** 2).toFixed(1)} MB`
export const dateText = (value: string) => new Intl.DateTimeFormat('zh-CN', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }).format(new Date(value))

export function ShotThumb({ shot, onLoad }: { shot: Shot; onLoad?: (value: Thumbnail) => void }) {
  const [thumb, setThumb] = useState(thumbCache.get(shot.id))
  const [error, setError] = useState(false)
  const host = useRef<HTMLDivElement>(null)
  useEffect(() => {
    let active = true
    const cancellation = new AbortController()
    setError(false); setThumb(thumbCache.get(shot.id))
    if (thumbCache.has(shot.id)) { onLoad?.(thumbCache.get(shot.id)!); return }
    const observer = new IntersectionObserver(entries => {
      if (!entries[0].isIntersecting) return
      observer.disconnect()
      request<Thumbnail>('library.thumbnail', { id: shot.id }, cancellation.signal).then(value => {
        if (thumbCache.size >= 200) thumbCache.delete(thumbCache.keys().next().value!)
        thumbCache.set(shot.id, value)
        if (active) { setThumb(value); onLoad?.(value) }
      }).catch(() => { if (active) setError(true) })
    }, { rootMargin: '120px' })
    if (host.current) observer.observe(host.current)
    return () => { active = false; observer.disconnect(); cancellation.abort() }
  }, [shot.id, onLoad])
  return <div ref={host} className="shot-thumb">{thumb ? <img src={thumb.src} alt={shot.name} /> : <div className="thumb-placeholder"><Image size={24} /><span>{error ? '无法预览 · 仍可打开原图' : '正在加载'}</span></div>}</div>
}

export function Dialog({ title, children, onClose, className = '' }: { title: string; children: ReactNode; onClose: () => void; className?: string }) {
  const host = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const previous = document.activeElement as HTMLElement
    host.current?.querySelector<HTMLElement>('button,input,select,textarea')?.focus()
    const key = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { e.preventDefault(); onClose() }
      if (e.key !== 'Tab') return
      const items = Array.from(host.current?.querySelectorAll<HTMLElement>('button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),[tabindex="0"]') || [])
      const first = items[0], last = items[items.length - 1]
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last?.focus() }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first?.focus() }
    }
    document.addEventListener('keydown', key)
    return () => { document.removeEventListener('keydown', key); previous?.focus() }
  }, [onClose])
  return <div className="scrim" onMouseDown={e => { if (e.target === e.currentTarget) onClose() }}><div ref={host} role="dialog" aria-modal="true" aria-label={title} className={`dialog ${className}`}><div className="dialog-title"><span>{title}</span><button className="icon-button" aria-label="关闭" onClick={onClose}><X size={20} /></button></div>{children}</div></div>
}

function Viewfinder() {
  return <div className="viewfinder" aria-hidden="true">
    <div className="finder-tag">01 / EVERY PIXEL MATTERS</div>
    <svg className="finder-art" viewBox="0 0 500 350">
      <defs><linearGradient id="land" x1="0" y1="1" x2="1" y2="0"><stop stopColor="#34574c" /><stop offset="1" stopColor="#7cbba2" /></linearGradient><linearGradient id="light"><stop stopColor="#e4f486" /><stop offset="1" stopColor="#b4d548" /></linearGradient><filter id="grain"><feTurbulence type="fractalNoise" baseFrequency=".8" numOctaves="3" seed="1618" stitchTiles="stitch" /><feColorMatrix type="saturate" values="0" /><feComponentTransfer><feFuncA type="linear" slope=".08" /></feComponentTransfer><feBlend in="SourceGraphic" mode="soft-light" /></filter></defs>
      <g filter="url(#grain)"><rect x="78" y="25" width="355" height="277" rx="2" fill="#24392f" /><path d="M78 250 191 67 284 223 344 138 433 268v34H78Z" fill="url(#land)" /><path d="m173 302 86-185 70 185" fill="#102d25" /><path d="m313 302 43-118 77 96v22" fill="#8dba84" /><path d="m77 265 357-136" stroke="#c6db9f" strokeWidth="1" opacity=".3" /><rect x="292" y="56" width="76" height="76" fill="url(#light)" transform="rotate(-11 330 94)" /><path d="m98 292 300-82" stroke="#d4dfb8" strokeWidth="1" opacity=".35" /></g>
      <rect x="56" y="50" width="318" height="231" fill="none" stroke="#e7f59e" strokeWidth="1.5" />
      {[56, 215, 374].flatMap((x,i) => [50,281].map(y => <rect key={`${x}-${y}`} x={x-3} y={y-3} width="6" height="6" fill={i===1 ? '#e7f59e' : '#181b19'} stroke="#e7f59e" />))}
      <path d="M47 161h18M56 152v18M365 161h18M374 152v18" stroke="#e7f59e" />
      <rect x="56" y="25" width="126" height="25" fill="#ddf369" /><text x="67" y="42" fontFamily="Outfit Variable" fontSize="11" fill="#1b211b">318 × 231 / FRAME 01</text>
      <path d="m361 265 13 37 7-12 13-3Z" fill="#f5f4e9" stroke="#18231c" strokeWidth="2" />
      <path d="M60 322h128M275 322h132" stroke="#b1b7a4" strokeWidth="1" opacity=".5" /><text x="202" y="326" fill="#b1b7a4" fontSize="10" fontFamily="Outfit Variable" letterSpacing="2">KEEP IT.</text>
    </svg>
    <div className="finder-note"><span className="light-dot" /> 让光，保留它的层次。<b>HDR READY</b></div>
  </div>
}

export function App() {
  const [boot, setBoot] = useState<Bootstrap | null>(null)
  const [fatal, setFatal] = useState('')
  const [page, setPage] = useState<Page>('studio')
  const [mode, setMode] = useState<CaptureMode>('region')
  const [library, setLibrary] = useState<Library>({ items: [], total: 0, offset: 0 })
  const [loading, setLoading] = useState(true)
  const [search, setSearch] = useState(''), [filter, setFilter] = useState('')
  const [refresh, setRefresh] = useState(0)
  const [toast, setToast] = useState<{ text: string; error: boolean; duration?: number } | null>(null)
  const [busy, setBusy] = useState(false)
  const [settings, setSettings] = useState<string | null>(null)
  const [selected, setSelected] = useState<Shot | null>(null)
  const [palette, setPalette] = useState(false), [commandSearch, setCommandSearch] = useState('')
  const [clip, setClip] = useState<ClipboardState | null>(null)
  const [clipLoading, setClipLoading] = useState(false)
  const workspace = useRef({ page, mode, search, filter, settings, busy })
  workspace.current = { page, mode, search, filter, settings, busy }
  useEffect(() => {
    const snapshot = () => {
      const current = workspace.current
      // Unsaved settings forms stay alive; their secrets never enter a snapshot.
      return current.settings || current.busy ? null : snapshotWorkspace(current)
    }
    window.starshotWorkspaceSnapshot = snapshot
    const off = onEvent('workspace.restore', value => {
      const restored = restoreWorkspace(value)
      if (restored) { setPage(restored.page); setMode(restored.mode); setSearch(restored.search); setFilter(restored.filter) }
    })
    return () => { off(); if (window.starshotWorkspaceSnapshot === snapshot) delete window.starshotWorkspaceSnapshot }
  }, [])
  const notify = useCallback((text: string, error = false) => setToast({ text, error }), [])
  const execute = useCallback(async <T,>(action: () => Promise<T>, success?: string): Promise<T | undefined> => {
    try { const value = await action(); if (success) notify(success); return value }
    catch (e) { notify(e instanceof Error ? e.message : '操作失败。', true) }
  }, [notify])
  useEffect(() => { request<Bootstrap>('app.bootstrap').then(async data => { setBoot(data); await request('app.ready') }).catch(e => setFatal(e.message)) }, [])
  useEffect(() => { document.documentElement.dataset.theme = boot?.settings.theme === 2 ? 'dark' : boot?.settings.theme === 1 ? 'light' : 'system' }, [boot?.settings.theme])
  useEffect(() => {
    if (!toast || toast.duration === 0) return
    const timer = window.setTimeout(() => setToast(null), toast.duration ?? 5500)
    return () => clearTimeout(timer)
  }, [toast])
  useEffect(() => onEvent('library.changed', () => setRefresh(r => r + 1)), [])
  useEffect(() => onEvent<{ text: string; error: boolean; duration: number }>('notice', value => setToast(value)), [])
  useEffect(() => onEvent('settings.changed', () => { void request<Bootstrap>('app.bootstrap').then(setBoot).catch(e => notify(e.message, true)) }), [notify])
  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); setPalette(p => !p) }
    }
    document.addEventListener('keydown', key)
    return () => document.removeEventListener('keydown', key)
  }, [])
  useEffect(() => {
    if (!boot) return
    const cancellation = new AbortController()
    const timer = window.setTimeout(() => {
      setLoading(true)
      request<Library>('library.list', { search: page === 'studio' ? '' : search, format: page === 'studio' ? '' : filter }, cancellation.signal)
        .then(setLibrary).catch(e => { if (!cancellation.signal.aborted) notify(e.message, true) })
        .finally(() => { if (!cancellation.signal.aborted) setLoading(false) })
    }, 180)
    return () => { clearTimeout(timer); cancellation.abort() }
  }, [boot?.version, boot?.settings.screenshotFolder, boot?.settings.subfolders, JSON.stringify(boot?.settings.extraFolders), page, search, filter, refresh, notify])
  const readClip = useCallback(async () => {
    setClipLoading(true)
    try { setClip(await request<ClipboardState>('clipboard.read')) }
    catch (e) { notify((e as Error).message, true) }
    finally { setClipLoading(false) }
  }, [notify])
  useEffect(() => { if (page === 'clipboard') void readClip() }, [page, readClip])
  const capture = useCallback(async (captureMode: CaptureMode) => {
    if (busy) return
    setPalette(false); setBusy(true)
    if (captureMode === 'long' || captureMode === 'gif') notify(captureMode === 'long' ? '框选区域后，按 L 或点击长截图工具。' : '框选区域后，按 G 或点击 GIF 工具。')
    await execute(() => request('capture.begin', { mode: captureMode }))
    setBusy(false)
  }, [busy, execute, notify])
  const updateBoot = useCallback((value: Bootstrap) => setBoot(value), [])
  const closeSettings = useCallback(() => setSettings(null), [])
  const closeShot = useCallback(() => setSelected(null), [])
  const closePalette = useCallback(() => setPalette(false), [])
  const importImages = async () => {
    const images = await execute(() => request<Shot[]>('library.import'))
    if (images?.length) { setLibrary(l => ({ ...l, items: [...images, ...l.items.filter(i => !images.some(x => x.id === i.id))] })); setPage('gallery'); notify(`已打开 ${images.length} 张图片，源文件保持原位。`) }
  }

  if (fatal) return <div className="fatal"><Aperture size={40} /><h1>创作台暂时无法打开</h1><p>{fatal}</p><button onClick={() => location.reload()}>重试</button></div>
  if (!boot) return <div className="app-loading"><Aperture size={38} /><span>STARSHOT</span><p>正在连接原生能力…</p></div>
  const activeMode = modes.find(m => m.id === mode) || modes[0]
  const hotkey = boot.hotkeys.find(k => k.id === activeMode.hotkey)?.text
  const modal = !!settings || !!selected || palette
  const recent = page === 'studio' ? library.items.slice(0, 4) : library.items
  return <>
    <div className="app" data-theme={boot.settings.theme === 2 ? 'dark' : boot.settings.theme === 1 ? 'light' : 'system'} inert={modal}>
      <aside className="rail">
        <button className="brand-mark" aria-label="Starshot 创作台" onClick={() => setPage('studio')}><Aperture size={28} strokeWidth={1.7} /></button>
        <div className="rail-line" />
        {([{ id: 'studio', icon: Crop }, { id: 'gallery', icon: Image }, { id: 'ocr', icon: ScanText }, { id: 'clipboard', icon: Clipboard }] as const).map(item => <button key={item.id} className={`rail-button ${page === item.id ? 'active' : ''}`} aria-label={pageNames[item.id]} aria-current={page === item.id ? 'page' : undefined} onClick={() => setPage(item.id)}><item.icon size={21} /><span className="rail-tooltip">{pageNames[item.id]}</span></button>)}
        <div className="rail-bottom"><button className="rail-button" aria-label="快捷键" onClick={() => setSettings('keys')}><Keyboard size={21} /><span className="rail-tooltip">快捷键</span></button><button className="rail-button" aria-label="设置" onClick={() => setSettings('capture')}><Settings2 size={21} /><span className="rail-tooltip">设置</span></button><div className="rail-version">S / 01</div></div>
      </aside>
      <div className="workspace">
        <header className="topbar"><div className="wordmark">starshot<span> / {pageNames[page]}</span></div><div className="top-actions"><button className="command-trigger" onClick={() => setPalette(true)}><Search size={15} /><span>找功能</span><kbd>Ctrl K</kbd></button><span className={`connection ${isNative ? '' : 'preview'}`}><i />{isNative ? '原生已连接' : '设计预览'}</span><button className="icon-button" onClick={() => void execute(() => request('window.hide'))} aria-label="收起到托盘"><ArrowDownToLine size={17} /></button></div></header>
        <main>
          {page === 'studio' && <div className="studio page-enter">
            <section className="hero">
              <div className="hero-copy"><div className="eyebrow"><span className="small-cross">+</span> A LITTLE TOOL. A LOT OF POSSIBILITIES.</div><h1>Make it<br /><em>a keeper.</em><span className="hero-period">✳</span></h1><p>一张截图，也值得认真对待。<br />留住画面，延伸想法。</p><button className="capture-primary" disabled={busy} onClick={() => void capture(mode)}>{busy ? <LoaderCircle className="spin" size={20} /> : <activeMode.icon size={21} />}<span>{activeMode.label}</span><kbd>{hotkey || (mode === 'long' ? 'L' : 'G')}</kbd><ArrowUpRight size={20} /></button></div>
              <Viewfinder />
            </section>
            <div className="mode-strip" role="group" aria-label="截图方式">{modes.map((item,i) => <button key={item.id} className={mode === item.id ? 'mode-button selected' : 'mode-button'} aria-pressed={mode === item.id} onClick={() => setMode(item.id)}><span className="mode-number">0{i+1}</span><item.icon size={19} /><span>{item.label}</span><span className="mode-dot" /></button>)}</div>
            <div className="mode-caption"><span>{activeMode.caption}</span><button onClick={() => void capture('copy')}>只复制，不保存 <Copy size={13} /></button></div>
            <div className="section-heading"><div><span className="eyebrow">THE COLLECTION</span><h2>最近留下的画面<span>{library.total.toString().padStart(2,'0')}</span></h2></div><button className="text-button" onClick={() => setPage('gallery')}>进入截图库 <ArrowRight size={16} /></button></div>
            {loading && !recent.length ? <div className="loading-line"><LoaderCircle size={18} className="spin" />正在读取截图库…</div> : recent.length ? <div className="shot-grid recent-grid">{recent.map((shot,i) => <button key={shot.id} className="shot-card" onClick={() => setSelected(shot)} style={{ '--tilt': `${(detail(i)-.5)*1.4}deg` } as React.CSSProperties}><ShotThumb shot={shot} /><div className="shot-meta"><span>{shot.name}</span><b>{shot.kind === 'video' ? `${shot.hdr === null || shot.hdr === undefined ? '视频' : shot.hdr ? 'HDR' : 'SDR'} · ${shot.codec || shot.format}` : shot.format}</b></div><div className="shot-detail">{dateText(shot.timestamp)}<span>{sizeText(shot.bytes)}</span></div></button>)}</div> : <div className="empty-collection"><div className="empty-frame"><Crop size={28} /></div><div><h3>你的下一张好截图，从这里开始。</h3><p>按 {boot.hotkeys.find(k => k.id===44446)?.text || 'Alt + Q'}，或者点击上面的截图按钮。</p></div><button className="icon-button" aria-label="导入图片" onClick={() => void importImages()}><Plus size={21} /></button></div>}
            <footer className="studio-footer"><span><Zap size={13} /> 为灵感留一条捷径。</span><button onClick={() => setSettings('hdr')}>HDR / 保留光的层次 <ArrowUpRight size={14} /></button></footer>
          </div>}
          {page === 'gallery' && <div className="gallery page-enter"><div className="page-heading"><div><span className="eyebrow">EVERY FRAME, A LITTLE STORY.</span><h1>画面收藏室<span className="heading-count">{library.total}</span></h1></div><button className="button dark-button" onClick={() => void importImages()}><Plus size={17} />打开图片</button></div><div className="gallery-tools"><label className="search-field"><Search size={17} /><input aria-label="搜索截图" placeholder="搜索文件名…" value={search} onChange={e => setSearch(e.target.value)} />{search && <button aria-label="清空搜索" onClick={() => setSearch('')}><X size={14} /></button>}</label><div className="filter-tabs" role="group" aria-label="格式筛选">{['','png','jpg','avif','jxl','gif','mp4'].map(format => <button key={format} className={filter === format ? 'active' : ''} aria-pressed={filter===format} onClick={() => setFilter(format)}>{format ? format.toUpperCase() : '全部'}</button>)}</div><button className="icon-button" aria-label="打开截图文件夹" onClick={() => void execute(() => request('settings.openFolder'))}><FolderOpen size={18} /></button></div>{loading ? <div className="loading-line"><LoaderCircle className="spin" size={18} />正在读取画面…</div> : !recent.length ? <div className="empty-page"><Image size={46} strokeWidth={1} /><h2>{search || filter ? '没有找到匹配的画面' : '收藏室还是一张白纸'}</h2><p>{search || filter ? '换个关键词，或试试其他格式。' : '截图会自动出现在这里，也可以打开已有图片。'}</p><button className="button" onClick={() => search || filter ? (setSearch(''), setFilter('')) : void capture('region')}>{search || filter ? '清除筛选' : '开始截图'}<ArrowUpRight size={16} /></button></div> : <><div className="shot-grid">{recent.map(shot => <button className="shot-card" key={shot.id} onClick={() => setSelected(shot)}><ShotThumb shot={shot} /><div className="shot-meta"><span>{shot.name}</span><b>{shot.kind === 'video' ? `${shot.hdr === null || shot.hdr === undefined ? '视频' : shot.hdr ? 'HDR' : 'SDR'} · ${shot.codec || shot.format}` : shot.format}</b></div><div className="shot-detail">{dateText(shot.timestamp)}<span>{sizeText(shot.bytes)}</span></div></button>)}</div>{library.items.length < library.total && <button className="button load-more" onClick={() => void execute(async () => { const next = await request<Library>('library.list', { search, format: filter, offset: library.items.length }); setLibrary(l=>({...next, items:[...l.items,...next.items]})) })}>再看一些 <ChevronDown size={16} /></button>}</>}<button className="batch-link" onClick={() => void execute(() => request('utility.batch',{ids:library.items.map(i=>i.id)}))}><SlidersHorizontal size={16} />批量格式转换 <ArrowUpRight size={14} /></button></div>}
          <div hidden={page !== 'ocr'}><OcrLab boot={boot} update={updateBoot} configure={() => setSettings('translation')} notify={notify} /></div>
          {page === 'clipboard' && <div className="clipboard-page page-enter"><div className="page-heading"><div><span className="eyebrow">ALWAYS WITHIN REACH.</span><h1>暂存灵感</h1></div><button className="button" disabled={clipLoading} onClick={() => void readClip()}><Clipboard size={16} />读取剪贴板</button></div>{clipLoading ? <div className="loading-line"><LoaderCircle size={18} className="spin" />正在读取剪贴板…</div> : <><div className="clipboard-current"><div className="section-heading"><h2>当前内容</h2><button className="text-button" disabled={!clip?.image} onClick={() => void execute(() => request('clipboard.pin'), '已贴到屏幕。')}><Pin size={16} />贴到屏幕</button></div>{clip?.image && <img className="clipboard-image" src={clip.image} alt="当前剪贴板图片" />}{clip?.text && <div className="clipboard-text">{clip.text}<button className="icon-button" aria-label="复制文字" onClick={() => void execute(() => request('clipboard.copyText', {text:clip.text}), '已复制文字。')}><Copy size={16} /></button></div>}{!clip?.text && !clip?.image && <div className="empty-page"><Clipboard size={38} strokeWidth={1} /><h3>等待下一次复制</h3><p>图片或文字，随手放在这里。</p></div>}</div><div className="section-heading"><h2>剪贴板历史</h2><span className="muted">{clip?.historyEnabled ? `${clip.items.length} 条最近记录` : '请在 Windows 设置中开启剪贴板历史（Win + V）'}</span></div><div className="clip-history">{clip?.items.map(item=><button key={item.id} onClick={() => void execute(async()=>{await request('clipboard.restore',{id:item.id}); await readClip()},'已恢复到剪贴板。')}><span className="clip-type">{item.image ? <Image size={19}/> : <FileText size={19}/>}</span><span>{item.text || (item.image?'图片内容':'其他内容')}<small>{dateText(item.timestamp)}</small></span><Copy size={16}/></button>)}</div></>}</div>}
        </main>
      </div>
    </div>
    {toast && <div className={`toast ${toast.error?'error':''}`} role={toast.error?'alert':'status'}>{toast.error?<X size={17}/>:<Check size={17}/>}<span>{toast.text}</span><button aria-label="关闭提示" onClick={()=>setToast(null)}><X size={14}/></button></div>}
    {settings && <Dialog title="工作台设置" onClose={closeSettings} className="settings-dialog"><SettingsPanel initialTab={settings} boot={boot} update={updateBoot} notify={notify}/></Dialog>}
    {selected && <Dialog title="画面详情" onClose={closeShot} className="shot-dialog"><div className="preview-canvas"><ShotThumb shot={selected}/><div className="preview-label">{selected.kind === 'video' ? `VIDEO · ${selected.hdr ? 'HDR' : 'SDR'} · ${selected.codec || selected.format}` : 'WEB PREVIEW / SDR'}</div></div><div className="preview-info"><div><h2>{selected.name}</h2><p>{selected.format} · {sizeText(selected.bytes)} · {dateText(selected.timestamp)}</p></div><button className="button dark-button" onClick={()=>void execute(()=>request('library.open',{id:selected.id}))}><Maximize size={17}/>{selected.kind === 'video' ? '用系统播放器打开' : '原始画面预览'}</button></div><div className="preview-actions">{(selected.kind === 'video' ? [{method:'library.copy',label:'复制文件',icon:Copy},{method:'library.reveal',label:'文件位置',icon:FolderOpen}] : [{method:'library.copy',label:'复制图像',icon:Copy},{method:'library.pin',label:'贴到屏幕',icon:Pin},{method:'library.ocr',label:'提取文字',icon:ScanText},{method:'library.reveal',label:'文件位置',icon:FolderOpen}] as const).map(action=><button className="button" key={action.method} onClick={()=>void execute(async()=>{await request(action.method,{id:selected.id}); if(action.method==='library.ocr') setSelected(null)},action.method==='library.copy'?(selected.kind === 'video'?'已复制文件。':'已复制图像。'):undefined)}><action.icon size={17}/>{action.label}</button>)}</div><p className="preview-footnote">{selected.kind === 'video' ? '这里显示伴随图片的缩略图；原始 HDR 视频请用支持该格式的播放器打开。' : '网页显示色调映射后的缩略图。原始 HDR/P3 画面由原生查看器呈现。'}</p></Dialog>}
    {palette && <Dialog title="快速前往" onClose={closePalette} className="command-dialog"><label className="command-input"><Search size={21}/><input autoFocus placeholder="想做什么？" value={commandSearch} onChange={e=>setCommandSearch(e.target.value)}/><kbd>Esc</kbd></label><div className="command-list">{modes.filter(m=>m.label.includes(commandSearch)).map(m=><button key={m.id} onClick={()=>void capture(m.id)}><m.icon size={19}/><span>{m.label}<small>{m.caption}</small></span><ArrowUpRight size={17}/></button>)}{(Object.entries(pageNames) as [Page,string][]).filter(([,name])=>name.includes(commandSearch)).map(([id,name])=><button key={id} onClick={()=>{setPage(id);setPalette(false);setCommandSearch('')}}><ArrowRight size={19}/><span>{name}</span></button>)}<button onClick={()=>{setPalette(false);setSettings('translation')}}><Sparkles size={19}/><span>连接翻译模型</span></button></div></Dialog>}
  </>
}
