// Headless/software rendering only. Native caption painting is validated separately.
import { createRequire } from 'node:module'
import { createServer } from 'node:http'
import { readFile, mkdtemp, mkdir, copyFile, rm } from 'node:fs/promises'
import { resolve, extname, sep } from 'node:path'
import { tmpdir } from 'node:os'
import { fileURLToPath } from 'node:url'
import assert from 'node:assert/strict'
const { chromium } = createRequire(import.meta.url)(process.argv[2])
const root = fileURLToPath(new URL('../../src/Starshot.WebUI/dist/', import.meta.url))
const server = createServer(async (req, res) => {
  const path = resolve(root, '.' + decodeURIComponent(new URL(req.url, 'http://localhost').pathname.replace(/^\/$/, '/index.html')))
  if (!path.startsWith(root.replace(/[\\/]$/, '') + sep)) { res.writeHead(403); res.end(); return }
  try { res.setHeader('Content-Type', ({ '.html':'text/html', '.js':'text/javascript', '.css':'text/css', '.woff2':'font/woff2' })[extname(path)] || 'application/octet-stream'); res.end(await readFile(path)) }
  catch { res.writeHead(404); res.end() }
})
await new Promise(r => server.listen(0, '127.0.0.1', r))
const temp = await mkdtemp(resolve(tmpdir(), 'StarshotChromeUi-')), results = []
let browser, samples = 0
try {
  browser = await chromium.launch({ headless:true, executablePath:process.argv[3], args:['--disable-gpu'] })
  for (const [surface, width, height, theme, dpi] of [['main',1280,800,1,2],['main',960,640,2,1.5],['main',760,540,1,2],['ocr',460,300,1,2],['ocr',360,262,2,2],['image',1280,800,1,2]]) {
    const page = await browser.newPage({ viewport:{width,height}, deviceScaleFactor:dpi }), errors=[]
    page.on('pageerror', e => errors.push(e.message))
    await page.addInitScript(({theme,surface}) => {
      const handlers=[], calls=window.calls=[], src='data:image/svg+xml;base64,'+btoa('<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="800"><rect width="1200" height="800" fill="#354d3c"/><path d="M0 800 400 160 800 800Z" fill="#ddf369"/></svg>')
      const settings={theme,extraFolders:[],screenshotFolder:'',subfolders:false,ocrModel:'tiny',ocrGpuAcceleration:false,targetLanguage:'简体中文',captureMode:2,translationBackend:'api',autoCopyOcr:false,endpoint:'',model:'',sdrFormat:0,hdrFormat:0,quality:1}
      const chrome={enabled:true,inset:138,height:surface==='ocr'?32:48,active:true,maximized:false}
      const items=[{id:'a',name:'Light.png',format:'PNG',bytes:10000,timestamp:new Date().toISOString(),kind:'image'}]
      window.emitChrome=value=>handlers.forEach(h=>h({data:{type:'event',name:'window.chrome',data:{...chrome,...value}}}))
      window.chrome={webview:{addEventListener:(_,h)=>handlers.push(h),postMessage:m=>{
        calls.push(m); let result=null
        switch(m.method) {
          case 'app.bootstrap':result={version:'Chrome UI test',protocol:1,hasApiKey:false,settings,hotkeys:[],chrome};break
          case 'app.ready':if(surface==='ocr') setTimeout(()=>handlers.forEach(h=>h({data:{type:'event',name:'ocr.result',data:{id:'1',text:'让光，保留它的层次。\nKeep every little detail.',raw:'让光，保留它的层次。\nKeep every little detail.',lineCount:2,translate:false,autoCopied:false}}})),20);break
          case 'library.list':result={items,total:1,offset:0};break
          case 'library.thumbnail':result={src,width:1200,height:800};break
          case 'workspace.open':result={name:'Light.png',width:1200,height:800,src,hdr:false,referenceWhite:203,fingerprint:'a',carrier:'SDR',previewMax:4096};break
          case 'workspace.mask':result={src};break
          case 'clipboard.copyRichText':case 'workspace.draft.load':case 'workspace.draft.save':case 'workspace.close':case 'workspace.analysis.close':case 'request.cancel':case 'window.hide':break
          default:throw new Error('Unexpected mock call: '+m.method)
        }
        setTimeout(()=>handlers.forEach(h=>h({data:{type:'response',id:m.id,ok:true,result}})),0)
      }}}
    }, {theme,surface})
    try {
      await page.goto(`http://127.0.0.1:${server.address().port}/${surface==='ocr'?'?surface=ocr':''}`)
      await page.waitForFunction(()=>document.documentElement.classList.contains('native-chrome'))
      if(surface==='main') await page.locator('.shot-card').first().waitFor({state:'attached'})
      if(surface==='image') { await page.getByRole('button',{name:'截图库',exact:true}).click(); await page.getByRole('button',{name:/Light.png/}).first().click(); await page.waitForFunction(()=>document.querySelector('.workspace-document-name b')?.textContent==='Light.png') }
      if(surface==='ocr') await page.getByRole('textbox',{name:'可编辑的原文'}).waitFor()
      const selector=surface==='ocr'?'.compact-titlebar':surface==='image'?'.image-workspace-header':'.topbar'
      const metrics=await page.locator(selector).evaluate(el=>{
        const r=el.getBoundingClientRect(), style=getComputedStyle(el)
        return {height:r.height,right:r.right,drag:style.getPropertyValue('app-region'),buttons:[...el.querySelectorAll('button')].map(b=>({right:b.getBoundingClientRect().right,label:b.ariaLabel||b.innerText,drag:getComputedStyle(b).getPropertyValue('app-region')})),overflow:document.documentElement.scrollWidth>innerWidth}
      })
      assert.equal(metrics.height,surface==='ocr'?32:48)
      assert.equal(metrics.drag,'drag')
      assert.equal(metrics.overflow,false)
      for(const button of metrics.buttons) { assert.ok(button.right<=width-138,`Caption overlaps ${button.label}: ${JSON.stringify(metrics)}`); assert.equal(button.drag,'no-drag') }
      if(surface==='ocr') {
        await page.getByRole('button',{name:'复制原文',exact:true}).click()
        await page.waitForFunction(()=>window.calls.some(m=>m.method==='clipboard.copyRichText'))
        assert.equal(await page.locator('.ocr-popup').evaluate(el=>el.getBoundingClientRect().bottom),height)
      }
      await page.evaluate(()=>window.emitChrome({active:false,inset:150,maximized:true}))
      await page.waitForFunction(()=>document.documentElement.dataset.windowActive==='false'&&document.documentElement.style.getPropertyValue('--caption-inset')==='150px')
      await page.evaluate(()=>window.emitChrome({active:true,inset:138,maximized:false}))
      await page.waitForFunction(()=>document.documentElement.dataset.windowActive==='true')
      assert.deepEqual(errors,[])
      if(process.env.STARSHOT_UI_SAMPLE_DIR && samples<3 && (width===1280||surface==='ocr'&&width===460)) {
        const name=`${surface}-${width}.png`, path=resolve(temp,name)
        await page.screenshot({path});await mkdir(process.env.STARSHOT_UI_SAMPLE_DIR,{recursive:true});await copyFile(path,resolve(process.env.STARSHOT_UI_SAMPLE_DIR,name)); samples++
      }
      results.push({surface,width,height,theme,dpi,metrics})
    } finally { await page.close() }
  }
  console.log(JSON.stringify({ok:true,softwareRendering:true,desktopInput:false,modelInference:false,results},null,2))
} finally { await browser?.close(); await new Promise(r=>server.close(r)); await rm(temp,{recursive:true,force:true}) }
