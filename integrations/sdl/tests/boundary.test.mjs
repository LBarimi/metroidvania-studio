import test from 'node:test';
import assert from 'node:assert/strict';
import { inspectBoundaries } from '../../../tools/repository/check-boundaries.mjs';

test('new engine boundaries allow only adapter source and still protect the studio core',()=>{
  const source='#include <SDL3/SDL.h>';
  const entry=name=>({name,bytes:Buffer.from(source)});
  assert.deepEqual(inspectBoundaries([entry('integrations/sdl/src/Room.cpp')]),[]);
  assert.deepEqual(inspectBoundaries([entry('engine-packages/sdl/src-sample/main.cpp')]),[]);
  for(const name of ['engine-packages/sdl/Room.cpp','engine-packages/sdl/src-sample/Room.ts',
    'engine-packages/godot/src-sample/Room.cpp','metroidvania-studio/web/src-sample/Room.cpp'])
    assert.ok(inspectBoundaries([entry(name)]).some(x=>x.rule==='engine-code'),name);
  assert.ok(inspectBoundaries([entry('metroidvania-studio/core/Room.cpp')]).some(x=>x.rule==='engine-code'));
  for(const engine of ['godot','ue4','ue5']) {
    const name=engine==='godot'?'addons/studio/import.gd':'Studio.uplugin';
    assert.deepEqual(inspectBoundaries([{name:`integrations/${engine}/${name}`,bytes:Buffer.from('')}]),[]);
    assert.ok(inspectBoundaries([{name:`core/${name}`,bytes:Buffer.from('')}]).length);
  }
});
