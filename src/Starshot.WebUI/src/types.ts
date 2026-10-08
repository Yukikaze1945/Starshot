export type CaptureMode = 'region' | 'screen' | 'long' | 'gif' | 'ocr' | 'copy'
export type Page = 'studio' | 'gallery' | 'ocr' | 'clipboard'
export interface Settings {
  screenshotFolder: string; extraFolders: string[]; subfolders: boolean; subfolderPattern: string;
  filenamePattern: string; regionFilenamePattern: string; autoCopy: boolean; autoCopyOcr: boolean; ultraHdr: boolean;
  capacityManual: boolean; capacity: number; sdrFormat: number; hdrFormat: number; quality: number;
  captureMode: number; videoCodec: number; ocrModel: 'tiny' | 'small';
  colorManagement: boolean; deleteSdrHdr: boolean; sdrWhite: number; monitorSource: number;
  muteFullscreen: boolean; endpoint: string; model: string; targetLanguage: string;
  theme: number; language: string; autoUpdate: boolean; previewUpdates: boolean; startupHidden: boolean; highPriority: boolean;
}
export interface OcrModelsStatus {
  selected: 'tiny' | 'small'; state: 'notInstalled' | 'downloading' | 'validating' | 'installed' | 'corrupt' | 'canceled' | 'error' | 'deleting';
  installed: boolean; downloadedBytes: number; totalBytes: number; error: string | null; directory: string; version: string;
}
export interface Hotkey { id: number; modifiers: number; key: number; text: string; registered: boolean; error: boolean }
export interface Bootstrap { version: string; protocol: number; settings: Settings; hotkeys: Hotkey[]; hasApiKey: boolean }
export interface Shot { id: string; name: string; format: string; bytes: number; timestamp: string; kind?: 'image' | 'video'; codec?: string | null; hdr?: boolean | null }
export interface Library { total: number; items: Shot[]; offset: number }
export interface Thumbnail { src: string; width: number; height: number }
export interface OcrResult { id: string; text: string; raw: string; lineCount: number; translate: boolean; autoCopied: boolean; copyError?: string }
export interface ClipboardState { text: string; image: string | null; historyEnabled: boolean; items: { id: string; text: string; image: boolean; timestamp: string }[] }
