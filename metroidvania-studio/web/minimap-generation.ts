import { MAX_MINIMAP_ROOMS, MINIMAP_CHIP_GROUPS } from './contracts.generated.js';

export function generationProgress(start: number): number {
  return Math.min(1, Math.max(0, (performance.now() - start) / 1000));
}

export function generationSettings(presets: Map<string, string>, t: (key: string) => string, previousSeed?: string, terrain: [string, string][] = []) {
  const form = document.createElement('div');
  form.className = 'generation-settings';

  function field(key: string, control: HTMLInputElement | HTMLSelectElement) {
    const label = document.createElement('label'), caption = document.createElement('span');
    caption.id = 'generation-label-' + key; caption.textContent = t(key);
    control.setAttribute('aria-labelledby', caption.id);
    label.append(caption, control);
    form.append(label);
  }
  function mode(key: string, choices: string[]) {
    const list = document.createElement('select');
    for (const choice of choices) list.add(new Option(t(choice), choice));
    field(key, list);
    return list;
  }
  function checkbox(group: HTMLElement, value: string, caption: string, color?: string) {
    const label = document.createElement('label'), input = document.createElement('input');
    input.type = 'checkbox'; input.value = value;
    label.append(input);
    if (color) {
      const swatch = document.createElement('span');
      swatch.className = 'generation-swatch'; swatch.style.backgroundColor = color; label.append(swatch);
    }
    label.append(caption); group.append(label);
    return input;
  }
  const count = document.createElement('input');
  count.type = 'number'; count.min = '1'; count.max = String(MAX_MINIMAP_ROOMS); count.step = '1'; count.value = '32';
  field('generationRooms', count);

  const seed = document.createElement('input');
  seed.type = 'number'; seed.min = '-2147483648'; seed.max = '2147483647'; seed.step = '1';
  const randomSeed = () => String(crypto.getRandomValues(new Int32Array(1))[0]);
  seed.value = randomSeed(); seed.disabled = true;
  field('generationSeed', seed);
  const toggles = document.createElement('div');
  toggles.className = 'generation-toggles';
  const random = checkbox(toggles, 'randomSeed', t('generationRandomSeed'));
  random.checked = true;
  const generateTiles = checkbox(toggles, 'generateTiles', t('generationTiles'));
  generateTiles.id = 'generation-tiles';
  generateTiles.checked = true;
  form.append(toggles);
  let manualSeed = previousSeed;
  random.addEventListener('change', () => {
    if (random.checked) { manualSeed = seed.value; seed.value = randomSeed(); }
    else { seed.value = manualSeed ?? seed.value; }
    seed.disabled = random.checked;
    if (!random.checked) { seed.focus(); seed.select(); }
  });

  const chipMode = mode('chipPalette', ['generationAll', 'generationInclude', 'generationExclude']);
  const chips = document.createElement('div'), chipInputs: HTMLInputElement[] = [];
  chips.className = 'generation-chips'; chips.hidden = true;
  for (const { label, chips: sizes } of MINIMAP_CHIP_GROUPS) {
    const group = document.createElement('fieldset'), legend = document.createElement('legend');
    legend.textContent = t(label); group.append(legend);
    for (const { width, height } of sizes) chipInputs.push(checkbox(group, width + 'x' + height, width + ' × ' + height));
    chips.append(group);
  }
  form.append(chips);
  chipMode.addEventListener('change', () => { chips.hidden = chipMode.value === 'generationAll'; });

  const themeMode = mode('theme', ['generationAutoColors', 'generationChooseColors']);
  const colors = document.createElement('div'); colors.className = 'generation-colors'; colors.hidden = true;
  const colorInputs = [...presets].map(([color, name]) => checkbox(colors, color, name, color));
  form.append(colors);
  themeMode.addEventListener('change', () => { colors.hidden = themeMode.value === 'generationAutoColors'; });

  const terrainTheme = document.createElement('select');
  terrainTheme.add(new Option(t('generationMatchRegion'), 'auto'));
  for (const [id, name] of terrain) terrainTheme.add(new Option(name, id));
  field('generationTerrain', terrainTheme);
  const difficulty = document.createElement('select');
  for (const level of ['progressive', 'easy', 'normal', 'hard']) difficulty.add(new Option(t('generationDifficulty.' + level), level));
  field('generationDifficulty', difficulty);
  function showTileFields() {
    terrainTheme.parentElement!.hidden = difficulty.parentElement!.hidden = !generateTiles.checked;
  }
  generateTiles.addEventListener('change', showTileFields); showTileFields();

  function read() {
    const roomCount = count.valueAsNumber;
    if (!Number.isInteger(roomCount) || roomCount < 1 || roomCount > MAX_MINIMAP_ROOMS) {
      count.focus(); throw new Error('@generationRoomRange:' + MAX_MINIMAP_ROOMS);
    }
    const seedValue = seed.valueAsNumber;
    if (!Number.isInteger(seedValue) || seedValue < -2147483648 || seedValue > 2147483647) {
      seed.focus(); throw new Error('@generationInvalidSeed');
    }
    const chipIds = chipInputs.filter(input => chipMode.value === 'generationAll'
      || input.checked === (chipMode.value === 'generationInclude')).map(input => input.value);
    if (!chipIds.length) throw new Error('@generationChipsRequired');
    const automaticThemes = themeMode.value === 'generationAutoColors';
    const selected = colorInputs.filter(input => automaticThemes || input.checked).map(input => input.value);
    if (!selected.length) throw new Error('@generationColorsRequired');
    if (!automaticThemes && selected.length > roomCount) throw new Error('@generationTooManyColors');
    return { roomCount, seed: seedValue, chipIds, colors: selected, automaticThemes,
      ...(generateTiles.checked ? { generateTiles: true, tileTheme: terrainTheme.value, tileDifficulty: difficulty.value } : {}) };
  }
  return { form, count, read };
}
