// Software-rendered browser only. No desktop capture, models, or real clipboard.
import {createRequire} from 'node:module'
import {createServer} from 'node:http'
import {readFile,writeFile,mkdir,mkdtemp,rm} from 'node:fs/promises'
import {resolve,extname,sep,join} from 'node:path'
import {tmpdir} from 'node:os'
import {fileURLToPath} from 'node:url'
import assert from 'node:assert/strict'
const {chromium}=createRequire(import.meta.url)(process.argv[2])
const root=resolve(fileURLToPath(new URL('../../src/Starshot.WebUI/dist/',import.meta.url))),reports=[],temporary=await mkdtemp(join(tmpdir(),'starshot-subtitle-ui-'))
const server=createServer(async(req,res)=>{
 const file=resolve(root,'.'+new URL(req.url,'http://localhost').pathname.replace(/^\/$/,'/index.html'))
 if(!file.startsWith(root+sep)){res.writeHead(403).end();return}
 try{res.setHeader('Content-Type',({'.html':'text/html','.js':'text/javascript','.css':'text/css','.woff2':'font/woff2'})[extname(file)]||'application/octet-stream');res.end(await readFile(file))}catch{res.writeHead(404).end()}
})
await new Promise(r=>server.listen(0,'127.0.0.1',r));let browser
try{
 browser=await chromium.launch({headless:true,executablePath:process.argv[3],args:['--disable-gpu']})
 for(const [width,height,theme] of [[420,400,1],[420,400,2],[360,360,1],[680,700,2]]){
  const context=await browser.newContext({viewport:{width,height},deviceScaleFactor:2}),page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message))
  await page.addInitScript(({theme})=>{
   let snapshot={hasSession:false,hasRegion:false,topmost:true,status:null};const handlers=[];window.calls=[];window.failNext=false
   window.subtitleSet=value=>{snapshot=value}
   window.chrome={webview:{addEventListener:(_,h)=>handlers.push(h),postMessage:m=>{
    window.calls.push(m);const reply=(result,ok=true,error='')=>queueMicrotask(()=>handlers.forEach(h=>h({data:{type:'response',id:m.id,ok,result,error}})))
    if(m.method==='app.bootstrap'){reply({version:'subtitle-test',protocol:1,hasApiKey:false,settings:{theme},hotkeys:[],chrome:{enabled:true,active:true,maximized:false,inset:138}});return}
    if(window.failNext&&m.method==='subtitles.start'){window.failNext=false;reply(null,false,'合成采集错误，请调整区域。');return}
    if(m.method==='subtitles.topmost')snapshot.topmost=m.params.value
    if(m.method==='subtitles.configure')snapshot={hasSession:true,hasRegion:true,topmost:snapshot.topmost,status:{State:'running',Region:{X:-1920,Y:300,Width:1280,Height:140},Count:0,Revision:0,Recent:[],Engine:'按需加载',OcrMs:0,CycleMs:0,Captures:0,Recognitions:0,Skipped:0,Unsaved:false,Path:'D:/Synthetic/SubtitleOCR/subtitle-123.txt',IntervalMs:350}}
    const s=snapshot.status
    if(m.method==='subtitles.start'){s.State='running';s.Count=2;s.Revision=2;s.Recent=[{Id:1,Text:'这是第一句对白。\nKeep every word.'},{Id:2,Text:'<script>这是纯文本</script>'}];s.Engine='Tiny / Cpu';s.OcrMs=58;s.CycleMs=65;s.Captures=12;s.Recognitions=3;s.Skipped=9}
    if(m.method==='subtitles.pause'||m.method==='subtitles.edit')s.State='paused'
    if(m.method==='subtitles.stop')s.State='stopped'
    if(m.method==='subtitles.interval')s.IntervalMs=m.params.value
    if(m.method==='subtitles.copy'){reply({copied:true});return}
    if(m.method==='subtitles.export'){reply({saved:true});return}
    reply(m.method==='subtitles.status'||m.method.startsWith('subtitles.')?snapshot:null)
   }}}
  },{theme})
  await page.goto(`http://127.0.0.1:${server.address().port}/?surface=subtitles`)
  await page.getByLabel('摘录状态',{exact:true}).waitFor()
  const start=()=>page.getByRole('button',{name:/开始摘录|继续摘录/}),copy=()=>page.getByRole('button',{name:'复制文案',exact:true})
  assert.ok(await start().isDisabled());assert.ok(await copy().isDisabled())
  const pin=page.getByRole('button',{name:'字幕窗口置顶',exact:true})
  assert.equal(await pin.getAttribute('aria-pressed'),'true')
  await pin.click();await page.waitForFunction(()=>document.querySelector('.subtitle-pin')?.getAttribute('aria-pressed')==='false')
  await pin.click();await page.waitForFunction(()=>document.querySelector('.subtitle-pin')?.getAttribute('aria-pressed')==='true')
  await page.getByRole('button',{name:'区域框选',exact:true}).click();await page.getByRole('button',{name:'暂停',exact:true}).waitFor();await page.getByRole('button',{name:'暂停',exact:true}).click();await start().waitFor({state:'visible'})
  await page.evaluate(()=>window.failNext=true);await start().click();await page.getByRole('alert').filter({hasText:'合成采集错误'}).waitFor()
  await start().click();await page.getByRole('button',{name:'暂停',exact:true}).waitFor()
  await page.getByRole('combobox',{name:'字幕采样节奏'}).selectOption('200')
  await page.waitForFunction(()=>document.querySelector('select')?.value==='200'&&!document.querySelector('select')?.disabled)
  assert.equal(await page.locator('.subtitle-feed script').count(),0,'OCR text is never HTML')
  assert.ok(await page.getByText('<script>这是纯文本</script>',{exact:true}).isVisible())
  await page.getByRole('button',{name:'暂停',exact:true}).click();await start().waitFor()
  await start().click();await page.getByRole('button',{name:'暂停',exact:true}).waitFor()
  await page.getByRole('button',{name:'结束摘录并保留结果'}).click();await page.getByText('已结束 · 结果保留',{exact:true}).waitFor()
  assert.ok(await copy().isEnabled(),'Stopped transcript remains available')
  await copy().click();await page.getByRole('status').filter({hasText:'已复制纯文案。'}).waitFor()
  await page.getByRole('button',{name:'整理文案',exact:true}).click();await page.getByRole('status').filter({hasText:'已暂停，并打开独立文案快照。'}).waitFor()
  await page.getByRole('button',{name:'导出纯文案 TXT'}).click();await page.getByRole('status').filter({hasText:'文案已导出。'}).waitFor()
  await page.getByText('识别状态与说明',{exact:false}).first().click()
  await page.evaluate(()=>document.fonts.ready)
  const overflow=await page.evaluate(()=>({viewport:innerWidth,body:document.body.scrollWidth,root:document.documentElement.scrollWidth}))
  assert.ok(overflow.root<=width+1&&overflow.body<=width+1,JSON.stringify(overflow))
  const buttons=await page.locator('.subtitle-results button').evaluateAll(es=>es.map(e=>{const r=e.getBoundingClientRect();return{x:r.x,right:r.right,width:r.width}}))
  assert.ok(buttons.every(b=>b.width>=30&&b.x>=0&&b.right<=width),'Buttons stay full size and within viewport')
  await page.screenshot({path:join(temporary,`${width}-${theme}.png`)})
  if(width===420&&theme===1&&process.env.STARSHOT_SUBTITLE_UI_HOLD){console.log('VIEW:'+join(temporary,`${width}-${theme}.png`));await Promise.race([new Promise(r=>process.stdin.once('data',r)),new Promise(r=>setTimeout(r,45000))])}
  if(width===420&&theme===1&&process.env.STARSHOT_SUBTITLE_UI_PREVIEW)await page.screenshot({path:process.env.STARSHOT_SUBTITLE_UI_PREVIEW})
  await page.getByRole('button',{name:'新任务',exact:true}).click();await page.getByRole('alertdialog',{name:'新建字幕摘录'}).waitFor()
  await page.getByRole('button',{name:'返回',exact:true}).click();assert.equal(await page.getByRole('alertdialog').count(),0)
  await page.getByRole('button',{name:'新任务',exact:true}).click();await page.getByRole('button',{name:'新建摘录',exact:true}).click()
  await page.getByRole('button',{name:'复制文案',exact:true}).waitFor();await page.waitForFunction(()=>document.querySelector('.subtitle-state strong')?.textContent==='0段')
  const calls=await page.evaluate(()=>window.calls.map(c=>c.method))
  assert.equal(calls.filter(c=>c==='subtitles.copy').length,1,'No automatic copy')
  assert.ok(!calls.some(c=>c==='capture.begin'||c==='library.open'||c==='translation.run'))
  assert.deepEqual(errors,[])
  await page.getByRole('button',{name:'关闭字幕摘录',exact:true}).click();await page.waitForFunction(()=>window.calls.some(c=>c.method==='subtitles.close'))
  await page.keyboard.press('Escape');await page.waitForFunction(()=>window.calls.filter(c=>c.method==='subtitles.close').length===2)
  reports.push({width,height,theme,dpi:200,overflow,buttons,actions:true,copyAfterStop:true,explicitCopyOnly:true,errorRecovery:true,newTaskConfirmation:true,pinToggle:true,errors})
  await context.close()
 }
 const page=await browser.newPage();await page.addInitScript(()=>{const handlers=[];window.openedSubtitle=false;window.chrome={webview:{addEventListener:(_,h)=>handlers.push(h),postMessage:m=>{if(m.method==='subtitles.configure')window.openedSubtitle=true;const result=m.method==='app.bootstrap'?{version:'test',protocol:1,hasApiKey:false,settings:{theme:1,captureMode:0},hotkeys:[]}:m.method==='library.list'?{items:[],total:0,offset:0}:m.method==='clipboard.read'?{items:[],text:'',image:null}:null;queueMicrotask(()=>handlers.forEach(h=>h({data:{type:'response',id:m.id,ok:true,result}})))}}}})
 await page.goto(`http://127.0.0.1:${server.address().port}/`);await page.getByRole('button',{name:/实时字幕摘录/}).click();await page.waitForFunction(()=>window.openedSubtitle);await page.close()
 const report=resolve('docs/reports/region-tools-20261021/subtitle-ui.json');await mkdir(resolve(report,'..'),{recursive:true});await writeFile(report,JSON.stringify({passed:true,studioEntryUsesRegionSelection:true,reports},null,2))
 console.log('PASS subtitle UI: 4 sizes/themes at 200% DPI; studio entry, lifecycle, explicit copy after stop, editor/export, errors, new-task confirmation, safe plain text, no horizontal overflow')
}finally{await browser?.close();await new Promise(r=>server.close(r));await rm(temporary,{recursive:true,force:true})}
