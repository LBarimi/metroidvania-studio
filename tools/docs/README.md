# Website sources

The public website is generated into `docs/` for GitHub Pages. Building locally does not publish it.

- `docs/index.md`: English introduction and links to the editing guides.
- `docs/guides.md`: guide directory and offline help entry content.
- `tools/docs/homepage.mjs`: page titles, descriptions, and landing-page layout.
- `tools/docs/homepage.css`: responsive landing-page styles.

The existing README recordings are copied into the public site's `docs/assets/` during generation. Offline documentation keeps its searchable guide entry and does not include these promotional GIFs.

## Preview and validate

```sh
node tools/docs/build-pages.mjs
node --test tools/docs/build.test.mjs
```

Open `docs/index.html` locally. Review generated pages and Markdown sources together. Existing reference URLs remain unchanged. The homepage has its own canonical URL and appears in the sitemap.

## After an approved publication

GitHub Pages publishes the `main` branch's `/docs` folder. Do not push or publish a review draft.

1. Verify the homepage and documentation on the public site.
2. Add the URL-prefix property `https://lbarimi.github.io/metroidvania-studio/` in Google Search Console and complete the ownership verification it requests.
3. Submit `https://lbarimi.github.io/metroidvania-studio/sitemap.xml`.
4. Inspect the homepage URL, then request indexing if needed.
5. Review search queries, impressions, and clicks before deciding on further content changes.

Ownership verification needs the site owner's account. No verification token or account information is included in this repository. Sitemap submission and indexing requests do not guarantee inclusion or ranking.
