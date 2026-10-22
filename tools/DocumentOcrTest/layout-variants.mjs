// Synthetic document only. No desktop capture, installation, or persistent test images.
import {createRequire} from 'node:module'
import {mkdtemp,writeFile,rm,mkdir} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {pathToFileURL,fileURLToPath} from 'node:url'
import {spawn} from 'node:child_process'
import assert from 'node:assert/strict'
const repo=fileURLToPath(new URL('../../',import.meta.url)),{chromium}=createRequire(import.meta.url)(process.argv[2])
const temp=await mkdtemp(join(tmpdir(),'starshot-layout-variants-')),report=join(repo,'docs/reports/doclayout-variants-20261021')
await mkdir(report,{recursive:true})
const python=join(repo,'build/document-ocr/venv/Scripts/python.exe')
const run=(exe,args)=>new Promise((yes,no)=>{let output='';const p=spawn(exe,args,{cwd:repo,windowsHide:true});p.stdout.on('data',d=>{output+=d;process.stdout.write(d)});p.stderr.on('data',d=>{output+=d;process.stderr.write(d)});p.on('error',no);p.on('exit',code=>code===0?yes():no(new Error(`${exe} returned ${code}: ${output.slice(-2400)}`)))})
let browser
try{
 browser=await chromium.launch({headless:true,executablePath:process.argv[3],args:['--disable-gpu']})
 const page=await browser.newPage({viewport:{width:1100,height:1500}})
 const katex=createRequire(import.meta.url)(join(repo,'src/Starshot.WebUI/node_modules/katex'))
 const formula=katex.renderToString(String.raw`\int_0^1 x^2\,dx=\frac13`,{displayMode:true})
 const p='<p>Content analysis preserves text, mathematics and table structure. Two columns establish an unambiguous reading order. Brightness is measured in nits and the original data remains unchanged.</p>'
 const table='<table><tr><th colspan="3">Measurement results</th></tr><tr><th>Sample</th><th>Value</th><th>Unit</th></tr><tr><td>Alpha</td><td>80</td><td>nits</td></tr><tr><td>Beta</td><td>200</td><td>nits</td></tr></table>'
 const html=`<!doctype html><meta charset="utf-8"><link rel="stylesheet" href="${pathToFileURL(join(repo,'src/Starshot.WebUI/node_modules/katex/dist/katex.min.css'))}"><style>body{margin:0;background:white;color:black}.paper{box-sizing:border-box;width:972px;height:1380px;padding:50px;font:20px/1.55 'Times New Roman'}h1{font-size:36px}h2{font-size:25px}.columns{columns:2;column-gap:40px}p{margin:0 0 20px}table{border-collapse:collapse;break-inside:avoid;font-size:18px}td,th{border:1px solid #555;padding:10px}.figure{height:130px;background:linear-gradient(90deg,#2958ac,#e6a444);margin:18px 0}</style><article class="paper"><h1>Scientific document understanding</h1><p>Abstract — Layout detection, equations and quantitative evaluation.</p><div class="columns"><h2>1. Introduction</h2>${p.repeat(3)}<h2>2. Method</h2>${p}${formula}${p}<h2>3. Measurements</h2>${table}<div class="figure"></div><p>Figure 1. A synthetic colour chart</p>${p.repeat(2)}</div></article>`
 await writeFile(join(temp,'page.html'),html);await page.goto(pathToFileURL(join(temp,'page.html')).href);await page.evaluate(()=>document.fonts.ready)
 await page.locator('.paper').screenshot({path:join(temp,'page.png')})
 await run(python,['-c',`from PIL import Image;from pathlib import Path;r=Path(${JSON.stringify(temp)});im=Image.open(r/'page.png').convert('RGBA');assert im.size==(972,1380);(r/'page.bgra').write_bytes(im.tobytes('raw','BGRA'));(r/'page4k.bgra').write_bytes(im.resize((3840,2160)).tobytes('raw','BGRA'))`])
 await run(join(repo,'tools/DocumentOcrTest/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/DocumentOcrTest.exe'),['--layout-variants',temp,join(repo,'build/doclayout-variants'),join(report,'inference.json')])
 await run(python,[join(repo,'tools/DocumentOcrTest/layout-variants-reference.py'),temp,join(repo,'build/doclayout-variants'),report])
}finally{
 if(browser)await browser.close()
 assert(resolve(temp).startsWith(resolve(tmpdir())+'\\'))
 await rm(temp,{recursive:true,force:true})
}
