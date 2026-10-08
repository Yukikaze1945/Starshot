import { useEffect, useRef, useState } from 'react'
import { ArrowUpRight, Check, ChevronRight, Feather, FolderOpen, Gauge, Keyboard, KeyRound, LoaderCircle, Plus, ScanText, Settings2, Sparkles, Sun, Video, X } from 'lucide-react'
import { request } from './bridge'
import { capacityFromStop, stopFromCapacity } from './math'
import { languages } from './OcrLab'
import type { Bootstrap, Hotkey, Settings } from './types'

const tabs = [{id:'capture',label:'截图',icon:ScanText},{id:'hdr',label:'光与色彩',icon:Sun},{id:'keys',label:'快捷键',icon:Keyboard},{id:'translation',label:'OCR 与翻译',icon:Sparkles},{id:'storage',label:'存储',icon:FolderOpen},{id:'system',label:'应用',icon:Settings2}]
const keyLabels:Record<number,[string,string]>={44446:['区域截图','框选、标注、长截图与 GIF 的起点'],44445:['全屏截图','按设置选择目标显示器'],44447:['区域仅复制','框选后只写入剪贴板'],44448:['识别文字','框选文字，进入排版编辑器'],44449:['贴图','把剪贴板图片固定到屏幕'],44450:['恢复贴图','重新打开最近关闭的贴图']}
function Toggle({value,onChange,label}:{value:boolean;onChange:(value:boolean)=>void;label:string}) {
  return <button type="button" role="switch" aria-checked={value} aria-label={label} className={`toggle ${value?'on':''}`} onClick={()=>onChange(!value)}><span/>{value && <Check size={10}/>}</button>
}
function Row({title,description,children}:{title:string;description?:string;children:React.ReactNode}) {return <div className="setting-row"><div><b>{title}</b>{description&&<p>{description}</p>}</div><div className="row-control">{children}</div></div>}

const captureProfiles = [
  {name:'轻量', icon:Feather, tag:'LESS IS MORE', detail:'GDI · SDR', description:'低占用，随手捕捉。'},
  {name:'标准', icon:Gauge, tag:'EVERYDAY', detail:'WGC · SDR', description:'快速响应，日常使用。'},
  {name:'高质量 HDR', icon:Sun, tag:'ALL THE LIGHT', detail:'WGC · HDR / 广色域', description:'保留高光与丰富色彩。'},
  {name:'单帧 HDR 视频', icon:Video, tag:'ONE FRAME, THREE SECONDS', detail:'MP4 · HDR / SDR', description:'一帧停留 3 秒，同时保存图片。'},
]
function CaptureModePicker({value,disabled,onChange}:{value:number;disabled:boolean;onChange:(value:number)=>void}) {
  return <section className="capture-profile-section">
    <div className="capture-profile-heading"><h3>截图模式</h3><span>也可在托盘右键快速切换</span></div>
    <fieldset className="capture-profile-picker" aria-label="截图模式" disabled={disabled}>
      {captureProfiles.map((profile,index)=><label key={profile.name} className={`capture-profile ${value===index?'selected':''}`}>
        <input className="sr-only" type="radio" name="capture-profile" value={index} checked={value===index} onChange={()=>onChange(index)}/>
        <span className="capture-profile-top"><profile.icon size={23} strokeWidth={1.5}/><span className="capture-profile-index">0{index+1}</span><Check className="capture-profile-check" size={15}/></span>
        <span className="eyebrow">{profile.tag}</span><b>{profile.name}</b><span className="capture-profile-detail">{profile.detail}</span><small>{profile.description}</small>
      </label>)}
    </fieldset>
    <p className="capture-profile-footnote">轻量与标准保存 SDR；高质量 HDR 保留浮点画面；单帧 HDR 视频只捕获一次，画面停留 3 秒并保存配套图片，HDR 不可用时回退到 SDR。</p>
  </section>
}

export function SettingsPanel({initialTab,boot,update,notify}:{initialTab:string;boot:Bootstrap;update:(value:Bootstrap)=>void;notify:(text:string,error?:boolean)=>void}) {
  const [tab,setTab]=useState(initialTab), [saving,setSaving]=useState(false)
  const [capacity,setCapacity]=useState(boot.settings.capacity)
  const [endpoint,setEndpoint]=useState(boot.settings.endpoint),[model,setModel]=useState(boot.settings.model),[apiKey,setApiKey]=useState(''),[target,setTarget]=useState(boot.settings.targetLanguage)
  const [models,setModels]=useState<string[]>([]),[modelsBusy,setModelsBusy]=useState(false),[apiBusy,setApiBusy]=useState(false)
  const [recording,setRecording]=useState<number|null>(null),[keyBusy,setKeyBusy]=useState(false)
  const cancellation=useRef<AbortController|null>(null)
  const latest=useRef(boot);latest.current=boot
  const s=boot.settings
  useEffect(()=>()=>cancellation.current?.abort(),[])
  const save=async(key:keyof Settings,value:unknown)=> {
    setSaving(true)
    try { const settings=await request<Settings>('settings.set',{key,value}); update({...latest.current,settings}) }
    catch(e){notify((e as Error).message,true)}
    finally{setSaving(false)}
  }
  const [checkingUpdate, setCheckingUpdate] = useState(false)
  const boolRow=(key:keyof Settings,title:string,description:string)=><Row title={title} description={description}><Toggle label={title} value={s[key] as boolean} onChange={value=>void save(key,value)}/></Row>
  const discover=async()=> {
    cancellation.current?.abort(); const controller=new AbortController();cancellation.current=controller;setModelsBusy(true)
    try {const values=await request<string[]>('translation.models',{endpoint,apiKey},controller.signal);setModels(values);notify(values.length?`发现 ${values.length} 个模型。`:'服务没有返回模型，可手动输入模型名。');if(values.length&&!values.includes(model))setModel(values[0])}
    catch(e){if(!controller.signal.aborted)notify((e as Error).message,true)}
    finally{if(cancellation.current===controller){setModelsBusy(false);cancellation.current=null}}
  }
  const configure=async()=> {
    setApiBusy(true)
    try {const response=await request<{settings:Settings;hasApiKey:boolean}>('translation.configure',{endpoint,model,apiKey,targetLanguage:target});setApiKey('');update({...latest.current,...response});notify('翻译服务已保存。')}
    catch(e){notify((e as Error).message,true)}finally{setApiBusy(false)}
  }
  const keyUpdate=async(hotkeyId:number,modifiers:number,key:number)=> {
    setKeyBusy(true);setRecording(null)
    try{const hotkeys=await request<Hotkey[]>('hotkey.set',{hotkeyId,modifiers,key});update({...latest.current,hotkeys});notify('快捷键已更新。')}
    catch(e){notify((e as Error).message,true)}finally{setKeyBusy(false)}
  }
  useEffect(()=> {
    if(recording===null)return
    let committed=false
    let modifierKey:number|null=null
    const commit=(modifiers:number,key:number)=> {
      if(committed||key<=0||key>=255)return
      committed=true
      void keyUpdate(recording,modifiers,key)
    }
    const capture=(e:KeyboardEvent)=> {
      e.preventDefault();e.stopPropagation()
      if(e.repeat||e.isComposing||e.keyCode===229)return
      if(['Control','Alt','Shift','Meta'].includes(e.key)){modifierKey=e.keyCode;return}
      modifierKey=null
      const modifiers=(e.altKey?1:0)|(e.ctrlKey?2:0)|(e.shiftKey?4:0)|(e.metaKey?8:0)
      commit(modifiers,e.keyCode)
    }
    const release=(e:KeyboardEvent)=> {
      e.preventDefault();e.stopPropagation()
      if(modifierKey===e.keyCode)commit(0,e.keyCode)
    }
    document.addEventListener('keydown',capture,true)
    document.addEventListener('keyup',release,true)
    return()=>{document.removeEventListener('keydown',capture,true);document.removeEventListener('keyup',release,true)}
  },[recording])
  return <><div className="settings-tabs">{tabs.map(item=><button key={item.id} className={tab===item.id?'active':''} onClick={()=>{setTab(item.id);setRecording(null)}}><item.icon size={16}/>{item.label}</button>)}</div><div className="settings-content">
    {tab==='capture'&&<><div className="settings-heading"><span className="eyebrow">MAKE IT YOURS.</span><h2>每次截图，都按你的习惯。</h2></div><CaptureModePicker value={s.captureMode} disabled={saving} onChange={value=>void save('captureMode',value)}/><Row title="SDR 文件格式"><select aria-label="SDR 文件格式" value={s.sdrFormat} onChange={e=>void save('sdrFormat',+e.target.value)}><option value={0}>PNG</option><option value={1}>AVIF</option><option value={2}>JPEG XL</option></select></Row><Row title="HDR 文件格式" description="在高质量 HDR 与 HDR 视频模式下生效。"><select aria-label="HDR 文件格式" value={s.hdrFormat} onChange={e=>void save('hdrFormat',+e.target.value)}><option value={0}>AVIF</option><option value={1}>JPEG XL</option></select></Row><Row title="视频编码" description="仅影响 HDR 视频；配套单帧图使用下方编码品质。"><select aria-label="视频编码" value={s.videoCodec} onChange={e=>void save('videoCodec',+e.target.value)}><option value={0}>HEVC（默认）</option><option value={1}>AV1</option></select></Row><Row title="编码品质" description="只影响图片文件与视频的配套单帧图，不影响视频编码。"><div className="segmented">{['均衡','高质量','无损'].map((label,i)=><button key={i} aria-pressed={s.quality===i} className={s.quality===i?'active':''} onClick={()=>void save('quality',i)}>{label}</button>)}</div></Row>{boolRow('autoCopy','截图后自动复制','截图落盘后，同时放进剪贴板。')}{boolRow('muteFullscreen','全屏时安静一点','游戏或全屏应用中隐藏截图通知。')}<Row title="全屏截图目标"><select aria-label="截图显示器来源" value={s.monitorSource} onChange={e=>void save('monitorSource',+e.target.value)}><option value={0}>前台窗口所在显示器</option><option value={1}>鼠标所在显示器</option></select></Row><div className="settings-note">标注、长图和 GIF 均从区域选区工具栏进入。切换模式不会改变文件格式、编码品质或 HDR 参数。</div></>}
    {tab==='hdr'&&<><div className="settings-heading"><span className="eyebrow">LIGHT, WITHOUT COMPROMISE.</span><h2>让高光，仍然是高光。</h2></div>{s.captureMode!==2&&s.captureMode!==3&&<div className="capture-hdr-notice"><Sun size={19}/><div><b>当前使用{captureProfiles[s.captureMode]?.name}模式</b><p>此模式只捕获 SDR。这些 HDR 与色彩设置会保留，切回高质量 HDR 后生效。</p></div><button className="text-button" disabled={saving} onClick={()=>void save('captureMode',2)}>切换<ArrowUpRight size={14}/></button></div>}{boolRow('ultraHdr','同时保存 Ultra HDR JPEG','SDR 基图与增益图，让画面适配更多屏幕。')}{boolRow('colorManagement','显示器色彩管理','使用显示器的色彩配置文件。')}{boolRow('deleteSdrHdr','识别实际 SDR 内容','内容没有 HDR 高光时，转为 SDR 并删除 HDR 文件。')}<div className="capacity-control"><div><span className="eyebrow">HDR CAPACITY MAX</span><div className="capacity-title"><h3>显示容量</h3><div className="segmented"><button className={!s.capacityManual?'active':''} aria-pressed={!s.capacityManual} onClick={()=>void save('capacityManual',false)}>自动</button><button className={s.capacityManual?'active':''} aria-pressed={s.capacityManual} onClick={()=>void save('capacityManual',true)}>手动</button></div></div></div><div className={`capacity-value ${!s.capacityManual?'disabled':''}`}><span>{capacity.toFixed(1)}</span><sup>×</sup><small>{s.capacityManual?'手动 headroom':'自动沿用内容的最大增益'}</small></div><input type="range" min={1} max={5} step={.01} value={stopFromCapacity(capacity)} aria-label="HDR 显示容量倍数" disabled={!s.capacityManual} onChange={e=>setCapacity(capacityFromStop(+e.target.value))} onPointerUp={()=>void save('capacity',capacity)} onKeyUp={()=>void save('capacity',capacity)}/><div className="range-labels"><span>2×</span><span>4×</span><span>8×</span><span>16×</span><span>32×</span></div><div className="capacity-presets">{[4,8,12,16].map(value=><button key={value} disabled={!s.capacityManual} className={capacity===value?'active':''} onClick={()=>{setCapacity(value);void save('capacity',value)}}>{value}×</button>)}</div><p>值越低，有限 HDR headroom 的屏幕越积极应用增益图。过低可能使高光裁切或触发系统色调映射。只修改容量元数据。</p></div><Row title="SDR 白基准" description="0 为跟随显示器；手动范围 1–1000 nit。"><input type="number" aria-label="SDR 白基准 nit" min={0} max={1000} defaultValue={s.sdrWhite} onBlur={e=>void save('sdrWhite',+e.target.value)}/><small>nit</small></Row></>}
    {tab==='keys'&&<><div className="settings-heading"><span className="eyebrow">LESS CLICKING. MORE MAKING.</span><h2>把常用动作，放在指尖。</h2><p>点击快捷键后，按下任意单键或组合键。再次点击可取消录入。</p></div><div className="hotkey-list">{boot.hotkeys.map(key=><div key={key.id} className="hotkey-row"><div><b>{keyLabels[key.id]?.[0]}</b><small>{keyLabels[key.id]?.[1]}</small>{key.error&&<span className="error-text">快捷键被占用，点击重新设置。</span>}</div><button disabled={keyBusy} className={`hotkey-recorder ${recording===key.id?'recording':''}`} onClick={()=>setRecording(recording===key.id?null:key.id)}>{recording===key.id?'按下按键 · 点击取消':key.key?key.text:'未设置'}</button><button className="icon-button" aria-label={`删除${keyLabels[key.id]?.[0]}快捷键`} disabled={keyBusy} onClick={()=>void keyUpdate(key.id,0,0)}><X size={14}/></button></div>)}</div><div className="settings-note"><Keyboard size={18}/>全局快捷键在窗口收起到托盘时同样有效。</div></>}
    {tab==='translation'&&<><div className="settings-heading"><span className="eyebrow">WORDS, ACROSS WORLDS.</span><h2>从识别，到理解。</h2></div><Row title="文字识别引擎" description={boot.oneOcrReady?'OneOCR 已就绪':'未配置 OneOCR 时自动使用系统引擎。'}><select aria-label="OCR 引擎" value={s.ocrEngine} onChange={e=>void save('ocrEngine',+e.target.value)}><option value={0}>OneOCR（优先）</option><option value={1}>Windows OCR</option></select></Row>{boolRow('autoCopyOcr','OCR 识别后自动复制','默认先在独立文字窗口排版；开启后识别完成时同时复制纯文本。')}<button className="text-button engine-link" onClick={()=>void(async()=>{try{const result=await request<{ready:boolean;settings:Settings}>('utility.ocrEngine');update({...latest.current,oneOcrReady:result.ready,settings:result.settings})}catch(e){notify((e as Error).message,true)}})()}>获取或管理 OneOCR <ArrowUpRight size={15}/></button><div className="api-form"><div className="api-heading"><Sparkles size={21}/><h3>连接你的大模型</h3><span>OpenAI compatible</span></div><label>API 地址<input placeholder="https://…/v1/chat/completions" type="url" value={endpoint} onChange={e=>{setEndpoint(e.target.value);setModels([])}}/></label><label>API Key<div className="key-input"><KeyRound size={16}/><input type="password" autoComplete="new-password" placeholder={boot.hasApiKey?'已加密保存 · 留空保留现有密钥':'输入 API Key'} value={apiKey} onChange={e=>setApiKey(e.target.value)}/>{boot.hasApiKey&&<Check size={16}/>}</div></label><label>模型<div className="model-field"><input aria-label="翻译模型" list="model-options" placeholder="输入模型名称，或自动发现" value={model} onChange={e=>setModel(e.target.value)}/><datalist id="model-options">{models.map(value=><option key={value} value={value}/>)}</datalist><button className="button" disabled={modelsBusy||!endpoint} onClick={()=>void discover()}>{modelsBusy?<LoaderCircle size={16} className="spin"/>:<Sparkles size={16}/>}自动发现</button></div>{models.length>0&&<small className="model-count">发现 {models.length} 个模型 · 在模型输入框中选择或搜索。</small>}</label><label>默认翻译为<select value={target} onChange={e=>setTarget(e.target.value)}>{languages.map(language=><option key={language}>{language}</option>)}</select></label><div className="api-footer"><p>密钥由 Windows DPAPI 加密。<br/>仅在点击翻译时发送编辑后的文本。</p><button className="button dark-button" disabled={apiBusy||modelsBusy||!endpoint||!model} onClick={()=>void configure()}>{apiBusy?<LoaderCircle className="spin" size={16}/>:<Check size={16}/>}保存连接</button></div>{boot.hasApiKey&&<button className="text-button muted" onClick={()=>void(async()=>{try{await request('translation.clearKey');update({...latest.current,hasApiKey:false});notify('已移除保存的密钥。')}catch(e){notify((e as Error).message,true)}})()}>移除已保存的密钥</button>}</div></>}
    {tab==='storage'&&<><div className="settings-heading"><span className="eyebrow">A PLACE FOR EVERY FRAME.</span><h2>把画面收好。</h2></div><div className="folder-card"><FolderOpen size={25}/><div><b>截图保存位置</b><code>{s.screenshotFolder}</code></div><button className="button" onClick={()=>void(async()=>{try{const settings=await request<Settings>('settings.pickFolder');update({...latest.current,settings})}catch(e){notify((e as Error).message,true)}})()}>更改<ChevronRight size={14}/></button></div>{boolRow('subfolders','按文件夹归类','同时递归读取截图库中的子文件夹。')}<label className="setting-field">子文件夹模板<input defaultValue={s.subfolderPattern} onBlur={e=>void save('subfolderPattern',e.target.value)}/></label><label className="setting-field">全屏截图文件名<input defaultValue={s.filenamePattern} onBlur={e=>void save('filenamePattern',e.target.value)}/></label><label className="setting-field">区域截图文件名<input defaultValue={s.regionFilenamePattern} onBlur={e=>void save('regionFilenamePattern',e.target.value)}/></label><div className="template-tokens">{['{title}','{process}','{timestamp}','{date}','{width}','{height}'].map(value=><code key={value}>{value}</code>)}</div><div className="section-heading"><h3>其他截图库文件夹</h3><button className="text-button" onClick={()=>void(async()=>{try{const settings=await request<Settings>('settings.addFolder');update({...latest.current,settings})}catch(e){notify((e as Error).message,true)}})()}><Plus size={15}/>添加</button></div>{s.extraFolders.map((folder,index)=><div className="extra-folder" key={folder}><code>{folder}</code><button className="icon-button" aria-label="移除此截图库文件夹" onClick={()=>void(async()=>{try{const settings=await request<Settings>('settings.removeFolder',{index});update({...latest.current,settings})}catch(e){notify((e as Error).message,true)}})()}><X size={14}/></button></div>)}<div className="settings-note">移除文件夹只影响图库，不删除其中的图片。</div></>}
    {tab==='system'&&<><div className="settings-heading"><span className="eyebrow">SMALL DETAILS. BIG DIFFERENCE.</span><h2>舒服一点，顺手一点。</h2></div><Row title="界面主题"><div className="segmented">{['系统','浅色','深色'].map((label,i)=><button key={label} className={s.theme===i?'active':''} aria-pressed={s.theme===i} onClick={()=>void save('theme',i)}>{label}</button>)}</div></Row>{boolRow('startupHidden','开机启动时收起','使用已配置的自启动入口时直接进入托盘。')}{boolRow('autoUpdate','自动检查更新','从公开下载仓库检查版本，确认后才下载并安装。')}{boolRow('previewUpdates','接收预览版','预览构建默认开启；关闭后只接收正式版。')}<Row title="当前版本" description={boot.version}><button className="button" disabled={checkingUpdate} onClick={()=>void(async()=>{setCheckingUpdate(true);try{const result=await request<{available:boolean;latestTag:string|null}>('utility.checkUpdate');if(!result.available)notify(result.latestTag?'当前已是最新版本。':'此更新渠道暂无已发布版本。')}catch(e){notify((e as Error).message,true)}finally{setCheckingUpdate(false)}})()}>{checkingUpdate?<LoaderCircle className="spin" size={15}/>:<ArrowUpRight size={15}/>}检查更新</button></Row>{boolRow('highPriority','高优先级进程','按当前原生策略运行截图任务。')}<div className="about-card"><div className="about-wordmark">starshot<span>✳</span></div><p>为画面，也为那些一闪而过的想法。</p><small>{boot.version} · .NET 10 / WebView2 / React 19</small><div className="math-signature">DESIGNED IN THE SPACES BETWEEN <span>φ</span> & <span>π</span></div></div></>}
  </div><div className="settings-status"><span className="light-dot"/>{saving?'正在保存…':'更改即时保存'}<span>STARSHOT / PREFERENCES</span></div></>
}
