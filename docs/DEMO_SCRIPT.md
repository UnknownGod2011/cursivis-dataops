# Cursivis DataOps demo (2:45)

0:00–0:20 — “AI can fix SQL syntax, but it does not know our authoritative datasets, schema, ownership, or blast radius.”

0:20–1:20 — Open `examples/broken-query.sql`, select it, and invoke Cursivis. Call out **Grounding with DataHub** while it resolves `analytics.customer_360`. Show the corrected fields, the DataHub Context Used section in the response, the `data-platform` owner, and the two downstream consumers.

1:20–1:50 — Click Copy or Insert/Replace. Emphasize that the text action is local and reversible.

1:50–2:20 — Review `examples/resolution-example.md`, then explicitly save it as an approved DataHub context document using the documented MCP `save_document` command. Search the document in DataHub to show read-after-write.

2:20–2:40 — Ask “what breaks if this field changes?” and show the lineage-aware downstream list.

2:40–2:55 — “Cursivis turns what you are working on into intent. DataHub makes sure the agent understands what that data actually means.”
