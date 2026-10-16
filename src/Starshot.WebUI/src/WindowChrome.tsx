import { useEffect } from 'react'
import { Aperture } from 'lucide-react'
import { onEvent } from './bridge'
import type { Bootstrap, ChromeState } from './types'
import './WindowChrome.css'

function applyChrome(chrome: ChromeState) {
  const root = document.documentElement
  root.classList.toggle('native-chrome', chrome.enabled)
  root.dataset.windowActive = String(chrome.active)
  root.dataset.windowMaximized = String(chrome.maximized)
  root.style.setProperty('--caption-inset', `${Math.max(0, Math.min(512, Number.isFinite(chrome.inset) ? chrome.inset : 138))}px`)
}

export function useWindowChrome(boot: Bootstrap | null) {
  useEffect(() => onEvent<ChromeState>('window.chrome', applyChrome), [])
  useEffect(() => { if (boot?.chrome) applyChrome(boot.chrome) }, [boot?.chrome])
}

// Windows paints the real caption controls over the reserved area at the right.
// The remaining header is a WebView2 drag region, not a JavaScript mouse loop.
export function CompactTitleBar({ title = '文字识别' }: { title?: string }) {
  return <header className="compact-titlebar"><Aperture size={15} strokeWidth={1.7}/><span>starshot</span><i>/</i><span className="compact-title">{title}</span></header>
}
