import { useCallback, useEffect, useState } from 'react'
import { ArrowDownToLine, Check, Cpu, Download, Languages, LoaderCircle, Package, ScanText, Scissors, ShieldCheck, Trash2, X } from 'lucide-react'
import { request } from './bridge'
import { OcrModels } from './OcrModels'
import type { Bootstrap } from './types'
import './ModelLibrary.css'

interface Model { Id: string; Name: string; Task: string; Bytes: number; Downloaded: number; State: string; Error: string | null; License: string; Source: string; Sha256?: string; Installed: boolean }
interface Catalog { items: Model[]; translationBackend: 'api' | 'local'; localLoaded: boolean }
const stateText: Record<string,string> = { notInstalled:'未安装', downloading:'正在下载', validating:'校验中', installed:'已校验', error:'下载失败', canceled:'已取消', corrupt:'文件损坏', deleting:'释放后删除' }
const size = (n: number) => n >= 1024 ** 3 ? `${(n / 1024 ** 3).toFixed(2)} GiB` : `${(n / 1024 ** 2).toFixed(1)} MiB`
export function ModelLibrary({ boot, update, notify, embedded = false }: { boot: Bootstrap; update: (b: Bootstrap) => void; notify: (text: string, error?: boolean) => void; embedded?: boolean }) {
  const [catalog,setCatalog] = useState<Catalog|null>(null), [error,setError] = useState(''), [acting,setActing] = useState('')
  const refresh = useCallback(async () => { try { setCatalog(await request<Catalog>('models.list')); setError('') } catch(e) { setError((e as Error).message) } }, [])
  useEffect(() => { let live=true, pending=false; const poll=async()=>{if(pending)return;pending=true;try{const value=await request<Catalog>('models.list');if(live){setCatalog(value);setError('')}}catch(e){if(live)setError((e as Error).message)}finally{pending=false}};void poll();const timer=setInterval(()=>void poll(),1200);return()=>{live=false;clearInterval(timer)} },[])
  async function action(method: string, id: string, params = {}) { setActing(id);try { await request(`models.${method}`,{id,...params});await refresh() } catch(e){notify((e as Error).message,true)} finally {setActing('')} }
  return <section className={`model-library ${embedded?'embedded':''}`}>
    {!embedded&&<header className="model-library-heading"><div><span className="eyebrow">CAPABILITIES, ON YOUR TERMS.</span><h1>模型管理库<span>按需装入。</span></h1><p>识别、翻译、抠图。需要时加载，文件只留在自己的电脑。</p></div><Package size={48} strokeWidth={1}/></header>}
    <div className="model-library-note"><ShieldCheck size={17}/><span>固定来源 · SHA-256 校验 · 可取消下载 · 原子安装</span><span>CPU 推理 / 不常驻 GPU</span></div>
    {error&&<p className="model-error" role="alert">{error}</p>}
    <div className="model-family-label"><ScanText size={17}/><h2>文字识别</h2><span>Tiny 内置默认 · Small 可选</span></div>
    <OcrModels management selected={boot.settings.ocrModel} onSelected={ocrModel=>update({...boot,settings:{...boot.settings,ocrModel}})} notify={notify}/>
    <div className="model-family-label"><Package size={17}/><h2>离线能力</h2><span>模型在首次实际使用时加载</span></div>
    <div className="model-package-grid">{catalog?.items.map(model=>{
      const busy=['downloading','validating','deleting'].includes(model.State), translation=model.Task==='translation', Icon=translation?Languages:Scissors
      return <article className={`model-package ${model.Installed?'installed':''}`} key={model.Id}>
        <div className="model-package-top"><span className="model-package-icon"><Icon size={23}/></span><span className={`model-state ${busy?'working':''}`}>{busy?<LoaderCircle size={12} className="spin"/>:model.Installed?<Check size={12}/>:<ArrowDownToLine size={12}/>} {stateText[model.State]||model.State}</span></div>
        <span className="eyebrow">{translation?'OFFLINE TRANSLATION':'FOREGROUND SEGMENTATION'}</span><h3>{model.Name}</h3>
        <p>{translation?'中英日等多语种离线翻译，接入现有 OCR 和原位翻译。默认 Q5_K_M，无需视觉投影模型。':'轻量前景分割模型。生成原尺寸软边缘蒙版；也可以切换到多边形、套索或魔棒。'}</p>
        <div className="model-package-facts"><span>{size(model.Bytes)}</span><span><Cpu size={12}/> CPU</span><span>{model.License}</span></div>
        {busy&&<div className="model-progress"><progress max={model.Bytes} value={model.Downloaded}/><span>{size(model.Downloaded)} / {size(model.Bytes)}</span></div>}
        {model.Error&&<p className="model-error" role="alert">{model.Error}</p>}
        <div className="model-package-actions">
          {busy?<button className="button" onClick={()=>void action('cancel',model.Id)}><X size={15}/>取消下载</button>:!model.Installed?<button className="button dark-button" disabled={acting===model.Id} onClick={()=>void action('download',model.Id)}><Download size={15}/>下载 {translation?'Q5_K_M':'模型'}</button>:<>
            {translation&&<button className="button dark-button" aria-pressed={catalog.translationBackend==='local'} disabled={acting===model.Id} onClick={()=>void action('translation',model.Id,{backend:'local'})}>{catalog.translationBackend==='local'?<Check size={15}/>:<Languages size={15}/>} {catalog.translationBackend==='local'?'正在使用离线翻译':'设为离线翻译'}</button>}
            <button className="icon-button" aria-label={`校验 ${model.Name}`} disabled={!!acting} onClick={()=>void action('verify',model.Id)}><ShieldCheck size={17}/></button>
            <button className="icon-button" aria-label={`删除 ${model.Name}`} disabled={!!acting} onClick={()=>{if(confirm(`删除 ${model.Name}？原图片和翻译文本不会删除。`))void action('delete',model.Id)}}><Trash2 size={16}/></button>
          </>}
        </div><small>{translation?'约需 2–3 GiB 系统内存；空闲 60 秒自动卸载。':'320px 推理，原尺寸蒙版；每次使用后释放推理会话。'}</small>
      <details className="model-provenance"><summary>下载来源与校验</summary><span>{model.Source}</span><code>SHA-256 {model.Sha256||'请查看随程序附带的模型清单'}</code><span>{model.License}</span></details></article>
    })}</div>
    <div className="model-backend"><div><b>翻译方式</b><p>{catalog?.translationBackend==='local'?'本地 Index-Translate-2B；文本不发送到外网。':'使用设置中已配置的大模型 API。'}</p></div><select aria-label="翻译方式" value={catalog?.translationBackend||'api'} onChange={e=>void action('translation','backend',{backend:e.target.value})}><option value="api">在线 API</option><option value="local" disabled={!catalog?.items.find(m=>m.Id==='index-q5km')?.Installed}>本地 Index-Translate-2B</option></select>{catalog?.localLoaded&&<button className="button" onClick={()=>void action('unload','runtime')}>释放模型内存</button>}</div>
  </section>
}
