# Browser check

`ui-check.mjs` drives a real browser through login, project editing, the two-tab conflict, archiving, OAuth consent,
and revocation against a running instance. It covers the criteria the HTTP tests cannot: U1-U4 in
`docs/acceptance/milestone-1.md`.

It needs an instance with a user and a registered loopback client. It creates a project each time, so point it at a
disposable database.

```bash
npm install
```

```bash
BASE_URL=https://labhop.example.net LABHOP_EMAIL=you@example.net LABHOP_PASSWORD='...' node ui-check.mjs
```

The run saves screenshots to `shots/`.
