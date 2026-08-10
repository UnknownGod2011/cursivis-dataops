# Devpost submission draft

**Title:** Cursivis DataOps
**Tagline:** Selected SQL becomes DataHub-grounded, Gemini-reasoned data action.

## Short description

Cursivis DataOps is a Windows context-native agent for data teams. Select broken SQL, a schema question, or a pipeline error; Cursivis resolves the real DataHub asset, asks Gemini to reason over its schema, ownership, and lineage, and returns an actionable, safe result.

## Detailed description

Data engineers work in editors, terminals, and browser consoles—not in a generic chat tab. Cursivis starts from their selected context. When that context references data, its Gemini provider first queries DataHub GraphQL for the authoritative dataset and metadata. The model receives the selected text plus real catalog evidence; it is not allowed to claim grounding if DataHub cannot resolve the asset.

The result explains the correction and impact, then reuses Cursivis’ existing Copy, Insert, Replace, and safe-action interactions. A reviewed resolution can be written back explicitly as a DataHub context document with the official DataHub MCP `save_document` mutation, creating durable organizational knowledge for the next agent.

**Recommended challenge:** Agents That Do Real Work + Metadata-Aware Code Generation & Development

**Technologies:** WinUI 3/.NET 8, Gemini Developer API structured output, DataHub GraphQL API, DataHub MCP write-back, Docker/DataHub OSS.

**Why DataHub:** schema, lineage, ownership and catalog knowledge are the evidence that makes the SQL advice trustworthy.

**Originality:** context-native desktop action paired with a fail-closed catalog grounding boundary and reviewed organizational learning.

**GitHub:** `https://github.com/UnknownGod2011/cursivis-dataops`
**Demo video:** _add public YouTube/Vimeo URL_
