import {createRequire} from 'node:module'
import {mkdtemp,mkdir,rm,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {pathToFileURL,fileURLToPath} from 'node:url'
import {spawn} from 'node:child_process'
import assert from 'node:assert/strict'
const repo=fileURLToPath(new URL('../../',import.meta.url)),{chromium}=createRequire(import.meta.url)(process.argv[2])
const root=await mkdtemp(join(tmpdir(),'starshot-subtitle-speed-')),report=join(repo,'docs/reports/subtitles-20261021');await mkdir(report,{recursive:true})
const run=(exe,args)=>new Promise((yes,no)=>{const p=spawn(exe,args,{cwd:repo,windowsHide:true});let output='';p.stdout.on('data',d=>{output+=d;process.stdout.write(d)});p.stderr.on('data',d=>{output+=d;process.stderr.write(d)});p.on('error',no);p.on('exit',code=>code===0?yes():no(new Error(output.slice(-3000))))})
let browser
try{
 browser=await chromium.launch({headless:true,executablePath:process.argv[3],args:['--disable-gpu']});const page=await browser.newPage({viewport:{width:4000,height:600}}),fixtures=[]
 const font=pathToFileURL(join(repo,'src/Starshot.WebUI/node_modules/@fontsource-variable/noto-sans-sc/index.css')).href
 for(const [name,width,height,fontSize,chinese,english,expected] of [
  ['720p',1280,140,28,'我们在这里记录每一句对白','Keep every word, without interrupting playback.','记录'],
  ['1080p',1920,180,32,'让每一句对白都留下来','Read subtitles while the video keeps playing.','对白'],
  ['4k',3840,360,52,'这是四千分辨率的中文字幕识别测试','Capture every sentence without stopping playback.','分辨率'],
  ['scene-a',1280,140,30,'这是第一句字幕','The first subtitle is visible.','第一句'],
  ['scene-b',1280,140,30,'这是短暂的第二句','A short second subtitle.','第二句'],
  ['scene-c',1280,140,30,'这是第三句字幕','The third subtitle follows.','第三句']]){
  await writeFile(join(root,'fixture.html'),`<!doctype html><meta charset="utf-8"><link rel="stylesheet" href="${font}"><style>body{margin:0}.frame{box-sizing:border-box;width:${width}px;height:${height}px;display:flex;flex-direction:column;align-items:center;justify-content:center;background:#21272d;color:#f8f9f4;font:${fontSize}px/1.45 'Noto Sans SC Variable',sans-serif;text-shadow:1px 1px 2px black}.en{font-size:.8em}</style><div class="frame"><div>${chinese}</div><div class="en">${english}</div></div>`)
  await page.goto(pathToFileURL(join(root,'fixture.html')).href);await page.evaluate(()=>document.fonts.ready);await page.locator('.frame').screenshot({path:join(root,name+'.png')});fixtures.push({name,width,height,file:name+'.bgra',expected})
 }
 await writeFile(join(root,'fixtures.json'),JSON.stringify(fixtures))
 await run(join(repo,'build/document-ocr/venv/Scripts/python.exe'),['-c',`from PIL import Image;import json;from pathlib import Path;r=Path(${JSON.stringify(root)});fs=json.loads((r/'fixtures.json').read_text(encoding='utf-8'));[(r/f['file']).write_bytes(Image.open(r/(f['name']+'.png')).convert('RGBA').tobytes('raw','BGRA')) for f in fs]`])
 await run(join(repo,'tools/SubtitleOcrBenchmark/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/SubtitleOcrBenchmark.exe'),[root,join(repo,'build/ocr-tiny-source/SimdPaddleOCR-main/models'),report,process.argv[4]||'cpu'])
}finally{await browser?.close();assert(resolve(root).startsWith(resolve(tmpdir())+'\\'));await rm(root,{recursive:true,force:true})}
