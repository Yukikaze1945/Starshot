import { useCallback, useEffect, useRef, useState } from 'react'
import type { JSONContent } from '@tiptap/core'
import { useEditor } from '@tiptap/react'
import StarterKit from '@tiptap/starter-kit'
import { TextStyleKit } from '@tiptap/extension-text-style'
import TextAlign from '@tiptap/extension-text-align'
import Highlight from '@tiptap/extension-highlight'
import { AlignLeft, ArrowRight, Check, Copy, Languages, LoaderCircle, Clipboard, Settings2, Sparkles, Undo2, X } from 'lucide-react'
import { request } from './bridge'
import type { Bootstrap, OcrResult } from './types'
import { RichEditor } from './RichEditor'
import { paragraphText, textDocument, translatedDocument, translationSegments, type Segment } from './richDocument'
import { registerTextWorkspace, savedTextWorkspace } from './workspace'
export const languages = ['简体中文','繁体中文','英语','日语','韩语','法语','德语','西班牙语','葡萄牙语','俄语','意大利语','泰语','越南语','阿拉伯语']
const extensions = () => [StarterKit.configure({ link: { openOnClick: false } }), TextStyleKit, TextAlign.configure({ types: ['heading','paragraph'] }), Highlight.configure({ multicolor: true })]
export function OcrLab({ boot, result = null, configure, notify, compact = false, update }: { boot: Bootstrap; result?: OcrResult | null; configure: () => void; notify: (text: string, error?: boolean) => void; compact?: boolean; update: (boot: Bootstrap) => void }) {
  const restored = useRef(compact ? undefined : savedTextWorkspace())
  const [, refresh] = useState(0), [target, setTarget] = useState(restored.current?.target || boot.settings.targetLanguage), [busy, setBusy] = useState(false), [tab, setTab] = useState<'source'|'translation'>(restored.current?.tab || 'source'), [copied, setCopied] = useState(false)
  const source = useEditor({ extensions: extensions(), content: restored.current?.source || textDocument(''), editorProps: { attributes: { role: 'textbox', 'aria-label': '可编辑的原文', 'aria-multiline': 'true', spellcheck: 'false' } }, onTransaction: () => refresh(value => value + 1) })
  const translated = useEditor({ extensions: extensions(), content: restored.current?.translated || textDocument(''), editorProps: { attributes: { role: 'textbox', 'aria-label': '可编辑的译文', 'aria-multiline': 'true', spellcheck: 'false' } }, onTransaction: () => refresh(value => value + 1) })
  const workspace = useRef({ source, translated, target, tab, busy })
  workspace.current = { source, translated, target, tab, busy }
  useEffect(() => {
    if (compact) return
    return registerTextWorkspace(() => {
      const current = workspace.current
      if (current.busy || !current.source || !current.translated) return null
      return { source: current.source.getJSON(), translated: current.translated.getJSON(), target: current.target, tab: current.tab }
    })
  }, [compact])
  const job = useRef<AbortController | null>(null), handled = useRef(''), currentBoot = useRef(boot)
  currentBoot.current = boot
  const previousLanguage = useRef(boot.settings.targetLanguage)
  useEffect(() => {
    if (previousLanguage.current !== boot.settings.targetLanguage) setTarget(boot.settings.targetLanguage)
    previousLanguage.current = boot.settings.targetLanguage
  }, [boot.settings.targetLanguage])
  const translate = useCallback(async () => {
    if (!source || !translated || source.isEmpty) return
    if (!currentBoot.current.hasApiKey) { configure(); return }
    job.current?.abort(); const controller = new AbortController(); job.current = controller; setBusy(true)
    const document = source.getJSON()
    try {
      const values = await request<Segment[]>('translation.formatted', { segments: translationSegments(document), targetLanguage: target }, controller.signal)
      if (!controller.signal.aborted) { translated.commands.setContent(translatedDocument(document, values)); setTab('translation'); notify('翻译完成，已保留文字格式。') }
    } catch (error) { if (!controller.signal.aborted) notify((error as Error).message, true) }
    finally { if (job.current === controller) { job.current = null; setBusy(false) } }
  }, [source, translated, target, configure, notify])
  useEffect(() => {
    if (!result || !source || !translated || handled.current === result.id) return
    handled.current = result.id; job.current?.abort(); setBusy(false); source.commands.setContent(textDocument(result.text)); translated.commands.setContent(textDocument('')); setTab('source')
    if (result.copyError) notify(result.copyError, true)
    if (result.autoCopied) notify('识别结果已按设置自动复制。')
    if (result.translate) void translate()
  }, [result, source, translated, translate, notify])
  useEffect(() => { source?.setEditable(!busy); translated?.setEditable(!busy) }, [busy, source, translated])
  useEffect(() => () => job.current?.abort(), [])
  if (!source || !translated) return <div className="loading">正在准备文字编辑器…</div>
  const active = tab === 'source' ? source : translated, text = active.getText({ blockSeparator: '\n' })
  const copy = async () => { try { await request('clipboard.copyRichText', { text, html: active.getHTML() }); setCopied(true); window.setTimeout(() => setCopied(false), 1800) } catch (error) { notify((error as Error).message, true) } }
  const paste = async () => { try { const value = await request<string>('clipboard.readText'); if (value) { source.chain().focus().insertContent(textDocument(value).content || []).run(); setTab('source') } else notify('剪贴板中没有文字。') } catch (error) { notify((error as Error).message, true) } }
  const autoCopy = async (value: boolean) => { try { const settings = await request<Bootstrap['settings']>('settings.set', { key: 'autoCopyOcr', value }); update({ ...boot, settings }) } catch (error) { notify((error as Error).message, true) } }
  const hasMarks = (node: JSONContent): boolean => !!node.marks?.length || !!node.content?.some(hasMarks)
  return <div className={`ocr-page page-enter ${compact ? 'compact-ocr' : ''}`}>
    {!compact && <><div className="page-heading"><div><span className="eyebrow">WORDS WITHOUT BORDERS.</span><h1>让文字跨过语言。</h1></div><button className="button" onClick={() => void paste()}><Clipboard size={16}/>粘贴文字</button></div><div className="ocr-intro"><span><Sparkles size={16}/>选中文字，就能排版。译文会保留同样的格式。</span><button className="text-button" onClick={configure}><Settings2 size={15}/>{boot.hasApiKey ? boot.settings.model : '连接翻译模型'}<ArrowRight size={14}/></button></div></>}
    <div className="editor-shell"><div className="editor-top"><div className="editor-tabs"><button className={tab === 'source' ? 'active' : ''} onClick={() => setTab('source')}>原文 <span>{source.getText().length}</span></button><button className={tab === 'translation' ? 'active' : ''} onClick={() => setTab('translation')}>译文{!translated.isEmpty && <i/>}</button></div><div className="format-actions"><button title={hasMarks(source.getJSON()) ? '已有文字格式，请保留段落或先清除格式' : '合并 OCR 换行'} disabled={tab !== 'source' || source.isEmpty || busy || hasMarks(source.getJSON())} onClick={() => source.commands.setContent(textDocument(paragraphText(source.getText({ blockSeparator: '\n' }))))}><AlignLeft size={15}/>智能分段</button>{compact && <button disabled={tab !== 'source' || !result || busy} onClick={() => result && source.commands.setContent(textDocument(result.raw))}>原图断行</button>}<button disabled={busy || !active.can().undo()} aria-label="撤销" onClick={() => active.chain().focus().undo().run()}><Undo2 size={15}/></button></div></div>
      <div className="editor-body"><div className="editor-margin"><span>01</span><span>TEXT<br/>STUDIO</span><span>↳</span></div><div className="rich-editor-host" hidden={tab !== 'source'}><RichEditor editor={source} placeholder={compact ? '识别文字将在这里显示，选中文字可调整格式。' : '写下或粘贴一段文字。\n选中文字，调整格式，再翻译。'}/></div><div className="rich-editor-host" hidden={tab !== 'translation'}><RichEditor editor={translated} placeholder="译文会出现在这里，并保留原文的格式。"/></div></div>
      <div className="editor-bottom"><span><span className="light-dot"/>{busy ? '正在翻译…' : `${text.length} 字符 · ${text ? text.split('\n').length : 0} 行`}{compact && <label className="auto-copy-toggle"><input type="checkbox" checked={boot.settings.autoCopyOcr} onChange={event => void autoCopy(event.target.checked)}/>OCR 自动复制</label>}</span><button className="button dark-button" disabled={!text.trim()} onClick={() => void copy()}>{copied ? <Check size={16}/> : <Copy size={16}/>} {copied ? '已复制' : '复制' + (tab === 'source' ? '原文' : '译文')}</button></div>
    </div><div className="translation-bar">{!compact && <div className="translation-label"><Languages size={24}/><div><b>保留表达的样子</b><small>字号、颜色、列表与强调一并保留</small></div></div>}<label><span className="sr-only">翻译目标语言</span><select value={target} disabled={busy} onChange={event => setTarget(event.target.value)}>{languages.map(language => <option key={language}>{language}</option>)}</select></label>{busy ? <button className="button" onClick={() => job.current?.abort()}><X size={16}/>取消</button> : <button className="button accent-button" disabled={source.isEmpty} onClick={() => void translate()}><Languages size={17}/>{boot.hasApiKey ? '翻译' : '配置翻译'}<ArrowRight size={16}/></button>}{busy && <LoaderCircle className="spin" size={18}/>} {compact && <button className="icon-button" title="OCR 与翻译设置" aria-label="OCR 与翻译设置" onClick={configure}><Settings2 size={17}/></button>}</div>
    {!compact && <div className="ocr-bottom-note">独立的文字与翻译工作台<span>仅在点击翻译时发送文字至配置的 API。</span></div>}
  </div>
}
