import { localText } from './api.js';
export const LANGUAGES = [
  ['KR', '한국어', 'ko'], ['EN', 'English', 'en'], ['JA', '日本語', 'ja'],
  ['ZH_CN', '简体中文', 'zh-Hans'], ['ZH_TW', '繁體中文（台灣）', 'zh-Hant-TW'], ['RU', 'Русский', 'ru']
] as const;
export type Language = typeof LANGUAGES[number][0];
export function detectLanguage(languages: readonly string[]): Language {
  for (const locale of languages) {
    const tag = locale.toLowerCase();
    if (tag.startsWith('ko')) return 'KR'; if (tag.startsWith('ja')) return 'JA'; if (tag.startsWith('ru')) return 'RU';
    if (tag.startsWith('zh')) return /hant|tw|hk|mo/.test(tag) ? 'ZH_TW' : 'ZH_CN';
    if (tag.startsWith('en')) return 'EN';
  } return 'EN';
}
export class Locale extends EventTarget {
  language: Language;
  private table = new Map<string, Record<string, string>>();
  constructor() {
    super(); const saved = localStorage.getItem('mapstudio.language');
    this.language = LANGUAGES.some(([key]) => key === saved) ? saved as Language : detectLanguage(navigator.languages || [navigator.language]);
  }
  get htmlLanguage(): string { return LANGUAGES.find(([key]) => key === this.language)![2]; }
  async load(): Promise<void> { this.parse(await localText('/api/locale')); }
  set(language: Language): void {
    if (!LANGUAGES.some(([key]) => key === language)) return;
    this.language = language; localStorage.setItem('mapstudio.language', language); this.dispatchEvent(new Event('change'));
  }
  t(key: string): string {
    const row = this.table.get(`external.${key}`) || this.table.get(key);
    return row?.[this.language] || row?.EN || key;
  }
  enum(type: string, value: string, _index: number): string {
    const key = `enum.${type}.${value}`; return this.table.has(key) ? this.t(key) : value.replace(/([a-z])([A-Z])/g, '$1 $2');
  }
  private parse(csv: string): void {
    let row: string[] = [], value = '', quoted = false; const rows: string[][] = [];
    for (let i = 0; i < csv.length; i++) {
      const c = csv[i];
      if (c === '"') { if (quoted && csv[i + 1] === '"') { value += '"'; i++; } else quoted = !quoted; }
      else if (c === ',' && !quoted) { row.push(value); value = ''; }
      else if ((c === '\n' || c === '\r') && !quoted) { if (c === '\r' && csv[i + 1] === '\n') i++; row.push(value); rows.push(row); row = []; value = ''; }
      else value += c;
    }
    if (value || row.length) { row.push(value); rows.push(row); }
    const header = rows.shift()?.map(value => value.replace(/^\uFEFF/, '').trim());
    if (!header || header[0] !== 'Key' || !header.includes('EN') || quoted) throw new Error('Invalid localization table.');
    const table = new Map<string, Record<string, string>>();
    for (const fields of rows) {
      if (fields.length === 1 && !fields[0]) continue;
      if (fields.length !== header.length || !fields[0] || table.has(fields[0])) throw new Error('Invalid localization row.');
      table.set(fields[0], Object.fromEntries(header.slice(1).map((key, i) => [key, fields[i + 1]])));
    } this.table = table;
  }
}
