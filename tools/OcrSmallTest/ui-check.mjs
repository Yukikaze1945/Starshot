// Headless component/bridge contract test; never operates the desktop or captures images.
// Usage: node ui-check.mjs <playwright package directory> <chromium.exe> <report.json>
import { createRequire } from 'node:module'
import { createServer } from 'node:http'
import { readFile, writeFile, mkdtemp, rm, copyFile, mkdir } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { resolve, extname, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import assert from 'node:assert/strict'

const { chromium } = createRequire(import.meta.url)(process.argv[2])
const root = fileURLToPath(new URL('../../src/Starshot.WebUI/dist/', import.meta.url))
const server = createServer(async (req, res) => {
  const path = resolve(root, '.' + decodeURIComponent(new URL(req.url, 'http://localhost').pathname === '/' ? '/index.html' : new URL(req.url, 'http://localhost').pathname))
  if (!path.startsWith(root.replace(/[\\/]$/, '') + sep)) { res.writeHead(403); res.end(); return }
  try { res.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2' })[extname(path)] || 'application/octet-stream'); res.end(await readFile(path)) }
  catch { res.writeHead(404); res.end() }
})
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
let browser
const report = []
try {
  browser = await chromium.launch({ headless: true, executablePath: process.argv[3],args:['--disable-gpu'] })
  for (const [width, height, theme] of [[1280, 900, 1], [720, 900, 2]]) {
    const context = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 2 })
    const page = await context.newPage()
    const errors = []; page.on('pageerror', error => errors.push(error.message))
    page.on('dialog',dialog=>dialog.accept())
    await page.addInitScript(({ theme }) => {
      const settings = { screenshotFolder: '', extraFolders: [], subfolders: false, subfolderPattern: '', filenamePattern: '', regionFilenamePattern: '', autoCopy: false, autoCopyOcr: false, ocrModel: 'tiny', ultraHdr: false, capacityManual: false, capacity: 8, captureMode: 2, videoCodec: 0, sdrFormat: 0, hdrFormat: 0, quality: 1, colorManagement: false, deleteSdrHdr: false, sdrWhite: 0, monitorSource: 0, muteFullscreen: false, endpoint: '', model: '', targetLanguage: '简体中文', theme, language: '', autoUpdate: false, previewUpdates: false, startupHidden: false, highPriority: false }
      const boot = { version: 'DLC UI test', protocol: 1, hasApiKey: false, settings, hotkeys: [] }
      window.smallStatus = { selected: 'tiny', state: 'notInstalled', installed: false, downloadedBytes: 0, totalBytes: 31114837, error: null, directory: 'isolated test data / Models / OCR', version: '1.0.0' }
      window.catalog={downloadSource:'auto',translationBackend:'api',localLoaded:false,items:[
        ...['s','m','l'].map((v,i)=>({Id:'layout-doclayout-'+v,Name:`PP-DocLayout-${v.toUpperCase()} · 版面检测`,Task:'layout',Bytes:[4917852,23496395,129398697][i],Downloaded:0,State:v==='l'?'notInstalled':'installed',Error:null,Installed:v!=='l',License:'Apache-2.0',Source:'pinned community ONNX; official Paddle verified',Sources:[]})),
        {Id:'index-q5km',Name:'Index-Translate-2B · Q5_K_M',Task:'translation',Bytes:1454787072,Downloaded:1454787072,State:'installed',Error:null,Installed:true,License:'Apache-2.0',Source:'https://modelscope.cn/models/IndexTeam/Index-Translate-2B-GGUF',Warning:'SHA-256 与登记版本不一致，仍可启用。',ActualSha256:'A'.repeat(64),Sha256:'B'.repeat(64),Sources:[{Id:'modelscope',Label:'魔搭 ModelScope',Url:'https://modelscope.cn/verified-model'}]},
        {Id:'u2netp',Name:'U²-Net compact',Task:'segmentation',Bytes:4574861,Downloaded:0,State:'notInstalled',Error:null,Installed:false,License:'Apache-2.0',Source:'https://github.com/xuebinqin/U-2-Net',Sources:[]}]}
      window.runtime={loaded:false,resident:false,gpuEnabled:false,activeBackend:null,state:'idle',error:null,gpuRuntimeAvailable:true,apiEnabled:false,apiPort:43182,apiUrl:'http://127.0.0.1:43182/v1',apiActiveRequests:0,idleSeconds:60}
      window.calls = []
      const handlers = []
      window.chrome ??= {}
      window.chrome.webview = {
        addEventListener: (_, handler) => handlers.push(handler),
        postMessage: message => {
          window.calls.push(message.method)
          const params = message.params
          let result, ok = true, error
          switch (message.method) {
            case 'app.bootstrap': result = boot; break
            case 'settings.get': result = settings; break
            case 'library.list': result = { items: [], total: 0, offset: 0 }; break
            case 'app.ready': case 'window.hide': case 'ocr.models.source': result = null; break
            case 'ocr.models.status': result = window.smallStatus; break
            case 'ocr.models.download': Object.assign(window.smallStatus, { state: 'downloading', installed: false, downloadedBytes: 15000000, error: null }); result = window.smallStatus; break
            case 'ocr.models.cancel': Object.assign(window.smallStatus, { state: 'canceled', installed: false, error: '下载已取消，继续使用内置 Tiny。' }); result = window.smallStatus; break
            case 'ocr.models.delete': Object.assign(window.smallStatus, { state: 'notInstalled', installed: false, selected: 'tiny', error: null }); settings.ocrModel = 'tiny'; result = window.smallStatus; break
            case 'ocr.models.select': window.smallStatus.selected = settings.ocrModel = params.model; result = window.smallStatus; break
            case 'models.runtime.status':result=window.runtime;break
            case 'models.runtime.gpu':window.runtime.gpuEnabled=params.enabled;if(window.runtime.loaded)window.runtime.activeBackend=params.enabled?'Vulkan0':'CPU';result=window.runtime;break
            case 'models.runtime.load':Object.assign(window.runtime,{loaded:true,resident:true,state:'ready',activeBackend:window.runtime.gpuEnabled?'Vulkan0':'CPU'});result=window.runtime;break
            case 'models.runtime.unload':Object.assign(window.runtime,{loaded:false,resident:false,state:'idle',apiEnabled:false,activeBackend:null});result=window.runtime;break
            case 'models.runtime.api':Object.assign(window.runtime,{apiEnabled:params.enabled,apiPort:params.port,apiUrl:`http://127.0.0.1:${params.port}/v1`});if(params.enabled)Object.assign(window.runtime,{loaded:true,resident:true,state:'ready',activeBackend:window.runtime.gpuEnabled?'Vulkan0':'CPU'});result=window.runtime;break
            case 'models.runtime.copyKey':case 'clipboard.copyText':result=null;break
            case 'models.list':result=window.catalog;break
            case 'models.layout.select':window.catalog.layoutModel=params.id;result={layoutModel:params.id};break
            case 'models.download':{const model=window.catalog.items.find(m=>m.Id===params.id);Object.assign(model,{State:'downloading',Downloaded:1000000});result=model;break}
            case 'models.cancel':{const model=window.catalog.items.find(m=>m.Id===params.id);Object.assign(model,{State:'canceled'});result=model;break}
            case 'models.delete':{const model=window.catalog.items.find(m=>m.Id===params.id);Object.assign(model,{State:'notInstalled',Installed:false});result=model;break}
            case 'models.source':window.catalog.downloadSource=params.source;result={source:params.source};break
            case 'models.scan':result={Items:[{Id:'index-q5km',Path:'D:/模型/Index-Translate-2B.Q5_K_M.gguf',Bytes:1454787072},{Id:'ocr-small',Path:'D:/模型/Small',Bytes:0}],Visited:24,Limited:false};break
            case 'models.import':
              if(params.id==='ocr-small'){Object.assign(window.smallStatus,{state:'installed',installed:true,warning:'SHA-256 与登记版本不一致，仍可使用。'});result=window.smallStatus}
              else {const model=window.catalog.items.find(m=>m.Id===params.id);Object.assign(model,{State:'installed',Installed:true,Warning:'SHA-256 与登记版本不一致，仍可启用。',ActiveSource:'本地导入'});result=model}break
            case 'models.translation':window.catalog.translationBackend=params.backend;result={backend:params.backend};break
            case 'models.verify':result=window.catalog.items.find(m=>m.Id===params.id);break
            default: ok = false; error = 'Unexpected mock request: ' + message.method
          }
          const response = structuredClone({ type: 'response', id: message.id, ok, result, error })
          queueMicrotask(() => handlers.forEach(handler => handler({ data: response })))
        },
      }
    }, { theme })
    await page.goto(`http://127.0.0.1:${server.address().port}`)
    await page.getByRole('button', { name: '设置', exact: true }).click()
    await page.getByRole('button', { name: 'OCR 与翻译', exact: true }).click()
    await page.getByRole('region',{name:'本地翻译运行控制'}).waitFor()
    const picker = page.getByRole('combobox', { name: 'OCR 模型' })
    await picker.waitFor()
    assert.equal(await picker.inputValue(), 'tiny')
    assert.equal(await picker.locator('option[value=small]').isDisabled(), true)
    await page.getByRole('button', { name: '管理模型', exact: true }).click()
    await page.getByRole('button', { name: '下载 Small', exact: true }).click()
    await page.getByRole('button', { name: '取消下载', exact: true }).waitFor()
    assert.ok(await page.getByRole('progressbar', { name: 'Small 模型下载进度' }).evaluate(e => e.value) > 40)
    await page.getByRole('button', { name: '取消下载', exact: true }).click()
    await page.getByRole('button', { name: '重新下载', exact: true }).waitFor()
    await page.evaluate(() => Object.assign(window.smallStatus, { state: 'installed', installed: true, downloadedBytes: 31114837, error: null }))
    await page.getByRole('button', { name: '使用 Small', exact: true }).click()
    await page.getByRole('button', { name: '校验', exact: true }).click()
    const controls = await page.locator('.ocr-model-actions .button').evaluateAll(buttons => buttons.map(button => {
      const r = button.getBoundingClientRect(); const parent = button.closest('.ocr-model-card').getBoundingClientRect()
      return { text: button.textContent, visible: r.width > 0 && r.height > 0, fits: r.left >= parent.left && r.right <= parent.right }
    }))
    assert.ok(controls.every(control => control.visible && control.fits))
    await page.getByRole('button', { name: 'OCR 与翻译', exact: true }).click()
    await page.waitForFunction(() => document.querySelector('select[aria-label="OCR 模型"]')?.value === 'small')
    assert.equal(await picker.locator('option[value=small]').isDisabled(), false)
    await page.getByRole('button', { name: '管理模型', exact: true }).click()
    await page.getByRole('button', { name: '删除', exact: true }).click()
    await page.getByRole('button', { name: '下载 Small', exact: true }).waitFor()
    assert.equal(await page.evaluate(() => window.smallStatus.selected), 'tiny')
    await page.reload()
    await page.getByRole('button',{name:'模型管理库',exact:true}).click()
    const layoutCard=v=>page.locator('.model-package').filter({has:page.getByRole('heading',{name:`PP-DocLayout-${v} · 版面检测`,exact:true})})
    await layoutCard('S').getByRole('button',{name:'当前版面模型',exact:true}).waitFor()
    await layoutCard('M').getByRole('button',{name:'设为版面模型',exact:true}).click()
    await layoutCard('M').getByRole('button',{name:'当前版面模型',exact:true}).waitFor()
    assert.equal(await layoutCard('S').getByRole('button',{name:'设为版面模型',exact:true}).getAttribute('aria-pressed'),'false')
    await layoutCard('L').getByRole('button',{name:'下载 模型',exact:true}).click()
    await layoutCard('L').locator('progress').waitFor()
    await layoutCard('L').getByRole('button',{name:'取消下载',exact:true}).click()
    await layoutCard('L').getByRole('button',{name:'导入本地',exact:true}).click()
    await layoutCard('L').getByRole('button',{name:'设为版面模型',exact:true}).click()
    await layoutCard('L').getByRole('button',{name:'当前版面模型',exact:true}).waitFor()
    await layoutCard('L').getByRole('button',{name:'校验 PP-DocLayout-L · 版面检测',exact:true}).click()
    await layoutCard('L').getByRole('button',{name:'删除 PP-DocLayout-L · 版面检测',exact:true}).click()
    await layoutCard('L').getByRole('button',{name:'下载 模型',exact:true}).waitFor()
    await layoutCard('M').getByRole('button',{name:'设为版面模型',exact:true}).click()
    await layoutCard('M').getByRole('button',{name:'当前版面模型',exact:true}).waitFor()
    const source=page.getByRole('combobox',{name:'模型下载来源'})
    for(const value of ['modelscope','huggingface','hf-mirror','auto']){
      await source.selectOption(value);await page.waitForFunction(value=>window.catalog.downloadSource===value,value)
      await page.waitForFunction(()=>!document.querySelector('select[aria-label="模型下载来源"]')?.disabled)
    }
    const index=page.locator('.model-package').filter({has:page.getByRole('heading',{name:'Index-Translate-2B · Q5_K_M',exact:true})})
    await index.getByRole('button',{name:'设为离线翻译',exact:true}).click()
    await index.getByRole('button',{name:'正在使用离线翻译',exact:true}).waitFor()
    assert.ok(await index.locator('.model-warning').isVisible(),'Hash warning does not disable activation')
    const runtime=page.getByRole('region',{name:'本地翻译运行控制'})
    await runtime.getByRole('button',{name:'GPU',exact:true}).click();await page.waitForFunction(()=>window.runtime.gpuEnabled)
    await runtime.getByRole('button',{name:'加载并常驻',exact:true}).click();await runtime.getByRole('button',{name:'卸载模型',exact:true}).waitFor()
    await runtime.getByRole('switch',{name:'开启本地翻译 API'}).click();await runtime.getByRole('button',{name:'复制密钥',exact:true}).waitFor()
    const runtimeLayout=await runtime.locator('button,input,code').evaluateAll(elements=>elements.map(element=>{const r=element.getBoundingClientRect(),panel=element.closest('.local-runtime').getBoundingClientRect();return {fits:r.left>=panel.left-.5&&r.right<=panel.right+.5,text:element.textContent}}))
    assert.ok(runtimeLayout.every(item=>item.fits),'Runtime and API controls fit the panel at 200%')
    if(width===1280&&process.env.STARSHOT_RUNTIME_SAMPLE_DIR){const temp=await mkdtemp(resolve(tmpdir(),'Starshot-runtime-ui-'));try{await mkdir(process.env.STARSHOT_RUNTIME_SAMPLE_DIR,{recursive:true});const image=resolve(temp,'runtime.png');await runtime.screenshot({path:image});await copyFile(image,resolve(process.env.STARSHOT_RUNTIME_SAMPLE_DIR,'runtime-panel.png'))}finally{await rm(temp,{recursive:true,force:true})}}
    await runtime.getByRole('button',{name:'复制密钥',exact:true}).click();assert.ok(await page.evaluate(()=>window.calls.includes('models.runtime.copyKey')))
    await runtime.getByRole('spinbutton',{name:'本地翻译 API 端口'}).fill('43183');await runtime.getByRole('button',{name:'应用端口',exact:true}).click();await page.waitForFunction(()=>window.runtime.apiPort===43183)
    await runtime.getByRole('switch',{name:'开启本地翻译 API'}).click();await page.waitForFunction(()=>!window.runtime.apiEnabled);assert.equal(await runtime.getByRole('button',{name:'卸载模型',exact:true}).count(),1,'Disabling API preserves residency')
    await runtime.getByRole('button',{name:'卸载模型',exact:true}).click();await runtime.getByRole('button',{name:'加载并常驻',exact:true}).waitFor()
    await runtime.getByRole('button',{name:'CPU',exact:true}).click();await page.waitForFunction(()=>!window.runtime.gpuEnabled)
    assert.equal(await page.evaluate(()=>window.runtime.loaded),false,'Compute selection alone does not load model')
    await page.getByRole('button',{name:'扫描本地',exact:true}).click()
    await page.getByText('发现 2 项 · 已检查 24 个文件',{exact:true}).waitFor()
    await page.locator('.model-local-result').first().getByRole('button',{name:'导入副本',exact:true}).click()
    await index.getByText('当前来源：本地导入',{exact:true}).waitFor()
    await page.getByRole('button',{name:'导入本地 Small',exact:true}).click()
    await page.getByRole('button',{name:'使用 Small',exact:true}).waitFor()
    await page.getByRole('button',{name:'使用 Small',exact:true}).click()
    await page.getByRole('button',{name:'正在使用',exact:true}).last().waitFor()
    await page.getByRole('button',{name:'选择文件夹扫描',exact:true}).click()
    const acquisition=await page.locator('.model-acquisition button,.model-acquisition select,.model-package-actions button').evaluateAll(controls=>controls.map(control=>{
      const r=control.getBoundingClientRect(),parent=control.closest('.model-acquisition,.model-package').getBoundingClientRect()
      return {text:control.textContent,fits:r.left>=parent.left-.5&&r.right<=parent.right+.5}
    }))
    assert.ok(acquisition.every(control=>control.fits),'Source/local-import controls stay inside panels at 200%')
    assert.equal(await page.evaluate(() => window.calls.includes('capture.begin')), false)
    assert.deepEqual(errors, [])
    report.push({ width, height, scale: 2, theme, ok: true, controls, acquisition,calls: await page.evaluate(() => window.calls) })
    await context.close()
  }
  console.log('PASS headless UI: picker, DLC download/progress/cancel/verify/switch/delete; 200% light/dark; no toolbar overflow; no desktop interaction')
} finally {
  await browser?.close(); await new Promise(resolve => server.close(resolve))
  await writeFile(process.argv[4], JSON.stringify(report, null, 2))
}
