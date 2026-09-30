import type { JSONContent } from '@tiptap/core'
export type Segment = { id: string; text: string }
export function textDocument(text: string): JSONContent {
  return { type: 'doc', content: text.split(/\r?\n/).map(line => ({ type: 'paragraph', content: line ? [{ type: 'text', text: line }] : [] })) }
}
export function translationSegments(doc: JSONContent): Segment[] {
  const output: Segment[] = []
  const visit = (node: JSONContent) => { if (node.type === 'text' && node.text?.trim()) output.push({ id: `s${output.length}`, text: node.text }); node.content?.forEach(visit) }
  visit(doc); return output
}
export function translatedDocument(doc: JSONContent, values: Segment[]): JSONContent {
  const expected = translationSegments(doc), lookup = new Map(values.map(value => [value.id, value.text]))
  if (lookup.size !== values.length || lookup.size !== expected.length || expected.some(value => !lookup.get(value.id)?.trim())) throw new Error('翻译片段不完整，已保留原文和原有格式。')
  const output = structuredClone(doc); let index = 0
  const visit = (node: JSONContent) => { if (node.type === 'text' && node.text?.trim()) node.text = lookup.get(`s${index++}`)!; node.content?.forEach(visit) }
  visit(output); return output
}
export function paragraphText(text: string) {
  return text.replace(/([A-Za-z])-\r?\n(?=[a-z])/g, '$1').split(/\n\s*\n/).map(paragraph => paragraph.trim().split(/\r?\n/).reduce((all, line) => { const next = line.trim(); return all + (/^(?:[-*•]|\d+[.)])\s/.test(next) ? '\n' : /[a-zA-Z0-9]$/.test(all) && /^[a-zA-Z0-9]/.test(next) ? ' ' : '') + next }, '')).join('\n\n').trim()
}
