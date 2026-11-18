# MK HQ Billing (C# / .NET 8)

A billing app for MK Building Materials and MK Paints that writes **into HQ Business+NG's own SQL Server
databases exactly as HQ itself would**. Bills, estimates, purchases, receipts and payments made here appear in HQ
with HQ's own series, numbers, ledger rows and change log. Your father can switch to HQ at any moment, and back.

```
Start MK HQ Billing.cmd            ← double-click (opens the browser with the local access key)
mk-hq/
  src/MK.Hq.Domain   pure rules: GST engine, HQ ledger / voucher / settlement planners, GSTIN, amount in words
  src/MK.Hq.Engine   HQ integration: write plans, executor, write coordinator, journal, locks, readers, reports
  src/MK.Hq.Api      ASP.NET Core minimal API + the browser client (wwwroot, plain ES modules, no build step)
  tests/MK.Hq.Tests  parity oracles against the real HQ data, workflow / concurrency tests
  tools/             restore-demo.ps1, publish.ps1, start.ps1
```

## Run it

| Goal | Command |
|---|---|
| Try it safely on demo copies of both firms | `tools\restore-demo.ps1` then `Start MK HQ Billing.cmd -Demo` |
| Build the shop-PC release (no .NET install needed) | `tools\publish.ps1`, then copy `publish\` to the shop PC |
| Run against the real firm databases | `Start MK HQ Billing.cmd` (uses `publish\appsettings.json`: ISTMBM26, ISTMP26) |
| Run all tests | `dotnet test tests\MK.Hq.Tests` (writes only to scratch restores named `MKHQ_*`) |

The service listens on `127.0.0.1:47320` only. A random access key is created in `var\api.token`; the launcher passes
it to the browser in the URL fragment. Keyboard: F1 dashboard, F2 sale, F3 estimate, F4 purchase, F5 receipt,
F6 payment, F7 bills, F8 ledger, F9 outstanding, F10 save.

## Architecture: why it looks like this

**1. A write to HQ is data, not code.** The planner turns a request plus a fresh snapshot of HQ into a `WritePlan`,
which is an ordered list of HQ procedure calls with fully bound values. A generic executor runs it in one transaction.
Because the plan is data:
- its SHA-256 *is* the review fingerprint, so posting re-plans from live data and refuses unless the hash is identical
  (rates, accounts or settings changed → "review again"). What was reviewed is provably what gets written.
- the browser can show the exact HQ ledger before saving, and the same plan code is tested without a database.

**2. HQ's own procedures, HQ's own edit path.** New bills use `IQDoc_Header_Insert`, `IQDoc_Delv_insert`, `IQCash_TRAN_insert`,
`IQDoc_Chrgs_Insert`, `IQDOC_VAT_Insert`, `IQdaybook_insert`, `UpdateClosing_Stock_B+NG` and the `User_IQ*` audit copies.
HQ's "insert" procedures are upserts: given an existing `Doc_Id` or `Delv_Id` they update in place. That is HQ's edit
path, so corrections use it too, and kept lines keep their HQ identity. Deletes use `Outward_Delete`, `Inward_Delete`
and `Orders_Delete`. Vouchers use `Insert_Voudetails` and `IQbnktran_insert`.

**3. One coordinator for every write** (`WriteCoordinator`), layered outermost first:
1. an in-process keyed lock, plus a Windows delete-on-close file lease on the operation id: two clicks, two tabs or
   two processes on this PC can never run the same save twice, and a crashed process releases its lease automatically;
2. a SQLite operation journal: a committed id returns its stored receipt (idempotent retries);
3. HQ's own `.HQS` edit lock for documents being changed: an HQ user editing that bill and MK exclude each other;
4. one **SERIALIZABLE** transaction, with SQL Server application locks shared by every MK terminal on the LAN.
   The per-firm writer lock is taken *before the first read*, so two MK writers can never deadlock on each other's
   read locks. Application locks live only in SQL Server's lock manager and add nothing to HQ's schema;
5. evidence (`Doc_Id`, expected `ModifyCnt`) written durably *before* COMMIT. After a crash, one HQ read settles the
   outcome; it is never guessed.

**4. Numbering that coexists with HQ PCs.** HQ numbers client-side (`MAX(TNo)+1`, a duplicate check, then a plain insert,
with no transaction). MK reads MAX and runs every duplicate check with `READCOMMITTEDLOCK`, so it never holds a
key-range lock on `IQDoc_Header` that could delay an HQ PC's insert. HQ's stock procedure runs at READ COMMITTED
inside the otherwise serializable transaction for the same reason. The final duplicate check and `COMMIT` are sent
as **one server-side batch**, so nothing (not even a network round trip) separates "number still free" from
"committed". If the number was taken, nothing commits, and the coordinator re-plans with the next number.
`SET DEADLOCK_PRIORITY LOW` means that if SQL Server must choose, it sacrifices MK's transaction (which retries)
rather than an HQ save, which would be left half-written.

**5. A pinned HQ contract.** At start-up the engine fingerprints every HQ procedure it calls (definition plus
parameters) and pins them in `var\hq-contract.<db>.json`. It also checks that `READ_COMMITTED_SNAPSHOT` is OFF,
which the numbering protocol relies on. A vendor update that changes any of these stops **writes** (reads continue),
with the reason shown in *HQ health*, until the parity tests pass and someone presses *Accept updated HQ*.

**6. Zero database footprint.** No table, view, trigger, procedure, schema, login or setting is added to HQ's
databases. The engine's own state (journal, drafts, leases, contract) lives in its `var` folder.

**7. Exact money.** All arithmetic is `decimal` with HQ's half-away-from-zero rounding. The browser never computes money;
every keystroke is priced by the server (about 50 ms warm).

## How it is verified (the oracles are HQ's own data)

| Test | What it proves | Result |
|---|---|---|
| `GstParityTests` | Recompute every stored line HQ saved this year in both firms | 12,587 lines exact (13 explained by HQ's older edit-path rounding); 1,349 bill totals and round-offs exact |
| `LedgerParityTests` | Rebuild every bill's ledger from its own lines and charges | 1,337 of 1,349 identical; the other 12 are HQ's own inconsistent bills, pinned by number in the test |
| `ReplayParityTests` | Re-post real HQ bills into a scratch restore; diff **every column of 11 tables** against HQ's rows | 83 bills (sales cash/credit/card/UPI, IGST, purchases, orders, POs) identical |
| `VoucherParityTests` | Same for receipts, payments and credit/debit notes (cash and bank, with bank register and audit) | 42 of 42 identical |
| `WorkflowTests` | An amended bill equals a fresh save of the same content; settlements carried or refused; delete restores stock exactly; a double-submitted save makes one bill; a changed master invalidates the review; **two MK terminals plus a simulated HQ PC racing on series CS → 0 duplicate numbers** | 6 of 6 |
| `PlannerTests` | Pure domain rules | 11 of 11 |
| `MasterParityTests` | Items and accounts: clones of HQ-made masters equal the originals; HQ-saved masters re-save identically | 25 + 25 items, 20 + 6 accounts identical |
| `VoucherAmendTests` | Vouchers corrected in place: HQ's own vouchers re-saved unchanged come back identical; settlements move between bills | 26 of 26 |
| `IntegrityTests` | On scratch restores of the latest live backups (`Databases\`), every data-health check runs and counts as listed; every repair (and every owner decision, answered yes with the first choice) clears what it targets and makes no other check worse | 112 checks; 22 automatic repairs, the decisions one by one |

Values the replay deliberately does not compare are listed in the tests with the reason: capture timestamps,
identity columns, item-master values that changed after the original bill, and two columns that changed with HQ's
own update around 30 Oct and 4 Nov 2026.

## What the work found in HQ (shown live under *HQ health*)

- **Round-off sign (MBM):** the "Round Off" charge (73) is configured as *Less*. HQ therefore posts every rounded bill's
  round-off with the opposite sign, and 473 bills' ledgers are out by twice their round-off. MP configures it as *Add*
  and balances. MK writes exactly what HQ writes; changing the charge type in HQ fixes future bills in both programs.
- **Half-saved bills:** 2 sales have no ledger at all, and 21 orders lack HQ's VAT and audit rows (HQ's saves are not
  transactional). MK's saves are all-or-nothing.
- **47 registered parties have GSTINs that fail the official check digit** (for example spaces, a letter O for zero,
  or I for 1). GST returns will reject them; fix them in HQ's account master.
- The TypeScript prototype refused every bill with a non-zero round-off (most MBM bills), wrote `Bill_Time` in 12-hour
  format (HQ uses 24-hour), invented a purchase margin percentage HQ stores as 0, and missed several fields HQ fills.
  The replay oracle caught all of these.

## Data health (audits and repairs)

*Data health* lists 112 checks in nine areas (ledger and settlements; charges, tax heads and account mappings;
customers and suppliers; bills, orders and stock; between the two firms; GST returns; HQ change history; database
structure; passwords and access). Each line says in plain words what is wrong, how many, and the next step:

- **MK can repair** (22): the repair is HQ's own write (its procedures, its account-screen re-save, its stock
  refresh) done in one SERIALIZABLE transaction under MK's write lock. The check runs again inside the transaction
  and must find nothing, or everything rolls back. Repairs that touch many accounts give each one a savepoint: one
  HQ's own form rules refuse (e.g. a GSTIN of the wrong length) is undone and stays listed; the rest go through.
- **Needs your decision** (57): the question is put first. Some take a value: which account a charge posts to, which
  bank vouchers open with, the corrected GSTIN (suggested where a one-letter correction makes it valid, or typed).
  Nothing is preselected. Bills of months whose GST is likely filed (before 1 Oct 2026) are always a decision.
- **For information** (33): shown, never changed (e.g. invoice-number gaps for GSTR-1 Table 13, deleted bills).

Order is enforced (the Round Off setting before the bills it posts; duplicate account codes before other account
re-saves). Every repair is logged in `integrity.db`, never in HQ. Cross-firm checks only report: a repair there would
write the other firm's database outside its lock. `CompanyDatabase` (ISTMAIN) is read for the company and user checks.

The audits behind the checks, with the evidence and the HQ source each repair follows, are in `docs/audit/`
(`ledger`, `config`, `parties`, `transactions`, `crossfirm`); `docs/audit/plain.json` holds the shop's wording.
`python tools/integrity/build_catalogue.py` merges them into the engine's catalogue: duplicates found by two audits
kept once, partial and per-finding repairs declared, and nothing that needs an unknown value offered as a button.

## Bill photos (Google Antigravity)

Photos are read by **Google Antigravity** (`AntigravityBillReader`), over the same `agy acp` connection and Google sign-in
as the Assistant, so the shop needs no Gemini API key. Each read is an ACP session of its own in `var\photo-reader`: no MCP
tools, no file or terminal access, and every permission request refused. The pages go in as ACP image blocks; the prompt
and the parsing below are unchanged. If Antigravity is not signed in, the photo fails with "not signed in" and is kept:
sign in from the Assistant panel and press *Read again*. `"Agent": { "ReadPhotos": false }` goes back to Gemma 4 through
the Gemini API (`GemmaExtractor`, key in Settings), which is what the steps below and the live tests describe.

*Scan bill photo* (F11) takes scanner files (PNG, JPEG, BMP, TIFF), the PC's camera, pasted images, or phone photos sent
by scanning a QR code. The phone page lives on a second listener on the shop LAN (`PhoneUploadPort`, 47351); it can
upload photos for 20 minutes and nothing else.

1. **Normalise:** decode any format, apply phone EXIF rotation, scale to at most 2,400 px, and store as JPEG (the original
   is kept for lossless re-rotation).
2. **Upright check:** a small, fast Gemma question ("how many degrees to turn this?"). A sideways handwritten page made
   the model run past two minutes; upright, the same page reads in about a minute.
3. **Read:** `gemma-4-26b-a4b-it` (or `gemma-4-31b-it` via *Read with larger model*) through the Gemini API, with a prompt
   tuned on the shop's own scans: charge rows such as P&F are lines, subtotal rows are not, overlapping pages are not
   double-counted, and Telugu/Hindi wording is translated. The reply is parsed defensively: unreadable numbers become
   warnings, never guesses.
4. **Match:** suppliers by GSTIN (then by name, flagged as such); items by rare-word overlap with spelling tolerance,
   with sizes and packs as hard facts (4 ltr ≠ 1 ltr, 3/4" ≠ 1"); and above all by **learned wording**. Every bill saved
   from a photo teaches "this supplier's wording = this HQ item", which matters because the shop maps supplier names
   loosely ("25MM LEO TANK FILTER" → "LEO ROYAL PILLER TAP").
5. **Check:** when a row's printed amount cannot come from quantity × rate (P&F ₹220 + GST = ₹259.60), the amount wins and
   is flagged. Inclusive versus exclusive GST is chosen by which one reproduces the printed grand total.
6. **Draft:** the result opens in the normal bill editor beside the photo, with suggestions on uncertain lines and the
   printed versus calculated total. **Nothing is saved to HQ until the user presses Save.**

The API key (Settings) is encrypted with Windows DPAPI and never sent to the browser; it was imported once from the
previous app. `docs/scan-evaluation.md` is the accuracy report on the shop's labelled scans
(`dotnet test --filter LabelledScanEvaluation`, which uses the live API).

## Masters, notes and voucher corrections

Specs, researched from HQ's decompiled screens and checked against HQ's own rows: `docs/hq-spec-items.md`,
`docs/hq-spec-accounts.md`, `docs/hq-spec-voucher-modify.md`.

* **Items** (`FrmItems`): `IQItems_Mast_insert` (its update branch for a change), the images call, the single unit row
  (sale rate in `Unit_WSrate`; per-unit rates sent as a float exactly as HQ does, so 349.575 is stored as 349.57), and
  one `Ttype_Acnt` row per transaction type with the GST rate's accounts, looked up by HQ's trade-type labels.
  Oracle (`MasterParityTests`): HQ's 25 newest items cloned through the engine are identical column for column; 25
  HQ-saved items re-saved unchanged come back identical; changing a rate re-chooses exactly the accounts HQ uses.
* **Customers and suppliers** (`FrmAccounts`): `IQaccmst_insert`/`_update` (which files the account in the chart of
  accounts and renumbers its order), the address with HQ's blank rules (`' '` versus `''` per box), ten contact
  rows, the opening-balance row and the rewrite of account 5, "Opening Balance Difference". The state comes from the
  GSTIN. Oracle: HQ's 20 newest parties cloned identically; the 6 accounts HQ edited in November re-save byte-identical.
* **Credit and debit notes**: a party at the top, settled against its open bills, with "against" accounts as lines.
  Both real HQ notes re-post identically (HQ writes the series in capitals, `CRE`/`DEB`).
* **Correcting a voucher in place** (`FrmFin_Vouchers` edit mode): same Doc_Id and number, header via
  `Update_Voudetails`, ledger and bank rows re-inserted, settlements released and re-applied, and one more audit set.
  Oracle (`VoucherAmendTests`): 26 HQ vouchers (including the 8 HQ itself modified and both notes) re-saved
  unchanged come back identical.

Deliberate differences from HQ (each is HQ's own behaviour being unsafe, not a shortcut):
* Every master save is one transaction; HQ runs the same 4–30 calls unprotected.
* Editing an account whose opening balance has payments settled against it keeps that row and its settlements
  (HQ re-creates the row and leaves the settlements pointing at nothing). Without settlements MK does exactly what HQ does.
* Account names and addresses refuse apostrophes and non-Latin text (HQ doubles the first and turns the second into `?`).
  Item names keep apostrophes, since the shop types `''` as an inch mark.

## Tidying item names (the owner's guided review)

1. `sqlcmd … -i tools\item-cleanup\export.sql` per firm, then `python tools\item-cleanup\propose.py <export folder> "docs\item-cleanup\Item names review.xlsx" [filled-in review.xlsx]`.
   This writes the review sheet and `docs\item-cleanup\proposals-<MBM|MP>.json`; answers already typed in a filled-in sheet are carried over.
2. In MK: *Tidy item names* → *Load the list*. The owner answers in steps: automatic fixes (no questions), one question per
   formatting rule, one card per real decision (duplicates show the look-alike's sales and stock), one ticked list of items to
   retire, one card per GST/HSN point. Answers are saved as he goes (`var\cleanup.db`, never in HQ).
3. *Write to HQ* saves each item through the HQ-identical item save, leaves alone anything changed in HQ since the list was
   made (`ItemCleanupTests`). His rules: sizes with `*`; spelling/capitals/spaces need no
   approval; 63/75/90/110 MM elbows and tees say HEAVY, not ISI.

## Assistant (Google Antigravity, over ACP)

The *Assistant* button in the letterhead (Alt+A) opens a conversation docked beside every screen. It is Google
Antigravity's agent, using the shop's own Antigravity subscription (Google AI Pro), so there is no API key to manage.

```
browser panel (js/assistant.js) ──/api/agent──▶ AgentHub ──stdin/stdout, ACP v1──▶ agy acp (Antigravity CLI)
                                                                                      │ HTTP MCP, per-chat bearer key
                          AgentTools ◀──────────────── POST /mcp ◀────────────────────┘
```

* **ACP:** `AgentHub` starts one `agy acp --process-per-session --sandbox` for the service and opens one ACP session per
  conversation. It declares no file or terminal access. Commands and file edits are refused automatically; any other
  permission request is shown to the user. Replies, tool calls and plans stream to the browser over SSE; the model
  (Gemini, Claude, GPT-OSS, whatever the plan offers) can be switched per conversation.
* **MCP:** each session gets MK's tools at `/mcp` with its own key: look up parties and items, ledgers, outstanding,
  bills, day figures, stock, GST registers, HQ table columns and **one read-only SELECT** (no writes, no semicolons,
  dirty reads so it never holds up an HQ PC, rolled back anyway), plus `open_screen` (moves the user's window) and
  `prepare_bill`.
* **No AI writes to HQ.** `prepare_bill` saves a *draft* in the bill editor's own format (as photo drafts are), prices it
  with HQ's arithmetic and opens it. The user checks it and presses F10, which goes through the normal reviewed save.
* **Set-up on a PC:** `irm https://antigravity.google/cli/install.ps1 | iex`, then `agy` once in a terminal (sign in with
  Google, say hi). The sign-in lives in Windows Credential Manager, so the service must run in the same Windows user's
  desktop session (as the launcher does); an SSH session cannot see it. The panel's *Sign in…* button opens
  `agy acp --login` on the PC. Optional settings: `"Agent": { "AgyPath": …, "Arguments": [...], "ToolsBaseUrl": … }`.
* Conversations live in memory: a restart of the service starts fresh ones.

## Scope

Done: sales (cash, credit, card/UPI modes), IGST bills, estimates/sales orders and making a sale from an order,
purchases (with supplier-bill duplicate guard), purchase orders, correcting and deleting any of those (HQ's or MK's,
with bill-wise settlements carried over or released), cash/bank receipts and payments, credit and debit notes, all with
bill-wise allocation; correcting and deleting vouchers; items and customers/suppliers (new and changes); drafts, GST
invoice printing, bill photos (Antigravity, or Gemma 4), dashboard, account ledger and cash book, outstanding with ageing, day book,
sales/purchase registers with GST-by-rate (B2B/B2C) and HSN summaries, stock, HQ health, operations journal, and a
live change feed (the browser refreshes when anyone saves in HQ).

Left to HQ, deliberately (none is used in either firm's data this year): HQ's split "Others" payment mode, sales and
purchase returns, bill-wise opening balances, other account types (banks, expenses, groups), and items with two
packs or several unit rows. Each is refused with a clear message rather than guessed.

## Shop integrations

- See [External MCP](docs/MCP.md) for the Antigravity scanner, item lookup, and bill posting workflow.
- Run `tools\publish.ps1 -Staging` to build under `artifacts\publish` for a staged deployment.
