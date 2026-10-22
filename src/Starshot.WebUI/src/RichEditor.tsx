import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { EditorContent, type Editor } from '@tiptap/react'
import type { SelectionBookmark, Transaction } from '@tiptap/pm/state'
import { Bold, Italic, Underline, Strikethrough, Heading2, List, ListOrdered, AlignLeft, AlignCenter, AlignRight, Highlighter, RemoveFormatting } from 'lucide-react'
function FormatBubble({ editor }: { editor: Editor }) {
  const [position, setPosition] = useState<{ left: number; top: number } | null>(null), [, refresh] = useState(0)
  const bubble = useRef<HTMLDivElement>(null)
  const selection = useRef<SelectionBookmark | null>(null), interacting = useRef(false)
  useEffect(() => {
    const update = () => {
      if (editor.isDestroyed) return
      if (editor.isFocused && !interacting.current) selection.current = editor.state.selection.empty ? null : editor.state.selection.getBookmark()
      const { from, to, empty } = (interacting.current && selection.current ? selection.current.resolve(editor.state.doc) : editor.state.selection)
      if (!editor.isEditable || empty || (!interacting.current && !editor.isFocused && !bubble.current?.contains(document.activeElement))) { setPosition(null); return }
      const start = editor.view.coordsAtPos(from), end = editor.view.coordsAtPos(to), host = (editor.view.dom.closest('.editor-body') || editor.view.dom).getBoundingClientRect()
      if (end.bottom < host.top || start.top > host.bottom) { setPosition(null); return }
      const width = Math.min(560, window.innerWidth - 26)
      const bubbleHeight = bubble.current?.offsetHeight || 46
      const above = start.top - bubbleHeight - 8
      const desired = above >= host.top + 4 ? above : Math.min(end.bottom, host.bottom) + 8
      // Keep the floating controls inside the editor viewport. A fixed 70px
      // threshold overlapped OCR tabs after integrating the window title bar.
      const top = Math.max(host.top + 2, Math.min(desired, host.bottom - bubbleHeight - 2, window.innerHeight - bubbleHeight - 8))
      setPosition({ left: Math.max(13, Math.min((start.left + end.left) / 2 - width / 2, window.innerWidth - width - 13)), top }); refresh(value => value + 1)
    }
    const selected = () => {
      if (editor.view.hasFocus()) interacting.current = false
      update()
    }
    const focused = () => { interacting.current = false; update() }
    const transaction = ({ transaction }: { transaction: Transaction }) => { if (selection.current) selection.current = selection.current.map(transaction.mapping); update() }
    const outside = (event: Event) => {
      if (event.target instanceof Node && bubble.current?.contains(event.target)) return
      interacting.current = false
      update()
    }
    let blurTimer = 0
    const delayed = () => { window.clearTimeout(blurTimer); blurTimer = window.setTimeout(update, 0) }
    editor.on('selectionUpdate', selected).on('transaction', transaction).on('focus', focused).on('blur', delayed)
    document.addEventListener('pointerdown', outside, true); document.addEventListener('focusin', outside)
    window.addEventListener('resize', update); document.addEventListener('scroll', update, true)
    return () => { window.clearTimeout(blurTimer); editor.off('selectionUpdate', selected).off('transaction', transaction).off('focus', focused).off('blur', delayed); document.removeEventListener('pointerdown', outside, true); document.removeEventListener('focusin', outside); window.removeEventListener('resize', update); document.removeEventListener('scroll', update, true) }
  }, [editor])
  if (!position) return null
  const rememberSelection = () => {
    if (!editor.state.selection.empty) selection.current = editor.state.selection.getBookmark()
    interacting.current = true
  }
  // Native select/color popups can move focus and clear the DOM selection. Restore
  // the mapped editor bookmark, rather than formatting a collapsed caret position.
  const command = () => {
    const chain = editor.chain().focus()
    if (selection.current) { const { from, to } = selection.current.resolve(editor.state.doc); chain.setTextSelection({ from, to }) }
    return chain
  }
  const action = (label: string, icon: React.ReactNode, run: () => void, active = false) => <button title={label} aria-label={label} aria-pressed={active} onMouseDown={event => event.preventDefault()} onClick={run}>{icon}</button>
  return createPortal(<div ref={bubble} className="selection-format" style={position} role="toolbar" aria-label="选中文字格式" onPointerDownCapture={rememberSelection} onFocusCapture={rememberSelection}>
    {action('加粗', <Bold size={16}/>, () => { command().toggleBold().run() }, editor.isActive('bold'))}
    {action('斜体', <Italic size={16}/>, () => { command().toggleItalic().run() }, editor.isActive('italic'))}
    {action('下划线', <Underline size={16}/>, () => { command().toggleUnderline().run() }, editor.isActive('underline'))}
    {action('删除线', <Strikethrough size={16}/>, () => { command().toggleStrike().run() }, editor.isActive('strike'))}
    <select aria-label="字号" value={editor.getAttributes('textStyle').fontSize || '16px'} onChange={event => command().setFontSize(event.target.value).run()}>{[12,14,16,18,20,24,28,32].map(size => <option key={size} value={`${size}px`}>{size}</option>)}</select>
    <input type="color" aria-label="文字颜色" title="文字颜色" value={/^#[0-9a-f]{6}$/i.test(editor.getAttributes('textStyle').color || '') ? editor.getAttributes('textStyle').color : '#242b24'} onChange={event => command().setColor(event.target.value).run()}/>
    {action('高亮', <Highlighter size={16}/>, () => { command().toggleHighlight({ color: '#e3f29e' }).run() }, editor.isActive('highlight'))}
    {action('标题', <Heading2 size={16}/>, () => { command().toggleHeading({ level: 2 }).run() }, editor.isActive('heading'))}
    {action('项目列表', <List size={16}/>, () => { command().toggleBulletList().run() }, editor.isActive('bulletList'))}
    {action('编号列表', <ListOrdered size={16}/>, () => { command().toggleOrderedList().run() }, editor.isActive('orderedList'))}
    {action('左对齐', <AlignLeft size={16}/>, () => { command().setTextAlign('left').run() })}
    {action('居中', <AlignCenter size={16}/>, () => { command().setTextAlign('center').run() })}
    {action('右对齐', <AlignRight size={16}/>, () => { command().setTextAlign('right').run() })}
    {action('清除格式', <RemoveFormatting size={16}/>, () => { command().unsetAllMarks().clearNodes().run() })}
  </div>, document.body)
}
export function RichEditor({ editor, placeholder }: { editor: Editor; placeholder: string }) {
  return <div className="rich-editor"><EditorContent editor={editor}/>{editor.isEmpty && <div className="rich-placeholder">{placeholder}</div>}<FormatBubble editor={editor}/></div>
}
