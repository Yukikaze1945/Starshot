// Shared rich-editor regression. Software browser only, no models/desktop capture.
import {createRequire} from 'node:module'
import {createServer} from 'node:http'
import {readFile,writeFile,mkdir} from 'node:fs/promises'
import {resolve,extname,sep} from 'node:path'
import {fileURLToPath} from 'node:url'
import assert from 'node:assert/strict'
const {chromium}=createRequire(import.meta.url)(process.argv[2])
const root=resolve(process.env.STARSHOT_EDITOR_TEST_WEBUI||fileURLToPath(new URL('../../src/Starshot.WebUI/dist/',import.meta.url)))
const server=createServer(async(req,res)=>{
 const file=resolve(root,'.'+decodeURIComponent(new URL(req.url,'http://localhost').pathname.replace(/^\/$/,'/index.html')))
 if(!file.startsWith(root+sep)){res.writeHead(403).end();return}
 try{res.setHeader('Content-Type',({'.html':'text/html','.js':'text/javascript','.css':'text/css','.woff2':'font/woff2'})[extname(file)]||'application/octet-stream');res.end(await readFile(file))}catch{res.writeHead(404).end()}
})
await new Promise(r=>server.listen(0,'127.0.0.1',r))
const reports=[];let browser
try{
 browser=await chromium.launch({headless:true,executablePath:process.argv[3],args:['--disable-gpu']})
 for(const [width,height,theme,surface] of [[1060,780,1,'lab'],[1060,780,2,'lab'],[584,391,2,'ocr'],[344,240,1,'ocr']]){
  const context=await browser.newContext({viewport:{width,height},deviceScaleFactor:2}),page=await context.newPage(),errors=[]
  page.on('pageerror',e=>errors.push(e.message))
  await page.addInitScript(({theme})=>{
   const handlers=[],settings={ocrModel:'tiny',ocrGpuAcceleration:false,theme,targetLanguage:'简体中文',translationBackend:'api',screenshotFolder:'',extraFolders:[]}
   window.lastCopy=null;window.calls=[]
   window.chrome={webview:{addEventListener:(_,h)=>handlers.push(h),postMessage:m=>{
    window.calls.push(m);const reply=result=>queueMicrotask(()=>handlers.forEach(h=>h({data:{type:'response',id:m.id,ok:true,result}})))
    if(m.method==='app.bootstrap')reply({version:'format regression',protocol:1,hasApiKey:true,settings,hotkeys:[]})
    else if(m.method==='library.list')reply({items:[],total:0,offset:0})
    else if(m.method==='clipboard.read')reply({text:'',image:null,items:[],historyEnabled:false})
    else if(m.method==='clipboard.copyRichText'){window.lastCopy=m.params;reply(null)}
    else if(m.method==='translation.formatted')reply(m.params.segments.map(s=>({id:s.id,text:'译：'+s.text})))
    else reply(null)
   }}}
  },{theme})
  await page.goto(`http://127.0.0.1:${server.address().port}/${surface==='ocr'?'?surface=ocr':''}`)
  if(surface==='lab')await page.getByRole('button',{name:'文字实验室',exact:true}).click()
  const source=page.getByRole('textbox',{name:'可编辑的原文'}),bubble=page.getByRole('toolbar',{name:'选中文字格式'})
  await source.fill('ControlNet extra words 中文排版')
  await page.evaluate(()=>document.fonts.ready)
  async function selectText(start=0,end=10){
   await source.evaluate((element,{start,end})=>{
    element.focus();const walker=document.createTreeWalker(element,NodeFilter.SHOW_TEXT),range=document.createRange();let offset=0,startSet=false,node
    while(node=walker.nextNode()){
     const next=offset+node.textContent.length
     if(!startSet&&start>=offset&&start<next){range.setStart(node,start-offset);startSet=true}
     if(startSet&&end<=next){range.setEnd(node,end-offset);break}offset=next
    }
    const selection=getSelection();selection.removeAllRanges();selection.addRange(range)
   },{start,end})
   await frame();await bubble.waitFor()
  }
  const frame=()=>page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))))
  await selectText()
  const size=bubble.getByRole('combobox',{name:'字号'})
  await size.click();await size.press('ArrowDown');await size.press('Enter');await frame()
  assert.ok(await bubble.isVisible(),'Native size picker must not unmount the toolbar')
  // Native selectors may temporarily report body as activeElement and clear the
  // DOM range. Model this explicitly before their delayed change notification.
  await size.dispatchEvent('pointerdown');await size.focus();await size.evaluate(e=>{e.blur();getSelection().removeAllRanges()});await frame()
  assert.ok(await bubble.isVisible(),'OS-popup blur must preserve the toolbar and saved range')
  await size.selectOption('24px')
  await page.waitForFunction(()=>document.querySelector('[aria-label="可编辑的原文"] span[style*="24px"]')?.textContent==='ControlNet')
  await page.waitForFunction(()=>getSelection()?.toString()==='ControlNet')
  await bubble.getByRole('button',{name:'斜体',exact:true}).click()
  assert.equal(await source.locator('em').textContent(),'ControlNet')
  const italic=await source.locator('em').evaluate(e=>({style:getComputedStyle(e).fontStyle,synthesis:getComputedStyle(e).fontSynthesisStyle}))
  assert.equal(italic.style,'italic');assert.notEqual(italic.synthesis,'none','Font without an italic face must synthesize the requested slant')
  await source.evaluate(e=>{e.blur();getSelection().removeAllRanges()});await frame()
  const paragraph=source.locator('p').first()
  await source.locator('em').evaluate(e=>e.style.fontSynthesis='none')
  const uprightPixels=await paragraph.screenshot({caret:'hide',animations:'disabled'})
  await source.locator('em').evaluate(e=>e.style.fontSynthesis='')
  const italicPixels=await paragraph.screenshot({caret:'hide',animations:'disabled'})
  assert.notDeepEqual(italicPixels,uprightPixels,'Italic must change actual rendered glyph pixels')
  await selectText()
  const color=bubble.getByLabel('文字颜色',{exact:true})
  await color.dispatchEvent('pointerdown');await color.focus();await color.evaluate(e=>{e.blur();getSelection().removeAllRanges()});await frame()
  assert.ok(await bubble.isVisible(),'Native color dialog blur must not clear the bookmark')
  await color.evaluate(e=>{Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(e,'#c84433');e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}))})
  await page.waitForFunction(()=>document.querySelector('[aria-label="可编辑的原文"] span[style*="color"]')?.style.color==='rgb(200, 68, 51)')
  assert.equal(await source.locator('span[style*="color"]').textContent(),'ControlNet','Only the selected word receives colour')
  await page.waitForFunction(()=>getSelection()?.toString()==='ControlNet')
  await bubble.getByRole('button',{name:'斜体',exact:true}).click();assert.equal(await source.locator('em').count(),0,'Italic toggles off')
  await bubble.getByRole('button',{name:'斜体',exact:true}).click()
  await selectText(11,16);await bubble.getByRole('combobox',{name:'字号'}).selectOption('12px')
  assert.equal(await source.locator('span[style*="12px"]').textContent(),'extra','A new selection replaces the previous bookmark')
  await source.press('Control+z');assert.equal(await source.locator('span[style*="12px"]').count(),0,'Undo applies to the actual formatted range')
  await page.getByRole('button',{name:'复制原文',exact:true}).click();await bubble.waitFor({state:'hidden'})
  const copy=await page.evaluate(()=>window.lastCopy)
  assert.match(copy.html,/<em>/);assert.match(copy.html,/font-size: 24px/);assert.match(copy.html,/color: rgb\(200, 68, 51\)/)
  await page.getByRole('button',{name:'翻译',exact:true}).click()
  const translated=page.getByRole('textbox',{name:'可编辑的译文'})
  await translated.waitFor();assert.ok((await translated.locator('em').textContent()).includes('ControlNet'))
  assert.ok((await translated.locator('span[style*="24px"]').textContent()).includes('ControlNet'))
  assert.equal(await translated.locator('span[style*="color"]').evaluate(e=>e.style.color),'rgb(200, 68, 51)')
  assert.deepEqual(errors,[])
  reports.push({width,height,theme,surface,dpi:200,size:true,color:true,italic,italicRasterChanged:true,selectionRestored:true,newSelection:true,undo:true,copyFormatting:true,translationFormatting:true,nativePopupBlurSimulated:true,errors})
  await context.close()
 }
 const report=resolve(process.argv[4]||'build/ocr-format/format-ui.json');await mkdir(resolve(report,'..'),{recursive:true});await writeFile(report,JSON.stringify({passed:true,reports},null,2))
 console.log('PASS formatting: 4 editor/popup/theme/size cases; native picker focus loss, size, colour, italic, new selection, undo, copy, translation')
}finally{await browser?.close();await new Promise(r=>server.close(r))}
