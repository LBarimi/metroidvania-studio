export const commandModifier = /^(Mac|iPhone|iPad|iPod)/.test(navigator.platform) ? '⌘' : 'Ctrl';

export function shortcutKey(event: KeyboardEvent): string {
  if (event.isComposing || event.keyCode === 229 || event.getModifierState('AltGraph')) return '';
  // Keep Latin layouts such as AZERTY logical. Non-Latin layouts use the printed shortcut's key position.
  if (/^\p{L}$/u.test(event.key) && !/^[a-z]$/i.test(event.key) && /^Key[A-Z]$/.test(event.code))
    return event.code.slice(3).toLowerCase();
  return event.key.toLowerCase();
}
