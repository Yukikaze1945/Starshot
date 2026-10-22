// Software-rendered bridge integration tests; never loads a model or captures the desktop.
import {createRequire} from 'node:module'
import {createServer} from 'node:http'
import {readFile,writeFile,mkdtemp,rm,copyFile,mkdir} from 'node:fs/promises'
import {resolve,extname,sep} from 'node:path'
import {tmpdir} from 'node:os'
import {fileURLToPath} from 'node:url'
import assert from 'node:assert/strict'
const {chromium}=createRequire(import.meta.url)(process.argv[2])
const root=fileURLToPath(new URL('../../src/Starshot.WebUI/dist/',import.meta.url))
const server=createServer(async(req,res)=>{const path=resolve(root,'.'+decodeURIComponent(new URL(req.url,'http://localhost').pathname.replace(/^\/$/,'/index.html')));if(!path.startsWith(root.replace(/[\\/]$/,'')+sep)){res.writeHead(403);res.end();return}try{res.setHeader('Content-Type',({'.html':'text/html','.js':'text/javascript','.css':'text/css','.woff2':'font/woff2'})[extname(path)]||'application/octet-stream');res.end(await readFile(path))}catch{res.writeHead(404);res.end()}})
await new Promise(r=>server.listen(30000+Math.floor(Math.random()*20000),'127.0.0.1',r))
let browser;const temp=await mkdtemp(resolve(tmpdir(),'starshot-document-ui-')),reports=[]
try{
 browser=await chromium.launch({headless:true,executablePath:process.argv[3],args:['--disable-gpu','--disable-software-rasterizer']})
 for(const [width,height,theme] of [[1280,900,1],[680,460,2]]){
  const context=await browser.newContext({viewport:{width,height},deviceScaleFactor:2})
  const page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));page.on('dialog',d=>d.accept())
  await page.addInitScript(({theme})=>{
   const svg=color=>'data:image/svg+xml;base64,'+btoa(`<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="800"><rect width="1200" height="800" fill="${color}"/><circle cx="620" cy="390" r="240" fill="#ddf369"/><text x="65" y="100" font-size="42" fill="#f5f5e9">Starshot / Light in layers</text></svg>`)
   const source=svg('#405548'),second=svg('#5d526a'),calls=window.calls=[],handlers=[],drafts={};let current='a',backend='api',download='notInstalled',docMode='formula',job=0;const docRegions=()=>docMode==='formula'?[{Id:'f',Label:'formula',Score:1,Box:{X:0,Y:0,Width:1200,Height:800},Order:1,State:'done',Error:null,Text:'',Formula:{Latex:'\\frac{x^2+1}{\\sqrt{y}}',Complete:true,Tokens:20,ElapsedMs:200},Table:null,IsImage:false}]:[{Id:'t',Label:'text',Score:.98,Box:{X:40,Y:40,Width:500,Height:100},Order:1,State:'done',Text:'Document understanding',Formula:null,Table:null,IsImage:false},{Id:'table',Label:'table',Score:.95,Box:{X:40,Y:170,Width:900,Height:350},Order:2,State:'done',Text:'',Formula:null,Table:{Rows:2,Columns:2,Score:.9,Cells:[{Row:0,Column:0,RowSpan:1,ColSpan:2,Text:'Results',Box:null},{Row:1,Column:0,RowSpan:1,ColSpan:1,Text:'Alpha',Box:null},{Row:1,Column:1,RowSpan:1,ColSpan:1,Text:'80',Box:null}]},IsImage:false}];const pageResult=()=>({Id:'job-'+job,Width:1200,Height:800,State:'done',Stage:'提取完成',Error:null,ElapsedMs:700,Regions:docRegions()})
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
     case 'workspace.formula':case 'workspace.layout':docMode=m.method==='workspace.formula'?'formula':'layout';job++;result={jobId:'job-'+job};break
     case 'workspace.page.status':result=pageResult();break
     case 'workspace.page.cancel':window.documentCanceled=true;result=pageResult();break
     case 'workspace.page.models':result={items:['formula-rapidlatex','layout-doclayout-s','table-slanet-plus'].map(Id=>({Id,Name:Id,Installed:!window.missingModels,State:'installed',Bytes:1000000,Downloaded:1000000}))};break
     case 'workspace.page.crop':result={src:source};break
     case 'workspace.page.edit':window.documentEdited=m.params;result=pageResult();break
     case 'workspace.page.retry':result=pageResult();break
     case 'workspace.page.export':window.documentExport=m.params;result={saved:true};break
     case 'workspace.formula.copyWord':window.wordFormula=m.params;break
     case 'clipboard.copyText':window.copiedText=m.params.text;break
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
     case 'workspace.ocr':result=[{id:'line',text:'Bright highlights',x:120,y:130,width:410,height:55}];break
     case 'translation.formatted':delay=window.translationDelay||0;if(window.failTranslation)failure='请先配置翻译 API 或安装并启用离线模型。';else result=m.params.segments.map(s=>({...s,text:'高光层次'}));break
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
  await page.getByRole('button',{name:'公式识别',exact:true}).click()
  const formula=page.getByRole('dialog',{name:'公式识别',exact:true});await formula.waitFor();await formula.locator('.katex').first().waitFor()
  await formula.getByRole('button',{name:'Word 公式',exact:true}).click();await page.waitForFunction(()=>window.wordFormula!==undefined)
  assert.match(await page.evaluate(()=>window.wordFormula.mathml),/MathML/)
  await formula.getByRole('textbox',{name:'编辑 LaTeX'}).fill(String.raw`\frac{a}{b}`)
  await formula.getByRole('button',{name:'Markdown',exact:true}).click();await page.waitForFunction(()=>window.copiedText?.includes('frac{a}{b}'))
  assert.equal(await page.evaluate(()=>window.copiedText),String.raw`$$
\frac{a}{b}
$$`)
  await formula.getByRole('textbox',{name:'编辑 LaTeX'}).fill(String.raw`\notARealCommand`);await formula.getByRole('alert').filter({hasText:'无法预览'}).waitFor()
  await formula.getByRole('textbox',{name:'编辑 LaTeX'}).fill(String.raw`\frac{x}{2}`);await formula.locator('.katex').first().waitFor()
  const out=await formula.locator('button,textarea').evaluateAll(elements=>elements.filter(el=>{const r=el.getBoundingClientRect();return r.width&&r.height&&(r.left<0||r.right>innerWidth+1||r.top<0||r.bottom>innerHeight+1)}).map(el=>el.getAttribute('aria-label')||el.textContent))
  assert.deepEqual(out,[],'Formula controls fit the window')
  if(width===1280){await page.screenshot({path:resolve(temp,'formula.png')});await copyFile(resolve(temp,'formula.png'),resolve(process.argv[4],'formula-ui.png'))}
  await page.keyboard.press('Escape');await formula.waitFor({state:'detached'});await page.getByRole('dialog',{name:'图像工作区',exact:true}).waitFor()
  await page.getByRole('button',{name:'分析版面',exact:true}).click()
  const panel=page.getByRole('dialog',{name:'整页版面识别',exact:true});await panel.waitFor();await panel.locator('.doc-region').first().waitFor();const closeBounds=await panel.getByRole('button',{name:'关闭文档识别'}).boundingBox();assert.ok(closeBounds.x+closeBounds.width<=width-138,'Page close button stays outside Windows caption controls');assert.equal(await panel.getByRole('button',{name:'关闭文档识别'}).evaluate(el=>getComputedStyle(el).getPropertyValue('-webkit-app-region')),'no-drag')
  await panel.getByRole('button',{name:/表格/}).click();await panel.getByRole('table',{name:'识别表格'}).waitFor()
  assert.equal(await panel.locator('td[colspan="2"]').count(),1)
  await panel.getByRole('textbox',{name:'单元格 2-2'}).fill('200')
  await panel.getByRole('button',{name:'复制 TSV',exact:true}).click();await page.waitForFunction(()=>window.copiedText?.includes('200'))
  await panel.getByRole('button',{name:'Excel',exact:true}).click();await page.waitForFunction(()=>window.documentExport?.format==='xlsx')
  await panel.getByRole('button',{name:'HTML',exact:true}).click();await page.waitForFunction(()=>window.documentExport?.format==='html')
  await panel.getByRole('button',{name:'导出整页 Markdown',exact:true}).click();await page.waitForFunction(()=>window.documentExport?.format==='markdown')
  await panel.getByRole('button',{name:'放大版面原图'}).click();assert.match(await panel.locator('.doc-source-image').getAttribute('style'),/150%/)
  await panel.getByRole('button',{name:'缩小版面原图'}).click()
  await panel.getByRole('button',{name:'切换版面区域框'}).click();assert.equal(await panel.locator('.doc-region').count(),0)
  await panel.getByRole('button',{name:'切换版面区域框'}).click();assert.equal(await panel.locator('.doc-region').count(),2)
  if(width===1280){await page.screenshot({path:resolve(temp,'page.png')});await copyFile(resolve(temp,'page.png'),resolve(process.argv[4],'page-ui.png'))}
  await panel.getByRole('button',{name:'关闭文档识别'}).click();await panel.waitFor({state:'detached'})
  await page.waitForFunction(()=>window.documentCanceled)
  await page.goto(`http://127.0.0.1:${server.address().port}/?surface=formula`)
  await page.getByRole('dialog',{name:'公式识别',exact:true}).waitFor();await page.locator('.katex').first().waitFor()
  await page.getByRole('button',{name:'关闭文档识别'}).click();await page.waitForFunction(()=>window.closedByRequest)
  await page.goto(`http://127.0.0.1:${server.address().port}/?surface=capture&tool=layout`)
  const capturePage=page.getByRole('dialog',{name:'整页版面识别',exact:true});await capturePage.waitFor();await capturePage.getByRole('button',{name:'关闭文档识别'}).click();await capturePage.waitFor({state:'detached'})
  assert.equal(await page.getByRole('button',{name:'查看 / 移动画面',exact:true}).getAttribute('aria-pressed'),'true','Layout entry returns to a working hand tool')
  assert.equal(await page.evaluate(()=>window.calls.some(m=>m.method==='library.list')),false,'Capture analysis never navigates to gallery')
  assert.deepEqual(errors,[])
  reports.push({width,height,theme,dpi:200,formulaWord:true,editableLatex:true,tableSpans:true,tableExport:true,cancelOnClose:true,standalone:true,errors})
  await context.close()
 }
 await mkdir(process.argv[4],{recursive:true});await writeFile(resolve(process.argv[4],'ui.json'),JSON.stringify({ok:true,reports},null,2));console.log(JSON.stringify({ok:true,reports}))
}finally{if(browser)await browser.close();server.close();await rm(temp,{recursive:true,force:true})}
