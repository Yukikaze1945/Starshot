import { useCallback, useEffect, useRef, useState } from 'react'
import { Check, Copy, Cpu, HardDrive, LoaderCircle, Plug, Power, Unplug, X, Zap } from 'lucide-react'
import { request } from './bridge'
import './LocalTranslationRuntime.css'

interface Runtime {
  loaded:boolean;resident:boolean;gpuEnabled:boolean;activeBackend:string|null;state:string;error:string|null;
  gpuRuntimeAvailable:boolean;apiEnabled:boolean;apiPort:number;apiUrl:string;apiActiveRequests:number;idleSeconds:number
}
export function LocalTranslationRuntime({notify}:{notify:(text:string,error?:boolean)=>void}){
  const [runtime,setRuntime]=useState<Runtime|null>(null),[error,setError]=useState(''),[action,setAction]=useState(''),[port,setPort]=useState(43182)
  const active=useRef<AbortController|null>(null)
  const refresh=useCallback(async()=>{try{setRuntime(await request<Runtime>('models.runtime.status'))}catch(e){setError((e as Error).message)}},[])
  useEffect(()=>{let live=true,pending=false;const poll=async()=>{if(pending)return;pending=true;try{const value=await request<Runtime>('models.runtime.status');if(live)setRuntime(value)}catch(e){if(live)setError((e as Error).message)}finally{pending=false}};void poll();const timer=setInterval(()=>void poll(),1500);return()=>{live=false;clearInterval(timer);active.current?.abort()}},[])
  useEffect(()=>{if(runtime)setPort(runtime.apiPort)},[runtime?.apiPort])
  async function run(method:string,label:string,params:object={}){
    if(active.current)return
    const controller=new AbortController();active.current=controller;setAction(label);setError('')
    try{const value=await request<Runtime|null>(`models.runtime.${method}`,params,controller.signal);if(value)setRuntime(value);if(method==='copyKey')notify('已复制本地 API 密钥。')}
    catch(e){if(!controller.signal.aborted)setError((e as Error).message)}
    finally{if(active.current===controller){active.current=null;setAction('');void refresh()}}
  }
  const working=!!action||['loading','unloading'].includes(runtime?.state||'')
  const status=runtime?.loaded?(runtime.resident?'常驻中':'已加载 · 空闲后释放'):runtime?.state==='loading'?'加载中':runtime?.state==='error'?'需要处理':'未加载'
  return <section className="local-runtime" aria-label="本地翻译运行控制">
    <header><span className={`runtime-indicator ${runtime?.loaded?'ready':''}`}/><div><span className="eyebrow">INDEX / LOCAL ENGINE</span><h3>让翻译随时就绪</h3></div><span className="runtime-state">{status}</span></header>
    <div className="runtime-compute"><div><Cpu size={18}/><span><b>计算方式</b><small>{runtime?.loaded?`当前 ${runtime.activeBackend==='CPU'?'CPU':'GPU · Vulkan'}`:'首次使用时加载模型'}</small></span></div><div className="segmented" role="group" aria-label="离线翻译计算方式"><button aria-pressed={!runtime?.gpuEnabled} disabled={!runtime||working} onClick={()=>void run('gpu','正在切换到 CPU…',{enabled:false})}><Cpu size={14}/>CPU</button><button aria-pressed={!!runtime?.gpuEnabled} disabled={!runtime||working||!runtime.gpuRuntimeAvailable} title={runtime?.gpuRuntimeAvailable?'Vulkan GPU 推理':'GPU 推理组件缺失，请重新运行源码启动脚本'} onClick={()=>void run('gpu','正在切换到 GPU…',{enabled:true})}><Zap size={14}/>GPU</button></div></div>
    <div className="runtime-residency"><div><HardDrive size={18}/><span><b>{runtime?.resident?'保持模型加载':'内存 / 显存常驻'}</b><small>{runtime?.gpuEnabled?'GPU 模式占用显存，也需要部分系统内存。':'CPU 模式使用系统内存。'}{runtime?.resident?'常驻期间不会因空闲自动卸载。':'默认空闲 60 秒释放。'}</small></span></div><button className={`button ${runtime?.resident?'':'dark-button'}`} disabled={!runtime||working} onClick={()=>void run(runtime?.resident?'unload':'load',runtime?.resident?'正在卸载，等待任务收尾…':'正在加载模型…')}>{runtime?.resident?<Unplug size={15}/>:<Power size={15}/>} {runtime?.resident?'卸载模型':'加载并常驻'}</button>{runtime?.loaded&&!runtime.resident&&<button className="text-button" disabled={working} onClick={()=>void run('unload','正在卸载模型…')}>卸载</button>}</div>
    <div className="runtime-api-row"><div><Plug size={18}/><span><b>开启本地 API</b><small>供本机其他软件调用；开启时自动加载并常驻。</small></span></div><button className="runtime-switch" role="switch" aria-label="开启本地翻译 API" aria-checked={!!runtime?.apiEnabled} disabled={!runtime||working||!Number.isInteger(port)||port<1024||port>65535} onClick={()=>void run('api',runtime?.apiEnabled?'正在关闭 API…':'正在开启本地 API…',{enabled:!runtime?.apiEnabled,port})}><span/></button></div>
    <div className="runtime-endpoint"><label>端口<input aria-label="本地翻译 API 端口" type="number" min={1024} max={65535} value={port} disabled={working} onChange={e=>setPort(Number(e.target.value))}/></label><code>{runtime?.apiUrl||'http://127.0.0.1:43182/v1'}</code>{runtime?.apiEnabled&&port!==runtime.apiPort&&<button className="text-button" disabled={working||!Number.isInteger(port)||port<1024||port>65535} onClick={()=>void run('api','正在应用端口…',{enabled:true,port})}>应用端口</button>}</div>
    {runtime?.apiEnabled&&<div className="runtime-api-details"><span><Check size={13}/>仅本机 · Bearer Key · OpenAI 兼容</span><button className="text-button" disabled={working} onClick={()=>void request('clipboard.copyText',{text:runtime.apiUrl}).then(()=>notify('已复制 API 地址。')).catch(e=>notify(e.message,true))}><Copy size={13}/>复制地址</button><button className="text-button" disabled={working} onClick={()=>void run('copyKey','正在复制密钥…')}><Copy size={13}/>复制密钥</button><small>模型名 <code>Index-Translate-2B</code>{runtime.apiActiveRequests?` · ${runtime.apiActiveRequests} 个请求处理中`:''}</small></div>}
    {working&&<div className="runtime-progress" role="status"><LoaderCircle size={14} className="spin"/>{action||'正在准备模型…'}{active.current&&<button className="icon-button" aria-label="取消模型操作" onClick={()=>active.current?.abort()}><X size={13}/></button>}</div>}
    {(error||runtime?.error)&&<p className="runtime-error" role="alert">{error||runtime?.error}</p>}
    <footer>常驻与 API 仅在本次运行生效。关闭 API 保留模型；卸载同时关闭 API 并释放运行资源。</footer>
  </section>
}
