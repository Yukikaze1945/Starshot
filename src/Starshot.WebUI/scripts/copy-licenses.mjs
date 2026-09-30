import { mkdir, copyFile, readFile, writeFile } from 'node:fs/promises'
const fonts = ['outfit', 'newsreader', 'noto-sans-sc']
await mkdir('dist/licenses', { recursive: true })
for (const font of fonts) await copyFile(`node_modules/@fontsource-variable/${font}/LICENSE`, `dist/licenses/${font}-OFL.txt`)

// Preserve notices for the runtime bundle, including transitive editor dependencies.
const root = JSON.parse(await readFile('package.json', 'utf8'))
const queue = Object.keys(root.dependencies)
const seen = new Set()
const notices = []
while (queue.length) {
  const name = queue.shift()
  if (seen.has(name)) continue
  seen.add(name)
  const directory = `node_modules/${name}`
  const metadata = JSON.parse(await readFile(`${directory}/package.json`, 'utf8'))
  queue.push(...Object.keys(metadata.dependencies ?? {}))
  for (const filename of ['LICENSE', 'LICENSE.md', 'LICENSE.txt', 'license', 'license.md', 'LICENSE-MIT']) {
    try {
      const content = await readFile(`${directory}/${filename}`, 'utf8')
      const target = `${name.replaceAll('/', '_').replaceAll('@', '')}-LICENSE.txt`
      await writeFile(`dist/licenses/${target}`, content)
      notices.push(`${name} ${metadata.version}: ${target}`)
      break
    } catch (error) { if (error.code !== 'ENOENT') throw error }
  }
}
await writeFile('dist/licenses/THIRD-PARTY-NOTICES.txt', notices.join('\n') + '\n')
