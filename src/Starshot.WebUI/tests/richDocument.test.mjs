import test from 'node:test'
import assert from 'node:assert/strict'
import { textDocument, translationSegments, translatedDocument } from '../src/richDocument.ts'
test('translation replaces only text and preserves nested lists, headings, alignments and marks', () => {
  const original = {type:'doc',content:[{type:'heading',attrs:{level:2,textAlign:'center'},content:[{type:'text',text:'Heading',marks:[{type:'bold'},{type:'textStyle',attrs:{color:'#123456',fontSize:'24px'}}]}]},{type:'bulletList',content:[{type:'listItem',content:[{type:'paragraph',content:[{type:'text',text:'Item',marks:[{type:'italic'}]},{type:'text',text:' '},{type:'text',text:'emphasis',marks:[{type:'underline'}]}]}]}]}]}
  assert.deepEqual(translationSegments(original),[{id:'s0',text:'Heading'},{id:'s1',text:'Item'},{id:'s2',text:'emphasis'}])
  const output = translatedDocument(original,[{id:'s2',text:'强调'},{id:'s0',text:'标题'},{id:'s1',text:'项目'}])
  assert.equal(output.content[0].content[0].text,'标题'); assert.equal(original.content[0].content[0].text,'Heading')
  assert.deepEqual(output.content[0].attrs,original.content[0].attrs); assert.deepEqual(output.content[0].content[0].marks,original.content[0].content[0].marks)
  assert.equal(output.content[1].content[0].content[0].content[1].text,' ')
})
test('missing, duplicate or unrecognized translated segments cannot silently destroy formatting', () => {
  const original = textDocument('One\nTwo')
  for (const values of [[{id:'s0',text:'一'}],[{id:'s0',text:'一'},{id:'s0',text:'二'}],[{id:'s0',text:'一'},{id:'wrong',text:'二'}],[{id:'s0',text:' '},{id:'s1',text:'二'}]]) assert.throws(()=>translatedDocument(original,values))
})
test('model output is literal text and not HTML instructions', () => {
  const output = translatedDocument(textDocument('original'),[{id:'s0',text:'<script>alert(1)</script>'}])
  assert.equal(output.content[0].content[0].type,'text'); assert.equal(output.content[0].content[0].text,'<script>alert(1)</script>')
})
