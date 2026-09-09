// Landing-page metadata is separate from guide headings and visible body copy.
export const homepages = {
  'docs/index.md': {
    lang: 'en',
    title: 'Metroidvania Studio',
    description: 'Free, open-source 2D level editor for metroidvania games. Build connected rooms, paint autotiled maps, and design minimaps with JSON export.',
  },
};

export const homeMedia = new Set([
  'media/readme/create-and-paint.gif',
  'media/readme/design-the-minimap.gif',
]);

export function renderHomepage(page, { prefix, version, escape }) {
  const sectionAt = page.html.indexOf('<h2');
  const intro = page.html.slice(0, sectionAt);
  const sections = page.html.slice(sectionAt).split(/(?=<h2\b)/).filter(Boolean);
  const guide = prefix + 'guides.html';
  return `<body class="homepage"><a class="skip" href="#content">Skip to content</a>
<header><a class="brand" href="${page.href.split('/').at(-1)}" aria-current="page"><img src="${prefix}studio-icon.svg" width="28" height="28" alt="">Metroidvania Studio</a><nav aria-label="Main navigation"><a href="${guide}">Documentation</a><a href="https://github.com/LBarimi/metroidvania-studio">GitHub</a></nav></header>
<main id="content"><div class="hero"><span class="eyebrow">Free · Open source · Local </span>${intro}</div>${sections.map(section => '<section class="home-section">' + section + '</section>').join('\n')}
<footer><span>Metroidvania Studio ${escape(version)}</span><a href="${guide}">Guides and API reference</a></footer></main></body>`;
}
