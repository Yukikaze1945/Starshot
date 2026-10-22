// Synthetic images only. All images and temporary application data are removed in finally.
import {createRequire} from 'node:module'
import {mkdtemp,writeFile,readFile,rm,mkdir} from 'node:fs/promises'
import {tmpdir} from 'node:os'
import {resolve,join} from 'node:path'
import {pathToFileURL} from 'node:url'
import {spawn} from 'node:child_process'
import assert from 'node:assert/strict'
const {chromium}=createRequire(import.meta.url)(process.argv[2])
const repo=resolve(import.meta.dirname,'../..'),report=join(repo,'docs/reports/document-ocr-20261020')
await mkdir(report,{recursive:true})
const temp=await mkdtemp(join(tmpdir(),'starshot-document-ocr-'))
const models=join(repo,'build/document-ocr/models'),python=join(repo,'build/document-ocr/venv/Scripts/python.exe')
const run=(exe,args,extra={})=>new Promise((yes,no)=>{const child=spawn(exe,args,{cwd:repo,windowsHide:true,...extra});let output='';child.stdout.on('data',b=>{output+=b.toString();process.stdout.write(b)});child.stderr.on('data',b=>{output+=b.toString();process.stderr.write(b)});child.on('error',no);child.on('exit',code=>code===0?yes(output):no(new Error(`${exe} exited ${code}: ${output.slice(-2000)}`)))})
let browser
try{
 browser=await chromium.launch({headless:true,executablePath:process.argv[3],args:['--disable-gpu']})
 const page=await browser.newPage({viewport:{width:1400,height:1400},deviceScaleFactor:1})
 const css=pathToFileURL(join(repo,'src/Starshot.WebUI/node_modules/katex/dist/katex.min.css')).href
 const katex=createRequire(import.meta.url)(join(repo,'src/Starshot.WebUI/node_modules/katex'))
 const fixtures=[]
 const formulas=[['fraction',String.raw`\frac{x^2+1}{\sqrt{y}}`],['sum',String.raw`\sum_{i=1}^{n} i=\frac{n(n+1)}{2}`],['integral',String.raw`\int_0^1 x^2\,dx=\frac{1}{3}`],['matrix',String.raw`\begin{pmatrix}a&b\\c&d\end{pmatrix}`],['multiline',String.raw`\begin{aligned}x+y&=3\\x-y&=1\end{aligned}`],['roots',String.raw`\sqrt{x}+\sqrt[3]{y}=\alpha`]]
 async function fixture(name,html,kind,expected){
  const path=join(temp,'fixture.html');await writeFile(path,`<!doctype html><meta charset="utf-8"><link rel="stylesheet" href="${css}"><style>body{margin:0;background:white;color:black}.fixture{display:inline-block;padding:24px;font-size:32px}table{border-collapse:collapse;font:22px 'Times New Roman'}td,th{padding:10px 16px}h1{font-size:32px}.page{font:18px/1.6 'Times New Roman';width:900px;padding:36px}.cols{columns:2;column-gap:38px}p{margin:0 0 15px}</style><div class="fixture">${html}</div>`)
  await page.goto(pathToFileURL(path).href);await page.evaluate(()=>document.fonts.ready)
  const box=await page.locator('.fixture').boundingBox();await page.locator('.fixture').screenshot({path:join(temp,name+'.png')})
  fixtures.push({name,width:Math.round(box.width),height:Math.round(box.height),file:name+'.bgra',kind,expected})
 }
 for(const [name,latex] of formulas)await fixture(name,katex.renderToString(latex,{displayMode:true,throwOnError:true}),'formula',latex)
 const table=`<table border="1"><tr><th colspan="3">Measurement results</th></tr><tr><th>Sample</th><th>Value</th><th>Unit</th></tr><tr><td>Alpha</td><td>80</td><td>nits</td></tr><tr><td rowspan="2">Beta</td><td>200</td><td>nits</td></tr><tr><td>800</td><td>nits</td></tr></table>`
 await fixture('table-wired',table,'table',null)
 await fixture('table-wireless',table.replace('border="1"','border="0"'),'table',null)
 const para='<p>Document analysis identifies the reading order of scientific articles. It preserves text, equations and tables while keeping the original image unchanged. This example describes a mathematical model and its measured performance.</p>'
 await fixture('page',`<article class="page"><h1>Perceptual analysis of scientific documents</h1><p>Abstract — A lightweight system for text, equations and table reconstruction.</p><div class="cols"><h2>1. Introduction</h2>${para.repeat(3)}<h2>2. Method</h2>${para}${katex.renderToString(formulas[0][1],{displayMode:true})}${para.repeat(2)}<h2>3. Results</h2>${table}</div></article>`,'layout',null)
 await writeFile(join(temp,'fixtures.json'),JSON.stringify(fixtures))
 await run(python,['-c',`import json,pathlib
from PIL import Image
r=pathlib.Path(${JSON.stringify(temp)})
fs=json.loads((r/'fixtures.json').read_text())
for f in fs:
 im=Image.open(r/(f['name']+'.png')).convert('RGBA')
 f['width'],f['height']=im.size
 (r/f['file']).write_bytes(im.tobytes('raw','BGRA'))
(r/'fixtures.json').write_text(json.dumps(fs))
(r/'page4k.bgra').write_bytes(Image.open(r/'page.png').convert('RGBA').resize((3840,2160)).tobytes('raw','BGRA'))`])
 await run(join(repo,'tools/DocumentOcrTest/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/DocumentOcrTest.exe'),[temp,join(report,'inference.json'),models])
 await run(python,[join(repo,'tools/DocumentOcrTest/reference.py'),temp,models,join(report,'reference.json')])
 console.log('Document OCR fixtures and temporary app data cleaned on exit.')
}finally{
 if(browser)await browser.close()
 assert(resolve(temp).startsWith(resolve(tmpdir())+ '\\'))
 await rm(temp,{recursive:true,force:true})
}
