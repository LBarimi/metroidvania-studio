import type { Command, Material } from './types.js';
import type { Locale } from './locale.js';
import { composeTileset, defaultTile, drawTilesetExample, TILESET_MASKS } from './tileset-preview.js';
import type { TilesetMode, TilesetSettings } from './tileset-preview.js';

export function openPaletteDialog(material: Material, locale: Locale, command: Command): void {
  const t = (key: string) => locale.t(key);
  const element = <K extends keyof HTMLElementTagNameMap>(tag: K, className = '', label = '') => {
    const node = document.createElement(tag); node.className = className; node.textContent = label; return node;
  };
  const button = (label: string, action: () => void, className = '') => { const b = element('button', className, label); b.type = 'button'; b.onclick = action; return b; };
  const label = (caption: string, input: HTMLElement) => { const field = element('label'); field.append(element('span', '', caption), input); return field; };
  const errorText = (error: unknown) => { const message = error instanceof Error ? error.message : String(error); return message.startsWith('@') ? t(message.slice(1)) : message; };
  const saved = (material as Material & { editorTileset?: TilesetSettings }).editorTileset;
  let settings: TilesetSettings = saved ? structuredClone(saved) : { mode: 'blob47', source: material.sprites[0]?.asset || '', slots: Array(51).fill(null) };
  type Source = { image: HTMLImageElement; blob: Blob; name: string; png?: string };
  const sources = new Map<string, Source>();
  let currentSource = settings.source, selected = 0, zoom = 4, generation = 0, disposed = false, locked = false, loading = false;
  const dialog = element('dialog', 'tileset-dialog'); dialog.id = 'tileset-dialog';
  const heading = element('div', 'dialog-title', t('tilesetTitle'));
  heading.append(button('×', () => { if (!locked) dialog.close(); }, 'icon ghost'));
  const body = element('div', 'dialog-body'), footer = element('div', 'dialog-footer');
  const name = element('input'); name.id = 'tileset-name'; name.value = material.name; name.maxLength = 80;
  const color = element('input'); color.id = 'tileset-color'; color.type = 'color'; color.value = /^#[a-f0-9]{6}$/i.test(material.color) ? material.color : '#9655cf';
  const mode = element('select'); mode.id = 'tileset-mode';
  for (const [value, key] of [['template','tilesetTemplate'], ['four','tilesetFour'], ['blob47','tileset47']]) { const o = element('option', '', t(key)); o.value = value; mode.append(o); }
  mode.value = settings.mode;
  const fields = element('div', 'tileset-fields'); fields.append(label(t('name'), name), label(t('theme'), color), label(t('tilesetMethod'), mode));
  const hint = element('p', 'hint'), columns = element('div', 'tileset-columns'), sourcePane = element('section', 'tileset-source'), slotsPane = element('section', 'tileset-target');
  const tools = element('div', 'tileset-actions'), file = element('input'); file.type = 'file'; file.multiple = true; file.accept = 'image/png,.png'; file.hidden = true; file.id = 'tileset-file';
  const importButton = button(t('tilesetImport'), () => file.click()); importButton.id = 'tileset-import';
  const templateButton = button(t('tilesetLoadTemplate'), () => void template()); templateButton.id = 'tileset-load-template';
  const downloadButton = button(t('tilesetDownload'), () => {
    const source = sources.get(currentSource); if (!source) return;
    const url = URL.createObjectURL(source.blob), link = element('a'); link.href = url; link.download = source.name.split('/').pop() || 'tileset.png'; link.click(); window.setTimeout(() => URL.revokeObjectURL(url), 1000);
  });
  tools.append(importButton, templateButton, downloadButton, file);
  const sourcePath = element('input', 'tileset-path'); sourcePath.readOnly = true; sourcePath.setAttribute('aria-label', t('tilesetSource'));
  const sourceSelect = element('select'); sourceSelect.id = 'tileset-image'; sourceSelect.setAttribute('aria-label', t('tilesetSource'));
  sourceSelect.onchange = () => { currentSource = sourceSelect.value; render(); };
  const controls = element('div', 'tileset-actions'), auto = element('input'); auto.type = 'checkbox'; auto.checked = true;
  const zoomSelect = element('select'); zoomSelect.setAttribute('aria-label', t('tilesetZoom'));
  for (const value of [1,2,4,6,8]) { const o = element('option', '', '×' + value); o.value = String(value); zoomSelect.append(o); } zoomSelect.value = '4';
  zoomSelect.onchange = () => { zoom = Number(zoomSelect.value); drawSource(); };
  const autoLabel = label(t('tilesetNext'), auto); autoLabel.className = 'check';
  const automatic = button(t('tilesetAutoAssign'), () => { autoAssign(); render(); }); automatic.id = 'tileset-auto-assign';
  controls.append(automatic, zoomSelect, autoLabel);
  const scroll = element('div', 'tileset-image-scroll'); scroll.tabIndex = 0;
  const sourceCanvas = element('canvas'); sourceCanvas.id = 'tileset-source'; const empty = element('div', 'tileset-empty', t('tilesetDrop'));
  scroll.append(sourceCanvas, empty); sourcePane.append(tools, sourceSelect, sourcePath, controls, scroll);
  const tabs = element('div', 'tileset-tabs'), slotGrid = element('div', 'tileset-slots'); slotGrid.id = 'tileset-slots';
  let slopes = false;
  const solidTab = button(t('tilesetSolidSlots'), () => { slopes = false; selected = 0; render(); }); solidTab.id = 'tileset-solid-tab';
  const slopeTab = button(t('tilesetSlopeSlots'), () => { slopes = true; selected = settings.mode === 'four' ? 4 : 47; render(); }); slopeTab.id = 'tileset-slope-tab';
  const clear = button(t('tilesetClearSlot'), () => { settings.slots[selected] = null; render(); }); clear.id = 'tileset-clear-slot';
  tabs.append(solidTab, slopeTab, clear);
  const counter = element('div', 'hint'), example = element('canvas', 'tileset-example'); example.id = 'tileset-example';
  slotsPane.append(tabs, counter, slotGrid, element('div', 'section-title', t('tilesetPreview')), example);
  columns.append(sourcePane, slotsPane);
  const error = element('div', 'modal-error'); error.id = 'tileset-error'; error.setAttribute('role', 'status');
  const apply = button(t('apply'), () => void save(), 'accent'); apply.id = 'tileset-apply';
  footer.append(button(t('cancel'), () => dialog.close()), apply);
  body.append(fields, hint, columns, error); dialog.append(heading, body, footer); document.body.append(dialog); dialog.showModal();
  dialog.addEventListener('close', () => { disposed = true; generation++; dialog.remove(); });
  dialog.addEventListener('cancel', e => { if (locked) e.preventDefault(); });
  mode.onchange = () => {
    generation++; loading = false; apply.disabled = false;
    settings.mode = mode.value as TilesetMode; settings.slots = Array(settings.mode === 'four' ? 8 : settings.mode === 'blob47' ? 51 : 0).fill(null);
    selected = 0; slopes = false; render();
  };
  color.oninput = render;
  file.onchange = () => { const next = Array.from(file.files || []); file.value = ''; if (next.length) void importFiles(next); };
  scroll.addEventListener('dragover', e => { if (!locked) { e.preventDefault(); scroll.classList.add('drop-target'); } });
  scroll.addEventListener('dragleave', () => scroll.classList.remove('drop-target'));
  scroll.addEventListener('drop', e => { e.preventDefault(); scroll.classList.remove('drop-target'); if (!locked && e.dataTransfer?.files.length) void importFiles(Array.from(e.dataTransfer.files)); });
  sourceCanvas.addEventListener('click', e => {
    const image = sources.get(currentSource)?.image;
    if (!image || locked || loading || settings.mode === 'template') return;
    const bounds = sourceCanvas.getBoundingClientRect();
    const x = Math.floor((e.clientX - bounds.left) / bounds.width * image.naturalWidth / 16) * 16;
    const y = Math.floor((e.clientY - bounds.top) / bounds.height * image.naturalHeight / 16) * 16;
    if (x < 0 || y < 0 || x + 16 > image.naturalWidth || y + 16 > image.naturalHeight) return;
    settings.slots[selected] = { x, y, asset: currentSource };
    const firstSlope = settings.mode === 'four' ? 4 : 47;
    if (auto.checked) selected = Math.min(selected + 1, slopes ? settings.slots.length - 1 : firstSlope - 1);
    render();
  });
  function autoAssign(keys = [currentSource], start = 0): void {
    if (settings.mode === 'template') return;
    let index = start;
    for (const asset of keys) {
      const image = sources.get(asset)?.image; if (!image) continue;
      const cols = Math.floor(image.naturalWidth / 16), rows = Math.floor(image.naturalHeight / 16);
      for (let i = 0; i < cols * rows && index < settings.slots.length; i++) settings.slots[index++] = { x: i % cols * 16, y: Math.floor(i / cols) * 16, asset };
    }
  }
  async function decode(blob: Blob, uploaded: boolean, name: string): Promise<Source> {
    let url = '';
    try {
      if (blob.size > 8 * 1024 * 1024) throw new Error('@tilesetImageLimit');
      const signature = new Uint8Array(await blob.slice(0, 8).arrayBuffer());
      if (signature.join(',') !== '137,80,78,71,13,10,26,10') throw new Error('@tilesetInvalidPng');
      url = URL.createObjectURL(blob); const next = new Image(); next.src = url; await next.decode();
      if (next.naturalWidth < 16 || next.naturalHeight < 16 || next.naturalWidth * next.naturalHeight > 2048 * 2048) throw new Error('@tilesetImageLimit');
      const data = uploaded ? await new Promise<string>((resolve, reject) => { const reader = new FileReader(); reader.onload = () => resolve(String(reader.result).split(',')[1]); reader.onerror = reject; reader.readAsDataURL(blob); }) : '';
      return { image: next, blob, name, png: data || undefined };
    } finally { if (url) URL.revokeObjectURL(url); }
  }
  async function importFiles(blobs: Blob[], token = ++generation): Promise<void> {
    error.textContent = ''; loading = true; apply.disabled = true;
    try {
      if (blobs.length > 51 || blobs.reduce((n,b) => n + b.size, 0) > 8 * 1024 * 1024) throw new Error('@tilesetImageLimit');
      const loaded = new Map<string, Source>(); let pixels = 0;
      for (const blob of blobs) {
        const source = await decode(blob, true, blob instanceof File ? blob.name : 'tileset-' + settings.mode + '.png');
        pixels += source.image.naturalWidth * source.image.naturalHeight;
        if (pixels > 2048 * 2048) throw new Error('@tilesetImageLimit');
        if (disposed || token !== generation) return;
        loaded.set('upload-' + crypto.randomUUID(), source);
      }
      if (settings.mode === 'template') { settings.mode = 'four'; mode.value = 'four'; settings.slots = Array(8).fill(null); }
      for (const [key,value] of loaded) sources.set(key, value);
      currentSource = loaded.keys().next().value!;
      // A single tile replaces the selected slot. An atlas or a batch fills slots in order.
      const one = loaded.get(currentSource)!.image;
      const start = blobs.length === 1 && one.naturalWidth === 16 && one.naturalHeight === 16 ? selected : slopes ? settings.mode === 'four' ? 4 : 47 : 0;
      if (start === 0) { settings.slots.fill(null); settings.source = currentSource; }
      autoAssign([...loaded.keys()], start);
      pruneSources(); render();
    } catch (e) { if (!disposed && token === generation) error.textContent = errorText(e); }
    finally { if (!disposed && token === generation) { loading = false; apply.disabled = false; } }
  }
  function pruneSources(): void {
    if (settings.slots.every(s => !s || s.asset)) settings.source = settings.slots.find(s => s)?.asset || currentSource;
    const used = new Set([settings.source, currentSource, ...settings.slots.map(s => s?.asset).filter(Boolean)]);
    for (const key of sources.keys()) if (!used.has(key)) sources.delete(key);
  }
  async function template(): Promise<void> {
    const token = ++generation; loading = true; apply.disabled = true;
    try {
      if (settings.mode === 'template') { settings.mode = 'four'; mode.value = 'four'; settings.slots = Array(8).fill(null); }
      const targetMode = settings.mode;
      const response = await fetch('/api/palette-template?mode=' + targetMode + '&color=' + encodeURIComponent(color.value));
      if (!response.ok) throw new Error((await response.json()).error);
      const blob = await response.blob(); if (disposed || generation !== token || settings.mode !== targetMode) return;
      slopes = false; selected = 0; await importFiles([blob], token);
    } catch (e) { if (!disposed && token === generation) error.textContent = errorText(e); }
    finally { if (!disposed && token === generation) { loading = false; apply.disabled = false; } }
  }
  function drawSource(): void {
    const image = sources.get(currentSource)?.image;
    sourceCanvas.hidden = !image; empty.hidden = !!image;
    if (!image) return;
    sourceCanvas.width = image.naturalWidth; sourceCanvas.height = image.naturalHeight;
    sourceCanvas.style.width = image.naturalWidth * zoom + 'px'; sourceCanvas.style.height = image.naturalHeight * zoom + 'px';
    const ctx = sourceCanvas.getContext('2d')!; ctx.imageSmoothingEnabled = false; ctx.drawImage(image, 0, 0);
    ctx.strokeStyle = 'rgba(160,170,180,.45)'; ctx.lineWidth = 1 / zoom; ctx.beginPath();
    for (let x = 0; x <= image.naturalWidth; x += 16) { ctx.moveTo(x,0); ctx.lineTo(x,image.naturalHeight); }
    for (let y = 0; y <= image.naturalHeight; y += 16) { ctx.moveTo(0,y); ctx.lineTo(image.naturalWidth,y); } ctx.stroke();
    const active = settings.slots[selected];
    if (active && (active.asset || settings.source) === currentSource) { ctx.strokeStyle = '#59c8ff'; ctx.lineWidth = 2 / zoom; ctx.strokeRect(active.x + 1 / zoom, active.y + 1 / zoom, 16 - 2 / zoom, 16 - 2 / zoom); }
  }
  function render(): void {
    sourceSelect.replaceChildren();
    for (const [key,value] of sources) { const o = element('option', '', value.name.split('/').pop() || value.name); o.value = key; sourceSelect.append(o); }
    sourceSelect.value = currentSource; sourcePath.value = sources.get(currentSource)?.name || '';
    const templateOnly = settings.mode === 'template', firstSlope = settings.mode === 'four' ? 4 : 47;
    hint.textContent = t(templateOnly ? 'tilesetTemplateHelp' : settings.mode === 'four' ? 'tilesetFourHelp' : 'tileset47Help');
    sourcePane.hidden = templateOnly; tabs.hidden = templateOnly; slotGrid.hidden = templateOnly;
    solidTab.classList.toggle('active', !slopes); slopeTab.classList.toggle('active', slopes);
    const missing = settings.slots.filter(s => !s).length;
    counter.textContent = templateOnly ? '' : t('tilesetAssigned').replace('{0}', String(settings.slots.length - missing)).replace('{1}', String(settings.slots.length))
      + (missing ? ' · ' + t('tilesetFallback') : '');
    slotGrid.replaceChildren(); slotGrid.classList.toggle('four-slots', settings.mode === 'four' || slopes);
    for (let i = slopes ? firstSlope : 0; !templateOnly && i < (slopes ? settings.slots.length : firstSlope); i++) {
      const index = i, tile = element('canvas'); tile.width = tile.height = 32; const ctx = tile.getContext('2d')!; ctx.imageSmoothingEnabled = false;
      const slot = settings.slots[i], shape = slopes ? i - firstSlope + 1 : 0;
      const caption = slopes ? locale.enum('TileShape', ['Solid','BottomLeft','BottomRight','TopLeft','TopRight'][shape], shape)
        : settings.mode === 'four' ? t(['tilesetEdge','tilesetOuter','tilesetInner','tilesetFill'][i]) : String(i + 1).padStart(2,'0');
      const b = button('', () => { selected = index; if (settings.slots[index]) currentSource = settings.slots[index]!.asset || settings.source; render(); }, 'tileset-slot'); b.dataset.slot = String(i);
      b.classList.toggle('active', i === selected); b.classList.toggle('unassigned', !slot); b.setAttribute('aria-label', caption);
      const image = sources.get(slot?.asset || settings.source)?.image;
      if (image && slot && slot.x + 16 <= image.naturalWidth && slot.y + 16 <= image.naturalHeight) ctx.drawImage(image, slot.x,slot.y,16,16,0,0,32,32);
      else ctx.drawImage(defaultTile(slopes ? 0 : settings.mode === 'four' ? [124,112,127,255][i] : TILESET_MASKS[i],shape,color.value),0,0,32,32);
      b.title = caption + (slot ? ' · ' + slot.x / 16 + ', ' + slot.y / 16 : ' · ' + t('tilesetUnassigned'));
      b.append(tile, element('span','',caption), element('small','',slot ? slot.x / 16 + ',' + slot.y / 16 : '—')); slotGrid.append(b);
    }
    drawSource(); drawTilesetExample(example, composeTileset(new Map([...sources].map(([k,v]) => [k,v.image])),settings,color.value));
    downloadButton.disabled = !sources.has(currentSource); automatic.disabled = !sources.has(currentSource);
  }
  async function save(): Promise<void> {
    if (locked || loading) return; error.textContent = '';
    try {
      if (!name.value.trim()) throw new Error('@paletteInvalidName');
      if (settings.mode !== 'template' && !sources.has(settings.source)) throw new Error('@tilesetChooseImage');
      if (settings.mode === 'four' && settings.slots.slice(0,4).some(s => !s)) throw new Error('@tilesetFourRequired');
      pruneSources();
      const used = settings.mode === 'template' ? [] : [...new Set([settings.source, ...settings.slots.map(s => s?.asset).filter((s): s is string => !!s)])];
      const inputs = used.map(key => sources.get(key));
      if (inputs.some(s => !s)) throw new Error('@tilesetChooseImage');
      if (inputs.reduce((n,s) => n + s!.blob.size,0) > 8 * 1024 * 1024 || inputs.reduce((n,s) => n + s!.image.naturalWidth * s!.image.naturalHeight,0) > 2048 * 2048) throw new Error('@tilesetImageLimit');
      const uploads = Object.fromEntries(used.filter(key => sources.get(key)?.png).map(key => [key, sources.get(key)!.png]));
      locked = true; body.inert = true; footer.inert = true;
      await command('paletteConfigure', { id: material.id, name: name.value, color: color.value, settings, uploads, expectedMaterial: material });
      dialog.close();
    } catch (e) { error.textContent = errorText(e); }
    finally { locked = false; body.inert = false; footer.inert = false; }
  }
  render();
  async function initial(): Promise<void> {
    const token = ++generation; loading = true; apply.disabled = true;
    try {
      const keys = saved ? [settings.source,...settings.slots.map(s => s?.asset).filter((s): s is string => !!s)] : material.sprites.map(s => s.asset);
      let bytes = 0, pixels = 0;
      for (const key of new Set(keys.filter(Boolean))) {
        const response = await fetch('/api/asset?path=' + encodeURIComponent(key), { cache: 'no-store' });
        if (!response.ok) throw new Error('@tilesetChooseImage');
        const source = await decode(await response.blob(),false,key);
        bytes += source.blob.size; pixels += source.image.naturalWidth * source.image.naturalHeight;
        if (bytes > 8 * 1024 * 1024 || pixels > 2048 * 2048) throw new Error('@tilesetImageLimit');
        if (disposed || token !== generation) return;
        sources.set(key, source);
      }
      if (!saved) settings.slots = [...TILESET_MASKS.map(mask => material.sprites.find(s => s.shape === 0 && s.mask === mask)),
        ...[1,2,3,4].map(shape => material.sprites.find(s => s.shape === shape))].map(s => s && sources.has(s.asset)
        ? { x: s.x, y: sources.get(s.asset)!.image.naturalHeight - s.y - s.height, asset: s.asset } : null);
      render();
    } catch (e) { if (!disposed && token === generation) error.textContent = errorText(e); }
    finally { if (!disposed && token === generation) { loading = false; apply.disabled = false; } }
  }
  if (settings.source) void initial();
}
