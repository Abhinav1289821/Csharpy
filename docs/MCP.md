# External MCP

Antigravity starts `mk-billing` as a local MCP process. It reconnects on demand and keeps the entire billing workflow inside the conversation.

The entire workflow stays inside Antigravity. Read the scan image, resolve item choices, show the barebone monochromatic HTML preview, obtain the user's approval, post the bill, and display the saved receipt in the conversation. Follow the full [shop rules](ANTIGRAVITY.md), including short Telenglish replies, native question controls, pipe sizes, units, quality choices, and extraction rules. External tools return their results directly and perform no browser navigation.

| Tool | Input | Output |
| --- | --- | --- |
| `scan` | Optional `scannerName` if several scanners exist | JPEG image content |
| `lookup_items` | `firm`, `queries` array, optional `take` per query | Candidates with item/unit IDs, rates, GST, HSN, stock and warning notes; query-level chosen candidate or null and reason |
| `post_bill` | `firm`, `operationId`, `kind`, `date`, `lines`, optional party/payment/tax fields | Saved HQ receipt, document ID, reference and total |

`firm` is `mbm` or `mp`. Use `kind` `estimate` for a sales estimate, `invoice` for a sales invoice, or `purchase` for a supplier invoice.

```json
{"name":"lookup_items","arguments":{"firm":"mbm","queries":["75 MM PVC PIPE","25 MM ELBOW"],"take":5}}
```

```json
{"name":"post_bill","arguments":{"firm":"mbm","operationId":"e192c123-973e-499f-9ef3-d364ee875a92","kind":"estimate","date":"2026-11-12","party":{"name":"Walk-in"},"lines":[{"itemId":123,"unitId":3,"quantity":2,"rate":118,"discountPercent":0}]}}
```

Use actual IDs from lookup results. `post_bill` saves directly to HQ. Reuse the same operation ID and identical payload after a timeout or lost response. Generate a new ID for a different bill. For sales, payment mode defaults to cash (`0`), credit (`1`) requires `accountId`, and `basis` is optional.

For purchases send `supplier: {number, date}`, `party.gstin` for the supplier, `buyerGstin` if printed, `invoiceTotal`, `basis`, and `paymentMode` (`0` cash or `1` credit). A unique supplier GSTIN match supplies the account ID. `supplier.date` supplies the posting date unless explicitly overridden. Include freight and packing as priced lines. Posting checks the printed total before saving.

For maintainers, run `./tools/configure-mcp.ps1` from the repository root. For demo use `-Demo`. To configure across workspaces, pass `-ConfigPath "$env:USERPROFILE/.gemini/config/mcp_config.json"`. The script merges the entry, syncs the [Antigravity rules](ANTIGRAVITY.md), updates the installed mk-shop rules, and refreshes desktop and CLI tool caches. For clients that require HTTP, use `-Transport http` with `http://127.0.0.1:47320/mcp`. Backups are saved under `artifacts/antigravity-setup-backups`. Generated configs are ignored by Git.

Reload MCP servers afterward. Start a fresh conversation when an existing chat still contains older tool names or billing instructions.

The server runs in MK Billing. Builds are in `artifacts/publish`; the live launcher uses `publish`. Deployment preserves the live `appsettings.json` and `var` directory. To revoke the external credential, stop the app, remove `var/mcp.token`, restart, and rerun configuration.

[Antigravity MCP configuration](https://antigravity.google/docs/mcp/)
