import React, { Suspense, lazy } from 'react'
import ReactDOM from 'react-dom/client'
import '@fontsource-variable/outfit'
import '@fontsource-variable/newsreader/wght-italic.css'
import '@fontsource-variable/noto-sans-sc'
import './styles.css'
import { OcrPopupApp } from './OcrPopupApp'
const App = lazy(() => import('./App').then(module => ({ default: module.App })))
const FormulaPopupApp = lazy(()=>import('./FormulaPopupApp').then(m=>({default:m.FormulaPopupApp})))
const CaptureEditorApp = lazy(() => import('./CaptureEditorApp').then(module => ({ default: module.CaptureEditorApp })))
const SubtitleApp = lazy(()=>import('./SubtitleApp').then(module=>({default:module.SubtitleApp})))

class ErrorBoundary extends React.Component<React.PropsWithChildren, { error: boolean }> {
  state = { error: false }
  static getDerivedStateFromError() { return { error: true } }
  render() {
    return this.state.error ? <div className="fatal"><h1>界面需要重新加载</h1><p>原生快捷键和托盘仍可使用。</p><button onClick={() => location.reload()}>重新加载</button></div> : this.props.children
  }
}
const surface=new URLSearchParams(location.search).get('surface')
if(surface==='ocr')document.documentElement.classList.add('ocr-surface')
ReactDOM.createRoot(document.getElementById('root')!).render(<ErrorBoundary>{surface === 'ocr' ? <OcrPopupApp/> : <Suspense fallback={<div className="loading">正在准备界面…</div>}>{surface==='subtitles'?<SubtitleApp/>:surface==='formula'?<FormulaPopupApp/>:surface==='capture'?<CaptureEditorApp/>:<App/>}</Suspense>}</ErrorBoundary>)
