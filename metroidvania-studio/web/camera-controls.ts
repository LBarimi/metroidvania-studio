import type { CameraProfile } from './types.js';

type CameraValues = Pick<CameraProfile, 'ppu' | 'referenceWidth' | 'referenceHeight'>;
export type CameraChange = Pick<CameraValues, 'ppu'> | Pick<CameraValues, 'referenceWidth' | 'referenceHeight'>;

const ppuPresets = Array.from({ length: 14 }, (_, index) => 2 ** index);
const resolutionPresets: Array<[string, Array<[number, number]>]> = [
  ['16:9', [[256, 144], [320, 180], [384, 216], [480, 270], [512, 288], [640, 360], [960, 540], [1280, 720], [1920, 1080]]],
  ['4:3', [[256, 192], [320, 240], [384, 288], [512, 384], [640, 480], [800, 600], [1024, 768]]],
  ['16:10', [[320, 200], [384, 240], [640, 400], [1280, 800]]],
  ['3:2', [[240, 160], [480, 320], [960, 640]]],
];

/** Only presentation is local; the ordered editor writer persists each changed field. */
export function createCameraControls(t: (key: string) => string,
  change: (values: CameraChange) => Promise<unknown>, error: (value: unknown) => void) {
  const element = document.createElement('div'); element.className = 'camera-controls';
  const ppu = document.createElement('select'), resolution = document.createElement('select');
  ppu.id = 'camera-ppu'; resolution.id = 'camera-resolution';
  ppu.append(...ppuPresets.map(value => new Option(String(value), String(value))));
  for (const [ratio, sizes] of resolutionPresets) {
    const group = document.createElement('optgroup'); group.label = ratio;
    group.append(...sizes.map(([width, height]) => new Option(`${width}×${height}`, `${width}x${height}`)));
    resolution.append(group);
  }
  for (const [caption, input] of [['PPU', ppu], [t('resolution'), resolution]] as const) {
    const label = document.createElement('label'); label.append(caption, input);
    input.setAttribute('aria-label', caption); input.title = t(input === ppu ? 'ppuHelp' : 'resolutionHelp');
    element.append(label);
  }
  let current: CameraValues | undefined;
  const pending = new Map<HTMLSelectElement, number>();
  function sync(input: HTMLSelectElement, value: string, caption: string): void {
    input.disabled = !current;
    if (!current || pending.get(input)) return;
    // Imported maps may use values outside the presets. Viewing them never changes the document.
    const custom = input.querySelector<HTMLOptionElement>('option[data-custom]');
    if (custom && custom.value !== value) custom.remove();
    if (![...input.options].some(option => option.value === value)) {
      const option = new Option(caption, value); option.dataset.custom = 'true'; input.append(option);
    }
    if (input.value !== value) input.value = value;
  }
  function update(camera: CameraValues | undefined): void {
    current = camera;
    sync(ppu, String(camera?.ppu ?? 16), String(camera?.ppu ?? 16));
    sync(resolution, `${camera?.referenceWidth ?? 320}x${camera?.referenceHeight ?? 180}`,
      `${camera?.referenceWidth ?? 320}×${camera?.referenceHeight ?? 180}`);
  }
  async function apply(input: HTMLSelectElement, values: CameraChange): Promise<void> {
    pending.set(input, (pending.get(input) || 0) + 1);
    try { await change(values); }
    catch (value) { error(value); }
    finally { pending.set(input, pending.get(input)! - 1); update(current); }
  }
  ppu.addEventListener('change', () => { void apply(ppu, { ppu: Number(ppu.value) }); });
  resolution.addEventListener('change', () => {
    const [referenceWidth, referenceHeight] = resolution.value.split('x').map(Number);
    void apply(resolution, { referenceWidth, referenceHeight });
  });
  update(undefined);
  return { element, update };
}
