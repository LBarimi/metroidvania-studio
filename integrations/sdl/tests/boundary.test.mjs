import test from 'node:test';
import assert from 'node:assert/strict';
import { inspectBoundaries } from '../../../tools/repository/check-boundaries.mjs';

test('new engine boundaries allow only adapter source and still protect the studio core',()=>{
  const source='#include <SDL3/SDL.h>';
  const entry=name=>({name,bytes:Buffer.from(source)});
  assert.deepEqual(inspectBoundaries([entry('integrations/sdl/src/Room.cpp')]),[]);
  assert.ok(inspectBoundaries([entry('metroidvania-studio/core/Room.cpp')]).some(x=>x.rule==='engine-code'));
  for(const engine of ['godot','ue']) {
    const name=engine==='godot'?'addons/studio/import.gd':'Studio.uplugin';
    assert.deepEqual(inspectBoundaries([{name:`integrations/${engine}/${name}`,bytes:Buffer.from('')}]),[]);
    assert.ok(inspectBoundaries([{name:`core/${name}`,bytes:Buffer.from('')}]).length);
  }
});
