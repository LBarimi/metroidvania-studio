import test from 'node:test';
import assert from 'node:assert/strict';
import { validateReleaseState, validatePublishContext } from './release.mjs';
import { releaseNames } from './archives.mjs';
const head = 'a'.repeat(40);
const state = { branch: 'main', head, status: '', version: '1.0.0', tagExists: false, remoteMain: head, changeLog: '# Changes\n\n## 1.0.0\n\nInitial release.\n' };
const report = { commit: head, version: '1.0.0', archives: releaseNames.map(name => ({ name })), blockingIssues: [] };
test('release preparation rejects feature, detached, dirty and stale main revisions', () => {
  for (const change of [{ branch: 'feature/test' }, { branch: '' }, { status: '?? pending.json' }, { status: ' M tracked.cs' }, { remoteMain: 'b'.repeat(40) }, { head: '' }]) assert.throws(() => validateReleaseState({ ...state, ...change }));
  assert.equal(validateReleaseState(state), 'v1.0.0');
});
test('invalid versions, duplicate tags and missing release notes block preparation', () => {
  for (const change of [{ tagExists: true }, { changeLog: '' }, { version: '../main' }, { version: '01.1.0' }, { version: '1.0.0-preview' }]) assert.throws(() => validateReleaseState({ ...state, ...change }));
});
test('publication requires the exact tag, reviewed archives and matching main revision', () => {
  for (const confirmation of ['', 'v0.1.0', '1.0.0']) assert.throws(() => validatePublishContext(state, confirmation, report));
  for (const change of [{ commit: 'b'.repeat(40) }, { version: '0.1.0' }, { archives: report.archives.slice(1) }, { blockingIssues: [{ path: 'unreviewed' }] }]) assert.throws(() => validatePublishContext(state, 'v1.0.0', { ...report, ...change }));
  assert.throws(() => validatePublishContext({ ...state, remoteMain: null }, 'v1.0.0', report));
  assert.doesNotThrow(() => validatePublishContext(state, 'v1.0.0', report));
});
