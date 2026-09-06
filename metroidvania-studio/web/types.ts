// Serialized data is generated from the C# producers. Only browser-local state
// and presentation helpers belong in this file.
export * from './contracts.generated.js';
import type { EditorState, Room } from './contracts.generated.js';
export type State = Omit<EditorState, 'document' | 'catalog' | 'selection' | 'connections'>
  & Required<Pick<EditorState, 'document' | 'catalog' | 'selection' | 'connections'>>;
export type Color = string | { r: number; g: number; b: number; a?: number };
export interface CommandExpectation { instanceId: string; revision: number }
export type CommandValues = object | (() => object);
export type Command = (action: string, values?: CommandValues, expectation?: CommandExpectation) => Promise<State>;
// Presentation only: inactive rooms remain visible, selectable and unchanged in saved data.
export function roomOpacity(roomId: string, activeRoomId: string | null | undefined): number {
  return activeRoomId && roomId !== activeRoomId ? .6 : 1;
}
export function colorCss(value: Color | undefined, fallback = '#c72b36'): string {
  if (!value) return fallback;
  if (typeof value === 'string') return value;
  return `rgba(${Math.round(value.r * 255)},${Math.round(value.g * 255)},${Math.round(value.b * 255)},${value.a ?? 1})`;
}
export function activeRoom(state: State | null): Room | undefined { return state?.document.rooms.find(room => room.id === state.selection.roomId); }
export function tileLayer(layer: number): boolean { return layer === 0 || layer === 1; }
