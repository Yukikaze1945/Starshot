import {useCallback,useEffect,useState} from 'react'
import {Copy,Download,LoaderCircle,Maximize,Pause,Pin,PinOff,Play,Plus,Square,TextCursorInput,X} from 'lucide-react'
import {onEvent,request} from './bridge'
import type {Bootstrap} from './types'
import {CompactTitleBar,useWindowChrome} from './WindowChrome'
import './SubtitleApp.css'

interface Status {State:string;Error:string|null;Region:{X:number;Y:number;Width:number;Height:number};Count:number;Revision:number;Recent:{Id:number;Text:string}[];Engine:string;OcrMs:number;CycleMs:number;Captures:number;Recognitions:number;Skipped:number;Unsaved:boolean;Path:string;IntervalMs:number}
interface Snapshot {hasSession:boolean;hasRegion:boolean;selecting?:boolean;status:Status|null;operationError?:string|null;topmost?:boolean}
const labels:Record<string,string>={editing:'调整区域',running:'正在摘录',paused:'已暂停',stopping:'正在结束识别',stopped:'已结束 · 结果保留'}
export function SubtitleApp(){
 const [boot,setBoot]=useState<Bootstrap|null>(null),[snapshot,setSnapshot]=useState<Snapshot>({hasSession:false,hasRegion:false,status:null}),[acting,setActing]=useState(''),[error,setError]=useState(''),[notice,setNotice]=useState(''),[fresh,setFresh]=useState(false)
 useWindowChrome(boot)
 useEffect(()=>{const close=(e:KeyboardEvent)=>{if(e.key!=='Escape')return;e.preventDefault();if(fresh)setFresh(false);else void request('subtitles.close').catch(e=>setError(e.message))};window.addEventListener('keydown',close);return()=>window.removeEventListener('keydown',close)},[fresh])
 const refresh=useCallback(async()=>{const value=await request<Snapshot>('subtitles.status');setSnapshot(value)},[])
 useEffect(()=>{let live=true;void request<Bootstrap>('app.bootstrap').then(value=>{if(live){setBoot(value);void request('app.ready')}}).catch(e=>live&&setError(e.message));return()=>{live=false}},[])
 useEffect(()=>onEvent('settings.changed',()=>{void request<Bootstrap>('app.bootstrap').then(setBoot).catch(e=>setError(e.message))}),[])
 useEffect(()=>{if(boot)document.documentElement.dataset.theme=boot.settings.theme===2?'dark':boot.settings.theme===1?'light':matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light'},[boot])
 useEffect(()=>{let live=true,pending=false;const tick=async()=>{if(pending||document.hidden)return;pending=true;try{const value=await request<Snapshot>('subtitles.status');if(live)setSnapshot(value)}catch(e){if(live)setError((e as Error).message)}finally{pending=false}};void tick();const timer=setInterval(()=>void tick(),500);return()=>{live=false;clearInterval(timer)}},[])
 useEffect(()=>{if(!notice)return;const timer=setTimeout(()=>setNotice(''),2500);return()=>clearTimeout(timer)},[notice])
 const s=snapshot.status,running=s?.State==='running',busy=!!acting||!!snapshot.selecting||s?.State==='stopping',hasText=(s?.Count||0)>0
 async function action(name:string,params={},message=''){
  if(acting)return;setActing(name);setError('');setNotice('')
  try{const result=await request<{saved?:boolean}|null>('subtitles.'+name,params);await refresh();if(message&&(name!=='export'||result?.saved))setNotice(message)}catch(e){setError((e as Error).message)}finally{setActing('')}
 }
 return <><CompactTitleBar title="实时字幕摘录"/><main className="subtitle-panel">
  
  <section className="subtitle-state" aria-label="摘录状态"><div><i className={running?'recording':''}/><b>{s?labels[s.State]||s.State:'尚未开始'}</b><span>{s&&snapshot.hasRegion?`${s.Region.X}, ${s.Region.Y} · ${s.Region.Width} × ${s.Region.Height} px`:'从区域截图选取字幕范围'}</span></div><aside className="subtitle-total"><strong>{s?.Count||0}<small>段</small></strong><button className={`icon-button subtitle-pin${snapshot.topmost!==false?' is-pinned':''}`} aria-label="字幕窗口置顶" aria-pressed={snapshot.topmost!==false} title={snapshot.topmost!==false?'已置顶 · 点击取消':'置顶字幕窗口'} disabled={!!acting} onClick={()=>void action('topmost',{value:snapshot.topmost===false})}>{snapshot.topmost!==false?<Pin size={14}/>:<PinOff size={14}/>}</button><button className="icon-button" aria-label="关闭字幕摘录" title="关闭并结束摘录 · Esc" onClick={()=>void action('close')}><X size={15}/></button></aside></section>
  <div className="subtitle-controls"><button className="button" disabled={busy} onClick={()=>void action('configure')}><Maximize size={15}/>{snapshot.hasRegion?'重新框选':'区域框选'}</button>
   {running?<button className="button accent-button" disabled={busy} onClick={()=>void action('pause')}><Pause size={15}/>暂停</button>:<button className="button accent-button" disabled={busy||!snapshot.hasRegion} onClick={()=>void action('start')}><Play size={15}/>{s?.State==='paused'||s?.State==='stopped'?'继续摘录':'开始摘录'}</button>}
   <button className="icon-button" aria-label="结束摘录并保留结果" title="结束摘录，文案保留" disabled={busy||!s||s.State==='stopped'} onClick={()=>void action('stop')}><Square size={15}/></button>
  </div>
  <div className="subtitle-tuning"><label>采样节奏<select aria-label="字幕采样节奏" disabled={busy||!s} value={s?.IntervalMs||350} onChange={e=>void action('interval',{value:Number(e.target.value)})}><option value={200}>灵敏 · 200 ms</option><option value={350}>均衡 · 350 ms</option><option value={600}>省电 · 600 ms</option></select></label><span>{s?.Engine?.replace('Cpu','CPU')||'使用当前 OCR 设置'}{s&&s.OcrMs>0&&<b>{Math.round(s.OcrMs)} ms / OCR</b>}</span></div>
  {(error||snapshot.operationError||s?.Error)&&<p className="subtitle-error" role="alert">{error||snapshot.operationError||s?.Error}</p>}
  {s&&running&&s.Recognitions===0&&s.Captures>0&&<p className="subtitle-hint">正在准备首次识别{boot?.settings.ocrGpuAcceleration?'；GPU 首次运行可能需要较长时间':''}。可暂停，已开始的推理会先安全收尾。</p>}
  {s&&s.OcrMs>s.IntervalMs&&running&&<p className="subtitle-hint">识别耗时超过采样间隔，短字幕可能漏读。建议缩小区域或在 OCR 设置中切换 Tiny。</p>}
  <section className="subtitle-feed" aria-label="最近识别文案" aria-live="off">
   {s?.Recent.length?s.Recent.map(c=><article key={c.Id}><span>{String(c.Id).padStart(2,'0')}</span><p>{c.Text}</p></article>):<div className="subtitle-empty"><TextCursorInput size={25} strokeWidth={1.2}/><p>{snapshot.hasRegion?'正在等待字幕画面。':'区域截图 → 框选字幕 → 实时字幕摘录。'}<small>确认选区即开始；不会自动复制。</small></p></div>}
  </section>
  <footer className="subtitle-results"><button className="button dark-button" disabled={busy||!hasText} onClick={()=>void action('copy',{},'已复制纯文案。')}><Copy size={15}/>复制文案</button><button className="button" disabled={busy||!hasText} onClick={()=>void action('edit',{},'已暂停，并打开独立文案快照。')}><TextCursorInput size={15}/>整理文案</button><button className="icon-button" aria-label="导出纯文案 TXT" title="导出 TXT" disabled={busy||!hasText} onClick={()=>void action('export',{},'文案已导出。')}><Download size={17}/></button></footer>
  <details className="subtitle-details"><summary>识别状态与说明<span>{s?.Unsaved?'尚未保存':hasText?'纯文本已保存':'无时间戳'}</span></summary><p>细边框标记识别范围，鼠标可穿透；暂停时保留，结束或关闭时移除。右上角图钉可切换窗口置顶。播放器移动、被遮挡或最小化后，请暂停并重新框选；关闭面板会结束采集。受保护视频可能无法读取。</p>{s&&<p>采样 {s.Captures} 次 · OCR {s.Recognitions} 次 · 未变化跳过 {s.Skipped} 次<br/>最近处理 {Math.round(s.CycleMs)} ms；后台忙碌时不排队。模型随全局 OCR 设置切换。</p>}{hasText&&<code>{s?.Path}</code>}</details>
  <div className="subtitle-bottom"><span role="status">{acting?<><LoaderCircle size={12} className="spin"/>处理中…</>:notice||'仅本地识别 · 纯文案'}</span><button className="text-button" disabled={busy} onClick={()=>hasText?setFresh(true):void action('configure',{fresh:true})}><Plus size={12}/>新任务</button></div>
  {fresh&&<div className="subtitle-confirm" role="alertdialog" aria-label="新建字幕摘录"><b>开始一份新的摘录？</b><p>原文案保存在原文本文件中。整理窗口中的编辑稿请先复制；保存失败时不会清空当前任务。</p><div><button className="button" onClick={()=>setFresh(false)}>返回</button><button className="button accent-button" onClick={()=>{setFresh(false);void action('configure',{fresh:true})}}>新建摘录</button></div></div>}
 </main></>
}
