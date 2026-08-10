# Cursivis DataOps demo (about 2:45)

**0:00–0:20 — Problem**  
“AI can fix SQL syntax, but it does not know our authoritative datasets, real schema, owners, or blast radius. Cursivis DataOps grounds the agent in DataHub before Gemini reasons.”

**0:20–1:20 — Real MCP grounding**  
Open `examples/broken-query.sql`, select the query, and invoke Cursivis. The selected SQL references `analytics.customers` but incorrectly uses `lifetime_value` and `tier`. Call out **Grounding with DataHub** while Cursivis uses the official DataHub MCP Server to run `search`, `get_entities`, `list_schema_fields`, and `get_lineage`. Show the governed fields `lifetime_value_usd` and `customer_tier`, ownership, and the downstream `analytics.executive_revenue` and `ml.churn_prediction_features` assets.

**1:20–1:45 — Act in place**  
Use Copy, Insert, or Replace. Emphasize that Cursivis acts on the captured local target rather than asking the user to shuttle content through another chat window.

**1:45–2:20 — Safe agent write-back**  
On the grounded result, click **Save to DataHub**. Point out that the first click only arms the mutation. Click **Confirm Save** to explicitly approve it. Cursivis invokes the official MCP `save_document` tool with the grounded dataset as a related asset, then calls MCP `get_entities` on the returned document URN. Show the success notice only after this read-after-write verification completes.

**2:20–2:35 — Why DataHub matters**  
Show the lineage-aware impact information: the answer is not merely syntactically valid SQL; it is an organization-aware decision based on the actual catalog.

**2:35–2:45 — Close**  
“Selection gives us intent. DataHub gives us organizational truth. Gemini reasons over it. Cursivis turns the result into action — and saves reviewed knowledge back for the next human or agent.”

## Before recording

```powershell
.\scripts\bootstrap-datahub.ps1
.\scripts\seed-demo-data.ps1
.\scripts\run-demo.ps1
```

Do not record a successful grounding or write-back if any seed/MCP/read-after-write verification step fails. The demo is designed to fail closed rather than substitute fixtures for live DataHub evidence.
