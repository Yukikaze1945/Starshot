import { lazy, Suspense, useCallback, useEffect, useState } from 'react'
import { request, onEvent } from './bridge'
import type { Bootstrap } from './types'
import { ImageWorkspace } from './ImageWorkspace'
import { useWindowChrome } from './WindowChrome'
import './CaptureEditor.css'

const Dialog=lazy(()=>import('./App').then(module=>({default:module.Dialog})))
const SettingsPanel=lazy(()=>import('./SettingsPanel').then(module=>({default:module.SettingsPanel})))

// A native window owns exactly one frozen selection. No library scan, gallery route or global broker.
export function CaptureEditorApp(){
  const [boot,setBoot]=useState<Bootstrap|null>(null),[error,setError]=useState(''),[settings,setSettings]=useState(false)
  const [message,setMessage]=useState<{text:string;error:boolean}|null>(null)
  const notify=useCallback((text:string,error=false)=>setMessage({text,error}),[])
  const configure=useCallback(()=>setSettings(true),[])
  useWindowChrome(boot)
  useEffect(()=>{let live=true;void request<Bootstrap>('app.bootstrap').then(value=>{if(live){setBoot(value);return request('app.ready')}}).catch(e=>live&&setError(e.message));return()=>{live=false}},[])
  useEffect(()=>onEvent('settings.changed',()=>{void request<Bootstrap>('app.bootstrap').then(setBoot).catch(e=>notify(e.message,true))}),[notify])
  useEffect(()=>{if(boot)document.documentElement.dataset.theme=boot.settings.theme===2?'dark':boot.settings.theme===1?'light':'system'},[boot])
  useEffect(()=>{if(!message)return;const timer=setTimeout(()=>setMessage(null),5000);return()=>clearTimeout(timer)},[message])
  if(error)return <div className="fatal"><p>{error}</p><button onClick={()=>location.reload()}>重新加载</button></div>
  if(!boot)return <div className="loading">正在准备当前选区…</div>
  return <><div inert={settings}><ImageWorkspace id="capture:selection" shots={[]} capture initialTarget={boot.settings.targetLanguage||'简体中文'} configure={configure} notify={notify} onNavigate={()=>{}} onClose={()=>{void request('window.close')}}/></div>{settings&&<Suspense fallback={<div className="loading">正在打开翻译设置…</div>}><Dialog title="翻译设置" className="settings-dialog" onClose={()=>setSettings(false)}><SettingsPanel initialTab="translation" boot={boot} update={setBoot} notify={notify}/></Dialog></Suspense>}{message&&<div role="status" className={`toast ${message.error?'error':''}`}>{message.text}</div>}</>
}
