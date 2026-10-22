import { lazy, Suspense, useCallback, useEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import { ArrowLeft, BarChart3, Check, ChevronLeft, ChevronRight, Copy, Download, FolderOpen, Hand, Languages, Pin, LoaderCircle, Maximize, MousePointer2, Redo2, QrCode, ScanText, Sigma, PanelsTopLeft, Scissors, Sparkles, Undo2, Wand2, X, ZoomIn, ZoomOut } from 'lucide-react'
import { onEvent, request } from './bridge'
import { emptyHistory, imagePoint, registerImageReplacementGuard, pushEdit, readImageDraft, redoEdit, undoEdit, type EditState, type History, type Point, type TextOverlay } from './imageEditing'
import { languages } from './OcrLab'
import type { Shot } from './types'
import './ImageWorkspace.css'
const DocumentRecognition=lazy(()=>import('./DocumentRecognition').then(m=>({default:m.DocumentRecognition})))

interface Document { name:string; width:number; height:number; src:string; hdr:boolean; referenceWhite:number; fingerprint:string; carrier:string; previewMax:number; initialTool?:Tool|'formula'|'layout' }
interface Analysis { stats:{Min:number;Max:number;Average:number;P99:number;SampleCount:number}; referenceWhite:number; approximate:boolean; sampleStep:number; scale:number; waveform:string; heatmap:string; gamutMap:string; gamut:{Samples:number;Black:number;Invalid:number;Srgb:number;DisplayP3Only:number;Bt2020Only:number;Outside2020:number;Density:number[];Grid:number;Approximate:boolean} }
interface Difference { available:boolean; reason?:string; mae?:number; rmse?:number; max?:number; units?:string; approximate?:boolean }
type Tool='hand'|'polygon'|'lasso'|'wand'|'translate'
const format=(n:number|undefined,digits=1)=>n===undefined?'—':new Intl.NumberFormat('zh-CN',{maximumFractionDigits:digits}).format(n)
const path=(points:Point[])=>points.map((p,i)=>`${i?'L':'M'}${p.x},${p.y}`).join(' ')

function GamutPlot({ analysis }: {analysis:Analysis}) {
  const canvas=useRef<HTMLCanvasElement>(null)
  useEffect(()=>{const target=canvas.current;if(!target)return;const ctx=target.getContext('2d');if(!ctx)return;const {Grid,Density}=analysis.gamut;target.width=Grid;target.height=Grid;const pixels=ctx.createImageData(Grid,Grid);const max=Math.max(1,...Density);for(let i=0;i<Density.length;i++){pixels.data[i*4]=221;pixels.data[i*4+1]=243;pixels.data[i*4+2]=105;pixels.data[i*4+3]=Density[i]?Math.round(40+215*Math.log1p(Density[i])/Math.log1p(max)):0}ctx.putImageData(pixels,0,0)},[analysis])
  const triangle=(values:number[][])=>values.map(([x,y])=>`${x/.8*256},${(1-y/.9)*256}`).join(' ')
  return <div className="gamut-plot"><canvas ref={canvas}/><svg viewBox="0 0 256 256" aria-label="CIE xy 色域分布"><g fill="none" strokeWidth="1"><polygon points={triangle([[.708,.292],[.17,.797],[.131,.046]])} stroke="#e7b67b"/><polygon points={triangle([[.68,.32],[.265,.69],[.15,.06]])} stroke="#a7bdff"/><polygon points={triangle([[.64,.33],[.3,.6],[.15,.06]])} stroke="#ddf369"/></g><g fontSize="9" fill="#aeb5a9"><text x="3" y="12">y .9</text><text x="221" y="248">x .8</text></g></svg><small>CIE 1931 xy · 原始 scRGB {analysis.gamut.Approximate?'抽样':'像素'}</small></div>
}

export function ImageWorkspace({ id, initialCompare, shots, onNavigate, onClose, notify, capture=false, initialTarget='简体中文', configure }: { capture?:boolean; initialTarget?:string; configure?:()=>void; id:string; initialCompare?:string; shots:Shot[]; onNavigate:(id:string)=>void; onClose:()=>void; notify:(text:string,error?:boolean)=>void }) {
  const root=useRef<HTMLDivElement>(null), stage=useRef<HTMLDivElement>(null), frame=useRef<HTMLDivElement>(null)
  const [doc,setDoc]=useState<Document|null>(null), [comparison,setComparison]=useState<Document|null>(null), [compareId,setCompareId]=useState(initialCompare||''), [difference,setDifference]=useState<Difference|null>(null)
  const [view,setView]=useState<'split'|'side'>('split'),[split,setSplit]=useState(50),[zoom,setZoom]=useState(1),[pan,setPan]=useState({x:0,y:0}),[stageSize,setStageSize]=useState({width:800,height:600})
  const [history,setHistory]=useState<History>(emptyHistory),[points,setPoints]=useState<Point[]>([]),[redoPoints,setRedoPoints]=useState<Point[]>([]),[hover,setHover]=useState<Point|null>(null)
  const [tool,setTool]=useState<Tool>('hand'),[masked,setMasked]=useState<string|null>(null),[busy,setBusy]=useState(''),[error,setError]=useState(''),[loading,setLoading]=useState(true),[inspector,setInspector]=useState<'edit'|'analysis'>('edit')
  const [appliedMask,setAppliedMask]=useState('null')
  const [analysis,setAnalysis]=useState<Analysis|null>(null),[scale,setScale]=useState(0),[heat,setHeat]=useState(false),[heatOpacity,setHeatOpacity]=useState(.65)
  const [gamutOverlay,setGamutOverlay]=useState(false)
  const [documentMode,setDocumentMode]=useState<'formula'|'layout'|null>(null)
  const autoDocument=useRef('')
  const [tolerance,setTolerance]=useState(28),[target,setTarget]=useState(initialTarget),[pixel,setPixel]=useState<{x:number;y:number;r:number;g:number;b:number;nits:number|null}|null>(null)
  const [translationError,setTranslationError]=useState(''),[translationProgress,setTranslationProgress]=useState('')
  const recognitionGeneration=useRef(0),recognitionRunning=useRef(false),autoTranslated=useRef('')
  const jobs=useRef(new Set<AbortController>()),loadGeneration=useRef(0),loadedFingerprint=useRef(''),space=useRef(false),drawing=useRef(false),panStart=useRef<{x:number;y:number;old:{x:number;y:number}}|null>(null),lastPixel=useRef(0)
  const state=useRef({history,points,tool,doc,loading,busy});state.current={history,points,tool,doc,loading,busy}
  const currentPoints=useRef(points);currentPoints.current=points
  const call=useCallback(async <T,>(method:string,params:object={})=>{const controller=new AbortController();jobs.current.add(controller);try{return await request<T>(method,params,controller.signal)}finally{jobs.current.delete(controller)}},[])
  const push=(next:EditState)=>setHistory(h=>pushEdit(h,next))
  const updatePoints=(next:Point[])=>{currentPoints.current=next;setPoints(next)}
  async function saveDraft(){if(!state.current.doc)return;await call('workspace.draft.save',{draft:{version:1,history:state.current.history,points:state.current.points}})}
  const dirty=history.past.length>0||points.length>0||history.current.mask!==null||history.current.overlays.length>0
  function cancelJobs(){recognitionGeneration.current++;if(recognitionRunning.current)setTranslationError('已取消，可以重新翻译。');recognitionRunning.current=false;setTranslationProgress('');for(const controller of jobs.current)controller.abort();setBusy('');if(appliedMask!==JSON.stringify(history.current.mask))setHistory(h=>pushEdit(h,{...h.current,mask:JSON.parse(appliedMask)}))}
  async function close(){if(dirty&&!confirm('关闭图像编辑器？已有文件的抠图草稿会保留；未保存截图请先保存或复制。'))return;try{cancelJobs();await saveDraft();await request('workspace.close');onClose()}catch(e){notify((e as Error).message,true)}}
  useEffect(()=>registerImageReplacementGuard(async()=>{if(dirty&&!confirm('新截图将替换当前编辑器，是否保存草稿后继续？未导出的截图像素不会保留。'))return false;cancelJobs();await saveDraft();return true}))
  useEffect(()=>capture?onEvent('capture.closeRequested',()=>{void close()}):undefined)
  function navigate(next:string){if(dirty&&!confirm('切换图片？当前抠图草稿会保留。'))return;cancelJobs();void saveDraft().then(()=>onNavigate(next)).catch(e=>notify(e.message,true))}
  function finishPolygon(){const next=currentPoints.current;if(next.length<3){notify('请至少放置三个顶点。',true);return}push({...state.current.history.current,mask:{kind:'polygon',points:[...next]}});updatePoints([]);setRedoPoints([])}
  function undo(){const current=state.current;if(current.points.length){setRedoPoints(p=>[current.points.at(-1)!,...p]);updatePoints(current.points.slice(0,-1))}else setHistory(undoEdit)}
  function redo(){if(redoPoints.length){updatePoints([...currentPoints.current,redoPoints[0]]);setRedoPoints(p=>p.slice(1))}else setHistory(redoEdit)}
  useEffect(()=>{
    const previous=document.activeElement as HTMLElement|null;root.current?.focus();const observer=new ResizeObserver(entries=>{const r=entries[0].contentRect;setStageSize({width:r.width,height:r.height})});if(stage.current)observer.observe(stage.current)
    return()=>{recognitionGeneration.current++;recognitionRunning.current=false;observer.disconnect();for(const controller of jobs.current)controller.abort();void request('workspace.close').catch(()=>{});previous?.focus()}
  },[])
  useEffect(()=>{
    recognitionGeneration.current++;recognitionRunning.current=false;setTranslationProgress('');setTranslationError('');
    const generation=++loadGeneration.current;for(const controller of jobs.current)controller.abort();setLoading(true);setError('');setDoc(null);setDocumentMode(null);setAnalysis(null);setHeat(false);setComparison(null);setCompareId(initialCompare||'');setDifference(null);setMasked(null);setHistory(emptyHistory());updatePoints([]);setRedoPoints([]);setZoom(1);setPan({x:0,y:0});setPixel(null);setAppliedMask('null');loadedFingerprint.current=''
    void call<Document>(id.startsWith('capture:')?'workspace.pending':'workspace.open',id.startsWith('capture:')?{}:{id}).then(async value=>{
      if(generation!==loadGeneration.current)return;setDoc(value);setTool(['hand','polygon','lasso','wand','translate'].includes(value.initialTool||'')?value.initialTool as Tool:'hand');setGamutOverlay(false);let draft:unknown=null;try{draft=await call('workspace.draft.load')}catch{ /* Fresh captures have no draft. */ }
      if(generation!==loadGeneration.current)return;const restored=readImageDraft(draft);if(restored){setHistory(restored.history);updatePoints(restored.points);if(restored.points.length)setTool('polygon')}
      loadedFingerprint.current=value.fingerprint;setLoading(false)
    }).catch(e=>{if(generation===loadGeneration.current){setError(e.message);setLoading(false)}})
    return()=>{loadGeneration.current++}
  },[id,call])
  useEffect(()=>{
    if(!doc||!compareId||loading)return;let active=true
    void call<Document>('workspace.compare',{id:compareId}).then(value=>{if(!active)return;setComparison(value);if(value.width!==doc.width||value.height!==doc.height)setView('side');return call<Difference>('workspace.difference')}).then(value=>{if(active&&value)setDifference(value)}).catch(e=>{if(active)notify(e.message,true)})
    return()=>{active=false}
  },[doc?.fingerprint,compareId,loading,call])
  useEffect(()=>{
    if(!doc||loading||loadedFingerprint.current!==doc.fingerprint)return;const controller=new AbortController();jobs.current.add(controller)
    const spec=history.current.mask;if(spec===null&&appliedMask==='null'){setMasked(null);return}setBusy('正在更新蒙版…')
    void request<{src:string}>('workspace.mask',spec||{kind:'clear'},controller.signal).then(result=>{if(!controller.signal.aborted){setMasked(spec?result.src:null);setAppliedMask(JSON.stringify(spec))}}).catch(e=>{if(!controller.signal.aborted){notify(e.message,true);setHistory(h=>JSON.stringify(h.current.mask)===JSON.stringify(spec)?undoEdit(h):h)}}).finally(()=>{jobs.current.delete(controller);if(!controller.signal.aborted)setBusy('')})
    return()=>controller.abort()
  },[history.current.mask,doc?.fingerprint,loading,notify])
  useEffect(()=>{
    if(!doc||loading||loadedFingerprint.current!==doc.fingerprint)return
    const timer=setTimeout(()=>void saveDraft().catch(e=>notify(`草稿保存失败：${e.message}`,true)),600);return()=>clearTimeout(timer)
  },[history,points,doc?.fingerprint,loading])
  useEffect(()=>{
    const key=(e:KeyboardEvent)=>{
      if(root.current?.closest('[inert]')||root.current?.querySelector('.doc-ocr-surface'))return
      const input=(e.target as HTMLElement).closest('input,textarea,[contenteditable=true]');if(input)return
      if(e.code==='Space'){space.current=e.type==='keydown';if(e.type==='keydown')e.preventDefault();return}
      if(e.type!=='keydown')return
      if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='z'){e.preventDefault();e.stopPropagation();e.shiftKey?redo():undo()}
      else if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='y'){e.preventDefault();e.stopPropagation();redo()}
      else if(e.key==='Enter'&&currentPoints.current.length>=3){e.preventDefault();e.stopPropagation();finishPolygon()}
      else if(e.key==='Escape'){e.preventDefault();e.stopPropagation();if(currentPoints.current.length&&state.current.tool!=='hand'){setTool('hand');notify('顶点已保留，切回多边形可继续。')}else void close()}
      else if(e.key==='Tab'){
        const items=Array.from(root.current?.querySelectorAll<HTMLElement>('button:not(:disabled),input:not(:disabled),select,textarea')||[]);const first=items[0],last=items.at(-1)
        if(e.shiftKey&&document.activeElement===first){e.preventDefault();last?.focus()}else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first?.focus()}
      }
    };document.addEventListener('keydown',key,true);document.addEventListener('keyup',key,true);return()=>{document.removeEventListener('keydown',key,true);document.removeEventListener('keyup',key,true)}
  })
  const fit=doc?Math.min((stageSize.width-64)/(comparison&&view==='side'?doc.width+comparison.width+24:doc.width),(stageSize.height-56)/Math.max(doc.height,comparison?.height||0)):1
  const actualScale=Math.max(.005,fit)*zoom
  function point(e:ReactPointerEvent){if(!doc||!frame.current)return null;return imagePoint(e.clientX,e.clientY,frame.current.getBoundingClientRect(),doc.width,doc.height)}
  function down(e:ReactPointerEvent){if(!doc||busy||loading||(e.target as HTMLElement).closest('textarea'))return;const p=point(e);if(!p)return;e.preventDefault();e.currentTarget.setPointerCapture(e.pointerId)
    if(tool==='hand'||space.current||e.button===1){panStart.current={x:e.clientX,y:e.clientY,old:pan};return}
    if(tool==='polygon'){if(points.length>=3&&Math.hypot(p.x-points[0].x,p.y-points[0].y)*actualScale<9){finishPolygon();return}if(points.length<20000){updatePoints([...points,p]);setRedoPoints([])} }
    else if(tool==='lasso'){drawing.current=true;updatePoints([p]);setRedoPoints([])}
    else if(tool==='wand')push({...history.current,mask:{kind:'wand',x:Math.floor(p.x),y:Math.floor(p.y),tolerance}})
  }
  function move(e:ReactPointerEvent){if(!doc)return;if(panStart.current){setPan({x:panStart.current.old.x+e.clientX-panStart.current.x,y:panStart.current.old.y+e.clientY-panStart.current.y});return}const p=point(e);if(!p)return;setHover(p)
    if(drawing.current&&currentPoints.current.length<20000){const last=currentPoints.current.at(-1);if(!last||Math.hypot(p.x-last.x,p.y-last.y)*actualScale>2)updatePoints([...currentPoints.current,p])}
    if(performance.now()-lastPixel.current>100&&!busy){lastPixel.current=performance.now();void call<typeof pixel>('workspace.pixel',{x:Math.floor(p.x),y:Math.floor(p.y)}).then(setPixel).catch(()=>{})}
  }
  function up(){panStart.current=null;if(drawing.current){drawing.current=false;if(currentPoints.current.length>=3)finishPolygon()}}
  async function analyze(nextScale=scale){if(!doc?.hdr)return;setBusy('分析原始 HDR 像素…');try{setAnalysis(await call<Analysis>('workspace.analysis',{scale:nextScale}));setInspector('analysis')}catch(e){notify((e as Error).message,true)}finally{setBusy('')}}
  async function recognize(translate=false){
    if(!state.current.doc||state.current.loading||state.current.busy||recognitionRunning.current)return
    const generation=++recognitionGeneration.current;recognitionRunning.current=true
    setTranslationError('');setBusy('正在识别选区文字…');setTranslationProgress('正在识别文字与位置…')
    try{
      let lines=await call<TextOverlay[]>('workspace.ocr')
      if(generation!==recognitionGeneration.current)return
      lines=lines.filter(line=>line.text.trim().length>0&&line.width>0&&line.height>0)
      if(!lines.length){setTranslationError('没有识别到文字，请选择包含清晰文字的画面。');return}
      if(translate){
        setBusy(`正在翻译 ${lines.length} 行文字…`);setTranslationProgress(`正在翻译 ${lines.length} 行 → ${target}`)
        const values=await call<{id:string;text:string}[]>('translation.formatted',{segments:lines.map(l=>({id:l.id,text:l.text})),targetLanguage:target})
        if(generation!==recognitionGeneration.current)return
        const translated=new Map(values.map(v=>[v.id,v.text]));lines=lines.map(l=>({...l,text:translated.get(l.id)||l.text}))
      }
      push({...state.current.history.current,overlays:lines.map(l=>({...l,height:Math.max(l.height,18),fontSize:Math.max(10,l.height*.8)}))})
      setTool('translate');setInspector('edit');notify(translate?'译文已覆盖到原文位置，可直接编辑。':'已标出文字位置，可直接编辑。')
    }catch(e){if(generation===recognitionGeneration.current){setTranslationError((e as Error).message);setTool('translate');setInspector('edit')}}
    finally{if(generation===recognitionGeneration.current){recognitionRunning.current=false;setTranslationProgress('');setBusy('')}}
  }
  useEffect(()=>{
    if(!capture||!doc||doc.initialTool!=='translate'||loading||busy||autoTranslated.current===doc.fingerprint)return
    autoTranslated.current=doc.fingerprint
    if(!history.current.overlays.length&&!points.length&&!history.past.length)void recognize(true)
  },[capture,doc?.fingerprint,loading,busy])
  useEffect(()=>{if(doc&&!loading&&['formula','layout'].includes(doc.initialTool||'')&&autoDocument.current!==doc.fingerprint){autoDocument.current=doc.fingerprint;setDocumentMode(doc.initialTool as 'formula'|'layout')}},[doc?.fingerprint,loading])
  async function recognizeQr(){setBusy('识别二维码…');try{await call('workspace.qr')}catch(e){notify((e as Error).message,true)}finally{setBusy('')}}
  async function exportImage(copy=false){if(points.length||appliedMask!==JSON.stringify(history.current.mask)){notify('请先完成多边形并等待蒙版生成。',true);return}setBusy(copy?'正在复制透明图片…':'正在导出…');try{const saved=await call(copy?'workspace.copy':'workspace.export',{overlays:history.current.overlays});if(copy||saved)notify(copy?'已复制透明 SDR 图像。':'已保存透明 SDR PNG。')}catch(e){notify((e as Error).message,true)}finally{setBusy('')}}
  const currentIndex=shots.findIndex(s=>s.id===id),sameSize=comparison&&doc&&comparison.width===doc.width&&comparison.height===doc.height
  return <div className={`image-workspace ${capture?'capture-editor':''}`} ref={root} role="dialog" aria-modal="true" aria-label={capture?'选区截图编辑':'图像工作区'} tabIndex={-1}>
    <header className="image-workspace-header"><button className="icon-button" aria-label={capture?'关闭选区编辑':'关闭图像工作区'} onClick={()=>void close()}>{capture?<X size={18}/>:<ArrowLeft size={20}/>}</button><div className="workspace-document-name"><b>{capture?'starshot / 当前选区':doc?.name||'打开图像'}</b><span>{doc?`${doc.carrier} · ${doc.width} × ${doc.height} px`:loading?'正在解码原始图像…':'图像工作区'}</span></div><span className="workspace-signal">{capture?'选区编辑':doc?.hdr?'HDR 源 / SDR 网页预览':'SDR'}</span><div className="workspace-header-actions">{!capture&&<><button className="icon-button" aria-label="上一张" disabled={currentIndex<=0} onClick={()=>navigate(shots[currentIndex-1].id)}><ChevronLeft size={19}/></button><button className="icon-button" aria-label="下一张" disabled={currentIndex<0||currentIndex>=shots.length-1} onClick={()=>navigate(shots[currentIndex+1].id)}><ChevronRight size={19}/></button></>}<button className="button" disabled={!doc||!!busy||points.length>0||appliedMask!==JSON.stringify(history.current.mask)} onClick={()=>void exportImage(true)}><Copy size={15}/>复制</button><button className="button dark-button" disabled={!doc||!!busy||points.length>0||appliedMask!==JSON.stringify(history.current.mask)} onClick={()=>void exportImage()}><Download size={15}/>{capture?'保存 PNG…':'导出 PNG'}</button></div></header>
    <div className="image-workspace-body"><nav className="image-tools" aria-label="图像编辑工具">{([{id:'hand',label:'查看 / 移动画面',icon:Hand},{id:'polygon',label:'多边形抠图',icon:MousePointer2},{id:'lasso',label:'自由套索',icon:Scissors},{id:'wand',label:'魔棒去背景',icon:Wand2},{id:'translate',label:'原位翻译覆盖',icon:Languages}] as const).map(t=><button className={tool===t.id?'active':''} key={t.id} title={t.label} aria-label={t.label} aria-pressed={tool===t.id} disabled={!doc||loading||!!busy} onClick={()=>{setTool(t.id);setInspector('edit');if(t.id==='translate')void recognize(true)}}><t.icon size={19}/></button>)}<span/><button aria-label="公式识别" title="公式识别 · 离线 LaTeX" disabled={!doc||!!busy} onClick={()=>setDocumentMode('formula')}><Sigma size={19}/></button><button aria-label="分析版面" title="分析版面 · 整页文字、公式与表格提取" disabled={!doc||!!busy} onClick={()=>setDocumentMode('layout')}><PanelsTopLeft size={19}/></button><button aria-label="识别二维码" title="识别二维码 · 纯 CPU，无需模型" disabled={!doc||!!busy} onClick={()=>void recognizeQr()}><QrCode size={19}/></button><button aria-label="智能抠图" title="智能抠图 · 需要 U²-Net 模型" disabled={!doc||!!busy} onClick={()=>push({...history.current,mask:{kind:'smart'}})}><Sparkles size={19}/></button><button aria-label="HDR 与色域分析" title={doc?.hdr?'HDR 与色域分析':'需要原始 HDR FP16 数据'} disabled={!doc?.hdr||!!busy} onClick={()=>void analyze()}><BarChart3 size={19}/></button><span/><button aria-label="撤销图像编辑" title="Ctrl+Z · 优先撤销当前顶点" disabled={!points.length&&!history.past.length} onClick={undo}><Undo2 size={18}/></button><button aria-label="重做图像编辑" title="Ctrl+Y" disabled={!redoPoints.length&&!history.future.length} onClick={redo}><Redo2 size={18}/></button></nav>
      <div className={`image-stage tool-${tool}`} ref={stage} onWheel={e=>{if(e.ctrlKey){e.preventDefault();setZoom(v=>Math.max(.2,Math.min(16,v*(e.deltaY<0?1.15:1/1.15))))}}}>
        {loading&&<div className="image-stage-message"><LoaderCircle className="spin" size={25}/><span>读取图像与原始像素…</span></div>}{error&&<div className="image-stage-message" role="alert"><b>图像未能打开</b><p>{error}</p><button className="button" onClick={()=>void close()}>{capture?'关闭选区编辑':'返回图库'}</button></div>}
        {doc&&<div className="image-scene" style={{width:comparison&&view==='side'?doc.width+comparison.width+24:doc.width,height:Math.max(doc.height,comparison?.height||0),transform:`translate(calc(-50% + ${pan.x}px),calc(-50% + ${pan.y}px)) scale(${actualScale})`}}>
          <div className="image-frame" ref={frame} style={{width:doc.width,height:doc.height}} onPointerDown={down} onPointerMove={move} onPointerUp={up} onDoubleClick={()=>{if(tool==='polygon'&&currentPoints.current.length>=3)finishPolygon()}}>
            <img className="workspace-raster" src={masked||doc.src} draggable={false} alt={doc.name}/>
            {comparison&&view==='split'&&sameSize&&<img className="workspace-raster compare-raster" src={comparison.src} draggable={false} alt={comparison.name} style={{clipPath:`inset(0 ${100-split}% 0 0)`}}/>}
            {heat&&analysis&&<img className="workspace-raster heatmap-raster" src={analysis.heatmap} alt="亮度热力图" style={{opacity:heatOpacity}}/>}
            {gamutOverlay&&analysis&&<img className="workspace-raster gamut-raster" src={analysis.gamutMap} alt="色域区域图" style={{opacity:heatOpacity}}/>}
            {points.length>0&&<svg className="mask-path" viewBox={`0 0 ${doc.width} ${doc.height}`}><path d={path(points)+(hover&&tool==='polygon'?` L${hover.x},${hover.y}`:'')} fill="#ddf36920" stroke="#ddf369" strokeWidth={1.5/actualScale} strokeDasharray={`${5/actualScale} ${4/actualScale}`}/>{points.map((p,i)=><circle key={i} cx={p.x} cy={p.y} r={(i===0?4:2.5)/actualScale} fill={i===0?'#ddf369':'white'} stroke="#253122" strokeWidth={1/actualScale}/>)}</svg>}
            {history.current.overlays.map((line,index)=><textarea key={line.id} className={`translation-cover ${line.dark?'dark':''}`} aria-label={`覆盖文字 ${index+1}`} spellCheck={false} value={line.text} style={{left:line.x,top:line.y,width:line.width,height:line.height,fontSize:line.fontSize||line.height*.8}} onPointerDown={e=>e.stopPropagation()} onPointerUp={e=>{const element=e.currentTarget;setHistory(h=>({...h,current:{...h.current,overlays:h.current.overlays.map(o=>o.id===line.id?{...o,width:element.offsetWidth,height:element.offsetHeight}:o)}}))}} onChange={e=>{const overlays=history.current.overlays.map(l=>l.id===line.id?{...l,text:e.target.value}:l);setHistory(h=>({...h,current:{...h.current,overlays}}))}} onBlur={()=>void saveDraft().catch(e=>notify(e.message,true))}/>)}
          </div>{comparison&&view==='side'&&<div className="image-frame compare-side" style={{width:comparison.width,height:comparison.height,left:doc.width+24}}><img className="workspace-raster" src={comparison.src} alt={comparison.name} draggable={false}/></div>}
        </div>}
        {busy&&<div className="workspace-busy"><LoaderCircle className="spin" size={14}/>{busy}<button className="icon-button" aria-label="取消图像操作" onClick={cancelJobs}><X size={13}/></button></div>}
        {points.length>0&&<div className="polygon-progress"><b>{points.length} 个顶点</b><span>Ctrl+Z 撤销顶点 · Enter 完成</span><button onClick={finishPolygon}><Check size={14}/>完成</button></div>}
      </div>
      <aside className="image-inspector"><div className="inspector-tabs"><button className={inspector==='edit'?'active':''} onClick={()=>setInspector('edit')}>编辑与对比</button><button className={inspector==='analysis'?'active':''} disabled={!doc?.hdr} onClick={()=>analysis?setInspector('analysis'):void analyze()}>HDR 分析</button></div>
        {inspector==='edit'?<>{!capture&&<section><span className="eyebrow">COMPARE</span><h3>双图对比</h3><select aria-label="选择对比图片" value={compareId} onChange={e=>{setCompareId(e.target.value);if(!e.target.value){setComparison(null);setDifference(null)}}}><option value="">选择另一张图像</option>{shots.filter(s=>s.kind!=='video'&&s.id!==id).map(s=><option value={s.id} key={s.id}>{s.name}</option>)}</select>{comparison&&<><div className="segmented"><button className={view==='split'?'active':''} disabled={!sameSize} onClick={()=>setView('split')}>分割</button><button className={view==='side'?'active':''} onClick={()=>setView('side')}>并排</button></div>{view==='split'&&<label className="workspace-range">B ← 分割线 → A<input aria-label="对比分割线" type="range" min={0} max={100} value={split} onChange={e=>setSplit(+e.target.value)}/></label>}<p className="inspector-note">缩放和平移同步；比较原始像素，显示为 SDR 预览。</p>{difference?.available?<div className="difference-values"><span>MAE <b>{format(difference.mae,5)}</b></span><span>RMSE <b>{format(difference.rmse,5)}</b></span><span>最大差 <b>{format(difference.max,5)}</b></span><small>{difference.units}{difference.approximate?' · 抽样':''}</small></div>:difference&&<p className="inspector-note">{difference.reason}</p>}</>}</section>}
          {(!capture||tool!=='translate')&&<section><span className="eyebrow">CUTOUT</span><h3>透明蒙版</h3><p className="inspector-note">多边形逐点选取，或按住拖动套索。魔棒点击背景，智能抠图使用已安装模型。</p>{tool==='wand'&&<label className="workspace-range">容差 {tolerance}<input aria-label="魔棒容差" type="range" min={0} max={100} value={tolerance} onChange={e=>setTolerance(+e.target.value)}/></label>}<button className="button" disabled={!history.current.mask||!!busy} onClick={()=>push({...history.current,mask:null})}>恢复完整图像</button><p className="inspector-note">透明导出使用 SDR PNG。原始 HDR 文件保持完整，热力图不会写入导出。</p></section>}
          <section><span className="eyebrow">TRANSLATE IN PLACE</span><h3>原位翻译</h3><p className="inspector-note">识别原文位置，再将译文覆盖到画面上。可直接修改译文，或撤销覆盖。</p>{translationProgress&&<div className="translation-feedback" role="status"><LoaderCircle className="spin" size={14}/>{translationProgress}</div>}{translationError&&<div className="translation-feedback error" role="alert"><p>{translationError}</p><button className="text-button" disabled={!!busy} onClick={()=>void recognize(true)}>重新翻译</button>{configure&&<button className="text-button" onClick={configure}>翻译设置</button>}</div>}<select aria-label="覆盖翻译目标语言" value={target} onChange={e=>setTarget(e.target.value)}>{languages.map(l=><option key={l}>{l}</option>)}</select><div className="workspace-action-row"><button className="button" disabled={!doc||!!busy} onClick={()=>void recognize()}><ScanText size={14}/>识别位置</button><button className="button" disabled={!doc||!!busy} onClick={()=>void recognize(true)}><Languages size={14}/>翻译覆盖</button></div>{history.current.overlays.length>0&&<><p className="inspector-note">点击图片上的文本可直接编辑。覆盖层只在复制或导出时合成。</p><button className="button" onClick={()=>push({...history.current,overlays:history.current.overlays.map(o=>({...o,dark:!o.dark}))})}>切换明暗底色</button><button className="text-button" onClick={()=>push({...history.current,overlays:[]})}>移除覆盖层</button></>}</section>
          {doc&&!id.startsWith('capture:')&&<section><div className="workspace-action-row"><button className="button" onClick={()=>void call('library.pin',{id}).catch(e=>notify(e.message,true))}><Pin size={14}/>贴到屏幕</button><button className="button" onClick={()=>void call('library.reveal',{id}).catch(e=>notify(e.message,true))}><FolderOpen size={14}/>文件位置</button></div><button className="button" onClick={()=>void call('library.open',{id}).catch(e=>notify(e.message,true))}><Maximize size={15}/>{doc.hdr?'原生 HDR 查看':'原始图像查看'}</button><p className="inspector-note">真实 HDR 由内置原生查看器显示，无需第三方软件。</p></section>}
        </>:<>{analysis&&<><section><div className="analysis-heading"><span className="eyebrow">RAW HDR / NITS</span><button className="icon-button" aria-label="关闭分析并释放缓存" onClick={()=>{setHeat(false);setGamutOverlay(false);setAnalysis(null);setInspector('edit');void call('workspace.analysis.close')}}><X size={14}/></button></div><div className="luminance-grid">{[['Cursor',pixel?.nits??undefined],['Peak',analysis.stats.Max],['Average',analysis.stats.Average],['Min',analysis.stats.Min],['P99',analysis.stats.P99],['SDR White',analysis.referenceWhite]].map(([label,value])=><div key={label}><span>{label}</span><b>{format(value as number|undefined)}</b></div>)}</div><p className="inspector-note">scRGB 信号亮度估计，不是显示器实测发光亮度。SDR White 是参考白；文件不提供显示器峰值。</p>{analysis.approximate&&<p className="inspector-note">抽样统计 · 步长 {analysis.sampleStep}px</p>}</section>
          <section><h3>亮度波形</h3><select aria-label="波形亮度刻度" value={scale} onChange={e=>{const value=+e.target.value;setScale(value);void analyze(value)}}>{[0,203,400,1000,2000,4000,10000].map(n=><option key={n} value={n}>{n?`${n} nits`:'自动刻度'}</option>)}</select><div className="workspace-wave"><img src={analysis.waveform} alt="水平位置与亮度波形"/>{[analysis.referenceWhite,1000].filter(n=>n<analysis.scale).map(n=><span key={n} style={{bottom:`${n/analysis.scale*100}%`}}>{n} nits</span>)}</div><small>0 → {format(analysis.scale)} nits</small><label className="workspace-checkbox"><input type="checkbox" checked={heat} onChange={e=>{setHeat(e.target.checked);if(e.target.checked)setGamutOverlay(false)}}/>亮度热力图</label>{heat&&<><label className="workspace-range">叠层透明度 {Math.round(heatOpacity*100)}%<input aria-label="热力图透明度" type="range" min={0} max={1} step={.05} value={heatOpacity} onChange={e=>setHeatOpacity(+e.target.value)}/></label><div className="heatmap-legend"/><small>0 · 80 · 203 · 400 · 1000 · 4000 · 10000 nits</small></>}</section>
          <section><h3>色域区域与像素占比</h3><label className="workspace-checkbox"><input type="checkbox" checked={gamutOverlay} onChange={e=>{setGamutOverlay(e.target.checked);if(e.target.checked)setHeat(false)}}/>色域区域叠层</label>{gamutOverlay&&<><div className="gamut-map-legend"><span>● sRGB 内</span><span>● P3 扩展</span><span>● 2020 扩展</span><span>● 2020 外</span></div><label className="workspace-range">叠层透明度 {Math.round(heatOpacity*100)}%<input aria-label="色域叠层透明度" type="range" min={0} max={1} step={.05} value={heatOpacity} onChange={e=>setHeatOpacity(+e.target.value)}/></label></>}<GamutPlot analysis={analysis}/>{[['sRGB 内',analysis.gamut.Srgb],['P3 扩展区',analysis.gamut.DisplayP3Only],['BT.2020 扩展区',analysis.gamut.Bt2020Only],['BT.2020 外',analysis.gamut.Outside2020]].map(([label,value])=><div className="gamut-ratio" key={label}><span>{label}</span><b>{format(value as number)}%</b></div>)}<p className="inspector-note">分析原图，不含蒙版与翻译层。按色度判断，亮度超过 SDR 白不会误算成越界色。黑色及无效像素不计入分母；各扩展区互不重叠。</p></section></>}</>}
      </aside>
    </div>
    <footer className="image-workspace-footer"><span>{pixel?`${pixel.x}, ${pixel.y} · RGB ${pixel.r}, ${pixel.g}, ${pixel.b}${pixel.nits!==null?` · ${format(pixel.nits)} nits`:''}`:'原图保留 · 非破坏式编辑'}{doc&&Math.max(doc.width,doc.height)>doc.previewMax?' · 显示为缩小预览':''}</span><div><button className="icon-button" aria-label="缩小图像" onClick={()=>setZoom(z=>Math.max(.2,z/1.25))}><ZoomOut size={15}/></button><button className="zoom-fit" onClick={()=>{setZoom(1);setPan({x:0,y:0})}}>{Math.round(actualScale*100)}% · 适应</button><button className="icon-button" aria-label="放大图像" onClick={()=>setZoom(z=>Math.min(16,z*1.25))}><ZoomIn size={15}/></button></div></footer>
    {documentMode&&doc&&<Suspense fallback={<div className="workspace-busy">准备文档识别…</div>}><DocumentRecognition source={doc} mode={documentMode} onClose={()=>setDocumentMode(null)}/></Suspense>}
  </div>
}
