# OAuth clients: findings and decisions

What each target agent needs from LabHop's authorization server, why LabHop registers clients the way it
does, and what has actually been tested. Checked 2026-10-30; re-check against the sources when upgrading.

## Verification status

| Client | Registration used | Status |
| --- | --- | --- |
| Claude Code | Pre-registered public client, loopback redirect | Not yet validated |
| Codex | Pre-registered public client, loopback redirect | Documented only; not tested |
| claude.ai, Claude Desktop, Cowork | Pre-registered public client, hosted redirect | Documented only; not tested. Needs a public URL |

Update this table when a client is exercised against a running server. Documented support is not the same as
tested support.

## What OpenIddict covers

OpenIddict 7.7.2 provides the authorization code flow, PKCE, refresh token rotation, revocation, RFC 8707
resource validation (`RegisterResources`), the RFC 9207 `iss` response parameter, both well-known metadata
paths, and port-agnostic loopback matching for clients registered as native applications with port-less
redirect URIs.

It does not provide dynamic client registration (RFC 7591), client ID metadata documents, or protected
resource metadata.

## What the MCP C# SDK covers

ModelContextProtocol.AspNetCore 2.3.0 serves protected resource metadata, and its `[Authorize]` filters hide
and block tools. LabHop adds the rest:

- The SDK's 401 challenge carries only `resource_metadata`, and it sends no challenge on 403. `McpBearerHandler`
  adds `scope` and the RFC 6750 error codes.
- A tool cannot return an HTTP 403, because the response has started by the time it runs. Clients treat a failed
  tool result as ordinary output and do not start step-up. `McpRequestGuards` answers before the SDK runs.
- The SDK does not validate `Origin`, which the Streamable HTTP transport requires. `McpRequestGuards` does.

## Why clients are pre-registered

All three target clients accept a pre-registered public client, and it adds no unauthenticated endpoint. For a
single-owner server, registering a client once from the shell is a small cost.

- **Dynamic registration** would add an unauthenticated endpoint that writes to the database and lets anyone
  create a client with a name of their choosing. Claude's documentation notes that it registers a new client
  on every fresh connection, so it also needs pruning. The 2026-07-28 MCP specification keeps it only for
  backwards compatibility and marks it deprecated, but today more clients document it than the alternative.
- **Client ID metadata documents** are what the current specification prefers (SHOULD). They make the server fetch
  a URL supplied by an unauthenticated caller, which is an SSRF risk, and they cannot prevent localhost
  impersonation by themselves.

Either can be added later as its own reviewed change. Until then, clients that insist on automatic registration
will not connect.

## Alternatives evaluated: Duende IdentityServer

Checked 2026-10-30 against version 8.0.10, which targets .NET 10. OpenIddict stays unless the owner decides
otherwise.

- **Provides:** a built-in DCR endpoint (`Duende.IdentityServer.Configuration`), RFC 9207 `iss` by default, and an MCP
  quickstart and sample.
- **Still custom with Duende:** redirect URI and `application_type` rules for DCR (a custom validator, per Duende's
  blog), `localhost` loopback matching (its built-in validator accepts only `127.0.0.1`, and Claude Code uses
  `localhost`), login and consent pages, the 401 and 403 challenges, and the `Origin` check.
- **Not provided:** client ID metadata documents. Duende's docs say to use pre-registration or DCR on v8.
- **License:** commercial, not open source. Free for development, testing, and personal projects, and for qualifying
  small organizations. Community Edition is for running it on your own infrastructure for your own use, and embedding
  it in software shipped to third parties needs a redistribution license. This repository is public, so anyone else
  self-hosting LabHop would raise that question. OpenIddict is Apache-2.0.
- **Edition details conflict across Duende's pages:** DCR and resource isolation (which binds tokens to one resource)
  are listed for Standard and above, Community is described as both "mostly" and fully equivalent to Standard, and one
  page lists RFC 7591 under Community. Confirm with Duende before relying on either.

Sources: [DCR](https://docs.duendesoftware.com/identityserver/configuration/dcr/),
[MCP guidance](https://docs.duendesoftware.com/identityserver/ai/),
[MCP quickstart](https://docs.duendesoftware.com/identityserver/quickstarts/8-mcp/),
[loopback redirects](https://duendesoftware.com/blog/20260707-port-agnostic-localhost-redirect-uris-mcp-auth),
[DCR validation](https://duendesoftware.com/blog/20260730-hardening-oauth-in-mcp-release-candidate),
[resource isolation](https://docs.duendesoftware.com/identityserver/fundamentals/resources/isolation/),
[Community Edition](https://duendesoftware.com/products/communityedition),
[pricing](https://duendesoftware.com/pricing). The license PDF itself has not been read.

## Alternatives evaluated: SqlOS

Checked 2026-10-30 by reading the [source](https://github.com/ross-slaney/sqlos) and issue tracker at v11.1.1, without
running it. Not adopted.

- **Provides:** MIT license, PostgreSQL and EF Core 10 support, its own OAuth server with S256-only PKCE, opt-in DCR and
  CIMD (with address checks on metadata fetches), and a documented pairing with the MCP SDK:
  `MapMcp("/mcp").RequireAuthorization("SqlOS.Mcp")`.
- **Conflicts with `AGENTS.md`:** it has its own users and sessions instead of ASP.NET Core Identity, and it creates and
  upgrades its tables at startup with no opt-out that I could find.
- **Maturity:** created February 2026, 395 of 396 commits from one maintainer, 6 stars, no security policy, and four
  major versions between 8.0.0 and 11.1.1 in 12 days.
- **Open issues that matter here:** [#429](https://github.com/ross-slaney/sqlos/issues/429) (any client can request a
  token for any `resource`), [#430](https://github.com/ross-slaney/sqlos/issues/430) (consent omits the redirect host,
  which the MCP specification requires), [#432](https://github.com/ross-slaney/sqlos/issues/432) (a reused code does not
  revoke its tokens), and [#425](https://github.com/ross-slaney/sqlos/issues/425) (HTML injection into the dashboard
  through public signup, which is on by default).
- **Loopback:** `localhost` is deliberately excluded from any-port matching, and Claude Code redirects to `localhost` on a
  random port.

Worth reading again if LabHop adds CIMD, for how it guards metadata fetches.

## Client requirements

### Claude Code

- Redirect is `http://localhost:PORT/callback` on a random port unless `--callback-port` is set. Registering with
  `--loopback` accepts any port.
- It sends credentials only to HTTPS token endpoints or to localhost.
- It rejects an authorization response naming an unexpected issuer, so the issuer must match byte for byte.
- It starts sign-in from `claude mcp login <name>` or `/mcp`. `claude -p` cannot run the OAuth flow itself.
- A `403 insufficient_scope` on a tool call fails with a message naming the scope. Re-authenticating does not widen
  access unless the client requests the scope. LabHop's 401 challenge names both scopes, so the client asks for both
  and the user chooses on the consent page.

```bash
LabHop oauth-client add --client-id claude-code --name "Claude Code" --loopback
```

```bash
claude mcp add --transport http --client-id claude-code labhop https://labhop.example.net/mcp
```

```bash
claude mcp login labhop
```

### Codex

- The default callback is `http://127.0.0.1/callback`, with the active port inserted during authorization. Without
  issuer support it appends a per-server ID to the path. LabHop advertises issuer support, so the plain path should
  apply; this is unconfirmed.
- It rejects a mismatched `iss`, and also a missing `iss` when issuer support is advertised.

```bash
LabHop oauth-client add --client-id codex --name "Codex" --loopback
```

```bash
codex mcp add labhop --url https://labhop.example.net/mcp --oauth-client-id codex
```

```bash
codex mcp login labhop
```

### claude.ai, Claude Desktop, Cowork

- These connect from Anthropic's infrastructure, so the server must be reachable from the public internet, from
  `160.79.104.0/21`. Exposing LabHop that way is the owner's decision.
- The redirect is exactly `https://claude.ai/api/mcp/auth_callback`.
- The client ID is entered in the custom connector dialog. Leaving the secret blank makes it a public client.
- Discovery, registration, and token endpoints have 10 seconds to respond, and refresh requests 30 seconds.

```bash
LabHop oauth-client add --client-id claude-hosted --name "Claude" --redirect-uri https://claude.ai/api/mcp/auth_callback
```

## Sources

- [MCP authorization specification, 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization)
  (current revision; the initial design used [2025-11-25](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization),
  and a full recheck against the current revision's sub-pages is still to do)
- [Claude Code MCP documentation](https://code.claude.com/docs/en/mcp)
- [Claude connector authentication](https://claude.com/docs/connectors/building/authentication)
- [Claude connector lazy authentication](https://claude.com/docs/connectors/building/lazy-authentication)
- [Codex MCP documentation](https://learn.chatgpt.com/docs/extend/mcp?surface=cli)
- [OpenIddict 7.0 migration notes](https://documentation.openiddict.com/guides/migration/60-to-70)
- OpenIddict 7.7.2 source: `OpenIddictApplicationManager.ValidateRedirectUriAsync`
- MCP C# SDK 2.3.0 source: `McpAuthenticationHandler`, `AuthorizationFilterSetup`, `StreamableHttpHandler`
