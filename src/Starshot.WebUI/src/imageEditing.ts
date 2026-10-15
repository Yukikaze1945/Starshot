export interface Point { x: number; y: number }
export type MaskSpec = { kind: 'polygon'; points: Point[] } | { kind: 'wand'; x: number; y: number; tolerance: number } | { kind: 'smart' }
export interface TextOverlay { id: string; text: string; x: number; y: number; width: number; height: number; fontSize?: number; dark?: boolean }
export interface EditState { mask: MaskSpec | null; overlays: TextOverlay[] }
export interface History { past: EditState[]; current: EditState; future: EditState[] }
export const emptyHistory = (): History => ({ past: [], current: { mask: null, overlays: [] }, future: [] })
export function pushEdit(history: History, next: EditState): History { return { past: [...history.past, history.current].slice(-40), current: structuredClone(next), future: [] } }
export function undoEdit(history: History): History { return history.past.length ? { past: history.past.slice(0,-1), current: history.past.at(-1)!, future: [history.current,...history.future].slice(0,40) } : history }
export function redoEdit(history: History): History { return history.future.length ? { past: [...history.past,history.current].slice(-40), current: history.future[0], future: history.future.slice(1) } : history }
export function imagePoint(clientX: number, clientY: number, rect: { left: number; top: number; width: number; height: number }, width: number, height: number): Point {
  return { x: Math.max(0,Math.min(width-.001,(clientX-rect.left)/Math.max(1,rect.width)*width)), y: Math.max(0,Math.min(height-.001,(clientY-rect.top)/Math.max(1,rect.height)*height)) }
}
export function validPoints(value: unknown): value is Point[] { return Array.isArray(value) && value.length<=20000 && value.every(p=>p && typeof p.x==='number' && typeof p.y==='number' && Number.isFinite(p.x) && Number.isFinite(p.y)) }
export function validMask(value: unknown): value is MaskSpec | null {
  if(value===null)return true
  if(!value || typeof value!=='object')return false
  const v=value as Partial<MaskSpec>
  return v.kind==='smart' || (v.kind==='polygon' && validPoints(v.points) && v.points.length>=3) || (v.kind==='wand' && Number.isFinite(v.x) && Number.isFinite(v.y) && Number.isFinite(v.tolerance))
}
export function readImageDraft(value: unknown): {history: History; points: Point[]} | null {
  if(!value || typeof value!=='object')return null
  const v=value as {version?:number;history?:History;points?:Point[]}
  const validState=(state: EditState)=>state && validMask(state.mask) && Array.isArray(state.overlays) && state.overlays.length<=2048 && state.overlays.every(o=>o && typeof o.id==='string' && typeof o.text==='string' && o.text.length<=10000 && [o.x,o.y,o.width,o.height].every(Number.isFinite) && o.width>0 && o.height>0)
  return v.version===1 && v.history && Array.isArray(v.history.past) && Array.isArray(v.history.future) && v.history.past.length<=40 && v.history.future.length<=40 && validState(v.history.current) && v.history.past.every(validState) && v.history.future.every(validState) && validPoints(v.points) ? {history:v.history,points:v.points} : null
}

// In-process handoff guard: flush unfinished polygon vertices before a new capture replaces the editor.
let replacementGuard: (() => Promise<boolean>) | undefined
export function registerImageReplacementGuard(guard: () => Promise<boolean>) {
  replacementGuard=guard
  return () => { if(replacementGuard===guard)replacementGuard=undefined }
}
export async function prepareImageReplacement() { return replacementGuard ? replacementGuard() : true }
