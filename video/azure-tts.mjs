// Azure AI Speech 配音（正式授權的商用語音）。給影片腳本的 tts 欄位用：
//   import { azureTts } from '../azure-tts.mjs'  →  export default { tts: azureTts, ... }
// 金鑰：環境變數 AZURE_SPEECH_KEY、AZURE_SPEECH_REGION；沒有就讀 ~/.config/formwright/azure-speech.env（不進版控）。
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

function creds() {
  const env = { ...process.env }
  const file = join(homedir(), '.config', 'formwright', 'azure-speech.env')
  if (!env.AZURE_SPEECH_KEY && existsSync(file)) {
    for (const line of readFileSync(file, 'utf8').split('\n')) {
      const m = line.match(/^(\w+)=(.*)$/)
      if (m) env[m[1]] = m[2].trim()
    }
  }
  if (!env.AZURE_SPEECH_KEY || !env.AZURE_SPEECH_REGION) throw new Error(`缺 Azure Speech 金鑰：設環境變數 AZURE_SPEECH_KEY／AZURE_SPEECH_REGION，或寫進 ${file}`)
  return { key: env.AZURE_SPEECH_KEY, region: env.AZURE_SPEECH_REGION }
}

const esc = (s) => s.replace(/[<>&'"]/g, (c) => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;', "'": '&apos;', '"': '&quot;' })[c])

export async function azureTts(text, outFile, { voice }) {
  const { key, region } = creds()
  const ssml = `<speak version="1.0" xml:lang="zh-TW"><voice name="${esc(voice)}">${esc(text)}</voice></speak>`
  const res = await fetch(`https://${region}.tts.speech.microsoft.com/cognitiveservices/v1`, {
    method: 'POST',
    headers: {
      'Ocp-Apim-Subscription-Key': key,
      'Content-Type': 'application/ssml+xml',
      'X-Microsoft-OutputFormat': 'audio-24khz-96kbitrate-mono-mp3',
      'User-Agent': 'demo-video',
    },
    body: ssml,
  })
  if (!res.ok) throw new Error(`Azure Speech 回 ${res.status}：${(await res.text()).slice(0, 200)}`)
  writeFileSync(outFile, Buffer.from(await res.arrayBuffer()))
}
