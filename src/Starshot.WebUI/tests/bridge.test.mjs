import test from 'node:test'
import assert from 'node:assert/strict'

const sent = []
let receive
globalThis.window = {
  setTimeout, clearTimeout,
  chrome: { webview: { postMessage: value => sent.push(value), addEventListener: (_, callback) => { receive = callback } } },
}
const { request, onEvent } = await import('../src/bridge.ts')
const { capacityFromStop, stopFromCapacity, detail } = await import('../src/math.ts')

test('concurrent RPC responses resolve by request identity, not arrival order', async () => {
  const first = request('settings.get'), second = request('library.list')
  const [a,b] = sent.slice(-2)
  receive({data:{type:'response',id:b.id,ok:true,result:{items:[]}}})
  receive({data:{type:'response',id:a.id,ok:true,result:{capacity:14}}})
  assert.deepEqual(await first,{capacity:14}); assert.deepEqual(await second,{items:[]})
  assert.equal(a.version,1)
})
test('native failure is surfaced and unknown responses cannot resolve a request', async () => {
  const operation = request('library.open'), message=sent.at(-1)
  receive({data:{type:'response',id:'unknown',ok:true,result:'unexpected'}})
  receive({data:{type:'response',id:message.id,ok:false,error:'图片已移动'}})
  await assert.rejects(operation,/图片已移动/)
})
test('cancelling a request rejects the caller and releases the native operation', async () => {
  const controller=new AbortController()
  const operation=request('translation.run',{text:'test'},controller.signal)
  const original=sent.at(-1)
  controller.abort()
  await assert.rejects(operation,{name:'AbortError'})
  assert.equal(sent.at(-1).method,'request.cancel')
  assert.equal(sent.at(-1).params.requestId,original.id)
  receive({data:{type:'response',id:original.id,ok:true,result:'late result'}})
})
test('event subscriptions unsubscribe without duplicate delivery', () => {
  let count=0
  const off=onEvent('ocr.result',()=>count++)
  receive({data:{type:'event',name:'ocr.result',data:{text:'one'}}})
  off()
  receive({data:{type:'event',name:'ocr.result',data:{text:'two'}}})
  assert.equal(count,1)
})
test('log capacity mapping preserves 14x and stays within the supported endpoints', () => {
  assert.equal(capacityFromStop(stopFromCapacity(14)),14)
  assert.equal(capacityFromStop(1),2);assert.equal(capacityFromStop(5),32)
  for(let i=0;i<100;i++) {assert.ok(detail(i)>=0&&detail(i)<1);assert.equal(detail(i),detail(i))}
})
