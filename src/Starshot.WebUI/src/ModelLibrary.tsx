import { useCallback, useEffect, useState } from 'react'
import { AlertTriangle, ArrowDownToLine, Check, Cpu, Download, FolderOpen, FolderSearch, Globe2, HardDrive, Sigma, PanelsTopLeft, FileSpreadsheet, Languages, LoaderCircle, Package, ScanText, Scissors, ShieldCheck, Trash2, X } from 'lucide-react'
import { request } from './bridge'
import { OcrModels } from './OcrModels'
import type { Bootstrap } from './types'
import './ModelLibrary.css'
import { LocalTranslationRuntime } from './LocalTranslationRuntime'

interface Model { Id: string; Name: string; Task: string; Bytes: number; Downloaded: number; State: string; Error: string | null; License: string; Source: string; Sha256?: string; Installed: boolean; Warning?:string|null; ActualSha256?:string|null; Directory?:string; ActiveSource?:string|null; Sources?:{Id:string;Label:string;Url:string|null;Note?:string}[] }
interface Catalog { items: Model[]; translationBackend: 'api' | 'local'; localLoaded: boolean; downloadSource?:string; layoutModel?:string }
interface Scan {Items:{Id:string;Path:string;Bytes:number}[];Visited:number;Limited:boolean}
const stateText: Record<string,string> = { notInstalled:'未安装', downloading:'正在下载', importing:'正在导入', validating:'校验中', installed:'已校验', error:'操作失败', canceled:'已取消', corrupt:'无法读取', deleting:'释放后删除' }
const size = (n: number) => n >= 1024 ** 3 ? `${(n / 1024 ** 3).toFixed(2)} GiB` : `${(n / 1024 ** 2).toFixed(1)} MiB`
export function ModelLibrary({ boot, update, notify, embedded = false }: { boot: Bootstrap; update: (b: Bootstrap) => void; notify: (text: string, error?: boolean) => void; embedded?: boolean }) {
  const [catalog,setCatalog] = useState<Catalog|null>(null), [error,setError] = useState(''), [acting,setActing] = useState('')
  const [scan,setScan]=useState<Scan|null>(null),[scanning,setScanning]=useState(false)
  const refresh = useCallback(async () => { try { setCatalog(await request<Catalog>('models.list')); setError('') } catch(e) { setError((e as Error).message) } }, [])
  useEffect(() => { let live=true, pending=false; const poll=async()=>{if(pending)return;pending=true;try{const value=await request<Catalog>('models.list');if(live){setCatalog(value);setError('')}}catch(e){if(live)setError((e as Error).message)}finally{pending=false}};void poll();const timer=setInterval(()=>void poll(),1200);return()=>{live=false;clearInterval(timer)} },[])
  async function action(method: string, id: string, params = {}) { setActing(id);try { await request(`models.${method}`,{id,...params});await refresh() } catch(e){notify((e as Error).message,true)} finally {setActing('')} }
  async function findLocal(chooseFolder=false){setScanning(true);try{setScan(await request<Scan>('models.scan',{chooseFolder}))}catch(e){notify((e as Error).message,true)}finally{setScanning(false)}}
  return <section className={`model-library ${embedded?'embedded':''}`}>
    {!embedded&&<header className="model-library-heading"><div><span className="eyebrow">CAPABILITIES, ON YOUR TERMS.</span><h1>模型管理库<span>按需装入。</span></h1><p>识别、翻译、抠图。需要时加载，文件只留在自己的电脑。</p></div><Package size={48} strokeWidth={1}/></header>}
    <div className="model-library-note"><ShieldCheck size={17}/><span>来源可选 · 哈希不一致时提醒 · 原子安装</span><span>默认按需加载 · 可手动常驻</span></div>
    {error&&<p className="model-error" role="alert">{error}</p>}
    <div className="model-acquisition">
      <div className="model-source-picker"><Globe2 size={18}/><div><b>下载首选来源</b><p>自动优先魔搭，再尝试 HF 镜像与 HF。所选来源未提供该模型时，使用已核实的可用来源。</p></div><select aria-label="模型下载来源" value={catalog?.downloadSource||'auto'} disabled={!!acting} onChange={e=>void action('source','source',{source:e.target.value})}><option value="auto">自动选择 · 国内优先</option><option value="modelscope">魔搭 ModelScope</option><option value="hf-mirror">HF 镜像 · hf-mirror.com</option><option value="huggingface">Hugging Face</option><option value="github">GitHub 原始来源</option></select></div>
      <div className="model-local-tools"><div><HardDrive size={18}/><span><b>模型已经在电脑里？</b><small>扫描常用下载与模型缓存目录，或选择一个文件夹；不会自动加载模型。</small></span></div><button className="button" disabled={scanning||!!acting} onClick={()=>void findLocal()}>{scanning?<LoaderCircle size={15} className="spin"/>:<FolderSearch size={15}/>}扫描本地</button><button className="button" disabled={scanning||!!acting} onClick={()=>void findLocal(true)}><FolderOpen size={15}/>选择文件夹扫描</button></div>
      {scan&&<div className="model-local-results"><div className="model-local-summary"><span>发现 {scan.Items.length} 项 · 已检查 {scan.Visited} 个文件{scan.Limited?' · 已达扫描上限，请缩小文件夹范围':''}</span><button className="icon-button" aria-label="关闭本地检索结果" onClick={()=>setScan(null)}><X size={15}/></button></div>{scan.Items.length===0?<p>没有找到匹配模型。可以选择文件夹扫描，或使用下面的「导入本地」按钮。</p>:scan.Items.map(candidate=><div className="model-local-result" key={candidate.Id+candidate.Path}><HardDrive size={16}/><div><b>{catalog?.items.find(m=>m.Id===candidate.Id)?.Name||(candidate.Id==='ocr-small'?'PP-OCRv6 Small':candidate.Id)}</b><code>{candidate.Path}</code><small>{candidate.Bytes?size(candidate.Bytes)+' · ':''}导入时检查格式与哈希</small></div><button className="button" disabled={!!acting||catalog?.items.some(m=>m.Id===candidate.Id&&['downloading','importing','validating','deleting'].includes(m.State))} onClick={()=>void action('import',candidate.Id,{path:candidate.Path})}>导入副本</button></div>)}</div>}
    </div>
    <div className="model-family-label"><ScanText size={17}/><h2>文字识别</h2><span>Tiny 内置默认 · Small 可选</span></div>
    <OcrModels management selected={boot.settings.ocrModel} onSelected={ocrModel=>update({...boot,settings:{...boot.settings,ocrModel}})} notify={notify}/>
    <div className="model-family-label"><Package size={17}/><h2>离线能力</h2><span>模型在首次实际使用时加载</span></div>
    <div className="model-package-grid">{catalog?.items.map(model=>{
      const busy=['downloading','importing','validating','deleting'].includes(model.State), translation=model.Task==='translation', layout=model.Task==='layout', documentModel=layout||['formula-rapidlatex','table-slanet-plus'].includes(model.Id), Icon=translation?Languages:model.Id==='formula-rapidlatex'?Sigma:layout?PanelsTopLeft:model.Id==='table-slanet-plus'?FileSpreadsheet:Scissors
      return <article className={`model-package ${model.Installed?'installed':''}`} key={model.Id}>
        <div className="model-package-top"><span className="model-package-icon"><Icon size={23}/></span><span className={`model-state ${busy?'working':''}`}>{busy?<LoaderCircle size={12} className="spin"/>:model.Warning?<AlertTriangle size={12}/>:model.Installed?<Check size={12}/>:<ArrowDownToLine size={12}/>} {model.Warning?'可用 · 校验提醒':stateText[model.State]||model.State}</span></div>
        <span className="eyebrow">{translation?'OFFLINE TRANSLATION':documentModel?'DOCUMENT INTELLIGENCE':'FOREGROUND SEGMENTATION'}</span><h3>{model.Name}</h3>
        <p>{layout?({'layout-doclayout-s':'轻量 · 480px 输入，23 类区域检测。适合快速处理简单页面。','layout-doclayout-m':'均衡 · 640px 输入，更适合正文、公式和表格混排。CPU 资源开销高于 S。','layout-doclayout-l':'质量优先 · 640px RT-DETR，适合复杂文档。模型更大，CPU 加载与识别更慢。'} as Record<string,string>)[model.Id]:documentModel?({'formula-rapidlatex':'公式图片转换为可编辑 LaTeX，支持本地预览与 Word 公式复制。包含视觉编码、解码、尺寸预测与字典。','layout-doclayout-s':'23 类文档区域检测：标题、正文、公式、表格及图片。社区 ONNX 导出，保留官方模型类别。','table-slanet-plus':'表格行列与合并单元格识别，结合现有 OCR 填入文字，导出 HTML、Excel 和 TSV。'} as Record<string,string>)[model.Id]:translation?'中英日等多语种离线翻译，接入现有 OCR 和原位翻译。默认 Q5_K_M，无需视觉投影模型。':'轻量前景分割模型。生成原尺寸软边缘蒙版；也可以切换到多边形、套索或魔棒。'}</p>
        <div className="model-package-facts"><span>{size(model.Bytes)}</span><span><Cpu size={12}/> {translation?'CPU / GPU':'CPU'}</span><span>{model.License}</span></div>
        {busy&&<div className="model-progress"><progress max={model.Bytes} value={model.Downloaded}/><span>{size(model.Downloaded)} / {size(model.Bytes)}</span></div>}
        {model.Error&&<p className="model-error" role="alert">{model.Error}</p>}
        {model.Warning&&<p className="model-warning" role="status"><AlertTriangle size={15}/>{model.Warning}</p>}
        <p className="model-source-note">{model.ActiveSource?`当前来源：${model.ActiveSource}`:`可用来源：${model.Sources?.filter(s=>s.Url).map(s=>s.Label).join(' / ')||'原始来源'}`}</p>
        <div className="model-package-actions">
          {busy?<button className="button" onClick={()=>void action('cancel',model.Id)}><X size={15}/>取消下载</button>:!model.Installed?<button className="button dark-button" disabled={acting===model.Id} onClick={()=>void action('download',model.Id)}><Download size={15}/>下载 {translation?'Q5_K_M':'模型'}</button>:<>
            {layout&&<button className="button dark-button" aria-pressed={(catalog.layoutModel||'layout-doclayout-s')===model.Id} disabled={!!acting} onClick={()=>void action('layout.select',model.Id)}>{(catalog.layoutModel||'layout-doclayout-s')===model.Id?<Check size={15}/>:<PanelsTopLeft size={15}/>} {(catalog.layoutModel||'layout-doclayout-s')===model.Id?'当前版面模型':'设为版面模型'}</button>}
            {translation&&<button className="button dark-button" aria-pressed={catalog.translationBackend==='local'} disabled={acting===model.Id} onClick={()=>void action('translation',model.Id,{backend:'local'})}>{catalog.translationBackend==='local'?<Check size={15}/>:<Languages size={15}/>} {catalog.translationBackend==='local'?'正在使用离线翻译':'设为离线翻译'}</button>}
            <button className="icon-button" aria-label={`校验 ${model.Name}`} disabled={!!acting} onClick={()=>void action('verify',model.Id)}><ShieldCheck size={17}/></button>
            <button className="icon-button" aria-label={`删除 ${model.Name}`} disabled={!!acting} onClick={()=>{if(confirm(`删除 ${model.Name}？原图片和翻译文本不会删除。`))void action('delete',model.Id)}}><Trash2 size={16}/></button>
          </>}
          {!busy&&<button className="button" disabled={!!acting} onClick={()=>void action('import',model.Id)}><FolderOpen size={15}/>导入本地</button>}
        </div><small>{layout?'仅加载所选一档 · 首次识别加载 · 空闲 60 秒释放':documentModel?'CPU 按需加载 · 空闲 60 秒释放 · 多文件整体安装':translation?'支持 CPU / Vulkan GPU；默认空闲 60 秒卸载，可手动常驻。':'320px 推理，原尺寸蒙版；每次使用后释放推理会话。'}</small>
      <details className="model-provenance"><summary>下载来源与校验</summary><span>{model.Source}</span>{model.Sources?.map(s=><span key={s.Id}>{s.Label}：{s.Url||s.Note}{s.Url&&s.Note?` · ${s.Note}`:''}</span>)}<code>登记 SHA-256 {model.Sha256||'请查看随程序附带的模型清单'}</code>{model.ActualSha256&&<code>实际 SHA-256 {model.ActualSha256}</code>}{model.Directory&&<code>{model.Directory}</code>}<span>{model.License} · 本地导入仅复制数据文件，删除模型不会删除原文件。</span></details></article>
    })}</div>
    <LocalTranslationRuntime notify={notify}/>
    <div className="model-backend"><div><b>翻译方式</b><p>{catalog?.translationBackend==='local'?'本地 Index-Translate-2B；文本不发送到外网。':'使用设置中已配置的大模型 API。'}</p></div><select aria-label="翻译方式" value={catalog?.translationBackend||'api'} onChange={e=>void action('translation','backend',{backend:e.target.value})}><option value="api">在线 API</option><option value="local" disabled={!catalog?.items.find(m=>m.Id==='index-q5km')?.Installed}>本地 Index-Translate-2B</option></select></div>
  </section>
}
