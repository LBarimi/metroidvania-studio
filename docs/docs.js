const search = document.getElementById('search'), results = document.getElementById('results'), nav = document.querySelector('.sidebar nav');
let timer;
search.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(() => {
  const query = search.value.trim().toLowerCase();
  results.replaceChildren(); results.hidden = !query; nav.hidden = !!query;
  if (!query) return;
  const terms = query.split(/\s+/);
  const matches = (window.studioDocs || []).filter(p => terms.every(term => (p.title + ' ' + p.text).toLowerCase().includes(term)))
    .sort((a, b) => Number(b.title.toLowerCase().includes(query)) - Number(a.title.toLowerCase().includes(query))).slice(0, 20);
  const count = document.createElement('p'); count.textContent = matches.length ? `${matches.length} matching pages` : 'No matching pages'; results.append(count);
  for (const match of matches) {
    const anchor = document.createElement('a'); anchor.href = match.href; anchor.textContent = match.title;
    const preview = document.createElement('small'), at = Math.max(0, match.text.toLowerCase().indexOf(terms[0]) - 30);
    preview.textContent = match.text.slice(at, at + 110) + '…'; anchor.append(preview); results.append(anchor);
  }
}, 100); });
document.addEventListener('keydown', e => { if (e.key === '/' && !/INPUT|TEXTAREA/.test(e.target.tagName)) { e.preventDefault(); search.focus(); } if (e.key === 'Escape' && e.target === search) { search.value = ''; search.dispatchEvent(new Event('input')); } });
for (const button of document.querySelectorAll('.copy')) button.addEventListener('click', async () => {
  const code = button.closest('.code-block').querySelector('code');
  try { if (!navigator.clipboard) throw new Error(); await navigator.clipboard.writeText(code.textContent); button.textContent = 'Copied'; }
  catch { const range = document.createRange(); range.selectNodeContents(code); const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range); button.textContent = 'Selected'; }
  setTimeout(() => button.textContent = 'Copy', 1600);
});
