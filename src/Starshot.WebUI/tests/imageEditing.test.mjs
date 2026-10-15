import assert from 'node:assert/strict'
import {test} from 'node:test'
import {emptyHistory,pushEdit,undoEdit,redoEdit,imagePoint,readImageDraft,validMask,registerImageReplacementGuard,prepareImageReplacement} from '../src/imageEditing.ts'

test('committed masks can undo and redo without dropping older contours',()=>{
  const original=emptyHistory(),points=[{x:1,y:2},{x:20,y:2},{x:20,y:30}]
  const first=pushEdit(original,{mask:{kind:'polygon',points},overlays:[]})
  points[0].x=999
  assert.equal(first.current.mask.points[0].x,1)
  const second=pushEdit(first,{mask:{kind:'wand',x:10,y:10,tolerance:20},overlays:[]})
  assert.equal(undoEdit(second).current.mask.kind,'polygon')
  assert.deepEqual(redoEdit(undoEdit(second)),second)
  assert.equal(original.current.mask,null)
})
test('history is bounded and branching clears redo',()=>{
  let h=emptyHistory();for(let i=0;i<70;i++)h=pushEdit(h,{mask:{kind:'wand',x:i,y:0,tolerance:1},overlays:[]})
  assert.equal(h.past.length,40)
  h=pushEdit(undoEdit(h),{mask:null,overlays:[]});assert.equal(h.future.length,0)
})
test('cursor maps CSS/DPI/zoom rectangle to original source pixels',()=>{
  assert.deepEqual(imagePoint(300,200,{left:100,top:100,width:400,height:200},3840,1920),{x:1920,y:960})
  assert.deepEqual(imagePoint(-10,-10,{left:100,top:100,width:400,height:200},3840,1920),{x:0,y:0})
})
test('unfinished polygon and history survive draft serialization',()=>{
  const h=pushEdit(emptyHistory(),{mask:{kind:'polygon',points:[{x:0,y:0},{x:40,y:0},{x:40,y:40}]},overlays:[]})
  const draft=JSON.parse(JSON.stringify({version:1,history:h,points:[{x:1,y:1},{x:2,y:2}]}))
  assert.deepEqual(readImageDraft(draft),{history:h,points:draft.points})
})
test('invalid and oversized drafts are rejected',()=>{
  assert.equal(readImageDraft({version:1,history:emptyHistory(),points:[{x:NaN,y:0}]}),null)
  assert.equal(validMask({kind:'polygon',points:[{x:1,y:1}]}),false)
  assert.equal(validMask({kind:'execute',path:'arbitrary'}),false)
  assert.equal(validMask({kind:'wand',x:Infinity,y:0,tolerance:5}),false)
})

test('image replacement waits for a draft flush and respects a refused handoff',async()=>{
 let flushed=false
 const remove=registerImageReplacementGuard(async()=>{await new Promise(r=>setTimeout(r,2));flushed=true;return false})
 assert.equal(await prepareImageReplacement(),false);assert.equal(flushed,true);remove()
 assert.equal(await prepareImageReplacement(),true)
})
