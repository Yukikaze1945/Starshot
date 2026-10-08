import assert from 'node:assert/strict'
import { test } from 'node:test'
import { registerTextWorkspace, restoreWorkspace, savedTextWorkspace, snapshotWorkspace } from '../src/workspace.ts'

test('hidden GUI preserves rich text and excludes settings secrets', () => {
  const source = { type: 'doc', content: [{ type: 'paragraph', attrs: { textAlign: 'center' }, content: [{ type: 'text', text: '草稿', marks: [{ type: 'bold' }, { type: 'textStyle', attrs: { color: '#123456', fontSize: '18px' } }] }] }] }
  const translated = { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Draft', marks: [{ type: 'bold' }] }] }] }
  const unregister = registerTextWorkspace(() => ({ source, translated, target: '英语', tab: 'translation' }))
  const snapshot = snapshotWorkspace({ page: 'ocr', mode: 'region', search: '', filter: '', apiKey: 'must-not-be-retained', settings: { apiKey: 'private' } })
  assert.equal(JSON.stringify(snapshot).includes('private'), false)
  assert.equal(JSON.stringify(snapshot).includes('must-not-be-retained'), false)
  unregister()
  source.content[0].content[0].text = 'changed after snapshot'
  const restored = restoreWorkspace(snapshot)
  assert.equal(restored.text.source.content[0].content[0].text, '草稿')
  assert.deepEqual(restored.text.source.content[0].content[0].marks, [{ type: 'bold' }, { type: 'textStyle', attrs: { color: '#123456', fontSize: '18px' } }])
  assert.equal(savedTextWorkspace().tab, 'translation')
  assert.equal(restored.text.translated.content[0].content[0].text, 'Draft')
})
test('busy translation defers teardown and version mismatches are rejected', () => {
  const unregister = registerTextWorkspace(() => null)
  assert.equal(snapshotWorkspace({ page: 'ocr', mode: 'region', search: '', filter: '' }), null)
  unregister()
  assert.equal(restoreWorkspace({ version: 99, page: 'ocr', mode: 'region' }), null)
})
