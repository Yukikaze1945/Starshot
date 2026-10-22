// Software-rendered bridge integration tests; never loads a model or captures the desktop.
import {createRequire} from 'node:module'
import {createServer} from 'node:http'
import {readFile,mkdtemp,rm,copyFile,mkdir} from 'node:fs/promises'
import {resolve,extname,sep} from 'node:path'
import {tmpdir} from 'node:os'
import {fileURLToPath} from 'node:url'
import assert from 'node:assert/strict'
const {chromium}=createRequire(import.meta.url)(process.argv[2])
const root=fileURLToPath(new URL('../../src/Starshot.WebUI/dist/',import.meta.url))
const server=createServer(async(req,res)=>{const path=resolve(root,'.'+decodeURIComponent(new URL(req.url,'http://localhost').pathname.replace(/^\/$/,'/index.html')));if(!path.startsWith(root.replace(/[\\/]$/,'')+sep)){res.writeHead(403);res.end();return}try{res.setHeader('Content-Type',({'.html':'text/html','.js':'text/javascript','.css':'text/css','.woff2':'font/woff2'})[extname(path)]||'application/octet-stream');res.end(await readFile(path))}catch{res.writeHead(404);res.end()}})
await new Promise(r=>server.listen(30000+Math.floor(Math.random()*20000),'127.0.0.1',r))
let browser;const temp=await mkdtemp(resolve(tmpdir(),'starshot-workspace-ui-')),reports=[]
try{
 browser=await chromium.launch({headless:true,executablePath:process.argv[3],args:['--disable-gpu','--disable-software-rasterizer']})
 for(const [width,height,theme] of [[1280,900,1],[960,640,2]]){
  const context=await browser.newContext({viewport:{width,height},deviceScaleFactor:2})
  const page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));page.on('dialog',d=>d.accept())
  await page.addInitScript(({theme})=>{
   const svg=color=>'data:image/svg+xml;base64,'+btoa(`<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="800"><rect width="1200" height="800" fill="${color}"/><circle cx="620" cy="390" r="240" fill="#ddf369"/><text x="65" y="100" font-size="42" fill="#f5f5e9">Starshot / Light in layers</text></svg>`)
   const source=svg('#405548'),second=svg('#5d526a'),calls=window.calls=[],handlers=[],drafts={};let current='a',backend='api',download='notInstalled'
   const settings={screenshotFolder:'',extraFolders:[],subfolders:false,subfolderPattern:'',filenamePattern:'',regionFilenamePattern:'',autoCopy:false,autoCopyOcr:false,ocrModel:'tiny',ocrGpuAcceleration:false,ultraHdr:false,capacityManual:false,capacity:8,captureMode:2,videoCodec:0,sdrFormat:0,hdrFormat:0,quality:1,colorManagement:false,deleteSdrHdr:false,sdrWhite:0,monitorSource:0,muteFullscreen:false,endpoint:'',model:'',targetLanguage:'简体中文',translationBackend:'api',theme,language:'',autoUpdate:false,previewUpdates:false,startupHidden:false,highPriority:false}
   const items=['a','b'].map((id,i)=>({id,name:i?'Reference.png':'Light.png',format:'PNG',bytes:10000,timestamp:new Date().toISOString(),kind:'image'}))
   const document=id=>({name:id==='b'?'Reference.png':'Light.png',width:1200,height:800,src:id==='b'?second:source,hdr:true,referenceWhite:203,fingerprint:id,carrier:'FP16 HDR',previewMax:4096})
   window.requestCaptureClose=()=>handlers.forEach(h=>h({data:{type:'event',name:'capture.closeRequested',data:null}}))
   window.emitCapture=()=>handlers.forEach(h=>h({data:{type:'event',name:'workspace.capture',data:null}}))
   window.chrome={webview:{addEventListener:(_,h)=>handlers.push(h),postMessage:m=>{
    calls.push(m);let result=null,delay=0,failure=null
    switch(m.method){
     case 'app.bootstrap':result={version:'Workspace UI test',protocol:1,hasApiKey:false,settings,hotkeys:[],chrome:{enabled:true,inset:138,height:48,active:true,maximized:false}};break
     case 'clipboard.read':result={text:'',image:source,items:[],historyEnabled:false};break
     case 'workspace.qr':case 'clipboard.qr':break
     case 'window.close':window.closedByRequest=true;break
     case 'app.ready':case 'workspace.close':case 'workspace.analysis.close':case 'workspace.discardPending':break
     case 'library.list':result={items,total:2,offset:0};break
     case 'library.thumbnail':result={src:source,width:1200,height:800};break
     case 'workspace.open':current=m.params.id;result=document(current);break
     case 'workspace.pending':current='capture';result={...document(current),initialTool:new URLSearchParams(location.search).get('tool')||'polygon'};break
     case 'workspace.compare':result=document(m.params.id);break
     case 'workspace.difference':result={available:true,mae:.1,rmse:.2,max:.3,units:'nits / linear RGB',approximate:false};break
     case 'workspace.pixel':result={...m.params,r:30,g:60,b:90,nits:200};break
     case 'workspace.draft.load':result=drafts[current]||null;break
     case 'workspace.draft.save':drafts[current]=m.params.draft;break
     case 'workspace.mask':result={src:source};delay=m.params.kind==='smart'?500:20;break
     case 'workspace.copy':window.lastCopy=m.params;break
     case 'workspace.export':result='saved';window.lastExport=m.params;break
     case 'workspace.analysis':result={stats:{Min:0,Max:1400,Average:180,P99:800,SampleCount:960000},referenceWhite:203,approximate:false,sampleStep:1,scale:2000,waveform:source,heatmap:second,gamutMap:second,gamut:{Samples:960000,Black:0,Invalid:0,Srgb:80,DisplayP3Only:15,Bt2020Only:4,Outside2020:1,Density:Array.from({length:16384},(_,i)=>i%97===0?5:0),Grid:128,Approximate:false}};break
     // Synthetic text boxes only. No OCR or translation model is called.
     case 'workspace.ocr':result=[{id:'line',text:'Bright highlights',x:120,y:130,width:410,height:55},{id:'empty-icon',text:' ',x:540,y:130,width:8,height:5}];break
     case 'translation.formatted':if(m.params.segments.some(s=>!s.text.trim()))throw new Error('Blank OCR passed to translation');delay=window.translationDelay||0;if(window.failTranslation)failure='请先配置翻译 API 或安装并启用离线模型。';else result=m.params.segments.map(s=>({...s,text:'高光层次'}));break
     case 'ocr.models.status':result={selected:'tiny',state:'notInstalled',installed:false,downloadedBytes:0,totalBytes:31114837,error:null,directory:'',version:'2'};break
     case 'models.runtime.status':result={loaded:false,resident:false,gpuEnabled:false,activeBackend:null,state:'idle',error:null,gpuRuntimeAvailable:true,apiEnabled:false,apiPort:43182,apiUrl:'http://127.0.0.1:43182/v1',apiActiveRequests:0,idleSeconds:60};break
     case 'models.list':result={translationBackend:backend,localLoaded:false,items:[{Id:'index-q5km',Name:'Index-Translate-2B · Q5_K_M',Task:'translation',Bytes:1454787072,Downloaded:download==='downloading'?400000000:0,State:download,Error:null,License:'Apache-2.0',Installed:download==='installed'},{Id:'u2netp',Name:'U²-Net compact',Task:'segmentation',Bytes:4574861,Downloaded:4574861,State:'installed',Installed:true,License:'Apache-2.0'}]};break
     case 'models.download':download='downloading';break
     case 'models.cancel':download='canceled';break
     case 'models.translation':backend=m.params.backend;break
     case 'models.verify':case 'models.delete':break
     case 'request.cancel':return
     default:throw new Error('Unexpected mock bridge method '+m.method)
    }
    setTimeout(()=>handlers.forEach(h=>h({data:{type:'response',id:m.id,ok:!failure,result,error:failure}})),delay)
   }}}
  },{theme})
  await page.goto(`http://127.0.0.1:${server.address().port}`)
  await page.locator('.shot-card').first().click()
  const dialog=page.getByRole('dialog',{name:'图像工作区'});await dialog.waitFor();await page.waitForFunction(()=>!document.querySelector('.workspace-busy'))
  await page.getByRole('button',{name:'识别二维码',exact:true}).click();await page.waitForFunction(()=>window.calls.some(m=>m.method==='workspace.qr'));await page.waitForFunction(()=>!document.querySelector('.workspace-busy'))
  const header=page.locator('.image-workspace-header')
  const headerRegions=await header.locator('button,button svg').evaluateAll(elements=>elements.map(el=>getComputedStyle(el).getPropertyValue('app-region')||getComputedStyle(el).getPropertyValue('-webkit-app-region')))
  assert.ok(headerRegions.length>=5&&headerRegions.every(value=>value==='no-drag'),'Buttons and icons explicitly opt out of WebView drag hit testing')
  await page.getByRole('button',{name:'复制',exact:true}).click();await page.waitForFunction(()=>window.lastCopy!==undefined)
  await page.getByRole('button',{name:'导出 PNG',exact:true}).click();await page.waitForFunction(()=>window.lastExport!==undefined)
  await page.getByRole('button',{name:'下一张',exact:true}).click();await page.waitForFunction(()=>document.querySelector('.workspace-document-name b')?.textContent==='Reference.png');await page.waitForFunction(()=>!document.querySelector('.workspace-busy'))
  await page.getByRole('button',{name:'上一张',exact:true}).click();await page.waitForFunction(()=>document.querySelector('.workspace-document-name b')?.textContent==='Light.png');await page.waitForFunction(()=>!document.querySelector('.workspace-busy'))
  await page.getByRole('button',{name:'关闭图像工作区',exact:true}).click();await dialog.waitFor({state:'detached'})
  await page.locator('.shot-card').first().click();await dialog.waitFor();await page.waitForFunction(()=>!document.querySelector('.workspace-busy'))
  await page.getByRole('combobox',{name:'选择对比图片'}).selectOption('b')
  await page.getByRole('button',{name:'并排',exact:true}).click();await page.locator('.compare-side img').waitFor();assert.equal(await page.locator('.compare-side img').count(),1)
  await page.getByRole('button',{name:'分割',exact:true}).click();await page.getByRole('slider',{name:'对比分割线'}).fill('37');assert.match(await page.locator('.compare-raster').getAttribute('style'),/63%/)
  await page.getByRole('combobox',{name:'选择对比图片'}).selectOption('')
  await page.getByRole('button',{name:'多边形抠图',exact:true}).click()
  const frame=await page.locator('.image-frame').boundingBox();assert.ok(frame.width>100)
  for(const [x,y] of [[.2,.2],[.8,.2],[.6,.8]])await page.mouse.click(frame.x+frame.width*x,frame.y+frame.height*y)
  await page.keyboard.press('Control+z');await page.getByText('2 个顶点',{exact:true}).waitFor()
  await page.keyboard.press('Control+y');await page.getByText('3 个顶点',{exact:true}).waitFor()
  await page.keyboard.press('Enter');await page.waitForFunction(()=>window.calls.some(m=>m.method==='workspace.mask'&&m.params.kind==='polygon'))
  await page.waitForFunction(()=>!document.querySelector('.workspace-busy'));await page.getByRole('button',{name:'复制',exact:true}).click();await page.waitForFunction(()=>window.lastCopy!==undefined)
  assert.deepEqual(await page.evaluate(()=>Object.keys(window.lastCopy)),['overlays'])
  await page.getByRole('button',{name:'撤销图像编辑'}).click();await page.waitForFunction(()=>!document.querySelector('.workspace-busy'))
  await page.getByRole('button',{name:'智能抠图',exact:true}).click();await page.getByRole('button',{name:'取消图像操作'}).click();await page.waitForFunction(()=>!document.querySelector('.workspace-busy'))
  await page.getByRole('button',{name:'魔棒去背景',exact:true}).click();await page.mouse.click(frame.x+frame.width*.3,frame.y+frame.height*.3);await page.waitForFunction(()=>window.calls.some(m=>m.method==='workspace.mask'&&m.params.kind==='wand'));await page.waitForFunction(()=>!document.querySelector('.workspace-busy'))
  await page.getByRole('button',{name:'自由套索',exact:true}).click();await page.mouse.move(frame.x+frame.width*.2,frame.y+frame.height*.2);await page.mouse.down();for(const [x,y] of [[.8,.2],[.8,.8],[.2,.8]])await page.mouse.move(frame.x+frame.width*x,frame.y+frame.height*y,{steps:4});await page.mouse.up();await page.waitForFunction(()=>!document.querySelector('.workspace-busy'))
  await page.getByRole('button',{name:'HDR 与色域分析'}).click();await page.getByText('色域区域与像素占比',{exact:true}).waitFor()
  await page.getByRole('checkbox',{name:'亮度热力图',exact:true}).check();await page.locator('.heatmap-raster').waitFor()
  await page.getByRole('checkbox',{name:'色域区域叠层',exact:true}).check();assert.equal(await page.locator('.heatmap-raster').count(),0);await page.locator('.gamut-raster').waitFor()
  await page.getByRole('button',{name:'导出 PNG',exact:true}).click();await page.waitForFunction(()=>window.lastExport!==undefined);assert.deepEqual(await page.evaluate(()=>Object.keys(window.lastExport)),['overlays'])
  await page.screenshot({path:resolve(temp,`analysis-${width}.png`)})
  if(process.env.STARSHOT_UI_SAMPLE_DIR){await mkdir(process.env.STARSHOT_UI_SAMPLE_DIR,{recursive:true});await copyFile(resolve(temp,`analysis-${width}.png`),resolve(process.env.STARSHOT_UI_SAMPLE_DIR,`analysis-${width}.png`))}
  await page.getByRole('button',{name:'关闭分析并释放缓存'}).click()
  await page.getByRole('button',{name:'原位翻译覆盖',exact:true}).click();await page.getByRole('textbox',{name:'覆盖文字 1'}).waitFor()
  assert.equal(await page.getByRole('textbox',{name:'覆盖文字 1'}).inputValue(),'高光层次','Gallery tool runs OCR and translation immediately')
  await page.evaluate(()=>window.failTranslation=true);await page.getByRole('button',{name:'原位翻译覆盖',exact:true}).click();await page.getByRole('alert').filter({hasText:'请先配置翻译 API'}).waitFor()
  assert.equal(await page.getByRole('textbox',{name:'覆盖文字 1'}).inputValue(),'高光层次','Failed translation retains existing overlays')
  await page.evaluate(()=>window.failTranslation=false);await page.getByRole('button',{name:'重新翻译',exact:true}).click();await page.waitForFunction(()=>!document.querySelector('.workspace-busy'))
  await page.getByRole('textbox',{name:'覆盖文字 1'}).fill('可编辑的翻译')
  await page.getByRole('button',{name:'导出 PNG',exact:true}).click();await page.waitForFunction(()=>window.lastExport.overlays[0]?.text==='可编辑的翻译')
  await page.getByRole('button',{name:'关闭图像工作区'}).click();await dialog.waitFor({state:'detached'})
  // Restore incomplete vertices, and prove a capture replacement flushes the <600ms draft.
  await page.locator('.shot-card').first().click();await dialog.waitFor();await page.waitForFunction(()=>!document.querySelector('.workspace-busy'))
  await page.getByRole('button',{name:'多边形抠图',exact:true}).click()
  const bounds=await page.locator('.image-frame').boundingBox();await page.mouse.click(bounds.x+bounds.width*.3,bounds.y+bounds.height*.3)
  await page.evaluate(()=>window.emitCapture());await page.waitForTimeout(250)
  assert.equal(await page.evaluate(()=>window.calls.some(m=>m.method==='workspace.pending')),false,'A stale capture event must never redirect the gallery')
  await page.waitForFunction(()=>window.calls.some(m=>m.method==='workspace.draft.save'&&m.params.draft.points.length===1))
  await page.getByRole('button',{name:'关闭图像工作区'}).click();await dialog.waitFor({state:'detached'})
  await page.locator('.shot-card').first().click();await dialog.waitFor();await page.getByText('1 个顶点',{exact:true}).waitFor();await page.keyboard.press('Control+z');await page.getByRole('button',{name:'关闭图像工作区'}).click();await dialog.waitFor({state:'detached'})
  await page.getByRole('button',{name:'剪贴板',exact:true}).click();await page.getByRole('button',{name:'识别二维码',exact:true}).click();await page.waitForFunction(()=>window.calls.some(m=>m.method==='clipboard.qr'))
  await page.getByRole('button',{name:'模型管理库',exact:true}).click();await page.getByRole('button',{name:'下载 Q5_K_M'}).click();await page.getByRole('button',{name:'取消下载',exact:true}).click();await page.getByText('已取消',{exact:true}).waitFor()
  await page.getByRole('button',{name:'校验 U²-Net compact'}).click();await page.getByRole('button',{name:'删除 U²-Net compact'}).click()
  const layout=await page.evaluate(()=>({width:innerWidth,scroll:document.documentElement.scrollWidth,buttons:[...document.querySelectorAll('.rail-button')].filter(b=>{const r=b.getBoundingClientRect();return r.x<0||r.right>innerWidth}).length}))
  assert.ok(layout.scroll<=width+1);assert.equal(layout.buttons,0);assert.deepEqual(errors,[])
  reports.push({width,height,theme,deviceScaleFactor:2,checks:'workspace and clipboard QR actions, internal viewer, split/side, polygon undo/redo, mask export, HDR/gamut UI, direct gallery translation/retry, draft persistence, stale capture rejection, model controls',errors})
  // The capture surface is independent: it never mounts the gallery or scans files.
  for(const initialTool of ['polygon','translate']){
   await page.goto(`http://127.0.0.1:${server.address().port}/?surface=capture&tool=${initialTool}`)
   const editor=page.getByRole('dialog',{name:'选区截图编辑'});await editor.waitFor();await page.waitForFunction(()=>!document.querySelector('.image-stage-message'))
   assert.equal(await page.locator('.shot-card,.rail').count(),0)
   assert.equal(await page.getByRole('combobox',{name:'选择对比图片'}).count(),0)
   assert.equal(await page.getByRole('button',{name:'上一张',exact:true}).count(),0)
   assert.equal(await page.evaluate(()=>window.calls.some(m=>m.method==='library.list'||m.method==='library.thumbnail')),false)
   if(initialTool==='translate'){
    await page.getByRole('textbox',{name:'覆盖文字 1'}).waitFor();assert.equal(await page.getByRole('textbox',{name:'覆盖文字 1'}).inputValue(),'高光层次')
    assert.equal(await page.evaluate(()=>window.calls.filter(m=>m.method==='workspace.ocr').length),1)
    if(width===1280&&process.env.STARSHOT_CAPTURE_SAMPLE_DIR){await mkdir(process.env.STARSHOT_CAPTURE_SAMPLE_DIR,{recursive:true});const sample=resolve(temp,'capture-translation.png');await page.screenshot({path:sample});await copyFile(sample,resolve(process.env.STARSHOT_CAPTURE_SAMPLE_DIR,'capture-translation.png'))}
    await page.evaluate(()=>window.failTranslation=true);await page.getByRole('button',{name:'原位翻译覆盖',exact:true}).click();await page.getByRole('button',{name:'翻译设置',exact:true}).click()
    const config=page.getByRole('dialog',{name:'翻译设置'});await config.waitFor();await page.keyboard.press('Escape');await config.waitFor({state:'detached'})
    assert.equal(await page.evaluate(()=>window.closedByRequest===true),false,'Esc in settings does not close capture editor')
    await page.evaluate(()=>window.failTranslation=false)
    await page.getByRole('button',{name:'撤销图像编辑'}).click();assert.equal(await page.getByRole('textbox',{name:'覆盖文字 1'}).count(),0)
    await page.evaluate(()=>window.translationDelay=600);await page.getByRole('button',{name:'原位翻译覆盖',exact:true}).click()
    await page.waitForFunction(()=>document.querySelector('.translation-feedback')?.textContent.includes('正在翻译'))
    await page.getByRole('button',{name:'取消图像操作'}).click();await page.waitForTimeout(700)
    assert.equal(await page.getByRole('textbox',{name:'覆盖文字 1'}).count(),0,'Late translation cannot republish after cancel')
   }else{
    const image=await page.locator('.image-frame').boundingBox();for(const [x,y] of [[.2,.2],[.8,.2],[.6,.8]])await page.mouse.click(image.x+image.width*x,image.y+image.height*y)
    await page.keyboard.press('Control+z');await page.getByText('2 个顶点',{exact:true}).waitFor();await page.keyboard.press('Control+y');await page.getByText('3 个顶点',{exact:true}).waitFor();await page.keyboard.press('Enter');await page.waitForFunction(()=>!document.querySelector('.workspace-busy'))
    await page.getByRole('button',{name:'保存 PNG…',exact:true}).click();await page.waitForFunction(()=>window.lastExport!==undefined)
   }
   await page.evaluate(()=>window.requestCaptureClose());await page.waitForFunction(()=>window.closedByRequest===true)
   assert.equal(await page.evaluate(()=>window.calls.some(m=>m.method==='library.list')),false)
  }
  reports.push({width,height,theme,captureEditor:'isolated selection, polygon undo/redo, direct save, automatic translation, cancellation, native close; no gallery scan or navigation',errors})
  assert.deepEqual(errors,[]);await context.close()
 }
 console.log(JSON.stringify({ok:true,softwareRendering:true,realModelInference:false,reports},null,2))
}finally{await browser?.close();await new Promise(r=>server.close(r));await rm(temp,{recursive:true,force:true})}
