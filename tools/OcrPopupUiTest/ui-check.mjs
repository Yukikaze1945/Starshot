// Headless OCR popup integration/layout check. No desktop interaction is used.
// Usage: node ui-check.mjs <playwright package directory> <msedge.exe>
import { createRequire } from 'node:module'
import { createServer } from 'node:http'
import { readFile, mkdtemp, rm } from 'node:fs/promises'
import { resolve, extname, sep } from 'node:path'
import { tmpdir } from 'node:os'
import { fileURLToPath } from 'node:url'
import assert from 'node:assert/strict'

const { chromium } = createRequire(import.meta.url)(process.argv[2])
const root = fileURLToPath(new URL('../../src/Starshot.WebUI/dist/', import.meta.url))
const previousRoot = resolve((process.argv[4] && !process.argv[4].startsWith('--') ? process.argv[4] : undefined) || 'C:/Users/18392/AppData/Local/Programs/Starshot Fork/app-2.6.0-preview.9/WebUI')
const server = createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost')
  const previous = url.pathname.startsWith('/previous/')
  const base = previous ? previousRoot : root
  const requestPath = previous ? url.pathname.slice('/previous'.length) : url.pathname
  const path = resolve(base, '.' + decodeURIComponent(requestPath === '/' ? '/index.html' : requestPath))
  if (!path.startsWith(base.replace(/[\\/]$/, '') + sep)) { res.writeHead(403); res.end(); return }
  try { res.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2' })[extname(path)] || 'application/octet-stream'); res.end(await readFile(path)) }
  catch { res.writeHead(404); res.end() }
})
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
let browser
let sampleDir
const report = []
const wantsPreview = process.argv.includes('--preview')
let previewImageBase64
async function seedMock(page, theme) {
  await page.addInitScript(({ theme }) => {
    const settings = { screenshotFolder: '', extraFolders: [], subfolders: false, subfolderPattern: '', filenamePattern: '', regionFilenamePattern: '', autoCopy: false, autoCopyOcr: true, ocrModel: 'tiny', ocrGpuAcceleration: false, ultraHdr: false, capacityManual: false, capacity: 8, captureMode: 2, videoCodec: 0, sdrFormat: 0, hdrFormat: 0, quality: 1, colorManagement: false, deleteSdrHdr: false, sdrWhite: 0, monitorSource: 0, muteFullscreen: false, endpoint: 'https://example.invalid/v1', model: 'test-model', targetLanguage: '简体中文', theme, language: '', autoUpdate: false, previewUpdates: false, startupHidden: false, highPriority: false }
    const boot = { version: 'UI test', protocol: 1, hasApiKey: true, settings, hotkeys: [] }
    const calls = window.calls = [], handlers = []; window.readyFrames = []
    window.chrome = { webview: { addEventListener: (_, handler) => handlers.push(handler), postMessage: message => {
      calls.push(message.method)
      const reply = (ok, result, error) => handlers.forEach(handler => handler({ data: { type: 'response', id: message.id, ok, result, error } }))
      if (message.method === 'app.bootstrap') {
        // Deliberately deliver the first OCR result before resolving bootstrap/app.ready.
        setTimeout(() => { window.resultDeliveredBeforeReady = !calls.includes('app.ready'); handlers.forEach(handler => handler({ data: { type: 'event', name: 'ocr.result', data: { id: 'first', text: 'First recognized line\nSecond recognized line', raw: 'First recognized lineSecond recognized line', lineCount: 2, translate: false, autoCopied: false } } })) }, 0)
        setTimeout(() => reply(true, structuredClone(boot)), 20)
      } else if (message.method === 'app.ready') { window.readyFrames.push({ id:message.params.ocrResultId, text:document.querySelector('[aria-label="可编辑的原文"]')?.textContent, animation:document.querySelector('.compact-ocr')?.getAnimations().length }); setTimeout(() => reply(true,null),0) }
      else if (message.method === 'window.close') setTimeout(() => reply(true,null),0)
      else if (message.method === 'clipboard.copyRichText') { window.lastCopy = message.params; setTimeout(() => reply(true, null), 0) }
      else if (message.method === 'settings.set') { window.lastSettingSet = message.params; settings[message.params.key] = message.params.value; setTimeout(() => reply(true, structuredClone(settings)), 0) }
      else if (message.method === 'translation.formatted') setTimeout(() => reply(true, message.params.segments.map(segment => ({ id: segment.id, text: `译：${segment.text}` }))), 0)
      else if (message.method === 'ocr.models.status') setTimeout(() => reply(true, { selected: 'tiny', state: 'notInstalled', installed: false, downloadedBytes: 0, totalBytes: 1, error: null, directory: '', version: '1' }), 0)
      else if (message.method === 'request.cancel') { /* expected for no-op cancellation */ }
      else setTimeout(() => reply(false, null, `Unexpected mock request: ${message.method}`), 0)
    } } }
  }, { theme })
}
try {
  browser = await chromium.launch({ headless: true, executablePath: process.argv[3], args: process.argv.includes('--software') ? ['--disable-gpu'] : [] })
  sampleDir = await mkdtemp(resolve(tmpdir(), 'starshot-ocr-popup-'))
  for (const [width, height, theme] of [[444, 270, 1], [444, 240, 2], [344, 198, 1], [584, 391, 2]]) {
    const context = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 2 })
    const page = await context.newPage()
    const errors = [], requested = []
    page.on('pageerror', error => errors.push(error.message))
    page.on('request', request => requested.push(new URL(request.url()).pathname))
    await seedMock(page, theme)
    const loadStarted = Date.now()
    await page.goto(`http://127.0.0.1:${server.address().port}/?surface=ocr`)
    const source = page.getByRole('textbox', { name: '可编辑的原文' })
    await source.waitFor().catch(async error => { console.error(JSON.stringify({ body: await page.locator('body').innerText(), errors, requests: requested, calls: await page.evaluate(() => window.calls) }, null, 2)); throw error })
    await page.waitForFunction(() => document.querySelector('[aria-label="可编辑的原文"]')?.innerText.includes('Second recognized line'))
    const editorReadyAt = Date.now()
    const startupMetrics = await page.evaluate(() => {
      const resources = performance.getEntriesByType('resource').filter(entry => /\.js(?:\?|$)/.test(entry.name))
      return { initialJsDecodedBytes: resources.reduce((total, entry) => total + entry.decodedBodySize, 0), initialJsResources: resources.map(entry => ({ name: new URL(entry.name).pathname, bytes: entry.decodedBodySize })) }
    })
    if (wantsPreview && width === 444 && theme === 1) {
      await page.locator('.compact-ocr').evaluate(element => element.getAnimations().forEach(animation => animation.finish()))
      previewImageBase64 = (await page.screenshot({ path: resolve(sampleDir, 'popup-initial-584.png') })).toString('base64')
    }
    await page.waitForFunction(()=>window.readyFrames?.some(frame=>frame.id==='first'))
    const committed=await page.evaluate(()=>window.readyFrames.find(frame=>frame.id==='first'))
    assert.match(committed.text,/First recognized line/,'Ready is sent only after the OCR document exists')
    assert.equal(committed.animation,0,'Compact editor has no fade/slide animation during native handoff')
    const firstReady = await page.evaluate(() => window.calls.indexOf('app.ready'))
    assert.ok(firstReady >= 0, 'app.ready should run after the result-bearing popup mounts')
    assert.equal(await page.evaluate(() => window.resultDeliveredBeforeReady), true, 'first OCR result should arrive before app.ready')
    const earlyAppRequests = requested.filter(path => /\/(?:App|SettingsPanel)-.*\.js$/.test(path))
    assert.deepEqual(earlyAppRequests, [], 'main App and SettingsPanel chunks should remain unloaded before configure')

    const textNode = await source.evaluate(element => {
      const node = element.querySelector('p')?.firstChild
      if (!node) throw new Error('OCR result text node was not rendered')
      const range = document.createRange(); range.setStart(node, 0); range.setEnd(node, 5)
      const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range)
      return node.textContent
    })
    assert.equal(textNode, 'First recognized line')
    const bubble = page.getByRole('toolbar', { name: '选中文字格式' })
    await bubble.waitFor()
    await bubble.getByRole('button', { name: '加粗' }).click()
    assert.match(await source.locator('strong').first().textContent(), /First/)
    await page.getByRole('button', { name: '原图断行' }).click()
    await page.waitForFunction(() => document.querySelector('[aria-label="可编辑的原文"]')?.innerText === 'First recognized lineSecond recognized line')
    await source.evaluate(element => { const selection = getSelection(); selection.removeAllRanges(); element.focus() })
    await page.getByRole('button', { name: '复制原文' }).click()
    await page.waitForFunction(() => window.calls.includes('clipboard.copyRichText'))
    assert.ok(await page.evaluate(() => window.lastCopy.text.includes('First recognized line')))
    const autoCopy = page.getByRole('checkbox', { name: 'OCR 自动复制' })
    await autoCopy.click()
    await page.waitForFunction(() => window.calls.includes('settings.set') && document.querySelector('.auto-copy-toggle input')?.checked === false)
    assert.equal(await autoCopy.isChecked(), false)
    await page.getByRole('button', { name: '翻译', exact: true }).click()
    await page.getByRole('button', { name: /译文/ }).first().click()
    await page.waitForFunction(() => document.querySelector('[aria-label="可编辑的译文"]')?.innerText.includes('译：'))

    const popup = page.locator('.ocr-popup')
    const beforeSettings = await popup.evaluate(element => ({ width: element.clientWidth, height: element.clientHeight, scrollWidth: element.scrollWidth, scrollHeight: element.scrollHeight }))
    assert.ok(beforeSettings.scrollWidth <= beforeSettings.width + 1, `popup has horizontal overflow: ${JSON.stringify(beforeSettings)}`)
    await page.getByRole('button', { name: 'OCR 与翻译设置' }).click()
    const dialog = page.getByRole('dialog', { name: 'OCR 与翻译设置' })
    await dialog.waitFor()
    await page.getByRole('button', { name: 'OCR 与翻译', exact: true }).waitFor()
    await dialog.getByRole('switch', { name: 'GPU 加速识别' }).click()
    await page.waitForFunction(() => window.lastSettingSet?.key === 'ocrGpuAcceleration' && window.lastSettingSet?.value === true)
    assert.ok((await page.evaluate(() => window.calls)).includes('settings.set'), 'GPU toggle should persist through settings.set')
    const settingsMeasure = await dialog.evaluate(element => {
      const content = element.querySelector('.settings-content')
      const bounds = element.getBoundingClientRect()
      return { width: element.clientWidth, height: element.clientHeight, scrollWidth: element.scrollWidth, contentWidth: content?.clientWidth, contentScrollWidth: content?.scrollWidth, left: bounds.left, right: bounds.right, top: bounds.top, bottom: bounds.bottom, viewport: innerWidth, viewportHeight: innerHeight }
    })
    assert.ok(settingsMeasure.left >= -1 && settingsMeasure.right <= width + 1, `settings dialog escapes viewport: ${JSON.stringify(settingsMeasure)}`)
    assert.ok(settingsMeasure.top >= 10 && settingsMeasure.viewportHeight - settingsMeasure.bottom >= 10, `settings dialog needs outer margin: ${JSON.stringify(settingsMeasure)}`)
    assert.ok(Math.abs((settingsMeasure.left + settingsMeasure.right) / 2 - width / 2) <= 1, `settings dialog should be centered: ${JSON.stringify(settingsMeasure)}`)
    assert.ok(settingsMeasure.scrollWidth <= settingsMeasure.width + 1, `settings dialog has horizontal overflow: ${JSON.stringify(settingsMeasure)}`)
    assert.ok(settingsMeasure.contentScrollWidth <= settingsMeasure.contentWidth + 1, `settings content has horizontal overflow: ${JSON.stringify(settingsMeasure)}`)
    assert.ok(requested.some(path => /\/App-.*\.js$/.test(path)), 'Dialog/App chunk should load on configure')
    assert.ok(requested.some(path => /\/SettingsPanel-.*\.js$/.test(path)), 'SettingsPanel chunk should load on configure')
    await page.screenshot({ path: resolve(sampleDir, `popup-${width}-${theme}.png`), fullPage: true })
    assert.deepEqual(errors, [])
    report.push({ width, height, scale: 2, theme: theme === 1 ? 'light' : 'dark', resultBeforeReady: true, firstVisibleEditorMs: editorReadyAt - loadStarted, initialJsDecodedBytes: startupMetrics.initialJsDecodedBytes, initialJsResources: startupMetrics.initialJsResources, settingsMeasure, calls: await page.evaluate(() => window.calls) })
    await context.close()
  }
  // Real bootstrap payload and hidden-preparation delivery, without the
  // artificially early event used by the preceding four regression cases.
  for (const delivery of ['bootstrap','prepared']) {
    const context=await browser.newContext({viewport:{width:460,height:300},deviceScaleFactor:2}),page=await context.newPage()
    await page.addInitScript(({delivery})=>{
      const handlers=[];window.readyFrames=[]
      const payload={id:'actual-payload',text:'启动即带文字 / Ready without a blank frame',raw:'启动即带文字 / Ready without a blank frame',lineCount:1,translate:false,autoCopied:false}
      window.chrome={webview:{addEventListener:(_,h)=>handlers.push(h),postMessage:m=>{
        const reply=result=>queueMicrotask(()=>handlers.forEach(h=>h({data:{type:'response',id:m.id,ok:true,result}})))
        if(m.method==='app.bootstrap'){reply({version:'test',protocol:1,hasApiKey:false,settings:{theme:2,targetLanguage:'简体中文',autoCopyOcr:false},hotkeys:[],ocr:delivery==='bootstrap'?payload:null});return}
        if(m.method==='app.ready'){
          window.readyFrames.push({id:m.params.ocrResultId,text:document.querySelector('[aria-label="可编辑的原文"]')?.textContent,theme:document.documentElement.dataset.theme,animations:document.querySelector('.compact-ocr')?.getAnimations().length})
          if(delivery==='prepared'&&!m.params.ocrResultId)setTimeout(()=>handlers.forEach(h=>h({data:{type:'event',name:'ocr.result',data:payload}})),20)
        }
        reply(null)
      }}}
    },{delivery})
    await page.goto(`http://127.0.0.1:${server.address().port}/?surface=ocr`)
    await page.waitForFunction(()=>window.readyFrames.some(f=>f.id==='actual-payload'))
    const frames=await page.evaluate(()=>window.readyFrames)
    const ready=frames.find(f=>f.id==='actual-payload')
    assert.match(ready.text,/启动即带文字/);assert.equal(ready.theme,'dark');assert.equal(ready.animations,0)
    if(delivery==='bootstrap')assert.ok(frames.every(f=>f.id),'Bootstrap OCR never announces an empty editor')
    else assert.equal(frames[0].id,null,'Prepared empty editor is distinguished from a rendered result')
    report.push({delivery,committedReadyFrames:frames})
    await context.close()
  }
  const benchmarkRuns = []
  for (const order of process.argv.includes('--skip-benchmark') ? [] : [['current', 'previous'], ['previous', 'current'], ['current', 'previous']]) {
    for (const build of order) {
      const context = await browser.newContext({ viewport: { width: 584, height: 391 }, deviceScaleFactor: 2 })
      const page = await context.newPage(), errors = []
      page.on('pageerror', error => errors.push(error.message))
      await seedMock(page, 1)
      const loadStarted = Date.now()
      const url = `http://127.0.0.1:${server.address().port}/${build === 'previous' ? 'previous/' : ''}?surface=ocr`
      await page.goto(url)
      const editor = page.locator('.compact-ocr [contenteditable="true"]').first()
      await editor.waitFor({ timeout: 10000 })
      await page.waitForFunction(() => document.querySelector('.compact-ocr [contenteditable="true"]')?.textContent?.includes('First recognized line'), null, { timeout: 10000 })
      const firstVisibleEditorMs = Date.now() - loadStarted
      const initialJs = await page.evaluate(() => performance.getEntriesByType('resource').filter(entry => /\.js(?:\?|$)/.test(entry.name)).map(entry => ({ name: new URL(entry.name).pathname, bytes: entry.decodedBodySize })))
      benchmarkRuns.push({ build, firstVisibleEditorMs, initialJsDecodedBytes: initialJs.reduce((sum, file) => sum + file.bytes, 0), initialJsResources: initialJs, errors })
      await context.close()
    }
  }
  const median = values => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)]
  const benchmark = Object.fromEntries((process.argv.includes('--skip-benchmark') ? [] : ['current', 'previous']).map(build => {
    const runs = benchmarkRuns.filter(run => run.build === build)
    return [build, { samples: runs.length, firstVisibleEditorMs: runs.map(run => run.firstVisibleEditorMs), medianFirstVisibleEditorMs: median(runs.map(run => run.firstVisibleEditorMs)), initialJsDecodedBytes: runs.map(run => run.initialJsDecodedBytes), medianInitialJsDecodedBytes: median(runs.map(run => run.initialJsDecodedBytes)), initialJsResources: runs[0].initialJsResources }]
  }))
  console.log(JSON.stringify({ ok: true, report, benchmarkOrder: ['current/previous', 'previous/current', 'current/previous'], benchmark, ...(previewImageBase64 ? { previewImageBase64 } : {}) }, null, 2))
} finally {
  await browser?.close()
  await new Promise(resolve => server.close(resolve))
  if (sampleDir) await rm(sampleDir, { recursive: true, force: true })
}
