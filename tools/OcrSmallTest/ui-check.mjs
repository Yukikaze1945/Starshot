// Headless component/bridge contract test; never operates the desktop or captures images.
// Usage: node ui-check.mjs <playwright package directory> <chromium.exe> <report.json>
import { createRequire } from 'node:module'
import { createServer } from 'node:http'
import { readFile, writeFile } from 'node:fs/promises'
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
  browser = await chromium.launch({ headless: true, executablePath: process.argv[3] })
  for (const [width, height, theme] of [[1280, 900, 1], [720, 900, 2]]) {
    const context = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 2 })
    const page = await context.newPage()
    const errors = []; page.on('pageerror', error => errors.push(error.message))
    await page.addInitScript(({ theme }) => {
      const settings = { screenshotFolder: '', extraFolders: [], subfolders: false, subfolderPattern: '', filenamePattern: '', regionFilenamePattern: '', autoCopy: false, autoCopyOcr: false, ocrModel: 'tiny', ultraHdr: false, capacityManual: false, capacity: 8, captureMode: 2, videoCodec: 0, sdrFormat: 0, hdrFormat: 0, quality: 1, colorManagement: false, deleteSdrHdr: false, sdrWhite: 0, monitorSource: 0, muteFullscreen: false, endpoint: '', model: '', targetLanguage: '简体中文', theme, language: '', autoUpdate: false, previewUpdates: false, startupHidden: false, highPriority: false }
      const boot = { version: 'DLC UI test', protocol: 1, hasApiKey: false, settings, hotkeys: [] }
      window.smallStatus = { selected: 'tiny', state: 'notInstalled', installed: false, downloadedBytes: 0, totalBytes: 31114837, error: null, directory: 'isolated test data / Models / OCR', version: '1.0.0' }
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
    assert.equal(await page.evaluate(() => window.calls.includes('capture.begin')), false)
    assert.deepEqual(errors, [])
    report.push({ width, height, scale: 2, theme, ok: true, controls, calls: await page.evaluate(() => window.calls) })
    await context.close()
  }
  console.log('PASS headless UI: picker, DLC download/progress/cancel/verify/switch/delete; 200% light/dark; no toolbar overflow; no desktop interaction')
} finally {
  await browser?.close(); await new Promise(resolve => server.close(resolve))
  await writeFile(process.argv[4], JSON.stringify(report, null, 2))
}
