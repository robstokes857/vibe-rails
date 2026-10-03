# Open security finding: automatic backup credential disclosure

Recorded 2026-10-02 while reviewing VB-8QLDH-107 (VIBE-35), against
origin/main `d5b296aa237fed55162d088a2041c42dfffe4a2c`, HEAD
`a4c62c74029e89bd68d24dc385db66e5dea554e0` and the existing uncommitted tree.

**P1 / unresolved.** `VibeRails/Services/CompleteBackups/BackupFiles.cs` captures
native and configured CLI homes automatically. Its JSON redaction selector misses
Claude plugin `.mcp.json` files, so their MCP `env` and `headers` credentials enter
the account archive verbatim. Codex/Grok `config.toml` MCP `env`/`http_headers`
tables are also copied verbatim. MCP argument arrays preserve literal credential
arguments even in recognized JSON configuration files.

Evidence: an isolated C# harness called the real `BackupFiles.EnumerateAsync` and
`WriteZipAsync` against a synthetic home. The archive retained credentials in
`native/.claude/plugins/synthetic/.mcp.json`, `native/.codex/config.toml`, and an
MCP `--token` argument in `native/.gemini/antigravity/mcp_config.json`. All three
files were captured with zero coverage issues. The recognized JSON object's
`env` block was correctly stripped, confirming that the remaining exposures
bypass that fix. No actual credentials were read or uploaded.

Owner action: keep approval/release pending until known credential configuration
is redacted across supported formats or safely excluded with honest coverage.
If this version has already delivered archives, assess affected archives and
credential rotation. The Board card is flagged for this unresolved issue.

Both repository-wide listener searches from `API_SEC.md` found only the approved
main Kestrel host, the non-serving PortFinder probe, and test hosts; the
cross-runtime search had no matches. This finding concerns outbound backup
contents, not a new listener or authentication bypass.

## Resolution (2026-10-02, VIBE-35 follow-up; pending re-review)

Fixed in the working tree, not yet committed or released:

- Every captured `.json`/`.jsonc` file is now rewritten through the redactor (not a
  settings/config name list), which covers plugin `.mcp.json`, `plugin.json`,
  `mcp_config.json` and `opencode.json(c)`. A BOM, comments and trailing commas are tolerated.
- Every `.toml` file gets line-based TOML redaction with the same rules: comments and
  credential-named keys dropped, `env`/`headers`/`http_headers` tables keep their header but
  no entries, inline env/headers tables become `{}`, dotted `env.X` keys are dropped.
- Every string value in both formats is scanned (`BackupFiles.RedactText`): URL passwords, the
  value of any credential-named `name=value`/`Name: value` pair at any depth
  (`--header=Authorization: Bearer ...`, `--env=API_KEY=...`, `?access_token=...`), and the word
  or list item after a credential flag (`--token x`) or `Bearer`/`Basic` become `[redacted]`.
  The nested forms were found by the second review run and are covered by
  `StringValuesLoseEmbeddedCredentials` and `CredentialArgumentsAreReplacedInPlace`.
- Encoded spellings (third review run): TOML quoted keys and basic strings are unescaped before
  classification (`"http\u005fheaders"` tables, `"--api\u002dkey"` arguments, `\u003d` in URLs,
  line-ending backslashes) and re-escaped only when redacted; query and flag names are also checked
  percent-decoded (`api%5Fkey`), and a string whose percent-decoded form reveals a credential pair
  is replaced whole. `TomlRedactionKeepsConfigurationAndDropsEveryCredentialShape` and the
  archive-level `PluginMcpJsonAndCliTomlReachTheArchiveWithoutCredentials` cover these.
- Names ending in `key`/`pat` beyond a bare `key` (`OPENAI_KEY=…` in `docker -e` arguments,
  `GITHUB_PAT`) are treated as credentials.
- Regressions: `BackupFilesCredentialTests.PluginMcpJsonAndCliTomlReachTheArchiveWithoutCredentials`
  (real enumeration + zip writer over `.mcp.json`, a BOM `plugin.json`, Codex and Grok
  `config.toml`), `TomlRedactionKeepsConfigurationAndDropsEveryCredentialShape`,
  `CredentialArgumentsAreReplacedInPlace`.

Exposure check: `~/.vibe_rails/complete-backups` does not exist on this machine, so no
archive was staged or delivered from it, and the backup commits (e3c1d59d, 56da0325) are
not on origin/main or in a release. Other machines that ran a local build of those commits
should be checked by the owner. Residual: YAML is copied verbatim (no supported CLI reads MCP
servers from YAML), and free text (prompts, scripts, Markdown) remains embedded-secret territory.

## Re-review (2026-10-02, VIBE-35): compound credential arguments remain open

**P1 / unresolved.** The fixes above remove the original synthetic secrets and capture the
configured `Auth review` home. However, `BackupFiles.RedactArguments` only classifies the
outer option name before `=`. Thus `--header=Authorization: Bearer SYNTHETIC_SECRET` and
`--env=API_KEY=SYNTHETIC_SECRET` are retained because `header` and `env` are not credential
property names. These are credential-bearing header/environment arguments in MCP configuration,
not an unstructured prompt or script.

Evidence: a new disposable C# harness called the real `EnumerateAsync` and `WriteZipAsync`.
Both plugin `.mcp.json` and Codex `config.toml` retained the synthetic header credential;
the JSON file also retained the synthetic environment credential. The archive had three
sources and zero coverage issues. Original JSON/TOML `--token`, object env/header and TOML
env-table secrets were removed. No actual credentials were read and nothing was uploaded.
The harness and retained synthetic fixture are under
`C:\Users\robst\AppData\Local\Temp\vb107-rereview-probes`.

Owner action: keep approval/release pending until compound header/environment argument
values are inspected and redacted, or unsupported shapes are excluded with explicit
incomplete coverage. The Board attention flag is set for this remaining security issue.

## Re-review (2026-10-02, VIBE-35): encoded configuration credentials remain open

**P1 / unresolved.** The latest fix removes the unencoded compound arguments above,
but redaction still inspects TOML source spelling instead of decoded keys and values.
`BackupFiles.cs:497`, `:521` and `:529-530` strip quote delimiters without decoding
basic-string escapes. A valid `[mcp_servers.synthetic."http\u005fheaders"]` table
therefore bypasses the `http_headers` block exclusion and retains arbitrary header
credentials. `args = ["--api\u002dkey", "TOML_ARGUMENT_SECRET"]` retains the API key,
and `url = "https://mcp.example/sse?api_key\u003dTOML_URL_SECRET"` retains a query key.
The string scanner at `BackupFiles.cs:408-413` also does not decode URL query names:
a JSON MCP URL ending in `?api%5Fkey=JSON_QUERY_SECRET` is archived unchanged.

Evidence: real `EnumerateAsync` and `WriteZipAsync` over disposable synthetic homes
captured both plugin `.mcp.json` and Codex `config.toml` with zero coverage issues.
Python `tomllib` successfully parsed the original and archived TOML, confirming that
the archived header table is `http_headers`, the archived argument is `--api-key`,
and all three synthetic TOML secrets remain. `urllib.parse` decoded the archived
JSON query name to `api_key` and confirmed its synthetic secret remains. The previous
unencoded compound arguments were correctly redacted in the same probe. No real
credentials were read and no upload occurred. Harness and synthetic fixtures:
`C:\Users\robst\AppData\Local\Temp\vb107-final-review-probes`.

Owner action: keep release/approval pending until redaction examines decoded supported
configuration formats and URL query names, or safely excludes unsupported forms with
explicit incomplete coverage. Add archive-level regressions for these valid encodings.
The Board attention flag is set for this confirmed remaining credential disclosure.

## Re-review (2026-10-02, VIBE-35): multiline TOML argv and arbitrary header arguments

The previous escaped-key, escaped-flag and percent-encoded URL reproductions are now
redacted in the current working tree. Two supported credential carriers remain open.

**P1 / unresolved.** `BackupFiles.cs:545-553` passes only single-line TOML strings
to `RedactArguments`. Triple-quoted basic and literal strings are inspected separately,
so the credential flag/value relationship is lost. These valid configurations all
retain their literal credentials in the captured archive:

```toml
args = ["--api-key", """TOML_MULTILINE_VALUE_SECRET"""]
args = ['''--api-key''', "TOML_MULTILINE_FLAG_SECRET"]
args = ["--token", '''TOML_LITERAL_VALUE_SECRET''']
```

**P1 / unresolved.** `RedactArguments`/`RedactPlainText` classify header arguments
by the header name rather than the explicit header carrier. MCP proxy arguments
`["https://mcp.example/sse", "--header", "X-Deployment: JSON_HEADER_SECRET"]`
are captured unchanged from a plugin `.mcp.json`. Arbitrary header names can carry
credentials, the same reason object-valued `headers` blocks lose every entry.

Evidence: a new isolated C# harness called the real `EnumerateAsync` and
`WriteZipAsync` on disposable synthetic CLI homes. Both files were archived with
zero coverage issues. Python `tomllib` parsed the original and archived TOML and
confirmed all three argv sequences and credentials were unchanged; JSON parsing
confirmed the custom header remained. Ordinary JSON `--api-key` values and the
previous encoded-key/URL probes were correctly redacted. No real secrets were read
and nothing was uploaded. Harness and retained synthetic fixtures:
`C:\Users\robst\AppData\Local\Temp\vb107-current-review`.

Owner action: keep approval/release pending until all supported TOML string forms
share decoded argv redaction, and explicit header argument values are stripped or
unsupported shapes are excluded with incomplete coverage. Add archive regressions
and validate the emitted TOML with a standard parser. The card is flagged for these
confirmed disclosures. No product code was changed by this review.

## Revalidated during VB-8L17B-108 / VIBE-36 review (2026-10-02)

The requested review includes all local changes against freshly fetched `origin/main`
`d5b296aa237fed55162d088a2041c42dfffe4a2c`, HEAD
`d0af0f9bf0d4869a5ee0bf3833b5e1eab0cfb6e7` and uncommitted/untracked files.
The two preceding P1 findings remain reproducible in that working tree; they belong
to the backup changes, separately from the linked terminal-card-button commit.

The existing disposable `vb107-current-review/Repro.csproj` harness was rebuilt
against current product source. Real `BackupFiles.EnumerateAsync` and `WriteZipAsync`
captured two synthetic configuration files with zero coverage issues. The custom
JSON `--header` value and all three triple-quoted TOML credential arguments remained;
an ordinary JSON `--api-key` value was redacted. Retained synthetic fixture:
`C:\Users\robst\AppData\Local\Temp\vb107-current-review\bin\Debug\net10.0\synthetic-63b4a89683a34fd9b6447278b098cf17`.
No actual credentials were read and nothing was uploaded. VB-8L17B-108 is flagged
for owner review of this broad change set. The required correction and any prior
archive exposure assessment remain as stated above.

Both mandatory repository-wide listener searches were repeated, including untracked
files: only the approved main Kestrel listener, non-serving PortFinder probe and
test hosts matched; the cross-runtime search had no matches. The changed HTTP
routes remain under the existing session-plus-tab gate. No production code was
changed by this review.

## Revalidated during VB-13 review (2026-10-02)

The requested review covers origin/main
`d5b296aa237fed55162d088a2041c42dfffe4a2c` through HEAD
`d0af0f9bf0d4869a5ee0bf3833b5e1eab0cfb6e7`, including staged, unstaged and
untracked files. The backup changes are outside VB-13's recall feature but are
within that explicitly requested scope.

Both preceding P1 findings remain reproducible. Rebuilt and ran the existing
`vb107-current-review/Repro.csproj` harness against current product source.
Real enumeration and ZIP capture retained the arbitrary JSON `--header` value
and all three triple-quoted TOML credential arguments, with two sources and zero
coverage issues. The ordinary JSON `--api-key` control was correctly redacted.
Retained synthetic fixture:
`C:/Users/robst/AppData/Local/Temp/vb107-current-review/bin/Debug/net10.0/synthetic-6cb5144d295043408a11ca0d41d081ac`.
No real credentials were read or uploaded. VB-13 is flagged for owner review of
these confirmed disclosures; the required correction and exposure assessment
remain as stated above.

Both mandatory listener searches were repeated, including untracked files:
only the approved main Kestrel host, the PortFinder probe and test-only hosts
matched, with no cross-runtime matches. Changed routes retain the existing
session-plus-tab middleware gate. No product code was changed by this review.

### VB-13 follow-up review after recall/discussion fixes (2026-10-02)

Both P1 backup findings above remain reproducible against the same remote base
and HEAD plus the current working tree. Real enumeration and ZIP capture retained
the arbitrary JSON `--header` credential and all three triple-quoted TOML argv
credentials, with two sources and zero coverage issues. The ordinary JSON API-key
control was redacted. Synthetic fixture:
`C:/Users/robst/AppData/Local/Temp/vb107-current-review/bin/Debug/net10.0/synthetic-6a39282d9af34ceea5626f336ce07893`.
No real credentials were read or uploaded. The two VB-13 recall/discussion P2
corrections passed targeted tests; they do not resolve these backup disclosures.
VB-13 retains its owner attention flag. Required listener searches again found
only the approved host, port probe and test hosts, with no cross-runtime matches.
The correction and any prior archive exposure assessment remain as stated above.

## Resolution (2026-10-03, VIBE-37; pending re-review)

Both remaining P1 findings are fixed in `VibeRails/Services/CompleteBackups/BackupFiles.cs`:

- **TOML argv in any quoting.** `RedactTomlArray` reads every string item of an array through one
  reader (`TryReadTomlString`: basic, literal, multi-line basic and multi-line literal; escapes decoded;
  the newline TOML trims after an opening `"""`/`'''` preserved) and runs the whole decoded list through
  `RedactArguments`, so a flag in one style and its value in another remain a pair. A redacted item is
  re-rendered in its own quoting (`"""[redacted]"""`, `'''[redacted]'''`); a literal delimiter that
  cannot hold the text falls back to a basic string. `TomlStringEnd` also accepts the up to two delimiter
  characters TOML allows before a closing `"""`/`'''`. Non-string items stay outside the sequence.
- **Header and environment carriers.** `--header`/`--headers`/`--http-header(s)`/`-H` and
  `--env`/`--environment`/`-e` are carriers in argv lists (`RedactArguments`) and in free text
  (`RedactPlainText` via `CarrierCut`), including the compound `--header=Name: value` and
  `--env=NAME=value` forms. The value is dropped whatever the name, matching the `headers`/`env` block
  policy; the name is kept (`X-Deployment:[redacted]`, `NAME=[redacted]`). A header value without a
  name is replaced whole; a bare `-e NAME` pass-through is kept. Deliberate consequence: non-credential
  environment values such as `-e LOG_LEVEL=debug` or `--env=REGION=us` are now redacted as well.

Regressions in `Tests/Services/BackupFilesCredentialTests.cs`: `CredentialArgumentsAreReplacedInPlace`
(carrier, compound, pass-through and non-carrier `--env-file`/`--header-case` cases),
`StringValuesLoseEmbeddedCredentials` (free-text carriers),
`TomlRedactionKeepsConfigurationAndDropsEveryCredentialShape` (`[mcp_servers.multiline]`: the three
reported argv shapes, a spread multi-line value, a header argv and a free-text header) and the
archive-level `PluginMcpJsonAndCliTomlReachTheArchiveWithoutCredentials` (real `EnumerateAsync` and
`WriteZipAsync`: a plugin `.mcp.json` `--header` argv, Codex `config.toml` multi-line and literal argv
with a header argument). 183 tests across the backup, MCP and recall suites pass.

Standard-parser validation: the redacted output of a synthetic Codex `config.toml` covering every
shape above (plus `"""two"""""` and a mixed-type array) parses with Python 3.13 `tomllib`, contains no
synthetic secret and keeps every server table. The unit-test fixture itself is deliberately not valid
TOML (an inline `env` table is later extended), so it is not checked this way. Harness: a throwaway
dump test removed before commit; input and output retained under
`%LOCALAPPDATA%\Temp\claude\C--source-vibe-rails\4bbfd146-e297-4dbb-8eae-0b57f880f9bd\scratchpad\tomlprobe`.

Residual, unchanged: YAML and free text (prompts, scripts, Markdown) are copied verbatim. The exposure
assessment above is unchanged: no archive was staged or delivered from this machine.
