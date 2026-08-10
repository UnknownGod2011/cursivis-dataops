# Devpost submission draft

**Title:** Cursivis DataOps  
**Tagline:** Selected SQL becomes DataHub-grounded, Gemini-reasoned action.

## Short description

Cursivis DataOps is a context-native Windows agent for data teams. Select broken SQL or a data problem; Cursivis reads the live organizational context through the official DataHub MCP Server, asks Gemini to reason over verified schema, ownership, descriptions, and lineage, and returns a safe result that can be acted on in place. Reviewed resolutions can be explicitly saved back to DataHub through MCP and verified by read-after-write.

## Detailed description

Data engineers work in editors, terminals, and browser consoles—not in a generic chat tab. Cursivis starts from the user's selected context. When selected SQL references data, the runtime extracts the dataset name and calls the official **DataHub MCP Server** tools `search`, `get_entities`, `list_schema_fields`, and `get_lineage`. Gemini receives a bounded package of that live DataHub evidence and is instructed not to invent fields, owners, or lineage relationships. If DataHub cannot resolve the asset confidently, the workflow fails closed rather than presenting generic model output as grounded truth.

The result reuses Cursivis' safe Copy, Insert, and Replace interactions. For durable organizational learning, **Save to DataHub** is a two-step confirmed action: the first click arms the mutation, the second explicitly approves it, Cursivis invokes MCP `save_document` linked to the grounded asset, and then uses MCP `get_entities` to read the new document back before reporting success.

The deterministic local catalog is seeded with DataHub OSS/Core and includes a deliberately broken query against `analytics.customers`, its governed schema (`customer_id`, `lifetime_value_usd`, `customer_tier`, `updated_at`), ownership, one upstream source, and two downstream consumers. Setup verifies the metadata is real and searchable rather than substituting example JSON for runtime context.

**Challenge:** Agents That Do Real Work  
**DataHub technologies:** DataHub OSS / Core Platform + DataHub MCP Server  
**Built with:** C#/.NET 8, WinUI 3, Gemini Developer API structured output, DataHub MCP Server, DataHub OSS/Core, DataHub Python SDK, Docker, PowerShell  
**Why DataHub:** schema, lineage, ownership, descriptions, and durable documents are the organizational evidence and memory that turn generic AI advice into a trustworthy data action.  
**Originality:** selection becomes immediate intent; DataHub becomes the agent's organizational truth; Gemini reasons over that truth; Cursivis safely turns the result into action and reviewed memory.

**GitHub:** `https://github.com/UnknownGod2011/cursivis-dataops`  
**Artifacts:** `https://github.com/UnknownGod2011/cursivis-dataops/tree/main/examples`  
**Demo video:** add the submitted public video URL
