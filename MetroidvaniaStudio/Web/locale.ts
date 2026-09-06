import { localText } from './api.js';

export class Locale extends EventTarget {
  language: 'KR' | 'EN' = (localStorage.getItem('mapstudio.language') || (navigator.language.toLowerCase().startsWith('ko') ? 'KR' : 'EN')) === 'KR' ? 'KR' : 'EN';
  private table = new Map<string, [string, string]>();
  async load(): Promise<void> { this.parse(await localText('/api/locale')); }
  set(language: 'KR' | 'EN'): void { this.language = language; localStorage.setItem('mapstudio.language', language); this.dispatchEvent(new Event('change')); }
  t(key: string): string { return (this.table.get(`external.${key}`) || this.table.get(key) || [key, key])[this.language === 'KR' ? 0 : 1]; }
  enum(type: string, value: string, index: number): string {
    const entry = this.table.get(`enum.${type}.${value}`);
    return entry ? entry[this.language === 'KR' ? 0 : 1] : value.replace(/([a-z])([A-Z])/g, '$1 $2');
  }
  private parse(csv: string): void {
    let row: string[] = [], value = '', quoted = false;
    const rows: string[][] = [];
    for (let i = 0; i < csv.length; i++) {
      const c = csv[i];
      if (c === '"') { if (quoted && csv[i + 1] === '"') { value += '"'; i++; } else quoted = !quoted; }
      else if (c === ',' && !quoted) { row.push(value); value = ''; }
      else if ((c === '\n' || c === '\r') && !quoted) { if (c === '\r' && csv[i + 1] === '\n') i++; row.push(value); rows.push(row); row = []; value = ''; }
      else value += c;
    }
    if (value || row.length) { row.push(value); rows.push(row); }
    for (const fields of rows) if (fields.length === 3) this.table.set(fields[0].replace(/^\uFEFF/, ''), [fields[1] || fields[2], fields[2]]);
  }
}
