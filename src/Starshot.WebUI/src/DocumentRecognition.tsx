import { useEffect, useRef, useState } from 'react'
import { AlertTriangle, Check, Copy, Download, Eye, EyeOff, FileSpreadsheet, ZoomIn, ZoomOut, LoaderCircle, RefreshCw, Sigma, X } from 'lucide-react'
import { request } from './bridge'
import './DocumentRecognition.css'

interface Box {X:number;Y:number;Width:number;Height:number}
interface Formula {Latex:string;Complete:boolean;Tokens:number;ElapsedMs:number}
interface Cell {Row:number;Column:number;RowSpan:number;ColSpan:number;Box:Box|null;Text:string}
interface Table {Rows:number;Columns:number;Cells:Cell[];Score:number}
interface Region {Id:string;Label:string;Score:number;Box:Box;Order:number;State:string;Error:string|null;Text:string;Formula:Formula|null;Table:Table|null;IsImage:boolean;ParentId?:string|null}
interface Page {LayoutModel?:string|null;Id:string;Width:number;Height:number;State:string;Stage:string;Error:string|null;ElapsedMs:number;Regions:Region[]}
interface Model {Id:string;Name:string;Installed:boolean;State:string;Bytes:number;Downloaded:number;Error:string|null;Warning?:string|null}
type Katex=typeof import('katex')
let renderer:Promise<Katex>|undefined
async function math(latex:string,output:'htmlAndMathml'|'mathml'='htmlAndMathml') {
  renderer??=import('katex');const katex=await renderer
  return katex.renderToString(latex,{displayMode:true,output,throwOnError:true,trust:false,maxExpand:500,maxSize:20,strict:'warn'})
}
export function FormulaPreview({latex}:{latex:string}) {
  const [html,setHtml]=useState(''),[error,setError]=useState('')
  useEffect(()=>{let live=true;const timer=setTimeout(()=>{if(!latex.trim()){setHtml('');setError('');return}void math(latex).then(value=>{if(live){setHtml(value);setError('')}}).catch(e=>{if(live){setHtml('');setError((e as Error).message)}})},120);return()=>{live=false;clearTimeout(timer)}},[latex])
  return <div className="formula-render">{error?<p role="alert">无法预览：{error}</p>:html?<div dangerouslySetInnerHTML={{__html:html}}/>:<span>公式预览</span>}</div>
}
const names:Record<string,string>={doc_title:'文档标题',paragraph_title:'小标题',text:'正文',abstract:'摘要',content:'目录',formula:'公式',formula_number:'公式编号',table:'表格',table_title:'表题',image:'图片',chart:'图表',figure_title:'图注',reference:'参考文献',footnote:'脚注',header:'页眉',footer:'页脚',algorithm:'算法',seal:'印章',number:'页码',aside_text:'侧栏',header_image:'页眉图片',footer_image:'页脚图片',chart_title:'图表标题'}
const modelState:Record<string,string>={notInstalled:'未安装',downloading:'下载中',importing:'导入中',validating:'校验中',error:'操作失败',corrupt:'文件不可读',canceled:'已取消',installed:'已安装'}
const kind=(r:Region)=>r.Formula||r.Label==='formula'?'formula':r.Table||r.Label==='table'?'table':r.IsImage?'image':'text'

export function DocumentRecognition({source,mode,standalone=false,onClose}:{source:{src:string;width:number;height:number};mode:'formula'|'layout';standalone?:boolean;onClose:()=>void}) {
  const [result,setResult]=useState<Page|null>(null),[selected,setSelected]=useState(''),[error,setError]=useState(''),[working,setWorking]=useState(''),[boxes,setBoxes]=useState(true),[attempt,setAttempt]=useState(0)
  const [models,setModels]=useState<Model[]>([]),[latex,setLatex]=useState(''),[text,setText]=useState(''),[cells,setCells]=useState<string[]>([]),[crop,setCrop]=useState(''),[message,setMessage]=useState('')
  const [sourceZoom,setSourceZoom]=useState(1)
  const closeAction=useRef(onClose);closeAction.current=onClose
  const id=useRef(''),dialog=useRef<HTMLDivElement>(null),lastRegion=useRef('')
  const region=result?.Regions.find(r=>r.Id===selected)||result?.Regions[0],running=result?result.State==='running':!error
  useEffect(()=>{
    let live=true,job='',pending=false,finished=false;setError('');setResult(null);setSelected('');lastRegion.current='';id.current=''
    const poll=async()=>{if(!job||pending||finished)return;pending=true;try{const value=await request<Page>('workspace.page.status',{jobId:job});if(live){setResult(value);finished=value.State!=='running'}}catch(e){if(live)setError((e as Error).message)}finally{pending=false}}
    void request<{jobId:string}>(mode==='formula'?'workspace.formula':'workspace.layout').then(value=>{job=value.jobId;if(!live){void request('workspace.page.cancel',{jobId:job}).catch(()=>{});return}id.current=job;void poll()}).catch(e=>live&&setError((e as Error).message))
    const timer=setInterval(()=>void poll(),450);return()=>{live=false;clearInterval(timer);if(job)void request('workspace.page.cancel',{jobId:job}).catch(()=>{})}
  },[mode,attempt])
  useEffect(()=>{
    let live=true,timer=0;const refresh=async()=>{try{const value=await request<{items:Model[]}>('workspace.page.models');if(live){const items=value.items.filter(m=>mode!=='formula'||m.Id==='formula-rapidlatex');setModels(previous=>JSON.stringify(previous)===JSON.stringify(items)?previous:items);if(items.every(m=>m.Installed))clearInterval(timer)}}catch(e){if(live)setError((e as Error).message)}}
    void refresh();timer=window.setInterval(()=>void refresh(),1500);return()=>{live=false;clearInterval(timer)}
  },[mode,attempt])
  useEffect(()=>{
    if(!region)return;const key=region.Id+region.State+attempt
    if(key===lastRegion.current)return;lastRegion.current=key
    setLatex(region.Formula?.Latex||'');setText(region.Text);setCells(region.Table?.Cells.map(c=>c.Text)||[])
    let live=true;void request<{src:string}>('workspace.page.crop',{jobId:id.current,regionId:region.Id}).then(value=>live&&setCrop(value.src)).catch(()=>{});return()=>{live=false}
  },[region?.Id,region?.State,attempt])
  useEffect(()=>{
    const old=document.activeElement as HTMLElement|null;dialog.current?.focus()
    const key=(e:KeyboardEvent)=>{if(e.key==='Escape'){e.preventDefault();e.stopImmediatePropagation();closeAction.current()}else if(e.key==='Tab'){
      const buttons=Array.from(dialog.current?.querySelectorAll<HTMLElement>('button:not(:disabled),textarea,input')||[]);if(e.shiftKey&&document.activeElement===buttons[0]){e.preventDefault();buttons.at(-1)?.focus()}else if(!e.shiftKey&&document.activeElement===buttons.at(-1)){e.preventDefault();buttons[0]?.focus()}
    }};document.addEventListener('keydown',key,true);return()=>{document.removeEventListener('keydown',key,true);old?.focus()}
  },[])
  async function action(run:()=>Promise<unknown>,label:string){setWorking(label);setMessage('');try{await run();setError('')}catch(e){setError((e as Error).message)}finally{setWorking('')}}
  async function commit(){if(!region||running)return;await request('workspace.page.edit',{jobId:id.current,regionId:region.Id,text:region.Formula?latex:text,...(region.Table?{cells}:{})})}
  async function copy(format:string){await action(async()=>{
    if(!region)return;await commit();let value=latex
    if(format==='word'||format==='mathml'){const markup=await math(latex,'mathml');const doc=new DOMParser().parseFromString(markup,'text/html');const root=doc.querySelector('math');if(!root)throw new Error('未能生成 MathML');value=new XMLSerializer().serializeToString(root);if(format==='word'){await request('workspace.formula.copyWord',{latex,mathml:value});setMessage('已复制，可在 Word 中粘贴为可编辑公式。');return}}
    else if(format==='markdown')value=`$$\n${latex}\n$$`
    else if(format==='text')value=text
    else if(format==='tsv'&&region.Table){const t=region.Table;value=Array.from({length:t.Rows},(_,r)=>Array.from({length:t.Columns},(_,col)=>{const i=t.Cells.findIndex(c=>c.Row===r&&c.Column===col);return i<0?'':cells[i].replace(/[\t\r\n]+/g,' ')}).join('\t')).join('\n')}
    await request('clipboard.copyText',{text:value});setMessage('已复制。')
  },'复制')}
  async function exportResult(format:string){await action(async()=>{await commit();const value=await request<{saved:boolean}|null>('workspace.page.export',{jobId:id.current,format,regionId:region?.Id});if(value?.saved)setMessage('已导出。')},'导出')}
  const missing=models.filter(m=>!m.Installed),hasMissing=missing.length>0
  return <div className={`doc-ocr-surface ${mode==='formula'?'formula-mode':'page-mode'} ${standalone?'standalone':''}`} ref={dialog} role="dialog" aria-modal="true" aria-label={mode==='formula'?'公式识别':'整页版面识别'} tabIndex={-1}>
    <header className="doc-ocr-header"><div><span className="doc-ocr-mark"><Sigma size={17}/></span><b>{mode==='formula'?'公式识别':'页面理解'}</b><small>{mode==='layout'?`PP-DocLayout-${result?.LayoutModel?.slice(-1).toUpperCase()||'S'} · 本地处理`:'本地处理 · 原图保持完整'}</small></div><button className="icon-button" aria-label="关闭文档识别" onClick={onClose}><X size={18}/></button></header>
    <div className="doc-ocr-status" role="status"><span>{running?<LoaderCircle size={13} className="spin"/>:result?.State==='done'?<Check size={13}/>:<AlertTriangle size={13}/>} {working||result?.Stage||(error?'未能开始识别':'准备识别…')}</span><span>{result?.Regions.length||0} 个区域{result?.ElapsedMs?` · ${(result.ElapsedMs/1000).toFixed(1)}s`:''}</span>{result?.State==='running'||working==='重试此区域'?<button onClick={()=>void request('workspace.page.cancel',{jobId:id.current})}>取消</button>:<button disabled={!!working} onClick={()=>setAttempt(n=>n+1)}><RefreshCw size={12}/>重新识别</button>}</div>
    {(error||result?.Error)&&<p className="doc-ocr-error" role="alert">{error||result?.Error}</p>}
    {hasMissing&&<div className="doc-models"><b>需要安装离线模型</b>{missing.map(m=><div key={m.Id}><span>{m.Name}<small>{(m.Bytes/1048576).toFixed(1)} MiB · {modelState[m.State]||m.State}{m.State==='downloading'?` ${(100*m.Downloaded/m.Bytes).toFixed(0)}%`:''}</small>{m.Error&&<small role="alert">{m.Error}</small>}</span><button className="button" onClick={()=>void action(()=>request(['downloading','importing','validating'].includes(m.State)?'models.cancel':'models.download',{id:m.Id}),'模型操作')}>{['downloading','importing','validating'].includes(m.State)?'取消':'下载'}</button><button className="button" onClick={()=>void action(()=>request('models.import',{id:m.Id}),'导入')}>导入目录</button></div>)}<p>安装完成后点击重新识别；图片不会上传。</p></div>}
    <div className="doc-ocr-body">
      <section className="doc-source"><div className="doc-source-label"><span>{mode==='formula'?'公式原图':'原始页面'}</span>{mode==='layout'&&<div className="doc-source-controls"><button aria-label="缩小版面原图" disabled={sourceZoom<=1} onClick={()=>setSourceZoom(v=>Math.max(1,v/1.5))}><ZoomOut size={14}/></button><small>{Math.round(sourceZoom*100)}%</small><button aria-label="放大版面原图" disabled={sourceZoom>=8} onClick={()=>setSourceZoom(v=>Math.min(8,v*1.5))}><ZoomIn size={14}/></button><button aria-label="切换版面区域框" onClick={()=>setBoxes(v=>!v)}>{boxes?<Eye size={14}/>:<EyeOff size={14}/>}区域</button></div>}</div><div className="doc-source-scroll"><div className="doc-source-image" style={mode==='layout'?{width:`${sourceZoom*100}%`}:undefined}><img src={mode==='formula'?crop||source.src:source.src} alt="识别来源"/>{mode==='layout'&&boxes&&<svg viewBox={`0 0 ${source.width} ${source.height}`} aria-label="检测到的版面区域">{result?.Regions.map(r=><g key={r.Id} className={`doc-region ${kind(r)} ${region?.Id===r.Id?'selected':''}`} onClick={()=>{if(!working)setSelected(r.Id)}}><rect x={r.Box.X} y={r.Box.Y} width={r.Box.Width} height={r.Box.Height} vectorEffect="non-scaling-stroke"/><text x={r.Box.X+4} y={r.Box.Y+Math.min(22,r.Box.Height*.6)} fontSize={Math.max(14,source.width/65)}>{r.Order}</text></g>)}</svg>}</div></div>{mode==='layout'&&<nav className="doc-regions" aria-label="阅读顺序">{result?.Regions.map(r=><button key={r.Id} className={`${kind(r)} ${region?.Id===r.Id?'active':''}`} onClick={()=>{if(!working)setSelected(r.Id)}}><span>{String(r.Order).padStart(2,'0')}</span>{names[r.Label]||r.Label}<small>{r.State==='done'?'✓':r.State==='error'?'失败':r.State==='running'?'识别中':'等待'}</small></button>)}</nav>}</section>
      <section className="doc-content"><div className="doc-content-label"><b>{region?names[region.Label]||region.Label:mode==='formula'?'LaTeX':'提取结果'}</b>{region&&<span>{mode==='formula'?'单公式识别':`检测置信度 ${Math.round(region.Score*100)}%`}</span>}</div>
        {region&&mode!=='formula'&&(region.Score<.5||(region.Table&&region.Table.Score<.7))&&<p className="doc-ocr-error">置信度较低，请对照原图检查结果。</p>}
        {mode==='layout'&&region&&<details className="doc-region-crop"><summary>查看选中原区域</summary><img src={crop||source.src} alt="选中区域原图"/></details>}
        {region?.Error&&<p className="doc-ocr-error" role="alert">{region.Error}</p>}
        {!region?<div className="doc-empty"><Sigma size={26}/><p>{hasMissing?'安装模型后即可开始。':running?'识别结果将逐块呈现。':'没有发现可识别的区域。'}</p></div>:region.Formula?<><FormulaPreview latex={latex}/>{!region.Formula.Complete&&<p className="doc-ocr-error">已达到长度上限，公式可能不完整。</p>}{!latex&&region.State==='done'&&<p className="doc-empty">未发现公式</p>}<label className="doc-latex-label">LaTeX<textarea aria-label="编辑 LaTeX" spellCheck={false} value={latex} disabled={running||!!working} onChange={e=>setLatex(e.target.value)} onBlur={()=>void commit().catch(e=>setError(e.message))}/></label><div className="doc-copy-actions"><button className="button dark-button" disabled={running||!!working||!latex} onClick={()=>void copy('latex')}><Copy size={14}/>LaTeX</button><button className="button" disabled={running||!!working||!latex} onClick={()=>void copy('markdown')}>Markdown</button><button className="button" disabled={running||!!working||!latex} onClick={()=>void copy('mathml')}>MathML</button><button className="button" disabled={running||!!working||!latex} onClick={()=>void copy('word')}>Word 公式</button></div></>:region.Table?<><div className="doc-table-scroll"><table aria-label="识别表格"><tbody>{Array.from({length:region.Table.Rows},(_,r)=><tr key={r}>{region.Table!.Cells.map((c,i)=>c.Row===r&&<td key={i} rowSpan={c.RowSpan} colSpan={c.ColSpan}><textarea aria-label={`单元格 ${c.Row+1}-${c.Column+1}`} value={cells[i]||''} disabled={running||!!working} onChange={e=>setCells(old=>old.map((v,j)=>j===i?e.target.value:v))} onBlur={()=>void commit().catch(e=>setError(e.message))}/></td>)}</tr>)}</tbody></table></div><div className="doc-copy-actions"><button className="button dark-button" disabled={running||!!working} onClick={()=>void exportResult('xlsx')}><FileSpreadsheet size={14}/>Excel</button><button className="button" disabled={running||!!working} onClick={()=>void exportResult('html')}>HTML</button><button className="button" disabled={running||!!working} onClick={()=>void copy('tsv')}>复制 TSV</button><button className="button" disabled={running||!!working} onClick={()=>void exportResult('tsv')}>导出 TSV</button></div></>:region.IsImage?<div className="doc-crop"><img src={crop||source.src} alt="图片区域"/><p>图片与图表保留为原图区域。</p></div>:region.ParentId?<p className="doc-empty">此文字已包含在公式或表格区域中，整页导出不会重复。</p>:region.State==='done'?<><textarea className="doc-text" aria-label="编辑区域文字" disabled={!!working} value={text} onChange={e=>setText(e.target.value)} onBlur={()=>void commit().catch(e=>setError(e.message))}/><button className="button" onClick={()=>void copy('text')}><Copy size={14}/>复制文字</button></>:<div className="doc-empty">{region.State==='error'?'此区域未能完成，可单独重试。':'等待识别此区域…'}</div>}
        {region&&!running&&<button className="doc-retry" disabled={!!working} onClick={()=>void action(async()=>{const value=await request<Page>('workspace.page.retry',{jobId:id.current,regionId:region.Id});lastRegion.current='';setResult(value);const updated=value.Regions.find(r=>r.Id===region.Id);if(updated){setLatex(updated.Formula?.Latex||'');setText(updated.Text);setCells(updated.Table?.Cells.map(c=>c.Text)||[])}},'重试此区域')}><RefreshCw size={12}/>重试此区域</button>}
      </section>
    </div>
    <footer className="doc-ocr-footer"><span aria-live="polite">{message||'离线识别 · 不会自动复制'}</span>{mode==='layout'&&<button className="button dark-button" disabled={running||!!working||!result?.Regions.length} onClick={()=>void exportResult('markdown')}><Download size={14}/>导出整页 Markdown</button>}</footer>
  </div>
}
