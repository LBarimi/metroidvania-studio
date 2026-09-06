import test from 'node:test';
import assert from 'node:assert/strict';
import { validateReleaseState, validatePublishContext } from './release.mjs';

const head = 'a'.repeat(40);
const state = { branch: 'main', head, status: '', version: '0.1.0',
  tagExists: false, remoteMain: head, changeLog: '# Changes\n\n## 0.1.0\n\nInitial release.\n' };
const environment = { GITHUB_ACTIONS: 'true', GITHUB_EVENT_NAME: 'workflow_dispatch',
  GITHUB_REF: 'refs/heads/main', RELEASE_PUBLISH: 'true', GITHUB_SHA: head };

test('release preparation rejects feature, detached, dirty and stale main revisions', () => {
  for (const change of [{ branch: 'feature/test' }, { branch: '' }, { status: '?? pending.json' },
    { status: ' M tracked.cs' }, { remoteMain: 'b'.repeat(40) }, { head: '' }]) {
    assert.throws(() => validateReleaseState({ ...state, ...change }));
  }
  assert.equal(validateReleaseState(state), 'v0.1.0');
});
test('invalid versions, duplicate tags and missing release notes block preparation', () => {
  for (const change of [{ tagExists: true }, { changeLog: '' },
    { version: '../main' }, { version: '01.1.0' }, { version: '0.1.0-preview' }]) {
    assert.throws(() => validateReleaseState({ ...state, ...change }));
  }
});
test('publication requires an explicit manual main workflow at the validated revision', () => {
  for (const change of [{ GITHUB_ACTIONS: 'false' }, { GITHUB_EVENT_NAME: 'push' },
    { GITHUB_EVENT_NAME: 'pull_request' }, { GITHUB_REF: 'refs/heads/feature/test' },
    { RELEASE_PUBLISH: 'false' }, { GITHUB_SHA: 'b'.repeat(40) }]) {
    assert.throws(() => validatePublishContext(state, { ...environment, ...change }));
  }
  assert.throws(() => validatePublishContext({ ...state, remoteMain: null }, environment));
  assert.doesNotThrow(() => validatePublishContext(state, environment));
});
