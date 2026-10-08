import type { JSONContent } from '@tiptap/core'
import type { Page, CaptureMode } from './types'

export interface TextWorkspace {
  source: JSONContent; translated: JSONContent; target: string; tab: 'source' | 'translation'
}
export interface Workspace {
  version: 1; page: Page; mode: CaptureMode; search: string; filter: string; text?: TextWorkspace
}

// This is only an in-process draft. Never persist clipboard contents or API settings.
let textDraft: TextWorkspace | undefined
let readEditor: (() => TextWorkspace | null) | undefined
export const savedTextWorkspace = () => textDraft
export function registerTextWorkspace(reader: () => TextWorkspace | null) {
  readEditor = reader
  return () => { if (readEditor === reader) { textDraft = reader() ?? textDraft; readEditor = undefined } }
}
export function restoreWorkspace(value: unknown): Workspace | null {
  if (!value || typeof value !== 'object') return null
  const v = value as Partial<Workspace>
  if (v.version !== 1 || !['studio', 'gallery', 'ocr', 'clipboard'].includes(v.page || '')
    || !['region', 'screen', 'long', 'gif', 'ocr', 'copy'].includes(v.mode || '')) return null
  const text = v.text
  textDraft = text?.source?.type === 'doc' && text.translated?.type === 'doc'
    ? { source: structuredClone(text.source), translated: structuredClone(text.translated), target: String(text.target).slice(0, 64), tab: text.tab === 'translation' ? 'translation' : 'source' } : undefined
  return { version: 1, page: v.page!, mode: v.mode!, search: String(v.search || '').slice(0, 1024), filter: String(v.filter || '').slice(0, 32), text: textDraft }
}
export function snapshotWorkspace(value: Omit<Workspace, 'version' | 'text'>): Workspace | null {
  const current = readEditor?.()
  if (readEditor && !current) return null
  if (current) textDraft = current
  return { version: 1, page: value.page, mode: value.mode, search: value.search, filter: value.filter,
    ...(textDraft ? { text: structuredClone(textDraft) } : {}) }
}
declare global { interface Window { starshotWorkspaceSnapshot?: () => Workspace | null } }
