import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { EditorContent, type Editor } from '@tiptap/react'
import { Bold, Italic, Underline, Strikethrough, Heading2, List, ListOrdered, AlignLeft, AlignCenter, AlignRight, Highlighter, RemoveFormatting } from 'lucide-react'
function FormatBubble({ editor }: { editor: Editor }) {
  const [position, setPosition] = useState<{ left: number; top: number } | null>(null), [, refresh] = useState(0)
  const bubble = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const update = () => {
      if (editor.isDestroyed) return
      const { from, to, empty } = editor.state.selection
      if (!editor.isEditable || empty || (!editor.isFocused && !bubble.current?.contains(document.activeElement))) { setPosition(null); return }
      const start = editor.view.coordsAtPos(from), end = editor.view.coordsAtPos(to), host = (editor.view.dom.closest('.editor-body') || editor.view.dom).getBoundingClientRect()
      if (end.bottom < host.top || start.top > host.bottom) { setPosition(null); return }
      const width = Math.min(560, window.innerWidth - 26)
      const anchor = Math.max(start.top, host.top)
      setPosition({ left: Math.max(13, Math.min((start.left + end.left) / 2 - width / 2, window.innerWidth - width - 13)), top: Math.max(12, Math.min(anchor >= 70 ? anchor - 58 : Math.min(start.bottom, host.bottom) + 9, window.innerHeight - 64)) }); refresh(value => value + 1)
    }
    const delayed = () => window.setTimeout(update, 0)
    editor.on('selectionUpdate', update).on('transaction', update).on('focus', update).on('blur', delayed)
    window.addEventListener('resize', update); document.addEventListener('scroll', update, true)
    return () => { editor.off('selectionUpdate', update).off('transaction', update).off('focus', update).off('blur', delayed); window.removeEventListener('resize', update); document.removeEventListener('scroll', update, true) }
  }, [editor])
  if (!position) return null
  const action = (label: string, icon: React.ReactNode, run: () => void, active = false) => <button title={label} aria-label={label} aria-pressed={active} onMouseDown={event => event.preventDefault()} onClick={run}>{icon}</button>
  return createPortal(<div ref={bubble} className="selection-format" style={position} role="toolbar" aria-label="选中文字格式">
    {action('加粗', <Bold size={16}/>, () => { editor.chain().focus().toggleBold().run() }, editor.isActive('bold'))}
    {action('斜体', <Italic size={16}/>, () => { editor.chain().focus().toggleItalic().run() }, editor.isActive('italic'))}
    {action('下划线', <Underline size={16}/>, () => { editor.chain().focus().toggleUnderline().run() }, editor.isActive('underline'))}
    {action('删除线', <Strikethrough size={16}/>, () => { editor.chain().focus().toggleStrike().run() }, editor.isActive('strike'))}
    <select aria-label="字号" value={editor.getAttributes('textStyle').fontSize || '16px'} onChange={event => editor.chain().focus().setFontSize(event.target.value).run()}>{[12,14,16,18,20,24,28,32].map(size => <option key={size} value={`${size}px`}>{size}</option>)}</select>
    <input type="color" aria-label="文字颜色" title="文字颜色" value={/^#[0-9a-f]{6}$/i.test(editor.getAttributes('textStyle').color || '') ? editor.getAttributes('textStyle').color : '#242b24'} onChange={event => editor.chain().focus().setColor(event.target.value).run()}/>
    {action('高亮', <Highlighter size={16}/>, () => { editor.chain().focus().toggleHighlight({ color: '#e3f29e' }).run() }, editor.isActive('highlight'))}
    {action('标题', <Heading2 size={16}/>, () => { editor.chain().focus().toggleHeading({ level: 2 }).run() }, editor.isActive('heading'))}
    {action('项目列表', <List size={16}/>, () => { editor.chain().focus().toggleBulletList().run() }, editor.isActive('bulletList'))}
    {action('编号列表', <ListOrdered size={16}/>, () => { editor.chain().focus().toggleOrderedList().run() }, editor.isActive('orderedList'))}
    {action('左对齐', <AlignLeft size={16}/>, () => { editor.chain().focus().setTextAlign('left').run() })}
    {action('居中', <AlignCenter size={16}/>, () => { editor.chain().focus().setTextAlign('center').run() })}
    {action('右对齐', <AlignRight size={16}/>, () => { editor.chain().focus().setTextAlign('right').run() })}
    {action('清除格式', <RemoveFormatting size={16}/>, () => { editor.chain().focus().unsetAllMarks().clearNodes().run() })}
  </div>, document.body)
}
export function RichEditor({ editor, placeholder }: { editor: Editor; placeholder: string }) {
  return <div className="rich-editor"><EditorContent editor={editor}/>{editor.isEmpty && <div className="rich-placeholder">{placeholder}</div>}<FormatBubble editor={editor}/></div>
}
