import { useCallback, useEffect, useState } from 'react'
import { request, onEvent } from './bridge'
import { DocumentRecognition } from './DocumentRecognition'
import { CompactTitleBar, useWindowChrome } from './WindowChrome'
import type { Bootstrap } from './types'
export function FormulaPopupApp(){
  const [boot,setBoot]=useState<Bootstrap|null>(null),[doc,setDoc]=useState<{src:string;width:number;height:number}|null>(null),[error,setError]=useState('')
  useWindowChrome(boot)
  const close=useCallback(()=>{void request('window.close')},[])
  useEffect(()=>onEvent('capture.closeRequested',close),[close])
  useEffect(()=>{let live=true;void request<Bootstrap>('app.bootstrap').then(async b=>{if(!live)return;setBoot(b);document.documentElement.dataset.theme=b.settings.theme===2?'dark':'light';const d=await request<{src:string;width:number;height:number}>('workspace.pending');if(live){setDoc(d);await request('app.ready')}}).catch(e=>live&&setError(e.message));return()=>{live=false;void request('workspace.close').catch(()=>{})}},[])
  return <><CompactTitleBar title="公式识别"/>{doc?<div className="formula-popup-root" style={{position:'absolute',inset:'var(--compact-titlebar-height,32px) 0 0'}}><DocumentRecognition source={doc} mode="formula" standalone onClose={close}/></div>:<div className="loading">{error||'准备公式原图…'}</div>}</>
}
