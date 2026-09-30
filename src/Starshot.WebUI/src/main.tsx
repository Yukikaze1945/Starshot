import React from 'react'
import ReactDOM from 'react-dom/client'
import '@fontsource-variable/outfit'
import '@fontsource-variable/newsreader/wght-italic.css'
import '@fontsource-variable/noto-sans-sc'
import './styles.css'
import { App } from './App'
import { OcrPopupApp } from './OcrPopupApp'

class ErrorBoundary extends React.Component<React.PropsWithChildren, { error: boolean }> {
  state = { error: false }
  static getDerivedStateFromError() { return { error: true } }
  render() {
    return this.state.error ? <div className="fatal"><h1>界面需要重新加载</h1><p>原生快捷键和托盘仍可使用。</p><button onClick={() => location.reload()}>重新加载</button></div> : this.props.children
  }
}
ReactDOM.createRoot(document.getElementById('root')!).render(<ErrorBoundary>{new URLSearchParams(location.search).get('surface') === 'ocr' ? <OcrPopupApp/> : <App/>}</ErrorBoundary>)
