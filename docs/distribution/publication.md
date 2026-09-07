# Publication checklist

Publication is pending approval. Local build and validation commands do not publish, create release tags, log into a registry, or change registry ownership.

Before publishing:

1. Review the 1.1.0 changes and validation reports, merge the approved work into `main`, and confirm the final version. All publication and release tagging must run from the approved `main` commit.
2. Check npm name availability and account ownership. The proposed package name is `metroidvania-studio`; availability has not been reserved.
3. Rebuild the local package from the approved commit. Confirm `version.json`, npm metadata, CLI version, and `server.json` agree.
4. Install the exact `.tgz` in a clean folder. Exercise CLI edits, dry runs, Lua limits, MCP initialization, tool discovery, cancellation, and workspace boundaries. Verify Windows, macOS, and Linux on native hosts before claiming platform coverage.
5. Inspect the package inventory and license notices. Retain the original BSD 3-Clause, Apache 2.0, and MIT notices supplied for managed dependencies. Keep private source, machine paths, maps, media, engine packages, and build symbols out of the package.
6. Publish the reviewed npm artifact only after explicit approval. Root package publication is disabled by `private: true`.
7. Confirm the public npm artifact and its `mcpName`, then authenticate the corresponding MCP Registry namespace and submit the matching `server.json` only after explicit approval.
8. Check the published metadata and install instructions before describing registry installation as available in the README.

The MCP Registry stores metadata and expects the underlying npm package to exist first. The registry `name` must match npm's `mcpName`; see the [official publication guide](https://modelcontextprotocol.io/registry/quickstart). Package files and executable mapping follow npm's [package manifest documentation](https://docs.npmjs.com/cli/v11/configuring-npm/package-json/).
