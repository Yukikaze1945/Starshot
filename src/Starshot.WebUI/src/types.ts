export type CaptureMode = 'region' | 'screen' | 'long' | 'gif' | 'ocr' | 'copy'
export type Page = 'studio' | 'gallery' | 'ocr' | 'clipboard'
export interface Settings {
  screenshotFolder: string; extraFolders: string[]; subfolders: boolean; subfolderPattern: string;
  filenamePattern: string; regionFilenamePattern: string; autoCopy: boolean; autoCopyOcr: boolean; ultraHdr: boolean;
  capacityManual: boolean; capacity: number; sdrFormat: number; hdrFormat: number; quality: number;
  colorManagement: boolean; deleteSdrHdr: boolean; sdrWhite: number; monitorSource: number;
  muteFullscreen: boolean; ocrEngine: number; endpoint: string; model: string; targetLanguage: string;
  theme: number; language: string; autoUpdate: boolean; previewUpdates: boolean; startupHidden: boolean; highPriority: boolean;
}
export interface Hotkey { id: number; modifiers: number; key: number; text: string; registered: boolean; error: boolean }
export interface Bootstrap { version: string; protocol: number; settings: Settings; hotkeys: Hotkey[]; oneOcrReady: boolean; hasApiKey: boolean }
export interface Shot { id: string; name: string; format: string; bytes: number; timestamp: string }
export interface Library { total: number; items: Shot[]; offset: number }
export interface Thumbnail { src: string; width: number; height: number }
export interface OcrResult { id: string; text: string; raw: string; lineCount: number; translate: boolean; autoCopied: boolean; copyError?: string }
export interface ClipboardState { text: string; image: string | null; historyEnabled: boolean; items: { id: string; text: string; image: boolean; timestamp: string }[] }
