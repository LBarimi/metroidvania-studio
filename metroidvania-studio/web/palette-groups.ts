import type { Command, EditorPaletteGroup, Material, State } from './types.js';
import type { Locale } from './locale.js';

export const paletteGroupName = (group: EditorPaletteGroup, locale: Locale): string => group.id === 'default' ? locale.t('paletteDefaultGroup') : group.name;
type Drag = { kind: 'palette' | 'group'; id: string; instanceId: string; revision: number };
let dragging: Drag | undefined, instance = '';
const collapsed = new Set<string>();
export function expandPaletteGroup(id: string): void { collapsed.delete(id); }
const mime = 'application/x-metroidvania-studio-palette';

export function renderPaletteGroups(container: HTMLElement, state: State, locale: Locale, query: string, command: Command, callbacks: {
  name(material: Material): string;
  thumbnail(material: Material): HTMLCanvasElement;
  select(material: Material): void;
  settings(material: Material): void;
  groupSettings(group: EditorPaletteGroup): void;
  report(error: unknown): void;
}): void {
  if (instance !== state.instanceId) { instance = state.instanceId; collapsed.clear(); dragging = undefined; }
  const groups = state.paletteGroups;
  const materials = new Map(state.catalog.materials.map(m => [m.id, m]));
  const element = <K extends keyof HTMLElementTagNameMap>(tag: K, className: string, label = '') => {
    const node = document.createElement(tag); node.className = className; node.textContent = label; return node;
  };
  const button = (className: string, label: string, action: () => void) => {
    const b = element('button', className, label); b.type = 'button'; b.onclick = action; return b;
  };
  const clear = () => { container.querySelectorAll('[data-drop]').forEach(n => n.removeAttribute('data-drop')); };
  const finish = () => { dragging = undefined; clear(); container.classList.remove('dragging-groups'); };
  function source(node: HTMLElement, kind: Drag['kind'], id: string): void {
    node.draggable = true;
    node.addEventListener('dragstart', e => {
      if (!e.dataTransfer) return;
      dragging = { kind, id, instanceId: state.instanceId, revision: state.revision };
      e.dataTransfer.setData(mime, JSON.stringify(dragging)); e.dataTransfer.effectAllowed = 'move';
      container.classList.add('dragging-groups'); e.stopPropagation();
    });
    node.addEventListener('dragend', finish);
  }
  type Target = { action: 'paletteMove' | 'paletteGroupMove'; values: object; mark: string };
  function target(node: HTMLElement, resolve: (drag: Drag, after: boolean) => Target | undefined): void {
    function destination(e: DragEvent): Target | undefined {
      if (!dragging || dragging.instanceId !== state.instanceId || !e.dataTransfer?.types.includes(mime)) return;
      const rect = node.getBoundingClientRect(); return resolve(dragging, e.clientY > rect.top + rect.height / 2);
    }
    node.addEventListener('dragover', e => {
      const drop = destination(e); if (!drop) return;
      e.preventDefault(); e.stopPropagation(); e.dataTransfer!.dropEffect = 'move'; clear(); node.dataset.drop = drop.mark;
    });
    node.addEventListener('drop', e => {
      const drop = destination(e), drag = dragging; if (!drop || !drag) return;
      e.preventDefault(); e.stopPropagation(); finish();
      void command(drop.action, drop.values, { instanceId: drag.instanceId, revision: drag.revision }).catch(callbacks.report);
    });
  }
  const afterGroup = (id: string) => groups[groups.findIndex(g => g.id === id) + 1]?.id ?? null;
  const afterMaterial = (group: EditorPaletteGroup, id: string) => group.materials[group.materials.indexOf(id) + 1] ?? null;
  for (const group of groups) {
    const name = paletteGroupName(group, locale), term = query.trim().toLocaleLowerCase();
    const entries = group.materials.map(id => materials.get(id)).filter((m): m is Material => !!m)
      .filter(m => !term || name.toLocaleLowerCase().includes(term) || callbacks.name(m).toLocaleLowerCase().includes(term));
    if (term && !entries.length && !name.toLocaleLowerCase().includes(term)) continue;
    const section = element('section', 'palette-group'), header = element('div', 'palette-group-header'), list = element('div', 'palette-group-items');
    section.dataset.groupId = group.id;
    const grip = element('span', 'palette-drag-handle', '⠿'); grip.title = locale.t('paletteGroupDrag'); source(grip, 'group', group.id);
    const toggle = button('palette-group-toggle', '', () => {
      if (collapsed.has(group.id)) collapsed.delete(group.id); else collapsed.add(group.id);
      updateCollapse();
    });
    const arrow = element('span', 'palette-group-arrow'), caption = element('span', 'palette-group-name', name), count = element('span', 'count', String(group.materials.length));
    toggle.append(arrow, caption, count); toggle.title = name;
    const settings = button('palette-group-settings', '…', () => callbacks.groupSettings(group));
    settings.title = locale.t('paletteGroupSettings'); settings.setAttribute('aria-label', locale.t('paletteGroupSettings') + ' · ' + name);
    function updateCollapse(): void { const open = !!term || !collapsed.has(group.id); list.hidden = !open; arrow.textContent = open ? '▾' : '▸'; toggle.setAttribute('aria-expanded', String(open)); }
    updateCollapse(); header.append(grip, toggle, settings); section.append(header, list); container.append(section);
    target(header, (drag, after) => drag.kind === 'palette'
      ? { action: 'paletteMove', values: { id: drag.id, groupId: group.id, beforeId: null }, mark: 'inside' }
      : drag.id === group.id ? undefined : { action: 'paletteGroupMove', values: { id: drag.id, beforeId: after ? afterGroup(group.id) : group.id }, mark: after ? 'after' : 'before' });
    for (const material of entries) {
      const row = element('div', 'palette-entry'); row.dataset.materialId = material.id; source(row, 'palette', material.id);
      const handle = element('span', 'palette-drag-handle', '⠿'); handle.title = locale.t('paletteDrag');
      const b = button('palette-item', '', () => callbacks.select(material)); b.classList.toggle('active', material.id === state.selection.material);
      b.append(callbacks.thumbnail(material), element('span', '', callbacks.name(material)));
      const settings = button('palette-settings', '…', () => callbacks.settings(material)); settings.dataset.materialId = material.id;
      settings.title = locale.t('tilesetTitle'); settings.setAttribute('aria-label', locale.t('tilesetTitle') + ' · ' + callbacks.name(material));
      row.append(handle, b, settings); list.append(row);
      target(row, (drag, after) => drag.kind !== 'palette' || drag.id === material.id ? undefined : {
        action: 'paletteMove', values: { id: drag.id, groupId: group.id, beforeId: after ? afterMaterial(group, material.id) : material.id }, mark: after ? 'after' : 'before'
      });
    }
    if (!entries.length) {
      const empty = element('div', 'palette-group-empty', locale.t('paletteGroupEmpty')); list.append(empty);
      target(empty, drag => drag.kind === 'palette' ? { action: 'paletteMove', values: { id: drag.id, groupId: group.id, beforeId: null }, mark: 'inside' } : undefined);
    }
  }
}

export function palettePlacementControls(materialId: string, groups: EditorPaletteGroup[], locale: Locale): {
  element: HTMLElement; value(): { groupId: string; beforeId: string | null; expectedGroups: EditorPaletteGroup[] } | undefined;
} {
  const original = groups.find(g => g.materials.includes(materialId))!;
  let groupId = original.id, index = original.materials.indexOf(materialId);
  const container = document.createElement('div'); container.className = 'palette-placement';
  const field = document.createElement('label'); field.textContent = locale.t('paletteGroup');
  const select = document.createElement('select'); select.id = 'tileset-group';
  for (const group of groups) { const option = document.createElement('option'); option.value = group.id; option.textContent = paletteGroupName(group, locale); select.append(option); }
  select.value = groupId; field.append(select);
  const position = document.createElement('span'); position.id = 'tileset-position'; position.setAttribute('aria-live', 'polite');
  const arrow = (id: string, caption: string, delta: number) => {
    const b = document.createElement('button'); b.id = id; b.type = 'button'; b.textContent = caption; b.title = locale.t(delta < 0 ? 'paletteMoveUp' : 'paletteMoveDown'); b.setAttribute('aria-label', b.title);
    b.onclick = () => { index += delta; update(); }; return b;
  };
  const up = arrow('tileset-up', '↑', -1), down = arrow('tileset-down', '↓', 1);
  const members = () => groups.find(g => g.id === groupId)!.materials.filter(id => id !== materialId);
  const update = () => { const count = members().length; index = Math.min(Math.max(0, index), count); position.textContent = locale.t('palettePosition').replace('{0}', String(index + 1)).replace('{1}', String(count + 1)); up.disabled = index === 0; down.disabled = index === count; };
  select.onchange = () => { groupId = select.value; index = members().length; update(); };
  container.append(field, position, up, down); update();
  return { element: container, value: () => groupId === original.id && index === original.materials.indexOf(materialId) ? undefined
    : { groupId, beforeId: members()[index] ?? null, expectedGroups: groups } };
}
